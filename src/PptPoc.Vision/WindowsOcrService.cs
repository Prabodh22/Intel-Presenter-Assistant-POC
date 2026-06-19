using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace PptPoc.Vision;

/// <summary>
/// OCR service backed by the built-in Windows.Media.Ocr engine.
/// Requires Windows 10 v1803+ (Build 17134).
/// </summary>
public class WindowsOcrService : IOcrService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<WindowsOcrService>();

    private OcrEngine? _engine;
    private bool _disposed;

    // ── Enhancement #6: Minimum pixel width before upscaling kicks in ────────
    // Chart images exported from PowerPoint shapes are often small (200-400px).
    // The Windows OCR engine performs poorly on tiny text at that resolution.
    // Upscaling to at least 800px wide significantly improves word extraction
    // from axis labels, legends, and data values in chart images.
    private const uint MinWidthForOcr = 800;
    private const uint MaxUpscaleFactor = 3;

    public async Task InitializeAsync()
    {
        if (_engine != null) return;

        await Task.Run(() =>
        {
            // Prefer US English; fall back to first available language
            var lang = new Language("en-US");
            _engine = OcrEngine.IsLanguageSupported(lang)
                ? OcrEngine.TryCreateFromLanguage(lang)
                : OcrEngine.TryCreateFromUserProfileLanguages();

            if (_engine == null)
            {
                Log.Warning("Windows OCR engine could not be initialized. " +
                            "Ensure an OCR language pack is installed (Settings → Time & language → Language).");
            }
            else
            {
                Log.Information("Windows OCR engine initialized with language: {Lang}",
                    _engine.RecognizerLanguage.DisplayName);
            }
        });
    }

    /// <summary>
    /// Enhancement #6: Extracts text from an image, upscaling small images for better OCR.
    /// When image width is below <see cref="MinWidthForOcr"/> pixels, the image is scaled up
    /// (up to 3x) using high-quality Fant interpolation before OCR. This dramatically improves
    /// recognition of small chart labels, axis values, and legend text.
    /// </summary>
    public async Task<List<OcrWordInfo>> ExtractTextAsync(byte[] imageData)
    {
        if (_engine == null || imageData.Length == 0)
            return new List<OcrWordInfo>();

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(imageData.AsBuffer());
            stream.Seek(0);

            var decoder = await BitmapDecoder.CreateAsync(stream);

            uint targetWidth = decoder.PixelWidth;
            uint targetHeight = decoder.PixelHeight;
            var transform = new BitmapTransform();

            // ── Upscale small images for better OCR on chart labels ──────────
            if (decoder.PixelWidth < MinWidthForOcr && decoder.PixelWidth > 0)
            {
                uint scale = Math.Min(MaxUpscaleFactor, MinWidthForOcr / Math.Max(1, decoder.PixelWidth) + 1);
                targetWidth = decoder.PixelWidth * scale;
                targetHeight = decoder.PixelHeight * scale;
                transform.ScaledWidth = targetWidth;
                transform.ScaledHeight = targetHeight;
                transform.InterpolationMode = BitmapInterpolationMode.Fant;

                Log.Debug("OCR upscaling image from {OrigW}x{OrigH} to {NewW}x{NewH} ({Scale}x)",
                    decoder.PixelWidth, decoder.PixelHeight, targetWidth, targetHeight, scale);
            }

            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied,
                transform,
                ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);

            var bitmap = SoftwareBitmap.CreateCopyFromBuffer(
                pixelData.DetachPixelData().AsBuffer(),
                BitmapPixelFormat.Bgra8,
                (int)targetWidth,
                (int)targetHeight,
                BitmapAlphaMode.Premultiplied);

            return await RecognizeAsync(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR failed on in-memory image ({Bytes} bytes)", imageData.Length);
            return new List<OcrWordInfo>();
        }
    }

    public async Task<List<OcrWordInfo>> ExtractTextAsync(string imagePath)
    {
        if (_engine == null || !File.Exists(imagePath))
            return new List<OcrWordInfo>();

        try
        {
            var data = await File.ReadAllBytesAsync(imagePath);
            return await ExtractTextAsync(data);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR failed on image file {Path}", imagePath);
            return new List<OcrWordInfo>();
        }
    }

    private async Task<List<OcrWordInfo>> RecognizeAsync(SoftwareBitmap bitmap)
    {
        var result = await _engine!.RecognizeAsync(bitmap);
        if (result.Lines.Count == 0) return new List<OcrWordInfo>();

        double w = bitmap.PixelWidth;
        double h = bitmap.PixelHeight;

        var words = new List<OcrWordInfo>();
        foreach (var line in result.Lines)
        {
            foreach (var word in line.Words)
            {
                words.Add(new OcrWordInfo
                {
                    Text = word.Text,
                    // Store as relative percentages [0.0 - 1.0] for easy projection onto PPT points
                    X = word.BoundingRect.X / w,
                    Y = word.BoundingRect.Y / h,
                    Width = word.BoundingRect.Width / w,
                    Height = word.BoundingRect.Height / h
                });
            }
        }
        return words;
    }

    public void Dispose()
    {
        _disposed = true;
        _engine = null;
    }
}
