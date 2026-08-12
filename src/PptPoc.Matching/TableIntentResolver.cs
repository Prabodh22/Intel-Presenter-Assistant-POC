using System.Text.RegularExpressions;
using PptPoc.Core.Models;

namespace PptPoc.Matching;

internal static class TableIntentResolver
{
    private static readonly Regex CellRefRegex = new(@":R(?<row>\d+)C(?<col>\d+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Dictionary<string, int> Ordinals = new(StringComparer.OrdinalIgnoreCase)
    {
        ["first"] = 1,
        ["1st"] = 1,
        ["second"] = 2,
        ["2nd"] = 2,
        ["third"] = 3,
        ["3rd"] = 3,
        ["fourth"] = 4,
        ["4th"] = 4,
        ["fifth"] = 5,
        ["5th"] = 5,
        ["sixth"] = 6,
        ["6th"] = 6,
        ["seventh"] = 7,
        ["7th"] = 7,
        ["eighth"] = 8,
        ["8th"] = 8,
        ["ninth"] = 9,
        ["9th"] = 9,
        ["tenth"] = 10,
        ["10th"] = 10
    };

    public static TableResolution? Resolve(string transcriptText, SlideSnapshot snapshot, string? activeTableKey = null)
    {
        var cells = BuildCells(snapshot);
        if (cells.Count == 0)
            return null;

        var transcriptNorm = TextNormalizer.Normalize(transcriptText);
        if (string.IsNullOrWhiteSpace(transcriptNorm))
            return null;

        var scopeText = $"{transcriptNorm} {transcriptText.ToLowerInvariant()}";
        var scopedTable = ResolveTableScope(scopeText, cells, allowContentScope: string.IsNullOrWhiteSpace(activeTableKey));
        var preferredTableKey = scopedTable?.TableKey ?? activeTableKey;
        var tableGroups = cells
            .GroupBy(cell => cell.TableKey, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!string.IsNullOrWhiteSpace(preferredTableKey) && tableGroups.Count > 1)
        {
            var scopedGroups = tableGroups
                .Where(group => string.Equals(group.Key, preferredTableKey, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (scopedGroups.Count > 0)
                tableGroups = scopedGroups;
        }

        var bestResult = tableGroups
            .Select(group => ResolveTable(transcriptText, transcriptNorm, group.ToList()))
            .Where(result => result != null)
            .OrderByDescending(result => result!.Result?.Confidence ?? result.ScopeConfidence)
            .ThenByDescending(result => CountPhraseWords(result!.Result?.MatchedPhrase ?? string.Empty))
            .FirstOrDefault();

        if (bestResult == null && scopedTable != null)
            return new TableResolution(null, scopedTable.TableKey, scopedTable.Confidence);

        return bestResult;
    }

    private static TableResolution? ResolveTable(string transcriptText, string transcriptNorm, List<TableCell> cells)
    {
        var tableKey = cells[0].TableKey;
        var maxColumn = cells.Max(cell => cell.Column);
        var maxRow = cells.Max(cell => cell.Row);
        var headerByColumn = cells
            .Where(cell => cell.Row == 1)
            .GroupBy(cell => cell.Column)
            .ToDictionary(group => group.Key, group => group.First());

        var bestCell = cells
            .Select(cell => new CellScore(cell, ScoreCell(transcriptText, transcriptNorm, cell.Element.RawText)))
            .Where(score => score.Score >= 0.55) // Lowered from 0.70 to catch "18.95" fuzzy matches
            .OrderByDescending(score => score.Score)
            .ThenBy(score => score.Cell.Row)
            .ThenBy(score => score.Cell.Column)
            .FirstOrDefault();

        var columnScores = new Dictionary<int, double>();
        var columnPhrases = new Dictionary<int, string>();

        foreach (var header in headerByColumn.Values)
        {
            var score = ScoreCell(transcriptText, transcriptNorm, header.Element.RawText);
            if (score >= 0.70)
            {
                columnScores[header.Column] = score;
                columnPhrases[header.Column] = header.Element.RawText;
            }
        }

        var ordinalColumn = ResolveOrdinalColumn(transcriptNorm, maxColumn);
        if (ordinalColumn != null)
        {
            columnScores[ordinalColumn.Value] = Math.Max(columnScores.GetValueOrDefault(ordinalColumn.Value), 0.82);
            columnPhrases[ordinalColumn.Value] = $"column {ordinalColumn.Value}";
        }

        var bestColumn = columnScores
            .OrderByDescending(pair => pair.Value)
            .ThenByDescending(pair => pair.Key)
            .Select(pair => (Column: pair.Key, Score: pair.Value, Phrase: columnPhrases[pair.Key]))
            .FirstOrDefault();

        var rowScores = cells
            .Where(cell => cell.Row > 1)
            .GroupBy(cell => cell.Row)
            .Select(group =>
            {
                var matches = group
                    .Select(cell => new CellScore(cell, ScoreCell(transcriptText, transcriptNorm, cell.Element.RawText)))
                    .Where(score => score.Score >= 0.60)
                    .OrderByDescending(score => score.Score)
                    .ToList();

                var evidenceScore = matches.Count == 0
                    ? 0.0
                    : Math.Min(0.95, matches.Sum(match => match.Score) / Math.Max(2.0, matches.Count));

                if (matches.Count >= 2)
                    evidenceScore = Math.Min(0.95, evidenceScore + 0.18);

                return new RowScore(group.Key, evidenceScore, matches);
            })
            .Where(row => row.Score >= 0.60)
            .OrderByDescending(row => row.Score)
            .ThenBy(row => row.Row)
            .FirstOrDefault();

        if (bestColumn.Score > 0 && rowScores != null)
        {
            var intersection = cells.FirstOrDefault(cell => cell.Row == rowScores.Row && cell.Column == bestColumn.Column);
            if (intersection != null)
            {
                var confidence = Math.Min(0.95, 0.72 + (rowScores.Score * 0.15) + (bestColumn.Score * 0.10));
                if (confidence >= 0.72)
                {
                    return Resolution(tableKey, intersection.Element, confidence,
                        JoinPhrases(rowScores.Matches.Select(match => match.Cell.Element.RawText).Concat(new[] { bestColumn.Phrase })));
                }
            }
        }

        if (bestColumn.Score >= 0.78 && HasColumnIntent(transcriptNorm))
        {
            var header = headerByColumn.GetValueOrDefault(bestColumn.Column)
                ?? cells.Where(cell => cell.Column == bestColumn.Column).OrderBy(cell => cell.Row).FirstOrDefault();
            if (header != null)
                return Resolution(tableKey, header.Element, 0.78, bestColumn.Phrase);
        }

        if (rowScores != null && HasRowIntent(transcriptNorm))
        {
            var anchor = rowScores.Matches
                .Select(match => match.Cell)
                .Where(cell => cell.Row == rowScores.Row)
                .OrderBy(cell => cell.Column)
                .FirstOrDefault()
                ?? cells.Where(cell => cell.Row == rowScores.Row).OrderBy(cell => cell.Column).FirstOrDefault();

            if (anchor != null)
                return Resolution(tableKey, anchor.Element, Math.Min(0.82, rowScores.Score), JoinPhrases(rowScores.Matches.Select(match => match.Cell.Element.RawText)));
        }

        if (bestCell != null && bestCell.Score >= 0.75) // Lowered from 0.78 to allow numeric values to trigger
        {
            return Resolution(tableKey, bestCell.Cell.Element, Math.Min(0.86, bestCell.Score), bestCell.Cell.Element.RawText);
        }

        return null;
    }

    private static List<TableCell> BuildCells(SlideSnapshot snapshot)
    {
        return snapshot.TextElements
            .Select(element => TryCreateCell(element))
            .Where(cell => cell != null)
            .Select(cell => cell!)
            .ToList();
    }

    private static TableCell? TryCreateCell(TextElement element)
    {
        if (string.IsNullOrWhiteSpace(element.ShapeName))
            return null;

        var match = CellRefRegex.Match(element.ShapeName);
        if (!match.Success)
            return null;

        if (!int.TryParse(match.Groups["row"].Value, out var row) || !int.TryParse(match.Groups["col"].Value, out var column))
            return null;

        var tableKey = !string.IsNullOrWhiteSpace(element.ParentVisualId)
            ? element.ParentVisualId
            : element.ShapeName[..match.Index];

        return new TableCell(tableKey, row, column, element);
    }

    private static double ScoreCell(string transcriptText, string transcriptNorm, string cellText)
    {
        if (string.IsNullOrWhiteSpace(cellText))
            return 0.0;

        var cellNorm = TextNormalizer.Normalize(cellText);
        if (string.IsNullOrWhiteSpace(cellNorm))
            return 0.0;

        if (transcriptNorm.Contains(cellNorm, StringComparison.OrdinalIgnoreCase))
        {
            // Boost short numeric cells so they can pass the threshold
            if (cellNorm.Length <= 3 || System.Text.RegularExpressions.Regex.IsMatch(cellNorm, @"^[\d\.]+$"))
                return 0.85; 
            return 0.95;
        }

        var cellTokens = TextNormalizer.Tokenize(cellNorm);
        if (cellTokens.Count > 0 && cellTokens.All(token => transcriptNorm.Contains(token, StringComparison.OrdinalIgnoreCase)))
            return Math.Min(0.90, 0.60 + (cellTokens.Count * 0.12));

        var (score, _) = FuzzyMatcher.Score(transcriptText, cellText);
        return score;
    }

    private static int? ResolveOrdinalColumn(string transcriptNorm, int maxColumn)
    {
        if (transcriptNorm.Contains("last column", StringComparison.OrdinalIgnoreCase))
            return maxColumn;

        foreach (var (word, column) in Ordinals)
        {
            if (column <= maxColumn && transcriptNorm.Contains($"{word} column", StringComparison.OrdinalIgnoreCase))
                return column;
        }

        return null;
    }

    private static bool HasColumnIntent(string transcriptNorm)
    {
        return transcriptNorm.Contains("column", StringComparison.OrdinalIgnoreCase)
               || transcriptNorm.Contains("col ", StringComparison.OrdinalIgnoreCase)
               || transcriptNorm.Contains("last column", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasRowIntent(string transcriptNorm)
    {
        return transcriptNorm.Contains("row", StringComparison.OrdinalIgnoreCase)
               || transcriptNorm.Contains("input prompt", StringComparison.OrdinalIgnoreCase)
               || transcriptNorm.Contains("configuration", StringComparison.OrdinalIgnoreCase);
    }

    private static TableScope? ResolveTableScope(string transcriptNorm, List<TableCell> cells, bool allowContentScope)
    {
        var groups = cells
            .GroupBy(cell => cell.TableKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new
            {
                TableKey = group.Key,
                Cells = group.ToList(),
                Left = group.Min(cell => cell.Element.Left),
                Top = group.Min(cell => cell.Element.Top)
            })
            .OrderBy(group => group.Top)
            .ThenBy(group => group.Left)
            .ToList();

        if (groups.Count <= 1)
            return groups.Count == 1 ? new TableScope(groups[0].TableKey, 0.50) : null;

        var explicitOrdinal = ResolveExplicitTableOrdinal(transcriptNorm, groups.Count);
        if (explicitOrdinal != null)
            return new TableScope(groups[explicitOrdinal.Value - 1].TableKey, 0.95);

        foreach (var (word, ordinal) in Ordinals)
        {
            if (ordinal <= groups.Count &&
                (transcriptNorm.Contains($"{word} table", StringComparison.OrdinalIgnoreCase)
                 || transcriptNorm.Contains($"table {ordinal}", StringComparison.OrdinalIgnoreCase)))
            {
                return new TableScope(groups[ordinal - 1].TableKey, 0.95);
            }
        }

        if (transcriptNorm.Contains("last table", StringComparison.OrdinalIgnoreCase))
            return new TableScope(groups[^1].TableKey, 0.95);

        var leftMost = groups.OrderBy(group => group.Left).First();
        var rightMost = groups.OrderByDescending(group => group.Left).First();
        var topMost = groups.OrderBy(group => group.Top).First();
        var bottomMost = groups.OrderByDescending(group => group.Top).First();

        if (ContainsAny(transcriptNorm, "left table", "left side table", "table on the left"))
            return new TableScope(leftMost.TableKey, 0.90);
        if (ContainsAny(transcriptNorm, "right table", "right side table", "table on the right"))
            return new TableScope(rightMost.TableKey, 0.90);
        if (ContainsAny(transcriptNorm, "top table", "upper table", "table at the top"))
            return new TableScope(topMost.TableKey, 0.90);
        if (ContainsAny(transcriptNorm, "bottom table", "lower table", "table at the bottom"))
            return new TableScope(bottomMost.TableKey, 0.90);

        if (!allowContentScope)
            return null;

        var bestContentScope = groups
            .Select(group => new
            {
                group.TableKey,
                Score = group.Cells.Max(cell => ScoreCell(transcriptNorm, transcriptNorm, cell.Element.RawText))
            })
            .Where(scope => scope.Score >= 0.88)
            .OrderByDescending(scope => scope.Score)
            .FirstOrDefault();

        return bestContentScope != null
            ? new TableScope(bestContentScope.TableKey, bestContentScope.Score)
            : null;
    }

    private static int? ResolveExplicitTableOrdinal(string transcriptNorm, int tableCount)
    {
        (string[] Phrases, int Ordinal)[] ordinalPhrases =
        {
            (new[] { "first table", "1st table", "table one", "table 1" }, 1),
            (new[] { "second table", "2nd table", "table two", "table 2" }, 2),
            (new[] { "third table", "3rd table", "table three", "table 3" }, 3),
            (new[] { "fourth table", "4th table", "table four", "table 4" }, 4)
        };

        foreach (var (phrases, ordinal) in ordinalPhrases)
        {
            if (ordinal <= tableCount && phrases.Any(phrase => transcriptNorm.Contains(phrase, StringComparison.OrdinalIgnoreCase)))
                return ordinal;
        }

        return null;
    }

    private static TableResolution Resolution(string tableKey, TextElement element, double confidence, string phrase)
    {
        return new TableResolution(Result(element, confidence, phrase), tableKey, confidence);
    }

    private static MatchResult Result(TextElement element, double confidence, string phrase)
    {
        var tableIntentConfidence = Math.Min(1.15, Math.Max(confidence, 1.05));
        return new MatchResult
        {
            Element = element,
            Type = PptPoc.Core.Models.MatchType.TextMatch,
            Confidence = tableIntentConfidence,
            Score = tableIntentConfidence,
            MatchedPhrase = phrase
        };
    }

    private static string JoinPhrases(IEnumerable<string> phrases)
    {
        return string.Join(" ", phrases
            .Where(phrase => !string.IsNullOrWhiteSpace(phrase))
            .Select(phrase => phrase.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static int CountPhraseWords(string phrase)
    {
        return TextNormalizer.Tokenize(TextNormalizer.Normalize(phrase)).Count;
    }

    private static bool ContainsAny(string normalizedText, params string[] phrases)
    {
        return phrases.Any(phrase => normalizedText.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }

    public sealed record TableResolution(MatchResult? Result, string TableKey, double ScopeConfidence);

    private sealed record TableScope(string TableKey, double Confidence);

    private sealed record TableCell(string TableKey, int Row, int Column, TextElement Element);

    private sealed record CellScore(TableCell Cell, double Score);

    private sealed record RowScore(int Row, double Score, List<CellScore> Matches);
}