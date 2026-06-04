using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

public interface ISlideReader
{
    SlideSnapshot ReadSlide(object slideComObject);

    /// <summary>
    /// Reads a slide and awaits all async enrichment (OCR, GPT-4o vision).
    /// Use this for preprocessing / knowledge base generation.
    /// </summary>
    Task<SlideSnapshot> ReadSlideFullAsync(object slideComObject);

    /// <summary>Phase 1: Extract shapes from COM (synchronous, STA thread).</summary>
    SlideSnapshot ExtractShapesSync(object slideComObject);

    /// <summary>Phase 2: Export image bytes from COM (synchronous, STA thread). Returns (ImageElement, shapeId, bytes, slideImageBytes).</summary>
    (List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) ExportImageBytes(SlideSnapshot snapshot, object slideComObject);

    /// <summary>Phase 3: Run API enrichment (OCR, explain, vision) — thread-safe, no COM needed.</summary>
    Task RunApiEnrichmentAsync(SlideSnapshot snapshot, (List<(ImageElement img, int shapeId, byte[] bytes)> images, byte[]? slideImage, string manifest) exports, object slideComObject);
}
