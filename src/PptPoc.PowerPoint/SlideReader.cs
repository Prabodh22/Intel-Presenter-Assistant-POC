using System.Runtime.InteropServices;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using Ppt = Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;

namespace PptPoc.PowerPoint;

public class SlideReader : ISlideReader
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SlideReader>();

    private readonly IOcrService? _ocr;
    private readonly Dictionary<int, string> _ocrCache = new();

    public SlideReader(IOcrService? ocr = null)
    {
        _ocr = ocr;
    }

    public SlideSnapshot ReadSlide(object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;
        var snapshot = new SlideSnapshot
        {
            SlideIndex = slide.SlideIndex,
            SlideId = slide.SlideID.ToString()
        };

        try
        {
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                ProcessShape(shape, snapshot, null, slide);
            }
        }
        catch (COMException ex)
        {
            Log.Error(ex, "Error reading shapes from slide {SlideIndex}", slide.SlideIndex);
        }

        // Run OCR on all image elements asynchronously
        if (_ocr != null && snapshot.ImageElements.Count > 0)
        {
            _ = RunOcrOnImagesAsync(snapshot.ImageElements, slide);
        }

        Log.Debug("Read slide {SlideIndex}: {TextCount} text elements, {ImageCount} image elements",
            snapshot.SlideIndex, snapshot.TextElements.Count, snapshot.ImageElements.Count);

        return snapshot;
    }

    private async Task RunOcrOnImagesAsync(List<ImageElement> images, Ppt.Slide slide)
    {
        foreach (var img in images)
        {
            try
            {
                var shapeId = ExtractShapeId(img.ElementId);
                if (shapeId <= 0) continue;

                if (_ocrCache.TryGetValue(shapeId, out var cachedText))
                {
                    img.ExtractedOcrText = cachedText;
                    foreach (var word in TokenizeWords(NormalizeText(cachedText)))
                    {
                        if (!img.InferredKeywords.Contains(word))
                            img.InferredKeywords.Add(word);
                    }
                    continue;
                }

                // Run COM export on task pool to avoid blocking the slide-read thread heavily
                byte[]? imageBytes = await Task.Run(() => ExportShapeAsImage(slide, shapeId));
                if (imageBytes == null || imageBytes.Length == 0) continue;

                var ocrText = await _ocr!.ExtractTextAsync(imageBytes);
                if (!string.IsNullOrWhiteSpace(ocrText))
                {
                    _ocrCache[shapeId] = ocrText;
                    img.ExtractedOcrText = ocrText;

                    var ocrWords = TokenizeWords(NormalizeText(ocrText));
                    foreach (var word in ocrWords)
                    {
                        if (!img.InferredKeywords.Contains(word))
                            img.InferredKeywords.Add(word);
                    }

                    Log.Debug("OCR on shape {Id}: \"{Text}\"", shapeId,
                        ocrText.Length > 80 ? ocrText[..80] + "…" : ocrText);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "OCR failed for image element {Id}", img.ElementId);
            }
        }
    }

    private static int ExtractShapeId(string elementId)
    {
        // Element ID format: "{prefix}I{shapeId}_{slideIndex}"
        var iIdx = elementId.LastIndexOf('I');
        if (iIdx < 0) return 0;
        var afterI = elementId[(iIdx + 1)..];
        var underIdx = afterI.IndexOf('_');
        var idStr = underIdx >= 0 ? afterI[..underIdx] : afterI;
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

    private void ProcessShape(Ppt.Shape shape, SlideSnapshot snapshot, string? groupPrefix, Ppt.Slide slide)
    {
        string idPrefix = groupPrefix ?? "";

        try
        {
            // Handle grouped shapes recursively
            if (shape.Type == Office.MsoShapeType.msoGroup)
            {
                foreach (Ppt.Shape child in shape.GroupItems)
                {
                    ProcessShape(child, snapshot, $"{idPrefix}G{shape.Id}_", slide);
                }
                return;
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
                        ZOrder         = shape.ZOrderPosition,
                        RawText        = text,
                        NormalizedText = normalized,
                        Words          = TokenizeWords(normalized),
                        ParagraphIndex = pi
                    });
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

                snapshot.ImageElements.Add(new ImageElement
                {
                    ElementId = $"{idPrefix}I{shape.Id}_{snapshot.SlideIndex}",
                    ShapeName = shape.Name,
                    Left = shape.Left,
                    Top = shape.Top,
                    Width = shape.Width,
                    Height = shape.Height,
                    ZOrder = shape.ZOrderPosition,
                    AltText = altText,
                    Title = title,
                    NearbyText = nearbyText,
                    InferredKeywords = keywords
                });
            }
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Error processing shape {ShapeName}", shape.Name);
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
}
