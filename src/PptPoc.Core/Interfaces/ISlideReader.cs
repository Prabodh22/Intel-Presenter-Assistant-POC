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
}
