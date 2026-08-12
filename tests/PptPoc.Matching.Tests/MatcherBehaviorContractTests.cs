using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;

namespace PptPoc.Matching.Tests;

public class MatcherBehaviorContractTests
{
    [Fact]
    public void TableIntent_RowAndColumn_SelectsIntersectionCell()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());
        var snapshot = PerformanceTableSlide();

        var results = matcher.Match("with old configuration for input prompt 256 the RAM consumption is 3 GB", snapshot);

        Assert.Equal("perf-R2C4", results[0].Element.ElementId);
        Assert.Equal("Table 1:R2C4", results[0].Element.ShapeName);
    }

    [Fact]
    public void TableIntent_ColumnReference_SelectsColumnHeader()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());
        var snapshot = ActionTableSlide();

        var results = matcher.Match("please talk about the action column", snapshot);

        Assert.Equal("actions-R1C3", results[0].Element.ElementId);
        Assert.Equal("Table 8:R1C3", results[0].Element.ShapeName);
    }

    [Fact]
    public void TableIntent_RowReference_SelectsReferencedRowKeyCell()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());
        var snapshot = PerformanceTableSlide();

        var results = matcher.Match("old configuration for input prompt 256", snapshot);

        Assert.Equal("perf-R2C2", results[0].Element.ElementId);
        Assert.Equal("Table 1:R2C2", results[0].Element.ShapeName);
    }

    [Fact]
    public void TableScope_ExplicitSecondTableScopesLaterDuplicateHeaderReference()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());
        var snapshot = DuplicateGenerationSpeedTablesSlide();

        var scopedResults = matcher.Match("in the second table show generation speed", snapshot);
        var followUpResults = matcher.Match("generation speed", snapshot);

        Assert.Equal("new-R1C2", scopedResults[0].Element.ElementId);
        Assert.Equal("new-R1C2", followUpResults[0].Element.ElementId);
    }

    [Fact]
    public void SemanticImageRegionReference_TargetsRegionInsideImage()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());
        var image = new ImageElement
        {
            ElementId = "workflow-img",
            ShapeName = "Picture 4",
            Left = 100,
            Top = 100,
            Width = 200,
            Height = 100,
            GptDescription = "workflow diagram with status indicators",
            SemanticEmbedding = new[] { 1f, 0f, 0f },
            VisualType = "diagram"
        };
        var snapshot = new SlideSnapshot
        {
            SlideIndex = 3,
            SlideId = "slide-3",
            ImageElements = new List<ImageElement> { image }
        };

        var results = matcher.Match("look at the top right workflow diagram", snapshot);

        Assert.Equal("workflow-img_region", results[0].Element.ElementId);
        Assert.Equal(image, results[0].ParentImageElement);
        Assert.True(results[0].Element.Left > image.Left + image.Width / 2);
        Assert.True(results[0].Element.Top < image.Top + image.Height / 2);
    }

    [Fact]
    public void ChartLabelMatch_RoutesHighlightToParentVisual()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.2 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());

        var parentChart = new ImageElement
        {
            ElementId = "chart-1",
            ShapeName = "Chart 2",
            Left = 100,
            Top = 100,
            Width = 200,
            Height = 120,
            GptDescription = "Revenue chart"
        };

        var label = new TextElement
        {
            ElementId = "chart-2-label",
            ShapeName = "Chart 2:Label5",
            RawText = "Revenue",
            NormalizedText = "revenue",
            Words = new List<string> { "revenue" },
            Left = 120,
            Top = 115,
            Width = 60,
            Height = 20,
            ParentVisualId = parentChart.ElementId,
            ParentVisualReason = "chart_label_shape_name_match"
        };

        var snapshot = new SlideSnapshot
        {
            SlideIndex = 8,
            SlideId = "slide-8",
            TextElements = new List<TextElement> { label },
            ImageElements = new List<ImageElement> { parentChart }
        };

        var results = matcher.Match("revenue", snapshot);

        Assert.Contains(results, result =>
            result.Element.ElementId == parentChart.ElementId && result.Element is ImageElement);
        Assert.DoesNotContain(results, result => result.Element.ElementId == label.ElementId);
    }

    [Fact]
    public void WeakQuery_DoesNotProduceHighlightWhenConfidenceIsTooLow()
    {
        var config = new AppConfig { MatchConfidenceThreshold = 0.75 };
        var matcher = new MatcherEngine(config, new FakeSemanticService());

        var text = new TextElement
        {
            ElementId = "t-1",
            ShapeName = "Body",
            RawText = "Revenue",
            NormalizedText = "revenue",
            Words = new List<string> { "revenue" },
            Left = 10,
            Top = 10,
            Width = 100,
            Height = 30
        };

        var snapshot = new SlideSnapshot
        {
            SlideIndex = 1,
            SlideId = "slide-1",
            TextElements = new List<TextElement> { text }
        };

        var results = matcher.Match("a", snapshot);

        Assert.Empty(results);
    }

    private static SlideSnapshot PerformanceTableSlide()
    {
        var snapshot = new SlideSnapshot
        {
            SlideIndex = 5,
            SlideId = "slide-5",
            ImageElements = new List<ImageElement>
            {
                new()
                {
                    ElementId = "perf-table",
                    ShapeName = "Table 1",
                    Left = 40,
                    Top = 120,
                    Width = 600,
                    Height = 180,
                    VisualType = "table"
                }
            }
        };

        AddCell(snapshot, "perf", "perf-table", "Table 1", 1, 1, "Configuration");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 1, 2, "Input Prompt");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 1, 3, "Latency");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 1, 4, "RAM Consumption");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 2, 1, "Old Configuration");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 2, 2, "256");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 2, 3, "120 ms");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 2, 4, "3 GB");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 3, 1, "New Configuration");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 3, 2, "256");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 3, 3, "80 ms");
        AddCell(snapshot, "perf", "perf-table", "Table 1", 3, 4, "2 GB");

        return snapshot;
    }

    private static SlideSnapshot ActionTableSlide()
    {
        var snapshot = new SlideSnapshot
        {
            SlideIndex = 8,
            SlideId = "slide-8",
            ImageElements = new List<ImageElement>
            {
                new()
                {
                    ElementId = "actions-table",
                    ShapeName = "Table 8",
                    Left = 40,
                    Top = 120,
                    Width = 600,
                    Height = 180,
                    VisualType = "table"
                }
            }
        };

        AddCell(snapshot, "actions", "actions-table", "Table 8", 1, 1, "Owner");
        AddCell(snapshot, "actions", "actions-table", "Table 8", 1, 2, "Status");
        AddCell(snapshot, "actions", "actions-table", "Table 8", 1, 3, "Action");
        AddCell(snapshot, "actions", "actions-table", "Table 8", 2, 1, "Asha");
        AddCell(snapshot, "actions", "actions-table", "Table 8", 2, 2, "Open");
        AddCell(snapshot, "actions", "actions-table", "Table 8", 2, 3, "Share debug logs");

        return snapshot;
    }

    private static SlideSnapshot DuplicateGenerationSpeedTablesSlide()
    {
        var snapshot = new SlideSnapshot
        {
            SlideIndex = 5,
            SlideId = "slide-5",
            ImageElements = new List<ImageElement>
            {
                new()
                {
                    ElementId = "old-table",
                    ShapeName = "Old Table",
                    Left = 40,
                    Top = 120,
                    Width = 360,
                    Height = 120,
                    VisualType = "table"
                },
                new()
                {
                    ElementId = "new-table",
                    ShapeName = "New Table",
                    Left = 460,
                    Top = 120,
                    Width = 360,
                    Height = 120,
                    VisualType = "table"
                }
            }
        };

        AddCell(snapshot, "old", "old-table", "Old Table", 1, 1, "Configuration");
        AddCell(snapshot, "old", "old-table", "Old Table", 1, 2, "Generation speed");
        AddCell(snapshot, "old", "old-table", "Old Table", 2, 1, "Old Configuration");
        AddCell(snapshot, "old", "old-table", "Old Table", 2, 2, "14 tokens/s");
        AddCell(snapshot, "new", "new-table", "New Table", 1, 1, "Configuration");
        AddCell(snapshot, "new", "new-table", "New Table", 1, 2, "Generation speed");
        AddCell(snapshot, "new", "new-table", "New Table", 2, 1, "New Configuration");
        AddCell(snapshot, "new", "new-table", "New Table", 2, 2, "22 tokens/s");

        return snapshot;
    }

    private static void AddCell(SlideSnapshot snapshot, string idPrefix, string parentId, string tableName, int row, int column, string text)
    {
        snapshot.TextElements.Add(new TextElement
        {
            ElementId = $"{idPrefix}-R{row}C{column}",
            ShapeName = $"{tableName}:R{row}C{column}",
            RawText = text,
            NormalizedText = TextNormalizer.Normalize(text),
            Words = TextNormalizer.Tokenize(TextNormalizer.Normalize(text)),
            Left = 40 + (column - 1) * 150,
            Top = 120 + (row - 1) * 45,
            Width = 150,
            Height = 45,
            ParentVisualId = parentId,
            ParentVisualReason = "table_cell_routes_to_table"
        });
    }
}
