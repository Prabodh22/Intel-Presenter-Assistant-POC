using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using PptPoc.Core.Interfaces;
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

    public async Task<string> ExtractTextAsync(byte[] imageData)
    {
        if (_engine == null || imageData.Length == 0)
            return string.Empty;

        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(imageData.AsBuffer());
            stream.Seek(0);

            var decoder  = await BitmapDecoder.CreateAsync(stream);
            var bitmap   = await decoder.GetSoftwareBitmapAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            return await RecognizeAsync(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR failed on in-memory image ({Bytes} bytes)", imageData.Length);
            return string.Empty;
        }
    }

    public async Task<string> ExtractTextAsync(string imagePath)
    {
        if (_engine == null || !File.Exists(imagePath))
            return string.Empty;

        try
        {
            var data = await File.ReadAllBytesAsync(imagePath);
            return await ExtractTextAsync(data);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "OCR failed on image file {Path}", imagePath);
            return string.Empty;
        }
    }

    private async Task<string> RecognizeAsync(SoftwareBitmap bitmap)
    {
        var result = await _engine!.RecognizeAsync(bitmap);
        if (result.Lines.Count == 0) return string.Empty;

        return string.Join(" ", result.Lines.Select(l => l.Text));
    }

    public void Dispose()
    {
        _disposed = true;
        _engine = null;
    }
}
