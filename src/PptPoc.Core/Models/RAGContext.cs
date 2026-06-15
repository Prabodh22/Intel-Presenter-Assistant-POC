using PptPoc.Core.Models;

namespace PptPoc.Core.Models;

/// <summary>
/// Context retrieved from the knowledge base for RAG (Retrieval Augmented Generation).
/// Contains similar elements from across all slides to augment matching confidence.
/// </summary>
public class RAGContext
{
    /// <summary>Text elements retrieved from KB, ranked by similarity.</summary>
    public List<TextElementWithScore> RetrievedTexts { get; set; } = new();

    /// <summary>Image elements retrieved from KB, ranked by similarity.</summary>
    public List<ImageElementWithScore> RetrievedImages { get; set; } = new();

    /// <summary>Keywords extracted from retrieved elements for vocabulary hints.</summary>
    public List<string> ContextKeywords { get; set; } = new();

    /// <summary>Confidence boost (0.0 to 0.30) based on retrieval quality and recurrence.</summary>
    public double ContextConfidenceBoost { get; set; } = 0.0;

    /// <summary>Count of high-confidence matches across retrieved elements.</summary>
    public int RecurrenceCount { get; set; } = 0;

    public bool HasContext => RetrievedTexts.Count > 0 || RetrievedImages.Count > 0;
}

/// <summary>Text element from KB with semantic similarity score.</summary>
public class TextElementWithScore
{
    public string ElementId { get; set; } = "";
    public string Text { get; set; } = "";
    public int SlideIndex { get; set; }
    public double SimilarityScore { get; set; } // 0.0 to 1.0
    public double HybridRankScore { get; set; } // Default 0.0
    public float[]? Embedding { get; set; }
}

/// <summary>Image element from KB with semantic similarity score.</summary>
public class ImageElementWithScore
{
    public string ElementId { get; set; } = "";
    public string Description { get; set; } = "";
    public int SlideIndex { get; set; }
    public double SimilarityScore { get; set; } // 0.0 to 1.0
    public float[]? Embedding { get; set; }
}
