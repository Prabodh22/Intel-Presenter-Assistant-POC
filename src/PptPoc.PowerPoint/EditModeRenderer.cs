using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using Ppt = Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;
using MatchType = PptPoc.Core.Models.MatchType;

namespace PptPoc.PowerPoint;

public class EditModeRenderer : IHighlightRenderer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EditModeRenderer>();
    private const string TagKey = "PPTPOC";
    private const string TimestampTagKey = "PPTPOC_TS";

    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, DateTime> _activeHighlights = new();
    private readonly ConcurrentDictionary<string, string> _highlightShapeNames = new();
    private bool _disposed;

    public EditModeRenderer(AppConfig config)
    {
        _config = config;
    }

    public void Highlight(HighlightRequest request, object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;

        try
        {
            // If already highlighted, just refresh timestamp instead of drawing a duplicate bounding box
            if (_highlightShapeNames.TryGetValue(request.Element.ElementId, out var shapeName) &&
                !string.IsNullOrWhiteSpace(shapeName))
            {
                try
                {
                    var existingShape = slide.Shapes[shapeName];
                    existingShape.Tags.Add(TimestampTagKey, DateTime.UtcNow.Ticks.ToString());
                    _activeHighlights[request.Element.ElementId] = DateTime.UtcNow;
                    return;
                }
                catch (COMException)
                {
                    _highlightShapeNames.TryRemove(request.Element.ElementId, out _);
                }
            }

            // Determine colors based on match type
            int fillColorRgb;
            int borderColorRgb;
            byte fillAlpha;

            if (request.Type == MatchType.TextMatch)
            {
                // Yellow highlight for text
                fillColorRgb = ColorTranslator.ToOle(ColorTranslator.FromHtml(_config.HighlightColorText));
                borderColorRgb = fillColorRgb;
                fillAlpha = 40; // Semi-transparent
            }
            else
            {
                // Cyan border spotlight for images (laser-pointer style)
                fillColorRgb = ColorTranslator.ToOle(ColorTranslator.FromHtml(_config.HighlightColorImage));
                borderColorRgb = fillColorRgb;
                fillAlpha = 15; // Very light fill for images
            }

            // Add highlight rectangle at the target element's position
            var highlightShape = slide.Shapes.AddShape(
                Office.MsoAutoShapeType.msoShapeRectangle,
                request.Element.Left - 2,
                request.Element.Top - 2,
                request.Element.Width + 4,
                request.Element.Height + 4);

            // Configure fill
            highlightShape.Fill.Visible = request.Type == MatchType.TextMatch ? Office.MsoTriState.msoTrue : Office.MsoTriState.msoFalse;
            highlightShape.Fill.ForeColor.RGB = fillColorRgb;
            highlightShape.Fill.Transparency = 1.0f - (fillAlpha / 255.0f);

            // Configure border visibility first to avoid inheriting slide master defaults
            if (request.Type == MatchType.TextMatch)
            {
                // Remove bounding border for text blocks; just use the transparent background highlight
                highlightShape.Line.Visible = Office.MsoTriState.msoFalse;
                highlightShape.Line.Transparency = 1.0f; // Force hide
            }
            else
            {
                // For images, only use the bounding box border (no fill)
                highlightShape.Line.Visible = Office.MsoTriState.msoTrue;
                highlightShape.Line.Transparency = 0.0f; // Force fully opaque
                highlightShape.Line.DashStyle = Office.MsoLineDashStyle.msoLineDash;
            }

            // Apply color and weight after visibility is established
            highlightShape.Line.ForeColor.RGB = borderColorRgb;
            highlightShape.Line.Weight = request.Type == MatchType.ImageMatch
                ? _config.HighlightBorderWeight
                : 2;

            // Tag the shape for identification and cleanup
            highlightShape.Tags.Add(TagKey, "highlight");
            highlightShape.Tags.Add(TimestampTagKey, DateTime.UtcNow.Ticks.ToString());

            // Send behind content so text/images remain visible
            highlightShape.ZOrder(Office.MsoZOrderCmd.msoSendToBack);

            _activeHighlights[request.Element.ElementId] = DateTime.UtcNow;
            _highlightShapeNames[request.Element.ElementId] = highlightShape.Name;

            Log.Debug("Added {MatchType} highlight on element {ElementId} at ({Left},{Top})",
                request.Type, request.Element.ElementId, request.Element.Left, request.Element.Top);
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to add highlight for element {ElementId}", request.Element.ElementId);
        }
    }

    public void ClearExpired(object? slideComObject)
    {
        if (slideComObject == null) return;

        var slide = (Ppt.Slide)slideComObject;
        var now = DateTime.UtcNow;
        var shapesToDelete = new List<Ppt.Shape>();

        try
        {
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                try
                {
                    string tagValue = shape.Tags[TagKey];
                    if (string.IsNullOrEmpty(tagValue)) continue;

                    string tsValue = shape.Tags[TimestampTagKey];
                    if (string.IsNullOrEmpty(tsValue)) continue;

                    if (long.TryParse(tsValue, out long ticks))
                    {
                        var createdAt = new DateTime(ticks, DateTimeKind.Utc);
                        if ((now - createdAt).TotalMilliseconds > _config.HighlightDurationMs)
                        {
                            shapesToDelete.Add(shape);
                        }
                    }
                }
                catch (COMException)
                {
                    // Skip shapes that throw COM errors
                }
            }

            foreach (var shape in shapesToDelete)
            {
                try
                {
                    shape.Delete();
                }
                catch (COMException ex)
                {
                    Log.Warning(ex, "Failed to delete expired highlight shape");
                }
            }

            if (shapesToDelete.Count > 0)
            {
                Log.Debug("Cleared {Count} expired highlight shapes", shapesToDelete.Count);
            }
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Error during ClearExpiredOnSlide");
        }
    }

    public void ClearAll(object? slideComObject)
    {
        if (slideComObject == null) return;

        var slide = (Ppt.Slide)slideComObject;
        var shapesToDelete = new List<Ppt.Shape>();

        try
        {
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                try
                {
                    string tagValue = shape.Tags[TagKey];
                    if (!string.IsNullOrEmpty(tagValue))
                    {
                        shapesToDelete.Add(shape);
                    }
                }
                catch (COMException)
                {
                    // Skip
                }
            }

            foreach (var shape in shapesToDelete)
            {
                try
                {
                    shape.Delete();
                }
                catch (COMException ex)
                {
                    Log.Warning(ex, "Failed to delete highlight shape during ClearAll");
                }
            }

            _activeHighlights.Clear();

            if (shapesToDelete.Count > 0)
            {
                Log.Information("ClearAll removed {Count} highlight shapes", shapesToDelete.Count);
            }
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Error during ClearAll");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _activeHighlights.Clear();
    }
}
