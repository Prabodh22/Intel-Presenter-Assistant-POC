using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;

namespace PptPoc.Matching;

public static class ImageReferenceMatcher
{
    private static readonly string[] SpatialPhrases =
    {
        "this image", "this picture", "this photo", "this diagram",
        "this chart", "this graph", "this figure", "this illustration",
        "the image", "the picture", "the photo", "the diagram",
        "the chart", "the graph", "the figure", "the illustration",
        "as you can see", "shown here", "look at this",
        "this slide shows", "here we see", "on the right",
        "on the left", "at the top", "at the bottom",
        "this table", "the table"
    };

    private static readonly Dictionary<string, int> OrdinalMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "first", 0 }, { "second", 1 }, { "third", 2 }, { "fourth", 3 },
        { "1st", 0 }, { "2nd", 1 }, { "3rd", 2 }, { "4th", 3 }
    };

    /// <summary>
    /// Scores how well the transcript text references a specific image element.
    /// Returns the matched score, matched phrase, and optionally a specific OCR word 
    /// if the match zeroed in on a sub-label inside the graph.
    /// </summary>
    public static (double Score, string MatchedPhrase, OcrWordInfo? TargetWord) Score(
        string transcriptText, float[]? transcriptEmbedding, ImageElement image, int imagePositionIndex, IReadOnlyList<ImageElement> allImages, ISemanticEmbeddingService semanticService)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return (0.0, string.Empty, null);

        var tNorm = TextNormalizer.Normalize(transcriptText);
        double bestScore = 0.0;
        string bestPhrase = string.Empty;
        OcrWordInfo? bestWord = null;

        // 1a. Semantic matching against the GPT-4o conceptual description
        if (transcriptEmbedding != null && semanticService.IsReady && image.SemanticEmbedding != null)
        {
            double semanticOcr = semanticService.ComputeCosineSimilarity(transcriptEmbedding, image.SemanticEmbedding);
            if (semanticOcr > bestScore)
            {
                bestScore = semanticOcr;
                bestPhrase = transcriptText;
                bestWord = null; // Whole image
            }
        }

        // 1b. Exact/Fuzzy matching against specific OCR words within the image to get bounding box!
        // Skip very short OCR words (single chars, numbers) that cause false positives.
        if (image.ExtractedWords != null && image.ExtractedWords.Count > 0)
        {
            int ocrHitCount = 0;
            foreach (var word in image.ExtractedWords)
            {
                if (word.Text.Length < 3) continue; // Skip noise: "1", "a", "%", etc.

                var (wordScore, wordPhrase) = FuzzyMatcher.Score(transcriptText, word.Text);
                if (wordScore > 0.7)
                {
                    ocrHitCount++;
                    // Single OCR word matches are capped at 0.45 to avoid false positives.
                    // Multiple hits or longer phrases score higher.
                    double adjustedScore = ocrHitCount >= 2 ? wordScore * 1.1 : Math.Min(wordScore, 0.45);
                    if (adjustedScore > 1.0) adjustedScore = 1.0;
                    if (adjustedScore > bestScore)
                    {
                        bestScore = adjustedScore;
                        bestPhrase = wordPhrase;
                        bestWord = word;
                    }
                }
            }
        }

        // 1c. Match against image alt text, title, nearby text, and keywords
        var candidateTexts = new List<string>();
        if (!string.IsNullOrWhiteSpace(image.AltText)) candidateTexts.Add(image.AltText);
        if (!string.IsNullOrWhiteSpace(image.Title)) candidateTexts.Add(image.Title);
        if (!string.IsNullOrWhiteSpace(image.NearbyText)) candidateTexts.Add(image.NearbyText);
        if (image.InferredKeywords.Count > 0) candidateTexts.Add(string.Join(" ", image.InferredKeywords));

        foreach (var candidate in candidateTexts)
        {
            var (fuzzyScore, phrase) = FuzzyMatcher.Score(transcriptText, candidate);
            double semanticCandidate = 0;
            
            if (transcriptEmbedding != null && semanticService.IsReady)
            {
                var candidateEmbedding = semanticService.GenerateEmbedding(candidate);
                semanticCandidate = semanticService.ComputeCosineSimilarity(transcriptEmbedding, candidateEmbedding);
            }

            // For short image metadata (few words), require fuzzy evidence — 
            // semantic similarity alone is unreliable for short strings.
            var candidateWordCount = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            double highestCandidateMatch;
            if (candidateWordCount <= 3 && fuzzyScore < 0.01)
                highestCandidateMatch = 0.0; // Suppress semantic-only for short metadata
            else
                highestCandidateMatch = Math.Max(fuzzyScore, semanticCandidate);
            
            if (highestCandidateMatch > bestScore)
            {
                bestScore = highestCandidateMatch;
                bestPhrase = semanticCandidate > fuzzyScore ? transcriptText : phrase;
                bestWord = null; // These apply to the whole image
            }
        }

        // 2. Check for spatial reference phrases
        double spatialBoost = 0.0;
        
        bool isLeftmost = !allImages.Any(o => o.Left < image.Left);
        bool isRightmost = !allImages.Any(o => (o.Left + o.Width) > (image.Left + image.Width));
        bool isTopmost = !allImages.Any(o => o.Top < image.Top);
        bool isBottommost = !allImages.Any(o => (o.Top + o.Height) > (image.Top + image.Height));

        foreach (var sp in SpatialPhrases)
        {
            if (tNorm.Contains(sp))
            {
                bool isDirectional = false;
                bool directionMatched = false;

                if (sp.Contains("left")) { isDirectional = true; directionMatched = isLeftmost; }
                else if (sp.Contains("right")) { isDirectional = true; directionMatched = isRightmost; }
                else if (sp.Contains("top")) { isDirectional = true; directionMatched = isTopmost; }
                else if (sp.Contains("bottom")) { isDirectional = true; directionMatched = isBottommost; }

                if (isDirectional)
                {
                    if (directionMatched)
                    {
                        spatialBoost = 0.9;
                        if (string.IsNullOrEmpty(bestPhrase)) bestPhrase = sp;
                    }
                    else
                    {
                        spatialBoost = -0.3;
                    }
                }
                else
                {
                    // Generic spatial phrases alone should NOT be enough to trigger a highlight.
                    // Only boost if there's already some content match.
                    spatialBoost = bestScore > 0.2 ? 0.3 : 0.1;
                    if (string.IsNullOrEmpty(bestPhrase)) bestPhrase = sp;
                }
                break;
            }
        }

        if (allImages.Count > 1)
        {
            foreach (var (ordinalWord, ordinalIndex) in OrdinalMap)
            {
                if (tNorm.Contains(ordinalWord) && ordinalIndex == imagePositionIndex)
                {
                    bestScore = Math.Max(bestScore, 0.8);
                    spatialBoost = Math.Max(spatialBoost, 0.8);
                    bestPhrase = string.IsNullOrEmpty(bestPhrase)
                        ? $"{ordinalWord} image"
                        : $"{ordinalWord} {bestPhrase}";
                    break;
                }
            }
        }

        // 4. Fallback if single image — only if there's already a meaningful content match
        if (allImages.Count == 1 && spatialBoost > 0 && bestScore > 0.3 && bestScore < 0.7)
        {
            bestScore = 0.7;
        }

        double finalScore = Math.Min(1.0, bestScore + spatialBoost);
        return (finalScore, bestPhrase, bestWord);
    }
}