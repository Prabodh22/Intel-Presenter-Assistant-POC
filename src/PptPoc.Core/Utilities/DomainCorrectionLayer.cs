using System;
using System.Collections.Generic;
using System.Linq;
using PptPoc.Core.Models;

namespace PptPoc.Core.Utilities;

public class WordCorrection
{
    public int StartToken { get; set; }
    public int TokenCount { get; set; }
    public string Original { get; set; } = string.Empty;
    public string Correction { get; set; } = string.Empty;
    public double Confidence { get; set; }
}

public class CorrectionResult
{
    public string CorrectedText { get; set; } = string.Empty;
    public List<WordCorrection> Corrections { get; set; } = new();
    public double OverallConfidence { get; set; }
}

public static class DomainCorrectionLayer
{
    // Lightweight Levenshtein distance for small strings
    private static int LevenshteinDistance(string a, string b)
    {
        if (a == b) return 0;
        if (string.IsNullOrEmpty(a)) return b.Length;
        if (string.IsNullOrEmpty(b)) return a.Length;

        int[,] d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (int j = 0; j <= b.Length; j++) d[0, j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
            }
        }
        return d[a.Length, b.Length];
    }

    private static double SimilarityRatio(string a, string b)
    {
        int max = Math.Max(a.Length, b.Length);
        if (max == 0) return 1.0;
        int dist = LevenshteinDistance(a, b);
        return 1.0 - (double)dist / max;
    }

    public static CorrectionResult CorrectTranscript(string rawTranscript, SlideSnapshot? snapshot, IReadOnlyList<string> vocabulary)
    {
        var result = new CorrectionResult { CorrectedText = rawTranscript, OverallConfidence = 1.0 };

        if (string.IsNullOrWhiteSpace(rawTranscript) || snapshot == null || vocabulary == null || vocabulary.Count == 0)
            return result;

        // Build candidate map from SemanticEntities
        var candidateMap = new Dictionary<string, (string Canonical, double BaseConfidence)>(StringComparer.OrdinalIgnoreCase);
        foreach (var ent in snapshot.SemanticEntities)
        {
            if (!string.IsNullOrWhiteSpace(ent.Canonical))
            {
                var key = NormalizeKey(ent.Canonical);
                if (!candidateMap.ContainsKey(key)) candidateMap[key] = (ent.Canonical, 1.0);
                // also add de-spaced form
                var compact = key.Replace(" ", string.Empty).Replace("-", string.Empty);
                if (!candidateMap.ContainsKey(compact)) candidateMap[compact] = (ent.Canonical, 0.95);
            }

            void addList(IEnumerable<string>? list, double conf)
            {
                if (list == null) return;
                foreach (var v in list)
                {
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    var k = NormalizeKey(v);
                    if (!candidateMap.ContainsKey(k)) candidateMap[k] = (ent.Canonical ?? v, conf);
                }
            }

            addList(ent.SpokenVariants, 0.95);
            addList(ent.OcrVariants, 0.9);
            addList(ent.AsrVariants, 0.9);
            addList(ent.TechnicalTerms, 0.95);

            if (ent.NumericNormalization != null)
            {
                foreach (var kv in ent.NumericNormalization)
                {
                    var k1 = NormalizeKey(kv.Key);
                    var k2 = NormalizeKey(kv.Value);
                    if (!candidateMap.ContainsKey(k1)) candidateMap[k1] = (kv.Value, 0.98);
                    if (!candidateMap.ContainsKey(k2)) candidateMap[k2] = (kv.Value, 0.99);
                }
            }
        }

        // Tokenize (preserve simple punctuation attached)
        var tokens = rawTranscript.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Phase 7: Pre-normalize numeric expressions (decimals, percents, sizes, freqs, versions)
        for (int i = 0; i < tokens.Count; i++)
        {
            if (string.IsNullOrWhiteSpace(tokens[i])) continue;
            if (TryNormalizeNumberSequence(tokens, i, out var normalized, out var consumed))
            {
                tokens[i] = normalized;
                for (int j = 1; j < consumed && i + j < tokens.Count; j++) tokens[i + j] = string.Empty;
            }
        }

        var normalizedTokens = tokens.Select(t => NormalizeKey(t)).ToArray();

        var corrections = new List<WordCorrection>();

        // No phonetic fallback in this conservative correction pipeline.

        for (int i = 0; i < normalizedTokens.Length; i++)
        {
            // Try multi-word ngrams up to 4
            bool replaced = false;
            for (int n = Math.Min(4, normalizedTokens.Length - i); n >= 1; n--)
            {
                var span = normalizedTokens.Skip(i).Take(n);
                var candidate = string.Join(' ', span).Trim();
                if (string.IsNullOrWhiteSpace(candidate)) continue;

                // direct equality
                if (candidateMap.TryGetValue(candidate, out var entry))
                {
                    var corr = new WordCorrection
                    {
                        StartToken = i,
                        TokenCount = n,
                        Original = string.Join(' ', tokens.Skip(i).Take(n)),
                        Correction = entry.Canonical ?? entry.Canonical,
                        Confidence = entry.BaseConfidence
                    };
                    corrections.Add(corr);
                    // replace tokens
                    tokens[i] = corr.Correction;
                    for (int j = 1; j < n; j++) tokens[i + j] = string.Empty;
                    replaced = true;
                    break;
                }

                // fuzzy: compare against all keys (snapshot sizes are small)
                foreach (var kv in candidateMap)
                {
                    // Skip trivial short comparisons
                    if (Math.Abs(kv.Key.Length - candidate.Length) > Math.Max(3, kv.Key.Length / 2)) continue;
                    double sim = SimilarityRatio(kv.Key, candidate);
                    if (sim >= 0.80)
                    {
                        var corr = new WordCorrection
                        {
                            StartToken = i,
                            TokenCount = n,
                            Original = string.Join(' ', tokens.Skip(i).Take(n)),
                            Correction = kv.Value.Canonical,
                            Confidence = 0.65 + 0.35 * sim // scale between 0.65-1.0
                        };
                        corrections.Add(corr);
                        tokens[i] = corr.Correction;
                        for (int j = 1; j < n; j++) tokens[i + j] = string.Empty;
                        replaced = true;
                        break;
                    }
                }

                // No phonetic fallback here; conservative exact + fuzzy matching only.

                if (replaced) break;
            }
            if (replaced)
            {
                // skip past emptied tokens
            }
        }

        // Reconstruct corrected text
        var rebuilt = string.Join(' ', tokens.Where(t => !string.IsNullOrWhiteSpace(t)));
        result.CorrectedText = rebuilt;
        result.Corrections = corrections;
        result.OverallConfidence = corrections.Count == 0 ? 1.0 : corrections.Average(c => c.Confidence);
        return result;
    }

    private static string NormalizeKey(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lower = s.Trim().ToLowerInvariant();
        // remove punctuation except percent
        var cleaned = new string(lower.Where(c => char.IsLetterOrDigit(c) || c == '%' || char.IsWhiteSpace(c) || c == '-').ToArray());
        // collapse multiple spaces
        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    // Attempt to normalize a number-like sequence starting at tokens[index].
    // Returns normalized single-token string (e.g., "17.4%", "17.4GB", "v1.2.3") and number of tokens consumed.
    private static bool TryNormalizeNumberSequence(List<string> tokens, int index, out string normalized, out int consumed)
    {
        normalized = string.Empty;
        consumed = 0;
        if (index < 0 || index >= tokens.Count) return false;

        string tok = tokens[index].Trim().ToLowerInvariant();

        // Quick regex for explicit numeric forms: 123, 1,234.56, 17.4%, 1.2.3 (versions)
        var m = System.Text.RegularExpressions.Regex.Match(tok, @"^(v?\d+(?:[\.,]\d+)*)(%|kb|mb|gb|tb|hz|khz|mhz|w|kw)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var num = m.Groups[1].Value.Replace(',', '.');
            var unit = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;
            if (!string.IsNullOrEmpty(unit)) unit = unit.ToUpperInvariant();
            // Normalize version-like sequences with multiple dots
            if (num.Count(c => c == '.') >= 1 && tok.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "v" + num;
            }
            else
            {
                normalized = num + (string.IsNullOrEmpty(unit) ? string.Empty : unit == "%" ? "%" : unit);
            }
            consumed = 1;
            return true;
        }

        // Try spoken-number parsing (e.g., "seventeen point four percent", "seventeen point four gigabytes")
        int i = index;
        long intPart = 0;
        bool intFound = false;
        bool negative = false;
        // Map words to numbers
        var smallNumbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5,
            ["six"] = 6, ["seven"] = 7, ["eight"] = 8, ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11,
            ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15, ["sixteen"] = 16,
            ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19
        };
        var tens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60,
            ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90
        };
        var scales = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["hundred"] = 100, ["thousand"] = 1000, ["million"] = 1000000, ["billion"] = 1000000000
        };

        long current = 0;
        long total = 0;
        int consumedCount = 0;
        while (i < tokens.Count)
        {
            var w = tokens[i].ToLowerInvariant();
            if (w == "negative" || w == "minus") { negative = true; i++; consumedCount++; continue; }
            if (smallNumbers.TryGetValue(w, out var sval)) { current += sval; intFound = true; i++; consumedCount++; continue; }
            if (tens.TryGetValue(w, out var tval)) { current += tval; intFound = true; i++; consumedCount++; continue; }
            if (scales.TryGetValue(w, out var scale)) { if (current == 0) current = 1; current *= scale; total += current; current = 0; intFound = true; i++; consumedCount++; continue; }
            // explicit digits like "17"
            if (System.Text.RegularExpressions.Regex.IsMatch(w, "^\\d+$")) { current += long.Parse(w); intFound = true; i++; consumedCount++; continue; }
            break;
        }
        total += current;

        // Check for decimal marker
        if (i < tokens.Count && (tokens[i].Equals("point", StringComparison.OrdinalIgnoreCase) || tokens[i].Equals("dot", StringComparison.OrdinalIgnoreCase)))
        {
            i++; consumedCount++;
            var decimalDigits = new List<string>();
            while (i < tokens.Count)
            {
                var w = tokens[i].ToLowerInvariant();
                if (smallNumbers.TryGetValue(w, out var d)) { decimalDigits.Add(d.ToString()); i++; consumedCount++; continue; }
                if (System.Text.RegularExpressions.Regex.IsMatch(w, "^\\d+$")) { foreach (var ch in w) decimalDigits.Add(ch.ToString()); i++; consumedCount++; continue; }
                // allow tens in decimal as individual digits ("forty five" -> "45")
                if (tens.TryGetValue(w, out var tv)) { decimalDigits.Add((tv/10).ToString()); i++; consumedCount++; continue; }
                break;
            }

            if (decimalDigits.Count > 0)
            {
                var intStr = total.ToString();
                var decStr = string.Concat(decimalDigits);
                var numStr = intStr + "." + decStr;
                // check for trailing unit
                string unit = string.Empty;
                if (i < tokens.Count)
                {
                    var next = tokens[i].ToLowerInvariant().Trim().TrimEnd('.');
                    var unitMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["percent"] = "%", ["%"] = "%",
                        ["kb"] = "KB", ["kilobyte"] = "KB", ["kilobytes"] = "KB",
                        ["mb"] = "MB", ["megabyte"] = "MB", ["megabytes"] = "MB",
                        ["gb"] = "GB", ["gigabyte"] = "GB", ["gigabytes"] = "GB",
                        ["tb"] = "TB", ["terabyte"] = "TB", ["terabytes"] = "TB",
                        ["hz"] = "Hz", ["khz"] = "kHz", ["mhz"] = "MHz",
                        ["w"] = "W", ["kw"] = "kW", ["watts"] = "W", ["watt"] = "W"
                    };
                    if (unitMap.TryGetValue(next, out var mapped)) { unit = mapped; i++; consumedCount++; }
                }
                normalized = numStr + unit;
                if (negative) normalized = "-" + normalized;
                consumed = consumedCount;
                return true;
            }
        }

        // If we parsed an integer-only spoken number and followed by unit or percent
        if (intFound)
        {
            string unit = string.Empty;
            if (i < tokens.Count)
            {
                var next = tokens[i].ToLowerInvariant().Trim().TrimEnd('.');
                var unitMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["percent"] = "%", ["%"] = "%",
                    ["kb"] = "KB", ["kilobyte"] = "KB", ["kilobytes"] = "KB",
                    ["mb"] = "MB", ["megabyte"] = "MB", ["megabytes"] = "MB",
                    ["gb"] = "GB", ["gigabyte"] = "GB", ["gigabytes"] = "GB",
                    ["tb"] = "TB", ["terabyte"] = "TB", ["terabytes"] = "TB",
                    ["hz"] = "Hz", ["khz"] = "kHz", ["mhz"] = "MHz",
                    ["w"] = "W", ["kw"] = "kW", ["watts"] = "W", ["watt"] = "W"
                };
                if (unitMap.TryGetValue(next, out var mapped)) { unit = mapped; i++; consumedCount++; }
            }
            normalized = (negative ? "-" : string.Empty) + total.ToString() + unit;
            consumed = consumedCount;
            return true;
        }

        // version detection: "version 1.2.3" or "v1.2.3"
        if (tok == "version" && index + 1 < tokens.Count)
        {
            var next = tokens[index + 1];
            if (System.Text.RegularExpressions.Regex.IsMatch(next, "^\\d+(?:\\.\\d+)+$"))
            {
                normalized = "v" + next;
                consumed = 2;
                return true;
            }
        }

        // leading v1.2 style
        if (tok.StartsWith("v") && System.Text.RegularExpressions.Regex.IsMatch(tok.Substring(1), "^\\d+(?:\\.\\d+)*$"))
        {
            normalized = tok;
            consumed = 1;
            return true;
        }

        return false;
    }

    // Phonetic correction was intentionally removed to keep the correction
    // pipeline conservative and slide-local. No public phonetic API provided.
}
