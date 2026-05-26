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

        // Build phonetic index for the slide vocabulary
        var phoneticIndex = BuildPhoneticIndex(vocabulary);

        var corrected = new List<string>(tokens.Count);

        for (int index = 0; index < tokens.Count; index++)
        {
            // --- 3-token merge (e.g. "deep sea carbon" → "deepseekcarbon" is unlikely, but "deep seek" → "deepseek") ---
            if (index + 1 < tokens.Count)
            {
                var mergedToken = tokens[index] + tokens[index + 1];
                if (TryChooseReplacement(mergedToken, vocabulary, phoneticIndex, isMergedCandidate: true, out var mergedReplacement))
                {
                    corrected.Add(mergedReplacement);
                    index++;
                    continue;
                }
            }

            // --- Single-token correction via Levenshtein + phonetic fallback ---
            if (TryChooseReplacement(tokens[index], vocabulary, phoneticIndex, isMergedCandidate: false, out var replacement))
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
        Dictionary<string, List<string>> phoneticIndex,
        bool isMergedCandidate,
        out string replacement)
    {
        replacement = token;

        // Already in vocabulary — no correction needed (unless it's a merge candidate proving two words should join)
        if (vocabulary.Contains(token))
            return isMergedCandidate;

        if (token.Length < 3)
            return false;

        var bestCandidate = string.Empty;
        var bestScore = 0d;
        var secondBestScore = 0d;

        // --- Pass 1: Levenshtein similarity (existing logic) ---
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
        if (bestScore >= threshold && bestScore - secondBestScore >= CandidateMargin)
        {
            replacement = bestCandidate;
            return !string.Equals(replacement, token, StringComparison.OrdinalIgnoreCase);
        }

        // --- Pass 2: Phonetic Vocabulary Projection ---
        // If Levenshtein didn't find a confident match, check if the token
        // *sounds like* a known vocabulary word using Soundex codes.
        // This catches ASR homophone errors: "quen"→"qwen", "ovid"→"ovid" etc.
        if (token.Length >= 3)
        {
            var tokenSoundex = Soundex(token);
            if (phoneticIndex.TryGetValue(tokenSoundex, out var phoneticMatches))
            {
                // Among phonetic matches, pick the one with the best Levenshtein similarity
                string? bestPhonetic = null;
                double bestPhoneticScore = 0;

                foreach (var pm in phoneticMatches)
                {
                    if (string.Equals(pm, token, StringComparison.OrdinalIgnoreCase))
                        continue; // Already the same word

                    double sim = Similarity(token, pm);
                    if (sim > bestPhoneticScore)
                    {
                        bestPhoneticScore = sim;
                        bestPhonetic = pm;
                    }
                }

                // Lower threshold for phonetic matches — the sound already matches,
                // so we just need the text to be in the same ballpark.
                if (bestPhonetic != null && bestPhoneticScore >= 0.5)
                {
                    replacement = bestPhonetic;
                    return true;
                }
            }
        }

        return false;
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

    // ── Phonetic Vocabulary Projection ─────────────────────────────
    // Builds a Soundex-keyed index from the slide vocabulary so that
    // ASR homophones ("quen" ≈ "qwen", "ovid" ≈ "ov id") can be
    // projected back onto the correct domain term.

    private static Dictionary<string, List<string>> BuildPhoneticIndex(HashSet<string> vocabulary)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in vocabulary)
        {
            if (word.Length < 2) continue;
            var code = Soundex(word);
            if (!index.TryGetValue(code, out var list))
            {
                list = new List<string>();
                index[code] = list;
            }
            list.Add(word);
        }
        return index;
    }

    /// <summary>
    /// American Soundex: maps a word to a 4-character phonetic code.
    /// Words that sound alike get the same code, e.g.:
    ///   "qwen" → Q500, "quen" → Q500 (match!)
    ///   "mmlu" → M400, "mmu" → M000 (no match — good)
    /// </summary>
    private static string Soundex(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
            return "0000";

        // Soundex coding table
        // B,F,P,V → 1   C,G,J,K,Q,S,X,Z → 2   D,T → 3
        // L → 4          M,N → 5                 R → 6
        static char SoundexCode(char c) => char.ToUpperInvariant(c) switch
        {
            'B' or 'F' or 'P' or 'V' => '1',
            'C' or 'G' or 'J' or 'K' or 'Q' or 'S' or 'X' or 'Z' => '2',
            'D' or 'T' => '3',
            'L' => '4',
            'M' or 'N' => '5',
            'R' => '6',
            _ => '0' // vowels, H, W, Y
        };

        var result = new char[4];
        result[0] = char.ToUpperInvariant(word[0]);
        int ri = 1;
        char lastCode = SoundexCode(word[0]);

        for (int i = 1; i < word.Length && ri < 4; i++)
        {
            char code = SoundexCode(word[i]);
            if (code != '0' && code != lastCode)
            {
                result[ri++] = code;
            }
            lastCode = code;
        }

        while (ri < 4)
            result[ri++] = '0';

        return new string(result);
    }
}