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
    /// </summary>
    public static (double Score, string MatchedPhrase) Score(
        string transcriptText, float[]? transcriptEmbedding, ImageElement image, int imagePositionIndex, IReadOnlyList<ImageElement> allImages, ISemanticEmbeddingService semanticService)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return (0.0, string.Empty);

        var tNorm = TextNormalizer.Normalize(transcriptText);
        double bestScore = 0.0;
        string bestPhrase = string.Empty;

        // 1a. OCR text — highest priority source (1.2× boost) using Phase 2 semantics
        if (!string.IsNullOrWhiteSpace(image.ExtractedOcrText))
        {
            var (ocrScore, ocrPhrase) = FuzzyMatcher.Score(transcriptText, image.ExtractedOcrText);
            
            double semanticOcr = 0;
            if (transcriptEmbedding != null && semanticService.IsReady)
            {
                if (image.SemanticEmbedding == null)
                {
                    // Prioritize GPT-4o's rich context over raw OCR
                    string embedSource = string.IsNullOrWhiteSpace(image.GptDescription) 
                        ? image.ExtractedOcrText 
                        : image.GptDescription;

                    image.SemanticEmbedding = semanticService.GenerateEmbedding(embedSource);
                }

                semanticOcr = semanticService.ComputeCosineSimilarity(transcriptEmbedding, image.SemanticEmbedding);
            }

            double highestOcrMatch = Math.Max(ocrScore, semanticOcr);
            double boosted = Math.Min(1.0, highestOcrMatch * 1.2);
            
            if (boosted > bestScore)
            {
                bestScore = boosted;
                bestPhrase = semanticOcr > ocrScore ? transcriptText : ocrPhrase;
            }
        }

        // 1b. Match against image alt text, title, nearby text, and keywords using Semantic Embeddings
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

            double highestCandidateMatch = Math.Max(fuzzyScore, semanticCandidate);
            
            if (highestCandidateMatch > bestScore)
            {
                bestScore = highestCandidateMatch;
                bestPhrase = semanticCandidate > fuzzyScore ? transcriptText : phrase;
            }
        }

        // 2. Check for spatial reference phrases and perform coordinate bounding box math
        double spatialBoost = 0.0;
        
        bool isLeftmost = !allImages.Any(o => o.Left < image.Left);
        bool isRightmost = !allImages.Any(o => (o.Left + o.Width) > (image.Left + image.Width));
        bool isTopmost = !allImages.Any(o => o.Top < image.Top);
        bool isBottommost = !allImages.Any(o => (o.Top + o.Height) > (image.Top + image.Height));

        foreach (var sp in SpatialPhrases)
        {
            if (tNorm.Contains(sp))
            {
                // Determine if it was a directional phrase
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
                        spatialBoost = 0.9; // Massive boost for mathematical hit
                        if (string.IsNullOrEmpty(bestPhrase)) bestPhrase = sp;
                    }
                    else
                    {
                        spatialBoost = -0.3; // Harsh penalty, this is objectively the wrong side!
                    }
                }
                else
                {
                    // Generic phrase like "this image", "on the screen"
                    spatialBoost = 0.5; // Moderate boost to be validated by OCR/semantics
                    if (string.IsNullOrEmpty(bestPhrase)) bestPhrase = sp;
                }
                break; // exit if spatial processed
            }
        }

        // 3. Ordinal matching: "the first image", "the second chart"
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

        // If there's only one image on the slide and a spatial reference is detected,
        if (allImages.Count == 1 && spatialBoost > 0 && bestScore < 0.8)
        {
            bestScore = 0.8;
        }

        double finalScore = Math.Min(1.0, bestScore + spatialBoost);
        return (finalScore, bestPhrase);
    }
}
