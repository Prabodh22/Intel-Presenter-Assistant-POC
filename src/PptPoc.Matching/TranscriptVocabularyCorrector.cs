namespace PptPoc.Matching;

public static class TranscriptVocabularyCorrector
{
    private const double CandidateMargin = 0.05;

    // ── Protected command words — NEVER corrected by vocab projection ─────────
    // These are critical for voice command recognition (laser on/off, slide nav).
    // "off" and "of" share Soundex O100 — without protection, "laser off" becomes
    // "laser of" when the slide contains text like "state of the art".
    // Similarly "on", "next", "previous" etc. must survive unchanged.
    //
    // NOTE: "back" is in this list to prevent "go back" from being corrupted.
    // However the 2-token merge check runs BEFORE the protected-word guard, so
    // "back end" → "backend" still merges correctly when "backend" is in the
    // slide vocabulary. A protected token is only preserved as-is when the merge
    // check finds nothing worth merging into.
    private static readonly HashSet<string> ProtectedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "on", "off", "next", "previous", "prev", "back", "laser", "slide",
        "please", "go", "move", "switch", "show", "jump", "take"
    };

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

        // No phonetic projection: conservative Levenshtein-only corrections.

        var corrected = new List<string>(tokens.Count);

        for (int index = 0; index < tokens.Count; index++)
        {
            // ── 2-token merge check — runs BEFORE the protected-word guard ───────
            // This allows protected tokens like "back" to still merge with the next
            // word when the compound ("backend") exists in the slide vocabulary.
            //
            // Guard: the NEXT token must not be a protected command word, so that
            // "laser off", "go back", "next slide" etc. are never accidentally fused.
            //
            // Examples:
            //   "back end"   + vocab "backend"   → "backend"   ✅  (fix: back is protected but merge runs first)
            //   "open vino"  + vocab "openvino"  → "openvino"  ✅
            //   "state full" + vocab "stateful"  → "stateful"  ✅  (statefull ≈ stateful, sim=0.889 > 0.84)
            //   "laser off"  → "laseroff" not in vocab → falls through → "laser" protected → preserved ✅
            //   "go back"    → "goback"   not in vocab → falls through → "go"    protected → preserved ✅
            if (index + 1 < tokens.Count && !ProtectedWords.Contains(tokens[index + 1]))
            {
                var mergedToken = tokens[index] + tokens[index + 1];
                if (TryChooseReplacement(mergedToken, vocabulary, isMergedCandidate: true, out var mergedReplacement))
                {
                    corrected.Add(mergedReplacement);
                    index++;
                    continue;
                }
            }

            // ── Skip protected command words entirely (no single-token correction) ─
            if (ProtectedWords.Contains(tokens[index]))
            {
                corrected.Add(tokens[index]);
                continue;
            }

            // --- Single-token correction via Levenshtein + phonetic fallback ---
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

        // Phonetic projection removed: conservative Levenshtein-only corrections.

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

    // Phonetic projection removed: keep vocab correction conservative.

    /// <summary>
    /// American Soundex: maps a word to a 4-character phonetic code.
    /// Words that sound alike get the same code, e.g.:
    ///   "qwen" → Q500, "quen" → Q500 (match!)
    ///   "mmlu" → M400, "mmu" → M000 (no match — good)
    /// </summary>
    // Soundex replaced by DomainCorrectionLayer.PhoneticKey
}
