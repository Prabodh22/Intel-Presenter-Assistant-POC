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
/// Highlights matched elements using a transparent WPF overlay window.
/// <para>
/// Uses one visual language in slideshow mode: a laser dot at the center of the
/// selected target. OCR word matches still target the merged word box center.
/// </para>
/// Falls back to a COM freeform shape when not in slideshow mode (edit mode).
/// </summary>
public class SlideshowLaserRenderer : IHighlightRenderer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SlideshowLaserRenderer>();

    private const string LaserTag     = "PPTPOC_LASER";
    private const string TimestampTag = "PPTPOC_TS";
    private const string DurationTag  = "PPTPOC_DURATION_MS";

    private readonly AppConfig _config;
    private readonly ConcurrentDictionary<string, string> _active = new();
    private LaserOverlayWindow? _overlay;
    private Dispatcher? _dispatcher;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public SlideshowLaserRenderer(AppConfig config)
    {
        _config = config;
    }

    /// <summary>
    /// Must be called from the WPF UI thread to capture the dispatcher and create the overlay.
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

    public bool Highlight(HighlightRequest request, object slideComObject)
    {
        try
        {
            var slide = (Ppt.Slide)slideComObject;
            var app   = slide.Application;
            bool inSlideshow = app.SlideShowWindows.Count > 0;

            if (inSlideshow && IsNativePowerPointPointerActive(app))
            {
                _active.Clear();
                _overlay?.ClearHighlight();
                Log.Debug("Skipped app overlay highlight because native PowerPoint pointer is active");
                return false;
            }

            _active.Clear();
            _active[request.Element.ElementId] = "active";

            if (inSlideshow && _overlay != null)
            {
                // ── Slideshow: WPF overlay ────────────────────────────────────
                float slideW = app.ActivePresentation.PageSetup.SlideWidth;
                float slideH = app.ActivePresentation.PageSetup.SlideHeight;

                double hostLeft   = 0;
                double hostTop    = 0;
                double hostWidth  = System.Windows.SystemParameters.PrimaryScreenWidth;
                double hostHeight = System.Windows.SystemParameters.PrimaryScreenHeight;
                double dpiScale   = 1.0;

                try
                {
                    var slideShowWindow = app.SlideShowWindows[1];
                    IntPtr hwnd = new IntPtr(slideShowWindow.HWND);
                    if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out RECT rect))
                    {
                        try
                        {
                            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                            if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, 0, out uint dpiX, out _) == 0)
                                dpiScale = dpiX / 96.0;
                        }
                        catch { /* pre-Win8.1: keep dpiScale=1 */ }

                        hostLeft   = rect.Left   / dpiScale;
                        hostTop    = rect.Top    / dpiScale;
                        hostWidth  = Math.Max(1, (rect.Right  - rect.Left) / dpiScale);
                        hostHeight = Math.Max(1, (rect.Bottom - rect.Top)  / dpiScale);
                    }
                }
                catch { /* fallback to primary screen */ }

                _overlay.SetOverlayBounds(hostLeft, hostTop, hostWidth, hostHeight);

                // Aspect-ratio-aware render area
                double scrW       = hostWidth;
                double scrH       = hostHeight;
                double slideAspect  = slideW / slideH;
                double screenAspect = scrW   / scrH;
                double renderW, renderH, offX, offY;

                if (slideAspect >= screenAspect)
                { renderW = scrW; renderH = scrW / slideAspect; offX = 0;               offY = (scrH - renderH) / 2; }
                else
                { renderH = scrH; renderW = scrH * slideAspect; offX = (scrW - renderW) / 2; offY = 0; }

                Log.Information(
                    "Laser dot highlight: shape='{Shape}' type={Type} conf={Conf:F2} " +
                    "L={L:F0} T={T:F0} W={W:F0} H={H:F0} | " +
                    "render={RW:F0}x{RH:F0} off={OX:F0},{OY:F0} dpi={DPI:F2}",
                    request.Element.ShapeName,
                    request.Type,
                    request.Confidence,
                    request.Element.Left, request.Element.Top,
                    request.Element.Width, request.Element.Height,
                    renderW, renderH, offX, offY, dpiScale);

                _overlay.AnimateLaserHighlight(
                    request.Element, slideW, slideH,
                    renderW, renderH, offX, offY,
                    request.DurationMs);
            }
            else
            {
                // ── Edit mode: static freeform shape fallback ────────────────
                RemoveAllLaserShapes(slide);
                DrawScribbleShape(slide, request.Element, request.DurationMs);
                Log.Debug("Edit-mode shape highlight → {Element}", request.Element.ShapeName);
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Laser highlight failed for {Id}", request.Element.ElementId);
            return false;
        }
    }

    private static bool IsNativePowerPointPointerActive(Ppt.Application app)
    {
        try
        {
            if (app.SlideShowWindows.Count <= 0)
                return false;

            var pointerType = app.SlideShowWindows[1].View.PointerType;

            // Only treat the native pointer as "active" when it's an interactive
            // drawing/pen/eraser tool. Previously we suppressed overlay for any
            // non-arrow pointer which blocked the WPF overlay in many presenter
            // setups (e.g., when presenter tools report other pointer types).
            return pointerType == Ppt.PpSlideShowPointerType.ppSlideShowPointerPen
                   || pointerType == Ppt.PpSlideShowPointerType.ppSlideShowPointerEraser;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Edit-mode fallback: draws a small red dot at the element center.
    /// When request.Element is an OCR proxy, the dot lands on the matched word(s)
    /// rather than the whole image.
    /// </summary>
    private static void DrawScribbleShape(Ppt.Slide slide, SlideElement element, int durationMs)
    {
        const float dotSize = 10f;
        float centerX = element.Left + element.Width / 2f;
        float centerY = element.Top + element.Height / 2f;

        var scribble = slide.Shapes.AddShape(
            Office.MsoAutoShapeType.msoShapeOval,
            centerX - dotSize / 2f,
            centerY - dotSize / 2f,
            dotSize,
            dotSize);

        scribble.Fill.Visible = Office.MsoTriState.msoTrue;
        scribble.Fill.ForeColor.RGB = ColorToRgb(0xFF, 0x11, 0x11);
        scribble.Fill.Transparency = 0.0f;
        scribble.Line.Visible = Office.MsoTriState.msoTrue;
        scribble.Line.ForeColor.RGB = ColorToRgb(0xFF, 0x66, 0x66);
        scribble.Line.Weight = 1.5f;
        scribble.Tags.Add(LaserTag, "laser-dot");
        scribble.Tags.Add(TimestampTag, DateTime.UtcNow.Ticks.ToString());
        scribble.Tags.Add(DurationTag, durationMs.ToString());
        scribble.ZOrder(Office.MsoZOrderCmd.msoBringToFront);
    }

    public void ClearExpired(object? slideComObject)
    {
        if (slideComObject == null) return;
        var slide = (Ppt.Slide)slideComObject;
        var now   = DateTime.UtcNow;

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
                        var durationMs = _config.HighlightDurationMs;
                        var durationValue = shape.Tags[DurationTag];
                        if (!string.IsNullOrWhiteSpace(durationValue) && int.TryParse(durationValue, out var taggedDurationMs))
                            durationMs = taggedDurationMs;

                        if ((now - created).TotalMilliseconds > durationMs)
                            toDelete.Add(shape);
                    }
                }
                catch { }
            }

            foreach (var s in toDelete) try { s.Delete(); } catch { }

            if (toDelete.Count > 0)
            {
                _active.Clear();
                Log.Debug("Cleared {Count} expired laser shapes", toDelete.Count);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ClearExpired error");
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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void RemoveAllLaserShapes(Ppt.Slide slide)
    {
        try
        {
            var toDelete = new List<Ppt.Shape>();
            foreach (Ppt.Shape shape in slide.Shapes)
                try { if (!string.IsNullOrEmpty(shape.Tags[LaserTag])) toDelete.Add(shape); } catch { }
            foreach (var s in toDelete) try { s.Delete(); } catch { }
        }
        catch { }
    }

    private static int ColorToRgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public void Dispose()
    {
        _overlay?.Close();
    }
}
