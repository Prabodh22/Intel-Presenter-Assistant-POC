namespace PptPoc.Matching;

public static class TranscriptVocabularyCorrector
{
    private const double CandidateMargin = 0.05;

    public static string Correct(string transcriptText, IEnumerable<string> vocabularyTerms)
    {
        var normalizedTranscript = TextNormalizer.Normalize(transcriptText);
        if (string.IsNullOrWhiteSpace(normalizedTranscript))
            return string.Empty;

        var tokens = TextNormalizer.Tokenize(normalizedTranscript);
        if (tokens.Count == 0)
            return normalizedTranscript;

        var vocabulary = BuildVocabulary(vocabularyTerms);
        if (vocabulary.Count == 0)
            return normalizedTranscript;

        var corrected = new List<string>(tokens.Count);

        for (int index = 0; index < tokens.Count; index++)
        {
            if (index + 1 < tokens.Count)
            {
                var mergedToken = tokens[index] + tokens[index + 1];
                if (TryChooseReplacement(mergedToken, vocabulary, isMergedCandidate: true, out var mergedReplacement))
                {
                    corrected.Add(mergedReplacement);
                    index++;
                    continue;
                }
            }

            if (TryChooseReplacement(tokens[index], vocabulary, isMergedCandidate: false, out var replacement))
            {
                corrected.Add(replacement);
                continue;
            }

            corrected.Add(tokens[index]);
        }

        return string.Join(' ', corrected);
    }

    private static HashSet<string> BuildVocabulary(IEnumerable<string> vocabularyTerms)
    {
        var vocabulary = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var term in vocabularyTerms)
        {
            var normalized = TextNormalizer.Normalize(term);
            if (string.IsNullOrWhiteSpace(normalized))
                continue;

            vocabulary.Add(normalized);

            foreach (var token in TextNormalizer.Tokenize(normalized))
            {
                vocabulary.Add(token);
            }
        }

        return vocabulary;
    }

    private static bool TryChooseReplacement(
        string token,
        HashSet<string> vocabulary,
        bool isMergedCandidate,
        out string replacement)
    {
        replacement = token;

        if (vocabulary.Contains(token))
            return isMergedCandidate;

        if (token.Length < 5)
            return false;

        var bestCandidate = string.Empty;
        var bestScore = 0d;
        var secondBestScore = 0d;

        foreach (var candidate in vocabulary)
        {
            if (Math.Abs(candidate.Length - token.Length) > 3)
                continue;

            var score = Similarity(token, candidate);
            if (score > bestScore)
            {
                secondBestScore = bestScore;
                bestScore = score;
                bestCandidate = candidate;
            }
            else if (score > secondBestScore)
            {
                secondBestScore = score;
            }
        }

        var threshold = isMergedCandidate ? 0.84 : 0.88;
        if (bestScore < threshold || bestScore - secondBestScore < CandidateMargin)
            return false;

        replacement = bestCandidate;
        return !string.Equals(replacement, token, StringComparison.OrdinalIgnoreCase);
    }

    private static double Similarity(string source, string target)
    {
        if (source.Length == 0 && target.Length == 0)
            return 1;

        var distance = LevenshteinDistance(source, target);
        var longestLength = Math.Max(source.Length, target.Length);
        return 1d - ((double)distance / longestLength);
    }

    private static int LevenshteinDistance(string source, string target)
    {
        var rows = source.Length + 1;
        var cols = target.Length + 1;
        var distance = new int[rows, cols];

        for (int row = 0; row < rows; row++)
            distance[row, 0] = row;

        for (int col = 0; col < cols; col++)
            distance[0, col] = col;

        for (int row = 1; row < rows; row++)
        {
            for (int col = 1; col < cols; col++)
            {
                var cost = source[row - 1] == target[col - 1] ? 0 : 1;
                distance[row, col] = Math.Min(
                    Math.Min(distance[row - 1, col] + 1, distance[row, col - 1] + 1),
                    distance[row - 1, col - 1] + cost);
            }
        }

        return distance[rows - 1, cols - 1];
    }
}