using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.Matching;

namespace PptPoc.Matching.Tests;

public class MatcherBehaviorContractTests
{
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
}
