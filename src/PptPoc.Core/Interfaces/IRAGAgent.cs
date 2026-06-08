using PptPoc.Core.Models;

namespace PptPoc.Core.Interfaces;

/// <summary>
/// RAG (Retrieval Augmented Generation) Agent that retrieves relevant context from the knowledge base.
/// Uses semantic embeddings to find similar elements across all slides and provides contextual augmentation.
/// </summary>
public interface IRAGAgent
{
    /// <summary>
    /// Check if the RAG agent is initialized and ready to retrieve.
    /// </summary>
    bool IsReady { get; }

    /// <summary>
    /// Initialize the RAG agent with a loaded knowledge base and current slide snapshot.
    /// Must be called before Retrieve operations.
    /// </summary>
    /// <param name="kbLoader">Loaded knowledge base loader (KnowledgeBaseLoader from Orchestration namespace)</param>
    /// <param name="currentSlideSnapshot">Current active slide</param>
    /// <param name="semanticService">Service for computing embeddings</param>
    void Initialize(object kbLoader, SlideSnapshot currentSlideSnapshot, ISemanticEmbeddingService semanticService);

    /// <summary>
    /// Retrieve contextually similar elements from the knowledge base based on transcript.
    /// Performs semantic search across all slides and returns top-K similar elements.
    /// </summary>
    /// <param name="transcriptText">Speaker transcript to search for</param>
    /// <param name="topK">Maximum number of results to retrieve per type</param>
    /// <returns>RAG context with retrieved elements and confidence adjustments</returns>
    Task<RAGContext> RetrieveContextAsync(string transcriptText, int topK = 5);

    /// <summary>
    /// Get context keyword hints for vocabulary enhancement.
    /// Extracted from retrieved elements to improve ASR vocabulary matching.
    /// </summary>
    /// <param name="maxCount">Maximum keywords to return</param>
    /// <returns>List of context-relevant keywords</returns>
    List<string> GetContextKeywords(int maxCount = 25);

    /// <summary>
    /// Augment a match result with RAG context confidence boost.
    /// Increases confidence if element found in retrieved KB context.
    /// </summary>
    /// <param name="matchResult">Current match result</param>
    /// <param name="context">Retrieved RAG context</param>
    /// <returns>Updated match result with boosted confidence</returns>
    MatchResult AugmentMatchConfidence(MatchResult matchResult, RAGContext context);

    /// <summary>
    /// Clear cached context (called on slide change).
    /// </summary>
    void ClearContext();

    /// <summary>
    /// Return the most recently retrieved context, if any.
    /// </summary>
    RAGContext? GetCachedContext();
}
