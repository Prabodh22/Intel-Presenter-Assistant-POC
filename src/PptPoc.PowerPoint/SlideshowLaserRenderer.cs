using System.Collections.Concurrent;
using System.Runtime.InteropServices;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

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

                double hostLeft = 0;
                double hostTop = 0;
                double hostWidth = System.Windows.SystemParameters.PrimaryScreenWidth;
                double hostHeight = System.Windows.SystemParameters.PrimaryScreenHeight;
                double dpiScale = 1.0;

                // Anchor overlay to the actual slideshow window monitor (external display safe)
                try
                {
                    var slideShowWindow = app.SlideShowWindows[1];
                    IntPtr hwnd = new IntPtr(slideShowWindow.HWND);
                    if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
                    {
                        // GetWindowRect returns physical pixels; WPF needs DIPs.
                        // Determine the DPI of the monitor the slideshow is on.
                        try
                        {
                            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                            if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0)
                                dpiScale = dpiX / 96.0;
                        }
                        catch { /* pre-Win8.1 fallback: keep dpiScale=1 */ }

                        hostLeft   = rect.Left   / dpiScale;
                        hostTop    = rect.Top    / dpiScale;
                        hostWidth  = Math.Max(1, (rect.Right - rect.Left) / dpiScale);
                        hostHeight = Math.Max(1, (rect.Bottom - rect.Top) / dpiScale);
                    }
                }
                catch
                {
                    // Fallback to primary screen metrics if slideshow window bounds are unavailable.
                }

                _overlay.SetOverlayBounds(hostLeft, hostTop, hostWidth, hostHeight);

                // Compute aspect-ratio-aware render area
                double scrW = hostWidth;
                double scrH = hostHeight;
                double slideAspect = slideW / slideH;
                double screenAspect = scrW / scrH;
                double renderW, renderH, offX, offY;

                if (slideAspect >= screenAspect)
                { renderW = scrW; renderH = scrW / slideAspect; offX = 0; offY = (scrH - renderH) / 2; }
                else
                { renderH = scrH; renderW = scrH * slideAspect; offX = (scrW - renderW) / 2; offY = 0; }

                Log.Information("Slideshow laser: element='{Shape}' L={EL:F0} T={ET:F0} W={EW:F0} H={EH:F0} | " +
                    "slideW={SW:F0} slideH={SH:F0} | host={HL:F0},{HT:F0} {HW:F0}x{HH:F0} | " +
                    "render={RW:F0}x{RH:F0} off={OX:F0},{OY:F0} | dpi={DPI:F2}",
                    request.Element.ShapeName,
                    request.Element.Left, request.Element.Top, request.Element.Width, request.Element.Height,
                    slideW, slideH, hostLeft, hostTop, hostWidth, hostHeight,
                    renderW, renderH, offX, offY, dpiScale);

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
        // Horizontal underline just below the element
        float pad = 5f;
        float lineY = element.Top + element.Height + pad;
        float lineLeft = element.Left - pad;
        float lineRight = element.Left + element.Width + pad;

        var builder = slide.Shapes.BuildFreeform(
            Office.MsoEditingType.msoEditingAuto, lineLeft, lineY);

        // Single horizontal line
        builder.AddNodes(Office.MsoSegmentType.msoSegmentLine,
            Office.MsoEditingType.msoEditingAuto, lineRight, lineY);

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
