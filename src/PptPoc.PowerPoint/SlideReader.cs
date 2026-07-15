using System.Runtime.InteropServices;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using Ppt = Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;
using Path = System.IO.Path;
using File = System.IO.File;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace PptPoc.PowerPoint;

public class SlideReader : ISlideReader
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SlideReader>();
    private static readonly object DebugArtifactLock = new();

    private readonly IOcrService? _ocr;
    private readonly IOpenAIVisionService? _gptVision;
    private readonly Dictionary<int, List<OcrWordInfo>> _ocrCache = new();

    // ── Enhancement #7: OCR noise words to filter from InferredKeywords ──────
    // These are chart-axis artifacts, statistical labels, and formatting noise
    // that cause false-positive matches when the user speaks naturally.
    private static readonly HashSet<string> OcrNoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "stderr", "acc", "std", "err", "avg", "mean", "min", "max",
        "nan", "inf", "null", "none", "true", "false",
        "fig", "figure", "table", "source", "note", "notes",
        "val", "var", "ref", "col", "row", "num", "pct"
    };

    private static readonly Regex NumericOcrTokenRegex =
        new(@"^\d+(?:\.\d+)?%?$", RegexOptions.Compiled);

    private static readonly Regex TerminalNoiseRegex =
        new(@"(--|/|\\|::|=>|\$|\.py|\.sh|\.exe|\.cache)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public SlideReader(IOcrService? ocr = null, IOpenAIVisionService? gptVision = null)
    {
        _ocr = ocr;
        _gptVision = gptVision;
    }

    public SlideSnapshot ReadSlide(object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;
        var snapshot = new SlideSnapshot
        {
            SlideIndex = slide.SlideIndex,
            SlideId = slide.SlideID.ToString()
        };

        float sW = 960f, sH = 540f;
        try 
        {
            sW = slide.Master.Width;
            sH = slide.Master.Height;
        } 
        catch { }

        try
        {
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                ProcessShape(shape, snapshot, null, slide, sW, sH);
            }
        }
        catch (COMException ex)
        {
            Log.Error(ex, "Error reading shapes from slide {SlideIndex}", slide.SlideIndex);
        }

        // Run LLM Vision on entire slide
        if (_gptVision != null && snapshot.ImageElements.Count > 0)
        {
            _ = RunGptVisionOnSlideAsync(snapshot, slide);
        }

        // Run image OCR/enrichment on all image elements asynchronously
        if ((_ocr != null || _gptVision != null) && snapshot.ImageElements.Count > 0)
        {
            _ = RunOcrOnImagesAsync(snapshot.ImageElements, slide);
        }

        Log.Debug("Read slide {SlideIndex}: {TextCount} text elements, {ImageCount} image elements",
            snapshot.SlideIndex, snapshot.TextElements.Count, snapshot.ImageElements.Count);

        return snapshot;
    }

    /// <summary>
    /// Reads a slide and awaits all async enrichment (OCR + LLM vision).
    /// Use for preprocessing where we need complete data before serializing.
    /// </summary>
    public async Task<SlideSnapshot> ReadSlideFullAsync(object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;
        var snapshot = new SlideSnapshot
        {
            SlideIndex = slide.SlideIndex,
            SlideId = slide.SlideID.ToString()
        };

        float sW = 960f, sH = 540f;
        try { sW = slide.Master.Width; sH = slide.Master.Height; } catch { }

        try
        {
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                ProcessShape(shape, snapshot, null, slide, sW, sH);
            }
        }
        catch (COMException ex)
        {
            Log.Error(ex, "Error reading shapes from slide {SlideIndex}", slide.SlideIndex);
        }

        // Await OCR and LLM vision enrichment in parallel
        var tasks = new List<Task>();

        if ((_ocr != null || _gptVision != null) && snapshot.ImageElements.Count > 0)
            tasks.Add(RunOcrOnImagesAsync(snapshot.ImageElements, slide));

        if (_gptVision != null && snapshot.ImageElements.Count > 0)
            tasks.Add(RunGptVisionOnSlideAsync(snapshot, slide));

        if (tasks.Count > 0)
            await Task.WhenAll(tasks);

        Log.Information("ReadSlideFullAsync slide {SlideIndex}: {TextCount} text, {ImageCount} image (OCR+Vision awaited)",
            snapshot.SlideIndex, snapshot.TextElements.Count, snapshot.ImageElements.Count);

        return snapshot;
    }

    // ── Pipeline methods for concurrent preprocessing ───────────────────

    /// <summary>Phase 1: Extract shapes from COM (synchronous, STA thread only).</summary>
    public SlideSnapshot ExtractShapesSync(object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;
        var snapshot = new SlideSnapshot
        {
            SlideIndex = slide.SlideIndex,
            SlideId = slide.SlideID.ToString()
        };

        float sW = 960f, sH = 540f;
        try { sW = slide.Master.Width; sH = slide.Master.Height; } catch { }

        try
        {
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                ProcessShape(shape, snapshot, null, slide, sW, sH);
            }
        }
        catch (COMException ex)
        {
            Log.Error(ex, "Error reading shapes from slide {SlideIndex}", slide.SlideIndex);
        }

        return snapshot;
    }

    /// <summary>Phase 2: Export all image bytes from COM (synchronous, STA thread only).</summary>
    public (List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) ExportImageBytes(SlideSnapshot snapshot, object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;
        var imageData = new List<(ImageElement img, int shapeId, byte[] bytes)>();
        byte[]? slideImageBytes = null;
        string manifest = "";

        // Export individual shape images
        foreach (var img in snapshot.ImageElements)
        {
            try
            {
                var shapeId = ExtractShapeId(img.ElementId);
                if (shapeId <= 0) continue;

                if (_ocrCache.TryGetValue(shapeId, out var cachedWords))
                {
                    img.ExtractedWords = cachedWords;
                    img.SearchableWords = FilterSearchableOcrWords(cachedWords);
                    img.FilteredOcrText = string.Join(" ", img.SearchableWords.Select(w => w.Text));
                    var combinedText = img.FilteredOcrText;
                    foreach (var word in FilteredTokenize(NormalizeText(combinedText)))
                    {
                        if (!img.InferredKeywords.Contains(word))
                            img.InferredKeywords.Add(word);
                    }
                    continue;
                }

                byte[]? imageBytes = ExportShapeAsImage(slide, shapeId);
                if (imageBytes != null && imageBytes.Length > 0)
                    imageData.Add((img, shapeId, imageBytes));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Export failed for image element {Id}", img.ElementId);
            }
        }

        // Export full slide image + build manifest for LLM vision
        if (_gptVision != null && snapshot.ImageElements.Count > 0)
        {
            try
            {
                var manifestParts = new List<string>();
                foreach (var img in snapshot.ImageElements)
                    manifestParts.Add($"- Image ['{img.ElementId}']: box_2d [{img.BoundingBox255[0]}, {img.BoundingBox255[1]}, {img.BoundingBox255[2]}, {img.BoundingBox255[3]}], title '{img.Title}'");
                foreach (var txt in snapshot.TextElements)
                    manifestParts.Add($"- Text ['{txt.ElementId}']: box_2d [{txt.BoundingBox255[0]}, {txt.BoundingBox255[1]}, {txt.BoundingBox255[2]}, {txt.BoundingBox255[3]}], content: \"{txt.RawText}\"");
                manifest = string.Join("\n", manifestParts);

                var tempPath = Path.Combine(Path.GetTempPath(), $"slide_full_{Guid.NewGuid():N}.png");
                slide.Export(tempPath, "PNG");
                slideImageBytes = File.ReadAllBytes(tempPath);
                File.Delete(tempPath);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to export slide image");
            }
        }

        return (imageData, slideImageBytes, manifest);
    }

    /// <summary>Phase 3: Run API enrichment (OCR, explain, vision) — no COM needed, thread-safe.</summary>
    public async Task RunApiEnrichmentAsync(
        SlideSnapshot snapshot,
        (List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) exports,
        object slideComObject)
    {
        var tasks = new List<Task>();

        // LLM vision analysis on full slide (no COM needed — bytes already exported)
        if (_gptVision != null && exports.slideImage != null)
        {
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    string gptJson = await _gptVision.AnalyzeSlideAsync(exports.slideImage, exports.manifest);
                    if (!string.IsNullOrWhiteSpace(gptJson))
                    {
                        // Strip markdown code fences that LLMs sometimes wrap around JSON
                        gptJson = StripMarkdownFences(gptJson);

                        // Handle potentially truncated JSON from max_tokens limit
                        JsonDocument? doc = null;
                        try { doc = JsonDocument.Parse(gptJson); }
                        catch (JsonException)
                        {
                            // Try to salvage by closing the JSON structure
                            var salvaged = gptJson;
                            // Find last complete object closing brace
                            int lastBrace = salvaged.LastIndexOf('}');
                            if (lastBrace > 0)
                            {
                                salvaged = salvaged[..(lastBrace + 1)] + "]}";
                                try { doc = JsonDocument.Parse(salvaged); }
                                catch { /* truly unparseable */ }
                            }
                        }

                        if (doc != null)
                        {
                            using (doc)
                            {
                                ApplyVisionDescriptions(doc, snapshot);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Failed to run vision analysis on slide.");
                }
            }));
        }

        // OCR + Explain for each image (parallel across images)
        foreach (var (img, shapeId, bytes) in exports.images)
        {
            tasks.Add(ProcessSingleImageAsync(img, shapeId, bytes, snapshot.SlideIndex));
        }

        await Task.WhenAll(tasks);
    }

    // ── Enhancement #1: CRITICAL FIX — Strip markdown fences before JSON parse ──
    // LLMs (Claude, GPT-4o, Gemini, etc.) often wrap JSON in ```json ... ```
    // This caused a JsonReaderException on every slide in the runtime path.
    private async Task RunGptVisionOnSlideAsync(SlideSnapshot snapshot, Ppt.Slide slide)
    {
        try
        {
            var manifestParts = new List<string>();
            foreach (var img in snapshot.ImageElements)
            {
                manifestParts.Add($"- Image ['{img.ElementId}']: box_2d [{img.BoundingBox255[0]}, {img.BoundingBox255[1]}, {img.BoundingBox255[2]}, {img.BoundingBox255[3]}], title '{img.Title}'");
            }
            foreach (var txt in snapshot.TextElements)
            {
                manifestParts.Add($"- Text ['{txt.ElementId}']: box_2d [{txt.BoundingBox255[0]}, {txt.BoundingBox255[1]}, {txt.BoundingBox255[2]}, {txt.BoundingBox255[3]}], content: \"{txt.RawText}\"");
            }

            string manifest = string.Join("\n", manifestParts);

            var tempPath = Path.Combine(Path.GetTempPath(), $"slide_full_{Guid.NewGuid():N}.png");
            slide.Export(tempPath, "PNG");
            byte[] imageBytes = File.ReadAllBytes(tempPath);
            File.Delete(tempPath);

            string gptJson = await _gptVision!.AnalyzeSlideAsync(imageBytes, manifest);
            
            if (!string.IsNullOrWhiteSpace(gptJson))
            {
                // ── FIX: Strip markdown code fences before parsing ──────────
                gptJson = StripMarkdownFences(gptJson);

                JsonDocument? doc = null;
                try
                {
                    doc = JsonDocument.Parse(gptJson);
                }
                catch (JsonException)
                {
                    // Try to salvage truncated JSON by closing the structure
                    var salvaged = gptJson;
                    int lastBrace = salvaged.LastIndexOf('}');
                    if (lastBrace > 0)
                    {
                        salvaged = salvaged[..(lastBrace + 1)] + "]}";
                        try { doc = JsonDocument.Parse(salvaged); }
                        catch { /* truly unparseable */ }
                    }
                }

                if (doc != null)
                {
                    using (doc)
                    {
                        ApplyVisionDescriptions(doc, snapshot);
                    }
                    Log.Information("Vision analysis applied successfully for slide {SlideIndex}", snapshot.SlideIndex);
                }
                else
                {
                    Log.Warning("Vision analysis returned unparseable JSON for slide {SlideIndex}", snapshot.SlideIndex);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to run vision analysis on slide.");
        }
    }

    /// <summary>
    /// Strips markdown code fences (```json ... ```) that LLMs commonly wrap around JSON responses.
    /// Works with any LLM provider (OpenAI, Anthropic/Claude, Google Gemini, etc.).
    /// </summary>
    private static string StripMarkdownFences(string raw)
    {
        var cleaned = raw.Trim();
        if (cleaned.StartsWith("```"))
        {
            // Remove opening fence (```json, ```JSON, ```, etc.)
            int firstNewline = cleaned.IndexOf('\n');
            if (firstNewline > 0)
                cleaned = cleaned[(firstNewline + 1)..];
            else
                cleaned = cleaned[3..]; // Edge case: no newline after ```

            // Remove closing fence
            if (cleaned.TrimEnd().EndsWith("```"))
            {
                int lastFence = cleaned.LastIndexOf("```");
                if (lastFence >= 0)
                    cleaned = cleaned[..lastFence];
            }
            cleaned = cleaned.Trim();
        }
        return cleaned;
    }

    /// <summary>
    /// Applies vision descriptions from a parsed JSON document to the snapshot elements.
    /// Shared between the runtime and preprocessing paths.
    /// </summary>
    private static void ApplyVisionDescriptions(JsonDocument doc, SlideSnapshot snapshot)
    {
        if (doc.RootElement.TryGetProperty("elements", out var elemArr))
        {
            foreach (var el in elemArr.EnumerateArray())
            {
                if (!el.TryGetProperty("id", out var idProp)) continue;
                string id = idProp.GetString() ?? "";
                string desc = el.TryGetProperty("rich_description", out var descProp)
                    ? descProp.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(desc)) continue;

                var imgTarget = snapshot.ImageElements.FirstOrDefault(x => x.ElementId == id);
                if (imgTarget != null) imgTarget.GptDescription = desc;

                var txtTarget = snapshot.TextElements.FirstOrDefault(x => x.ElementId == id);
                if (txtTarget != null) txtTarget.GptDescription = desc;
            }
        }
    }

    private async Task RunOcrOnImagesAsync(List<ImageElement> images, Ppt.Slide slide)
    {
        // Phase 1: Export all images from COM (must be sequential — STA thread)
        var imageData = new List<(ImageElement img, int shapeId, byte[] bytes)>();
        foreach (var img in images)
        {
            try
            {
                var shapeId = ExtractShapeId(img.ElementId);
                if (shapeId <= 0) continue;

                if (_ocrCache.TryGetValue(shapeId, out var cachedWords))
                {
                    img.ExtractedWords = cachedWords;
                    img.SearchableWords = FilterSearchableOcrWords(cachedWords);
                    img.FilteredOcrText = string.Join(" ", img.SearchableWords.Select(w => w.Text));
                    var combinedText = img.FilteredOcrText;
                    foreach (var word in FilteredTokenize(NormalizeText(combinedText)))
                    {
                        if (!img.InferredKeywords.Contains(word))
                            img.InferredKeywords.Add(word);
                    }
                    continue;
                }

                byte[]? imageBytes = await Task.Run(() => ExportShapeAsImage(slide, shapeId));
                if (imageBytes == null || imageBytes.Length == 0) continue;

                imageData.Add((img, shapeId, imageBytes));
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Export failed for image element {Id}", img.ElementId);
            }
        }

        // Phase 2: Run OCR + Explain API calls in parallel across all images
        var tasks = imageData.Select(item => ProcessSingleImageAsync(item.img, item.shapeId, item.bytes, slide.SlideIndex)).ToList();
        await Task.WhenAll(tasks);
    }

    private async Task ProcessSingleImageAsync(ImageElement img, int shapeId, byte[] imageBytes, int slideIndex)
    {
        try
        {
            List<OcrWordInfo> ocrWords = new();

            if (_gptVision != null)
            {
                ocrWords = await _gptVision.ExtractOcrWordsAsync(imageBytes);
            }

            if (ocrWords.Count == 0 && _ocr != null)
            {
                ocrWords = await _ocr.ExtractTextAsync(imageBytes);
            }

            if (ocrWords.Count > 0)
            {
                _ocrCache[shapeId] = ocrWords;
                img.ExtractedWords = ocrWords;
                img.SearchableWords = FilterSearchableOcrWords(ocrWords);
                img.FilteredOcrText = string.Join(" ", img.SearchableWords.Select(w => w.Text));

                var combinedText = img.FilteredOcrText;
                // ── Enhancement #7: Filter noise words from InferredKeywords ─
                var tokenized = FilteredTokenize(NormalizeText(combinedText));
                foreach (var word in tokenized)
                {
                    if (!img.InferredKeywords.Contains(word))
                        img.InferredKeywords.Add(word);
                }

                Log.Debug("OCR on shape {Id}: {Count} words extracted", shapeId, ocrWords.Count);
            }

            // Per-image conceptual explanation (includes OCR hint tokens when available).
            if (_gptVision != null && string.IsNullOrWhiteSpace(img.GptDescription))
            {
                var explanation = await _gptVision.ExplainImageAsync(imageBytes, ocrWords);
                if (!string.IsNullOrWhiteSpace(explanation))
                    img.GptDescription = explanation;
            }

            if (!string.IsNullOrWhiteSpace(img.GptDescription) || !string.IsNullOrWhiteSpace(img.FilteredOcrText))
            {
                img.VisualSearchText = $"{img.GptDescription} {img.FilteredOcrText}".Trim();
            }

            WriteImageDebugArtifact(slideIndex, img);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR/explain failed for image element {Id}", img.ElementId);
        }
    }

    /// <summary>
    /// Enhancement #7: Tokenize and filter out noise words from OCR output.
    /// Removes chart-axis artifacts (stderr, acc, std), very short tokens,
    /// and short numeric-only tokens that cause false-positive keyword matches.
    /// </summary>
    private static List<string> FilteredTokenize(string normalizedText)
    {
        var allTokens = TokenizeWords(normalizedText);
        var filtered = new List<string>();

        foreach (var word in allTokens)
        {
            // Skip very short tokens (1-2 chars)
            if (word.Length < 3) continue;

            // Skip known chart/statistical noise words
            if (OcrNoiseWords.Contains(word)) continue;

            // Skip short numeric-only tokens (e.g. "5107", "0041") — chart axis values
            if (Regex.IsMatch(word, @"^\d+$") && word.Length <= 4) continue;

            filtered.Add(word);
        }

        return filtered;
    }

    private static List<OcrWordInfo> FilterSearchableOcrWords(IReadOnlyList<OcrWordInfo> words)
    {
        var filtered = new List<OcrWordInfo>();

        foreach (var word in words)
        {
            if (word == null || string.IsNullOrWhiteSpace(word.Text))
                continue;

            var raw = word.Text.Trim();
            if (TerminalNoiseRegex.IsMatch(raw))
                continue;

            var normalized = NormalizeText(raw);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            var token = normalized
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(token))
                continue;

            bool isNumericToken = NumericOcrTokenRegex.IsMatch(token);

            if (token.Length < 3 && !isNumericToken)
                continue;

            if (OcrNoiseWords.Contains(token))
                continue;

            if (Regex.IsMatch(token, @"^\d+$") && token.Length <= 4)
                continue;

            filtered.Add(new OcrWordInfo
            {
                Text = token,
                X = word.X,
                Y = word.Y,
                Width = word.Width,
                Height = word.Height
            });
        }

        return filtered;
    }

    private static int ExtractShapeId(string elementId)
    {
        // Element ID format: "{prefix}I{shapeId}_{slideIndex}" or "{prefix}C{shapeId}_{slideIndex}" (charts)
        var iIdx = elementId.LastIndexOf('I');
        var cIdx = elementId.LastIndexOf('C');
        var prefixIdx = Math.Max(iIdx, cIdx);
        if (prefixIdx < 0) return 0;
        var afterPrefix = elementId[(prefixIdx + 1)..];
        var underIdx = afterPrefix.IndexOf('_');
        var idStr = underIdx >= 0 ? afterPrefix[..underIdx] : afterPrefix;
        return int.TryParse(idStr, out int id) ? id : 0;
    }

    private static byte[]? ExportShapeAsImage(Ppt.Slide slide, int shapeId)
    {
        var tempPath = Path.Combine(Path.GetTempPath(),
            $"pptpoc_shape_{shapeId}_{Guid.NewGuid():N}.png");
        try
        {
            Ppt.Shape? target = null;
            foreach (Ppt.Shape s in slide.Shapes)
            {
                if (s.Id == shapeId) { target = s; break; }
            }
            if (target == null) return null;

            target.Export(tempPath, Ppt.PpShapeFormat.ppShapeFormatPNG, 0, 0,
                Ppt.PpExportMode.ppScaleToFit);

            return File.Exists(tempPath) ? File.ReadAllBytes(tempPath) : null;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to export shape {Id} as image", shapeId);
            return null;
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* ignore */ }
        }
    }

    private static int[] CalcBox255(float left, float top, float width, float height, float sW, float sH)
    {
        return [
            Math.Clamp((int)(left / sW * 255), 0, 255),
            Math.Clamp((int)(top / sH * 255), 0, 255),
            Math.Clamp((int)((left + width) / sW * 255), 0, 255),
            Math.Clamp((int)((top + height) / sH * 255), 0, 255)
        ];
    }

    private void ProcessShape(Ppt.Shape shape, SlideSnapshot snapshot, string? groupPrefix, Ppt.Slide slide, float sW, float sH)
    {
        string idPrefix = groupPrefix ?? "";

        try
        {
            // Handle grouped shapes recursively
            if (shape.Type == Office.MsoShapeType.msoGroup)
            {
                foreach (Ppt.Shape child in shape.GroupItems)
                {
                    ProcessShape(child, snapshot, $"{idPrefix}G{shape.Id}_", slide, sW, sH);
                }
                return;
            }

            string? smartArtVisualId = null;
            if (IsSmartArtShape(shape))
            {
                var smartArtVisual = new ImageElement
                {
                    ElementId = $"{idPrefix}I{shape.Id}_{snapshot.SlideIndex}",
                    ShapeName = shape.Name,
                    Left = shape.Left,
                    Top = shape.Top,
                    Width = shape.Width,
                    Height = shape.Height,
                    BoundingBox255 = CalcBox255(shape.Left, shape.Top, shape.Width, shape.Height, sW, sH),
                    ZOrder = shape.ZOrderPosition,
                    AltText = GetAltText(shape),
                    Title = GetTitle(shape),
                    NearbyText = FindNearbyText(shape, snapshot),
                    InferredKeywords = InferKeywords(GetAltText(shape), GetTitle(shape), FindNearbyText(shape, snapshot)),
                    VisualType = "diagram",
                    VisualSubtype = "smartart",
                    Importance = "high"
                };

                ClassifyVisualImportance(smartArtVisual, sW, sH);
                snapshot.ImageElements.Add(smartArtVisual);
                smartArtVisualId = smartArtVisual.ElementId;
            }

            // Extract text elements — one per paragraph for sentence-level highlighting.
            if (shape.HasTextFrame == Office.MsoTriState.msoTrue)
            {
                var textRange = shape.TextFrame.TextRange;
                int paraCount = textRange.Paragraphs().Count;

                for (int pi = 1; pi <= paraCount; pi++)
                {
                    var para = textRange.Paragraphs(pi, 1);
                    var text = para.Text?.Trim('\r', '\v', '\n', ' ');
                    if (string.IsNullOrWhiteSpace(text)) continue;

                    // Try to get actual paragraph bounds from TextRange
                    float pLeft, pTop, pWidth, pHeight;
                    try
                    {
                        // TextRange exposes BoundLeft/Top/Width/Height for the text bounds
                        pLeft = para.BoundLeft;
                        pTop = para.BoundTop;
                        pWidth = para.BoundWidth;
                        pHeight = para.BoundHeight;
                    }
                    catch
                    {
                        // Fallback: proportional estimate based on shape dimensions
                        pLeft = shape.Left;
                        pTop = shape.Top + (float)(pi - 1) / paraCount * shape.Height;
                        pWidth = shape.Width;
                        pHeight = shape.Height / paraCount;
                    }

                    var normalized = NormalizeText(text);
                    snapshot.TextElements.Add(new TextElement
                    {
                        ElementId      = $"{idPrefix}S{shape.Id}_{snapshot.SlideIndex}_P{pi}",
                        ShapeName      = $"{shape.Name}:P{pi}",
                        Left           = pLeft,
                        Top            = pTop,
                        Width          = pWidth,
                        Height         = pHeight,
                        BoundingBox255 = CalcBox255(pLeft, pTop, pWidth, pHeight, sW, sH),
                        ZOrder         = shape.ZOrderPosition,
                        RawText        = text,
                        NormalizedText = normalized,
                        Words          = TokenizeWords(normalized),
                        ParagraphIndex = pi,
                        ParentVisualId = smartArtVisualId,
                        ParentVisualReason = smartArtVisualId != null ? "smartart_text_routes_to_diagram" : null
                    });
                }
            }

            // Extract table cells as individual text elements
            if (shape.HasTable == Office.MsoTriState.msoTrue)
            {
                try
                {
                    var table = shape.Table;
                    for (int row = 1; row <= table.Rows.Count; row++)
                    {
                        for (int col = 1; col <= table.Columns.Count; col++)
                        {
                            try
                            {
                                var cell = table.Cell(row, col);
                                var cellText = cell.Shape.TextFrame.TextRange.Text?.Trim('\r', '\v', '\n', ' ');
                                if (string.IsNullOrWhiteSpace(cellText)) continue;

                                // Approximate cell position from shape bounds and grid proportions
                                float cellLeft = shape.Left + (float)(col - 1) / table.Columns.Count * shape.Width;
                                float cellTop = shape.Top + (float)(row - 1) / table.Rows.Count * shape.Height;
                                float cellWidth = shape.Width / table.Columns.Count;
                                float cellHeight = shape.Height / table.Rows.Count;

                                var normalized = NormalizeText(cellText);
                                snapshot.TextElements.Add(new TextElement
                                {
                                    ElementId      = $"{idPrefix}T{shape.Id}_{snapshot.SlideIndex}_R{row}C{col}",
                                    ShapeName      = $"{shape.Name}:R{row}C{col}",
                                    Left           = cellLeft,
                                    Top            = cellTop,
                                    Width          = cellWidth,
                                    Height         = cellHeight,
                                    BoundingBox255 = CalcBox255(cellLeft, cellTop, cellWidth, cellHeight, sW, sH),
                                    ZOrder         = shape.ZOrderPosition,
                                    RawText        = cellText,
                                    NormalizedText = normalized,
                                    Words          = TokenizeWords(normalized),
                                    ParagraphIndex = 0
                                });
                            }
                            catch (COMException) { /* merged cell or inaccessible */ }
                        }
                    }
                    Log.Debug("Extracted table {ShapeName} on slide {SlideIndex}: {Rows}x{Cols}",
                        shape.Name, snapshot.SlideIndex, table.Rows.Count, table.Columns.Count);
                }
                catch (COMException ex)
                {
                    Log.Warning(ex, "Error reading table shape {ShapeName}", shape.Name);
                }
            }

            // Extract chart as an image element (OCR will read the labels)
            // AND extract chart data (categories, series, title) as text elements
            if (shape.HasChart == Office.MsoTriState.msoTrue)
            {
                var altText = GetAltText(shape);
                var title = GetTitle(shape);
                var nearbyText = FindNearbyText(shape, snapshot);
                var keywords = InferKeywords(altText, title, nearbyText);
                var numericFacts = new List<string>();
                var chartVisualId = $"{idPrefix}C{shape.Id}_{snapshot.SlideIndex}";

                var chartVisual = new ImageElement
                {
                    ElementId = chartVisualId,
                    ShapeName = shape.Name,
                    Left = shape.Left,
                    Top = shape.Top,
                    Width = shape.Width,
                    Height = shape.Height,
                    BoundingBox255 = CalcBox255(shape.Left, shape.Top, shape.Width, shape.Height, sW, sH),
                    ZOrder = shape.ZOrderPosition,
                    AltText = altText,
                    Title = title,
                    NearbyText = nearbyText,
                    InferredKeywords = keywords,
                    ChartNumericFacts = numericFacts,
                    VisualType = "chart",
                    Importance = "high"
                };
                ClassifyVisualImportance(chartVisual, sW, sH);
                snapshot.ImageElements.Add(chartVisual);

                // Extract chart text data (categories, series names, chart title) as TextElements
                try
                {
                    var chart = shape.Chart;
                    var chartTexts = new List<string>();

                    // Chart title
                    try
                    {
                        if (chart.HasTitle && chart.ChartTitle != null)
                        {
                            var ct = chart.ChartTitle.Text?.Trim();
                            if (!string.IsNullOrWhiteSpace(ct)) chartTexts.Add(ct);
                        }
                    }
                    catch (COMException) { }

                    // Category names from the first series (X-axis labels)
                    try
                    {
                        dynamic seriesColl = chart.SeriesCollection();
                        int seriesCount = (int)seriesColl.Count;

                        // Get category names from first series XValues
                        try
                        {
                            dynamic firstSeries = seriesColl.Item(1);
                            object xValsObj = firstSeries.XValues;
                            if (xValsObj is object[] xVals)
                            {
                                foreach (var xv in xVals)
                                {
                                    var s = xv?.ToString()?.Trim();
                                    if (!string.IsNullOrWhiteSpace(s)) chartTexts.Add(s);
                                }
                            }
                        }
                        catch (COMException) { }

                        // Series names
                        for (int si = 1; si <= seriesCount; si++)
                        {
                            try
                            {
                                dynamic series = seriesColl.Item(si);
                                string? sn = (string?)series.Name;
                                if (!string.IsNullOrWhiteSpace(sn)) chartTexts.Add(sn.Trim());

                                // Capture numeric facts directly from chart values (much more reliable than OCR).
                                object valsObj = series.Values;
                                if (valsObj is object[] vals)
                                {
                                    foreach (var v in vals)
                                    {
                                        var numeric = NormalizeNumericFact(v?.ToString());
                                        if (!string.IsNullOrWhiteSpace(numeric))
                                            numericFacts.Add(numeric);
                                    }
                                }
                            }
                            catch (COMException) { }
                        }

                        // Keep unique numeric facts only.
                        if (numericFacts.Count > 0)
                        {
                            numericFacts = numericFacts
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        }
                    }
                    catch (COMException) { }

                    // Add each unique chart text as a TextElement positioned inside the chart
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    int ci = 0;
                    foreach (var ct in chartTexts)
                    {
                        if (!seen.Add(ct)) continue;
                        ci++;
                        var normalized = NormalizeText(ct);
                        snapshot.TextElements.Add(new TextElement
                        {
                            ElementId      = $"{idPrefix}CT{shape.Id}_{snapshot.SlideIndex}_{ci}",
                            ShapeName      = $"{shape.Name}:Label{ci}",
                            Left           = shape.Left,
                            Top            = shape.Top,
                            Width          = shape.Width,
                            Height         = shape.Height,
                            BoundingBox255 = CalcBox255(shape.Left, shape.Top, shape.Width, shape.Height, sW, sH),
                            ZOrder         = shape.ZOrderPosition,
                            RawText        = ct,
                            NormalizedText = normalized,
                            Words          = TokenizeWords(normalized),
                            ParagraphIndex = 0,
                            ParentVisualId = chartVisualId,
                            ParentVisualReason = "chart_label_routes_to_chart_bbox"
                        });
                    }

                    Log.Debug("Extracted chart {ShapeName} on slide {SlideIndex}: {Count} text labels + image element",
                        shape.Name, snapshot.SlideIndex, ci);
                }
                catch (COMException ex)
                {
                    Log.Warning(ex, "Error reading chart data from {ShapeName}", shape.Name);
                }
            }

            // Extract image elements
            if (shape.Type == Office.MsoShapeType.msoPicture ||
                shape.Type == Office.MsoShapeType.msoLinkedPicture ||
                shape.Type == Office.MsoShapeType.msoPlaceholder && IsImagePlaceholder(shape))
            {
                var altText = GetAltText(shape);
                var title = GetTitle(shape);
                var nearbyText = FindNearbyText(shape, snapshot);
                var keywords = InferKeywords(altText, title, nearbyText);

                var imageVisual = new ImageElement
                {
                    ElementId = $"{idPrefix}I{shape.Id}_{snapshot.SlideIndex}",
                    ShapeName = shape.Name,
                    Left = shape.Left,
                    Top = shape.Top,
                    Width = shape.Width,
                    Height = shape.Height,
                    BoundingBox255 = CalcBox255(shape.Left, shape.Top, shape.Width, shape.Height, sW, sH),
                    ZOrder = shape.ZOrderPosition,
                    AltText = altText,
                    Title = title,
                    NearbyText = nearbyText,
                    InferredKeywords = keywords
                };

                ClassifyVisualImportance(imageVisual, sW, sH);
                snapshot.ImageElements.Add(imageVisual);
            }
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Error processing shape {ShapeName}", shape.Name);
        }
    }

    private static bool IsSmartArtShape(Ppt.Shape shape)
    {
        try
        {
            return shape.HasSmartArt == Office.MsoTriState.msoTrue;
        }
        catch
        {
            return false;
        }
    }

    private static void ClassifyVisualImportance(ImageElement image, float slideWidth, float slideHeight)
    {
        var combinedMeta = NormalizeText($"{image.ShapeName} {image.Title} {image.AltText}");
        bool logoLike = combinedMeta.Contains("logo") || combinedMeta.Contains("watermark") || combinedMeta.Contains("brand icon");
        double slideArea = Math.Max(1.0, slideWidth * slideHeight);
        double areaRatio = (image.Width * image.Height) / slideArea;

        if (string.IsNullOrWhiteSpace(image.VisualType))
            image.VisualType = "image";

        if (logoLike)
        {
            image.VisualType = "logo";
            image.IsDecorative = true;
            image.Importance = "low";
            return;
        }

        if (areaRatio < 0.015 && string.IsNullOrWhiteSpace(image.GptDescription) && image.InferredKeywords.Count <= 2)
        {
            image.IsDecorative = true;
            image.Importance = "low";
            image.VisualSubtype ??= "small_decorative";
        }
    }

    private static bool IsImagePlaceholder(Ppt.Shape shape)
    {
        try
        {
            if (shape.PlaceholderFormat == null) return false;
            var phType = shape.PlaceholderFormat.Type;
            
            // If it's a generic placeholder but has text, it's not strictly an image
            if (phType == Ppt.PpPlaceholderType.ppPlaceholderObject)
            {
                if (shape.HasTextFrame == Office.MsoTriState.msoTrue)
                {
                    if (shape.TextFrame.HasText == Office.MsoTriState.msoTrue)
                    {
                        var text = shape.TextFrame.TextRange.Text?.Trim();
                        if (!string.IsNullOrWhiteSpace(text))
                            return false;
                    }
                }
            }

            return phType == Ppt.PpPlaceholderType.ppPlaceholderObject ||
                   phType == Ppt.PpPlaceholderType.ppPlaceholderBitmap;
        }
        catch
        {
            return false;
        }
    }

    private static string GetAltText(Ppt.Shape shape)
    {
        try
        {
            return shape.AlternativeText?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetTitle(Ppt.Shape shape)
    {
        try
        {
            return shape.Title?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string FindNearbyText(Ppt.Shape imageShape, SlideSnapshot snapshot)
    {
        // Collect text from elements that are within 300 points of the image centre
        const float proximityThreshold = 300f;
        var nearby = new List<string>();

        foreach (var te in snapshot.TextElements)
        {
            float dx = Math.Abs((te.Left + te.Width / 2) - (imageShape.Left + imageShape.Width / 2));
            float dy = Math.Abs((te.Top + te.Height / 2) - (imageShape.Top + imageShape.Height / 2));
            float distance = (float)Math.Sqrt(dx * dx + dy * dy);

            if (distance < proximityThreshold)
            {
                nearby.Add(te.RawText);
            }
        }

        // Fallback: if nothing was close enough, include the first (title) text element so
        // charts get at least the slide heading as a keyword source.
        if (nearby.Count == 0 && snapshot.TextElements.Count > 0)
        {
            nearby.Add(snapshot.TextElements[0].RawText);
        }

        return string.Join(" ", nearby);
    }

    private static List<string> InferKeywords(string altText, string title, string nearbyText)
    {
        var combined = $"{altText} {title} {nearbyText}";
        return TokenizeWords(NormalizeText(combined));
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var result = text.ToLowerInvariant();
        // Remove punctuation except hyphens and apostrophes
        result = System.Text.RegularExpressions.Regex.Replace(result, @"[^\w\s\-']", " ");
        // Collapse whitespace
        result = System.Text.RegularExpressions.Regex.Replace(result, @"\s+", " ");
        return result.Trim();
    }

    private static List<string> TokenizeWords(string normalizedText)
    {
        if (string.IsNullOrWhiteSpace(normalizedText))
            return new List<string>();

        return normalizedText.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string NormalizeNumericFact(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var trimmed = input.Trim();
        bool hasPercent = trimmed.Contains('%');
        var cleaned = Regex.Replace(trimmed, @"[^0-9\.,\-]", string.Empty);

        if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;

        cleaned = cleaned.Replace(",", string.Empty);
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return string.Empty;

        var normalized = value.ToString("0.####", CultureInfo.InvariantCulture);
        return hasPercent ? normalized + "%" : normalized;
    }

    private static void WriteImageDebugArtifact(int slideIndex, ImageElement image)
    {
        try
        {
            var dir = Path.Combine(Environment.CurrentDirectory, "logs");
            System.IO.Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "slide_image_enrichment.ndjson");

            var entry = new
            {
                ts_utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                slide_index = slideIndex,
                element_id = image.ElementId,
                shape_name = image.ShapeName,
                ocr_word_count = image.ExtractedWords?.Count ?? 0,
                ocr_words_preview = (image.ExtractedWords ?? new List<OcrWordInfo>())
                    .Take(10)
                    .Select(w => new
                    {
                        text = w.Text,
                        x = Math.Round(w.X, 4),
                        y = Math.Round(w.Y, 4),
                        w = Math.Round(w.Width, 4),
                        h = Math.Round(w.Height, 4)
                    })
                    .ToList(),
                image_explanation_preview = string.IsNullOrWhiteSpace(image.GptDescription)
                    ? string.Empty
                    : image.GptDescription.Substring(0, Math.Min(300, image.GptDescription.Length))
            };

            var line = JsonSerializer.Serialize(entry);
            lock (DebugArtifactLock)
            {
                File.AppendAllText(path, line + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Failed writing image enrichment debug artifact for {ElementId}", image.ElementId);
        }
    }
}
