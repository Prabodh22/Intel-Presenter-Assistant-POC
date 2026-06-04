using System.Globalization;
using System.Text.RegularExpressions;
using PptPoc.Core.Models;

namespace PptPoc.Matching;

public static class NumericChartMatcher
{
    private static readonly Regex DigitNumberRegex = new(@"\b\d{1,3}(?:,\d{3})*(?:\.\d+)?%?\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> NumberWords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["zero"] = 0,
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["eleven"] = 11,
        ["twelve"] = 12,
        ["thirteen"] = 13,
        ["fourteen"] = 14,
        ["fifteen"] = 15,
        ["sixteen"] = 16,
        ["seventeen"] = 17,
        ["eighteen"] = 18,
        ["nineteen"] = 19,
        ["twenty"] = 20,
        ["thirty"] = 30,
        ["forty"] = 40,
        ["fifty"] = 50,
        ["sixty"] = 60,
        ["seventy"] = 70,
        ["eighty"] = 80,
        ["ninety"] = 90,
        ["hundred"] = 100,
        ["thousand"] = 1000,
        ["million"] = 1000000,
        ["billion"] = 1000000000,
    };

    public static (double Boost, string MatchedPhrase) Score(string transcriptText, ImageElement image)
    {
        if (string.IsNullOrWhiteSpace(transcriptText) || image.ChartNumericFacts.Count == 0)
            return (0.0, string.Empty);

        var transcriptNumbers = ExtractNumbers(transcriptText);
        if (transcriptNumbers.Count == 0)
            return (0.0, string.Empty);

        var chartNumbers = ExtractNumbers(string.Join(" ", image.ChartNumericFacts));
        if (chartNumbers.Count == 0)
            return (0.0, string.Empty);

        int hits = 0;
        string matched = string.Empty;

        foreach (var t in transcriptNumbers)
        {
            bool matchedThis = chartNumbers.Any(c => IsCloseNumber(t.Value, c.Value));
            if (matchedThis)
            {
                hits++;
                if (string.IsNullOrEmpty(matched))
                    matched = t.Raw;
            }
        }

        if (hits == 0)
            return (0.0, string.Empty);

        double boost = Math.Min(0.55, 0.20 + (hits - 1) * 0.15);

        var norm = TextNormalizer.Normalize(transcriptText);
        if (norm.Contains("chart") || norm.Contains("graph") || norm.Contains("percent") || norm.Contains("percentage"))
            boost = Math.Min(0.65, boost + 0.05);

        return (boost, matched);
    }

    private static bool IsCloseNumber(double a, double b)
    {
        double abs = Math.Abs(a - b);
        if (abs <= 0.001) return true;

        double denom = Math.Max(Math.Abs(a), Math.Abs(b));
        if (denom <= 1e-9) return true;

        return (abs / denom) <= 0.02;
    }

    private static List<(double Value, string Raw)> ExtractNumbers(string text)
    {
        var results = new List<(double Value, string Raw)>();

        foreach (Match m in DigitNumberRegex.Matches(text))
        {
            if (!m.Success) continue;

            string token = m.Value;
            bool isPercent = token.EndsWith("%", StringComparison.Ordinal);
            var cleaned = token.Replace(",", string.Empty).TrimEnd('%');
            if (double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
            {
                results.Add((num, token));
            }

            if (isPercent)
                results.Add((num, cleaned + " percent"));
        }

        var wordBased = ExtractWordNumbers(text);
        results.AddRange(wordBased);

        return results;
    }

    private static List<(double Value, string Raw)> ExtractWordNumbers(string text)
    {
        var results = new List<(double Value, string Raw)>();
        var tokens = TextNormalizer.Tokenize(TextNormalizer.Normalize(text));

        for (int i = 0; i < tokens.Count; i++)
        {
            if (!NumberWords.ContainsKey(tokens[i])) continue;

            int j = i;
            double current = 0;
            double total = 0;
            var phrase = new List<string>();
            bool hasPoint = false;
            string decimals = string.Empty;

            while (j < tokens.Count)
            {
                var tk = tokens[j];
                if (tk == "point")
                {
                    hasPoint = true;
                    phrase.Add(tk);
                    j++;
                    continue;
                }

                if (!NumberWords.TryGetValue(tk, out int value))
                    break;

                phrase.Add(tk);

                if (hasPoint)
                {
                    if (value >= 0 && value <= 9)
                    {
                        decimals += value.ToString(CultureInfo.InvariantCulture);
                        j++;
                        continue;
                    }
                    break;
                }

                if (value == 100)
                {
                    current = Math.Max(1, current) * 100;
                }
                else if (value == 1000 || value == 1000000 || value == 1000000000)
                {
                    total += Math.Max(1, current) * value;
                    current = 0;
                }
                else
                {
                    current += value;
                }

                j++;
            }

            if (phrase.Count == 0) continue;

            double baseNumber = total + current;
            if (hasPoint && decimals.Length > 0)
            {
                if (double.TryParse("0." + decimals, NumberStyles.Float, CultureInfo.InvariantCulture, out var frac))
                    baseNumber += frac;
            }

            if (baseNumber > 0)
            {
                string raw = string.Join(" ", phrase);
                bool percent = j < tokens.Count && (tokens[j] == "percent" || tokens[j] == "percentage");
                results.Add((baseNumber, percent ? raw + " percent" : raw));
                i = Math.Max(i, j - 1);
            }
        }

        return results;
    }
}
