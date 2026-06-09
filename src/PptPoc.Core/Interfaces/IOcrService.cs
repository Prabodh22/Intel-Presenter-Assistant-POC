using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

/// <summary>
/// Extracts text from images using platform OCR.
/// </summary>
public interface IOcrService : IDisposable
{
    /// <summary>Initialize the OCR engine (idempotent).</summary>
    Task InitializeAsync();

    /// <summary>Extract text from a PNG/JPG image supplied as raw bytes.</summary>
    Task<List<OcrWordInfo>> ExtractTextAsync(byte[] imageData);

    /// <summary>Extract text from an image file on disk.</summary>
    Task<List<OcrWordInfo>> ExtractTextAsync(string imagePath);
}
