using System.Collections.Concurrent;
using System.Windows.Threading;
using PptPoc.Core.Configuration;
using PptPoc.Core.Interfaces;
using PptPoc.Core.Models;
using PptPoc.PowerPoint.Views;
using Serilog;
using Ppt = Microsoft.Office.Interop.PowerPoint;
using Office = Microsoft.Office.Core;

namespace PptPoc.PowerPoint;

/// <summary>
/// Highlights matched elements using a transparent WPF overlay window
/// with an animated red laser dot that circles the element.
/// Falls back to a freeform shape in edit mode.
/// </summary>
public class SlideshowLaserRenderer : IHighlightRenderer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SlideshowLaserRenderer>();

    private const string LaserTag = "PPTPOC_LASER";
    private const string TimestampTag = "PPTPOC_TS";

    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, string> _active = new();
    private LaserOverlayWindow? _overlay;
    private Dispatcher? _dispatcher;

    public SlideshowLaserRenderer(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Must be called from the WPF UI thread to capture the dispatcher
    /// and create the overlay window.
    /// </summary>
    public void EnsureOverlay()
    {
        _dispatcher = Dispatcher.CurrentDispatcher;
        if (_overlay == null)
        {
            _overlay = new LaserOverlayWindow(_config.HighlightDurationMs);
            _overlay.Show();
            Log.Information("WPF laser overlay window created");
        }
    }

    public void Highlight(HighlightRequest request, object slideComObject)
    {
        try
        {
            var slide = (Ppt.Slide)slideComObject;
            var app = slide.Application;
            bool inSlideshow = app.SlideShowWindows.Count > 0;

            // Skip if this element is already being circled
            if (_active.ContainsKey(request.Element.ElementId))
                return;

            _active.Clear();
            _active[request.Element.ElementId] = "circling";

            if (inSlideshow && _overlay != null)
            {
                // ── Slideshow: WPF laser pointer overlay ────────────
                float slideW = app.ActivePresentation.PageSetup.SlideWidth;
                float slideH = app.ActivePresentation.PageSetup.SlideHeight;

                // Compute aspect-ratio-aware render area
                double scrW = System.Windows.SystemParameters.PrimaryScreenWidth;
                double scrH = System.Windows.SystemParameters.PrimaryScreenHeight;
                double slideAspect = slideW / slideH;
                double screenAspect = scrW / scrH;
                double renderW, renderH, offX, offY;

                if (slideAspect >= screenAspect)
                { renderW = scrW; renderH = scrW / slideAspect; offX = 0; offY = (scrH - renderH) / 2; }
                else
                { renderH = scrH; renderW = scrH * slideAspect; offX = (scrW - renderW) / 2; offY = 0; }

                _overlay.AnimateLaserHighlight(
                    request.Element, slideW, slideH,
                    renderW, renderH, offX, offY);

                Log.Debug("WPF laser → {Element}", request.Element.ShapeName);
            }
            else
            {
                // ── Edit mode: static freeform shape fallback ───────
                RemoveAllLaserShapes(slide);
                DrawScribbleShape(slide, request.Element);
                Log.Debug("Shape circle → {Element}", request.Element.ShapeName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Laser highlight failed for {Id}", request.Element.ElementId);
        }
    }

    /// <summary>
    /// Edit-mode fallback: draws a static freeform shape circle.
    /// </summary>
    private static void DrawScribbleShape(Ppt.Slide slide, SlideElement element)
    {
        float cx = element.Left + element.Width / 2;
        float cy = element.Top + element.Height / 2;
        float pad = 10f;
        float rx = element.Width / 2 + pad;
        float ry = element.Height / 2 + pad;

        const double orbits = 1.5;
        const int totalPoints = 72;
        double totalAngle = orbits * 2 * Math.PI;
        double startAngle = -Math.PI / 2;

        float startX = cx + rx * (float)Math.Cos(startAngle);
        float startY = cy + ry * (float)Math.Sin(startAngle);

        var builder = slide.Shapes.BuildFreeform(
            Office.MsoEditingType.msoEditingAuto, startX, startY);

        for (int i = 1; i <= totalPoints; i++)
        {
            double angle = startAngle + totalAngle * i / totalPoints;
            float px = cx + rx * (float)Math.Cos(angle);
            float py = cy + ry * (float)Math.Sin(angle);
            builder.AddNodes(
                Office.MsoSegmentType.msoSegmentLine,
                Office.MsoEditingType.msoEditingAuto,
                px, py);
        }

        var scribble = builder.ConvertToShape();
        scribble.Fill.Visible = Office.MsoTriState.msoFalse;
        scribble.Line.Visible = Office.MsoTriState.msoTrue;
        scribble.Line.ForeColor.RGB = ColorToRgb(0xFF, 0x22, 0x22);
        scribble.Line.Weight = 3f;
        scribble.Line.Transparency = 0.1f;
        scribble.Tags.Add(LaserTag, "scribble");
        scribble.Tags.Add(TimestampTag, DateTime.UtcNow.Ticks.ToString());
        scribble.ZOrder(Office.MsoZOrderCmd.msoBringToFront);
    }

    public void ClearExpired(object? slideComObject)
    {
        if (slideComObject == null) return;
        var slide = (Ppt.Slide)slideComObject;
        var now = DateTime.UtcNow;

        try
        {
            var toDelete = new List<Ppt.Shape>();
            foreach (Ppt.Shape shape in slide.Shapes)
            {
                try
                {
                    if (string.IsNullOrEmpty(shape.Tags[LaserTag])) continue;
                    string ts = shape.Tags[TimestampTag];
                    if (string.IsNullOrEmpty(ts)) continue;
                    if (long.TryParse(ts, out long ticks))
                    {
                        var created = new DateTime(ticks, DateTimeKind.Utc);
                        if ((now - created).TotalMilliseconds > _config.HighlightDurationMs)
                            toDelete.Add(shape);
                    }
                }
                catch { }
            }

            foreach (var s in toDelete)
                try { s.Delete(); } catch { }

            if (toDelete.Count > 0)
            {
                _active.Clear();
                Log.Debug("Cleared {Count} expired laser shapes", toDelete.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClearExpired laser error");
        }
    }

    public void ClearAll(object? slideComObject)
    {
        _active.Clear();
        _overlay?.ClearHighlight();

        if (slideComObject != null)
        {
            var slide = (Ppt.Slide)slideComObject;
            RemoveAllLaserShapes(slide);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────

    private static void RemoveAllLaserShapes(Ppt.Slide slide)
    {
        try
        {
            var toDelete = new List<Ppt.Shape>();
            foreach (Ppt.Shape shape in slide.Shapes)
                try { if (!string.IsNullOrEmpty(shape.Tags[LaserTag])) toDelete.Add(shape); } catch { }
            foreach (var s in toDelete)
                try { s.Delete(); } catch { }
        }
        catch { }
    }

    private static int ColorToRgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public void Dispose()
    {
        _overlay?.Close();
    }
}
