using System.Collections.Concurrent;
using System.Drawing;
using System.Runtime.InteropServices;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using Serilog;
using Ppt = Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;

namespace PptPoc.PowerPoint;

public class EditModeRenderer : IHighlightRenderer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<EditModeRenderer>();
    private const string TagKey = "PPTPOC";
    private const string TimestampTagKey = "PPTPOC_TS";
    private const string DurationTagKey = "PPTPOC_DURATION_MS";

    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, DateTime> _activeHighlights = new();
    private readonly ConcurrentDictionary<string, string> _highlightShapeNames = new();
    private bool _disposed;

    public EditModeRenderer(AppConfig config)
    {
        _config = config;
    }

    public bool Highlight(HighlightRequest request, object slideComObject)
    {
        var slide = (Ppt.Slide)slideComObject;

        try
        {
            // If already highlighted, just refresh timestamp instead of drawing a duplicate laser dot
            if (_highlightShapeNames.TryGetValue(request.Element.ElementId, out var shapeName) &&
                !string.IsNullOrWhiteSpace(shapeName))
            {
                try
                {
                    var existingShape = slide.Shapes[shapeName];
                    existingShape.Tags.Add(TimestampTagKey, DateTime.UtcNow.Ticks.ToString());
                    existingShape.Tags.Add(DurationTagKey, request.DurationMs.ToString());
                    _activeHighlights[request.Element.ElementId] = DateTime.UtcNow;
                    return true;
                }
                catch (COMException)
                {
                    _highlightShapeNames.TryRemove(request.Element.ElementId, out _);
                }
            }

            const float dotSize = 10f;
            var centerX = request.Element.Left + request.Element.Width / 2f;
            var centerY = request.Element.Top + request.Element.Height / 2f;
            var laserColor = ColorTranslator.ToOle(ColorTranslator.FromHtml("#FF1111"));
            var laserBorder = ColorTranslator.ToOle(ColorTranslator.FromHtml("#FF6666"));

            var highlightShape = slide.Shapes.AddShape(
                Office.MsoAutoShapeType.msoShapeOval,
                centerX - dotSize / 2f,
                centerY - dotSize / 2f,
                dotSize,
                dotSize);

            highlightShape.Fill.Visible = Office.MsoTriState.msoTrue;
            highlightShape.Fill.ForeColor.RGB = laserColor;
            highlightShape.Fill.Transparency = 0.0f;
            highlightShape.Line.Visible = Office.MsoTriState.msoTrue;
            highlightShape.Line.ForeColor.RGB = laserBorder;
            highlightShape.Line.Transparency = 0.0f;
            highlightShape.Line.Weight = 1.5f;

            // Tag the shape for identification and cleanup
            highlightShape.Tags.Add(TagKey, "highlight");
            highlightShape.Tags.Add(TimestampTagKey, DateTime.UtcNow.Ticks.ToString());
            highlightShape.Tags.Add(DurationTagKey, request.DurationMs.ToString());

            highlightShape.ZOrder(Office.MsoZOrderCmd.msoBringToFront);

            _activeHighlights[request.Element.ElementId] = DateTime.UtcNow;
            _highlightShapeNames[request.Element.ElementId] = highlightShape.Name;

            Log.Debug("Added {MatchType} highlight on element {ElementId} at ({Left},{Top})",
                request.Type, request.Element.ElementId, request.Element.Left, request.Element.Top);

            return true;
        }
        catch (COMException ex)
        {
            Log.Warning(ex, "Failed to add highlight for element {ElementId}", request.Element.ElementId);
            return false;
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
                        var durationMs = _config.HighlightDurationMs;
                        var durationValue = shape.Tags[DurationTagKey];
                        if (!string.IsNullOrWhiteSpace(durationValue) && int.TryParse(durationValue, out var taggedDurationMs))
                            durationMs = taggedDurationMs;

                        if ((now - createdAt).TotalMilliseconds > durationMs)
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
