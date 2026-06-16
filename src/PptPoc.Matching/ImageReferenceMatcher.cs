using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;

namespace PptPoc.Matching;

public static class ImageReferenceMatcher
{
    private static readonly System.Text.RegularExpressions.Regex NumericTokenRegex =
        new(@"^\d+(?:\.\d+)?%?$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly string[] SpatialPhrases =
    {
        "this image", "this picture", "this photo", "this diagram",
        "this chart", "this graph", "this figure", "this illustration",
        "the image", "the picture", "the photo", "the diagram",
        "the chart", "the graph", "the figure", "the illustration",
        "as you can see", "shown here", "look at this",
        "this slide shows", "here we see", "on the right",
        "on the left", "at the top", "at the bottom",
        "this table", "the table",
        "below image", "image below", "the below image", "in the below",
        "figure below", "below figure", "the below figure",
        "chart below", "below chart", "the below chart",
        "diagram below", "below diagram",
        "image above", "above image", "figure above", "above figure",
        "image on the right", "image on the left",
        "chart on the right", "chart on the left",
        "see here", "see below", "shown below", "shown above",
        "depicted here", "illustrated here"
    };

    private static readonly Dictionary<string, int> OrdinalMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "first", 0 }, { "second", 1 }, { "third", 2 }, { "fourth", 3 },
        { "1st", 0 }, { "2nd", 1 }, { "3rd", 2 }, { "4th", 3 }
    };

    /// <summary>
    /// Scores how well the transcript text references a specific image element.
    /// Returns the matched score, matched phrase, and the list of all OCR words
    /// whose text fired during matching (for word-level bbox highlighting).
    /// MatchedWords is null when no OCR evidence contributed.
    /// </summary>
    public static (double Score, string MatchedPhrase, List<OcrWordInfo>? MatchedWords) Score(
        string transcriptText,
        float[]? transcriptEmbedding,
        ImageElement image,
        int imagePositionIndex,
        IReadOnlyList<ImageElement> allImages,
        ISemanticEmbeddingService semanticService)
    {
        if (string.IsNullOrWhiteSpace(transcriptText))
            return (0.0, string.Empty, null);

        var tNorm = TextNormalizer.Normalize(transcriptText);
        double bestScore = 0.0;
        string bestPhrase = string.Empty;

        // All OCR words with fuzzy score > 0.7 — collected regardless of whether they
        // end up being the final winning signal so the renderer can highlight them all.
        var matchedWords = new List<OcrWordInfo>();

        // ── 1a. Semantic matching against the GPT-4o conceptual description ────────
        // Cap at 0.35 — semantic alone is too noisy for images.
        if (transcriptEmbedding != null && semanticService.IsReady && image.SemanticEmbedding != null)
        {
            double semanticOcr = semanticService.ComputeCosineSimilarity(transcriptEmbedding, image.SemanticEmbedding);
            double cappedSemantic = Math.Min(semanticOcr, 0.35);
            if (cappedSemantic > bestScore)
            {
                bestScore = cappedSemantic;
                bestPhrase = transcriptText;
            }
        }

        // ── 1b. Exact/Fuzzy matching against specific OCR words ──────────────────
        // Collect EVERY word with score > 0.7 so they can all be highlighted.
        if (image.ExtractedWords != null && image.ExtractedWords.Count > 0)
        {
            int ocrHitCount = 0;
            double bestOcrScore = 0;
            string bestOcrPhrase = string.Empty;

            foreach (var word in image.ExtractedWords)
            {
                // Guard: OCR pipeline may produce null entries or null Text
                if (word == null || word.Text == null) continue;

                bool isNumericToken = NumericTokenRegex.IsMatch(word.Text);
                if (word.Text.Length < 3 && !isNumericToken) continue;

                var (wordScore, wordPhrase) = FuzzyMatcher.Score(transcriptText, word.Text);
                if (wordScore > 0.7)
                {
                    matchedWords.Add(word); // ← record every hit for bbox merging
                    ocrHitCount++;

                    double adjustedScore;
                    if (ocrHitCount >= 4)
                        adjustedScore = Math.Min(wordScore * 1.1, 1.0);
                    else if (ocrHitCount == 3)
                        adjustedScore = Math.Min(wordScore, 0.70);
                    else if (ocrHitCount == 2)
                        adjustedScore = Math.Min(wordScore, 0.60);
                    else
                        adjustedScore = word.Text.Length >= 5
                            ? Math.Min(wordScore, 0.45)
                            : Math.Min(wordScore, 0.30);

                    if (adjustedScore > bestOcrScore)
                    {
                        bestOcrScore = adjustedScore;
                        bestOcrPhrase = wordPhrase;
                    }
                }
            }

            if (bestOcrScore > bestScore)
            {
                bestScore = bestOcrScore;
                bestPhrase = bestOcrPhrase;
            }

            // Density gate: scale down when matched words are a small fraction of a long transcript.
            if (ocrHitCount >= 2 && bestScore > 0.3)
            {
                var tTokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(transcriptText));
                double density = (double)ocrHitCount / Math.Max(1, tTokens.Count);
                if (density < 0.20 && tTokens.Count >= 8)
                {
                    bestScore *= (0.5 + density * 2.5);
                }
            }
        }

        // ── 1c. Match against alt text, title, nearby text, and keywords ────────
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

            var candidateWordCount = candidate.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
            double highestCandidateMatch;
            if (candidateWordCount <= 3 && fuzzyScore < 0.01)
                highestCandidateMatch = 0.0;
            else
            {
                double cappedSemanticCandidate = fuzzyScore > 0.01 ? semanticCandidate : Math.Min(semanticCandidate, 0.35);
                highestCandidateMatch = Math.Max(fuzzyScore, cappedSemanticCandidate);
            }

            if (highestCandidateMatch > bestScore)
            {
                double keywordPenalty = 1.0;
                if (fuzzyScore > 0.3 && candidateWordCount >= 3)
                {
                    var tTokens2 = TextNormalizer.Tokenize(tNorm);
                    if (tTokens2.Count >= 8)
                    {
                        var candTokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(candidate));
                        int hits = candTokens.Count(cw => cw.Length >= 3 && tTokens2.Any(tw =>
                            string.Equals(tw, cw, StringComparison.OrdinalIgnoreCase) ||
                            (tw.Length >= 4 && cw.Length >= 4 &&
                             (tw.StartsWith(cw, StringComparison.OrdinalIgnoreCase) ||
                              cw.StartsWith(tw, StringComparison.OrdinalIgnoreCase)))));
                        double density = (double)hits / tTokens2.Count;
                        if (density < 0.20)
                            keywordPenalty = 0.5 + density * 2.5;
                    }
                }

                bestScore = highestCandidateMatch * keywordPenalty;
                bestPhrase = semanticCandidate > fuzzyScore ? transcriptText : phrase;
                // Metadata match — OCR words still kept in matchedWords if they fired
            }
        }

        // ── 2. Spatial reference phrases ────────────────────────────────────────
        double spatialBoost = 0.0;

        bool isLeftmost   = !allImages.Any(o => o.Left < image.Left);
        bool isRightmost  = !allImages.Any(o => (o.Left + o.Width) > (image.Left + image.Width));
        bool isTopmost    = !allImages.Any(o => o.Top < image.Top);
        bool isBottommost = !allImages.Any(o => (o.Top + o.Height) > (image.Top + image.Height));

        foreach (var sp in SpatialPhrases)
        {
            if (tNorm.Contains(sp))
            {
                bool isDirectional = false;
                bool directionMatched = false;

                if (sp.Contains("left"))        { isDirectional = true; directionMatched = isLeftmost; }
                else if (sp.Contains("right"))  { isDirectional = true; directionMatched = isRightmost; }
                else if (sp.Contains("top") || sp.Contains("above"))   { isDirectional = true; directionMatched = isTopmost; }
                else if (sp.Contains("bottom") || sp.Contains("below")) { isDirectional = true; directionMatched = isBottommost; }

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
                    spatialBoost = bestScore > 0.2 ? 0.3 : 0.1;
                    if (string.IsNullOrEmpty(bestPhrase)) bestPhrase = sp;
                }
                break;
            }
        }

        // ── 3. Ordinal matching ──────────────────────────────────────────────────
        string[] imageNouns = { "image", "picture", "photo", "diagram", "chart", "graph", "figure", "illustration", "table" };
        if (allImages.Count > 1)
        {
            var tTokens = TextNormalizer.Tokenize(tNorm);
            foreach (var (ordinalWord, ordinalIndex) in OrdinalMap)
            {
                if (tNorm.Contains(ordinalWord) && ordinalIndex == imagePositionIndex)
                {
                    int ordinalPos = tTokens.FindIndex(t => t.Equals(ordinalWord, StringComparison.OrdinalIgnoreCase));
                    bool hasImageNoun = false;
                    if (ordinalPos >= 0)
                    {
                        int wStart = Math.Max(0, ordinalPos - 3);
                        int wEnd   = Math.Min(tTokens.Count - 1, ordinalPos + 3);
                        for (int j = wStart; j <= wEnd; j++)
                        {
                            if (j == ordinalPos) continue;
                            if (imageNouns.Any(noun => tTokens[j].Equals(noun, StringComparison.OrdinalIgnoreCase)))
                            {
                                hasImageNoun = true;
                                break;
                            }
                        }
                    }
                    if (hasImageNoun)
                    {
                        bestScore = Math.Max(bestScore, 0.8);
                        spatialBoost = Math.Max(spatialBoost, 0.8);
                        bestPhrase = string.IsNullOrEmpty(bestPhrase)
                            ? $"{ordinalWord} image"
                            : $"{ordinalWord} {bestPhrase}";
                    }
                    break;
                }
            }
        }

        // ── 4. Single-image spatial fallback ────────────────────────────────────
        if (allImages.Count == 1 && spatialBoost > 0 && bestScore > 0.3 && bestScore < 0.7)
        {
            bestScore = 0.7;
        }

        double finalScore = Math.Min(1.0, bestScore + spatialBoost);

        // Return matched OCR words only when there is at least one hit
        return (finalScore, bestPhrase, matchedWords.Count > 0 ? matchedWords : null);
    }
}
