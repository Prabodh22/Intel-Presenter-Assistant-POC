using System.Globalization;
using System.Text.RegularExpressions;
using PptPoc.Core.Models;

namespace PptPoc.Core.Utilities;

public class EntityVariantsResult
{
    public string? Canonical { get; set; }
    public List<string>? SpokenVariants { get; set; }
    public List<string>? OcrVariants { get; set; }
    public List<string>? AsrVariants { get; set; }
    public List<string>? TechnicalTerms { get; set; }
    public Dictionary<string,string>? NumericNormalization { get; set; }
    public List<string>? Units { get; set; }
    public Dictionary<string,string>? Relationships { get; set; }
}

public static class EntityVariantGenerator
{
    private static readonly Regex NumericToken = new(@"^[-+]?[0-9\,\.]+%?$", RegexOptions.Compiled);
    private static readonly Regex AcronymToken = new(@"^[A-Z]{2,}$", RegexOptions.Compiled);
    private static readonly Dictionary<char,string> DigitNames = new()
    {
        ['0'] = "zero", ['1'] = "one", ['2'] = "two", ['3'] = "three", ['4'] = "four",
        ['5'] = "five", ['6'] = "six", ['7'] = "seven", ['8'] = "eight", ['9'] = "nine"
    };

    public static EntityVariantsResult Generate(string? raw, List<string>? tokens = null, string? parentId = null)
    {
        var res = new EntityVariantsResult();
        if (string.IsNullOrWhiteSpace(raw) && (tokens == null || tokens.Count == 0))
            return res;

        var text = (raw ?? string.Join(' ', tokens ?? new List<string>())).Trim();
        // canonical: lowercased, punctuation-stripped normalized text
        res.Canonical = Normalize(text);

        // Ocr variants: token-level normalized forms
        res.OcrVariants = (tokens ?? Tokenize(res.Canonical)).Select(t => t).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Spoken variants: include raw, canonical, digit-spelled versions for numeric tokens
        var spoken = new List<string> { text, res.Canonical! };
        foreach (var tok in Tokenize(text))
        {
            if (NumericToken.IsMatch(tok))
            {
                var norm = NormalizeNumeric(tok);
                if (!string.IsNullOrEmpty(norm))
                {
                    res.NumericNormalization ??= new Dictionary<string,string>();
                    res.NumericNormalization[tok] = norm;
                    spoken.Add(DigitsSpelled(tok));
                    spoken.Add(DecimalToSpoken(norm));
                }
            }
            if (AcronymToken.IsMatch(tok))
            {
                spoken.Add(SpellOutAcronym(tok));
            }
        }
        res.SpokenVariants = spoken.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // ASR variants: include spaced digits and acronym spellings plus lowercased forms
        var asr = new List<string>();
        asr.Add(res.Canonical!);
        foreach (var tok in res.OcrVariants)
        {
            if (NumericToken.IsMatch(tok)) asr.Add(DigitsSpelled(tok));
            if (AcronymToken.IsMatch(tok)) asr.Add(SpellOutAcronym(tok));
            asr.Add(tok.ToLowerInvariant());
        }
        res.AsrVariants = asr.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Technical terms heuristic: tokens longer than 3 or contain '-' or CamelCase
        res.TechnicalTerms = Tokenize(raw ?? string.Empty)
            .Where(t => t.Length > 3 || t.Contains('-') || Regex.IsMatch(t, @"[A-Z][a-z]+[A-Z]"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Units: detect common unit suffixes inside the text
        var units = new List<string>();
        var unitMatches = Regex.Matches(text, @"\b(ms|s|sec|seconds|kg|g|m|km|cm|mm|%|percent|bps)\b", RegexOptions.IgnoreCase);
        foreach (Match m in unitMatches) units.Add(m.Value.ToLowerInvariant());
        res.Units = units.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Relationships: simple parent relationship if provided
        if (!string.IsNullOrWhiteSpace(parentId)) res.Relationships = new Dictionary<string,string> { ["part_of"] = parentId };

        return res;
    }

    private static string Normalize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return string.Empty;
        var lowered = s.ToLowerInvariant();
        lowered = Regex.Replace(lowered, @"[^\w\s\-']", " ");
        lowered = Regex.Replace(lowered, @"\s+", " ");
        return lowered.Trim();
    }

    private static List<string> Tokenize(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return new List<string>();
        return s.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
    }

    private static string NormalizeNumeric(string token)
    {
        var trimmed = token.Trim();
        bool hasPercent = trimmed.Contains('%');
        var cleaned = Regex.Replace(trimmed, "[^0-9\\.,\\-]", string.Empty);
        if (string.IsNullOrWhiteSpace(cleaned)) return string.Empty;
        cleaned = cleaned.Replace(",", string.Empty);
        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return string.Empty;
        var normalized = value.ToString("0.####", CultureInfo.InvariantCulture);
        return hasPercent ? normalized + "%" : normalized;
    }

    private static string DigitsSpelled(string token)
    {
        var digits = Regex.Replace(token, "[^0-9]", string.Empty);
        if (string.IsNullOrEmpty(digits)) return token;
        return string.Join(' ', digits.Select(d => DigitNames.ContainsKey(d) ? DigitNames[d] : d.ToString()));
    }

    private static string DecimalToSpoken(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return normalized;
        if (normalized.EndsWith("%")) normalized = normalized.TrimEnd('%');
        if (!normalized.Contains('.')) return DigitsSpelled(normalized);
        var parts = normalized.Split('.');
        return DigitsSpelled(parts[0]) + " point " + string.Join(' ', parts[1].Select(c => DigitNames[c]));
    }

    private static string SpellOutAcronym(string s)
    {
        return string.Join(' ', s.ToCharArray());
    }
}
