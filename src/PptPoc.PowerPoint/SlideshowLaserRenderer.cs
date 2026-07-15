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
/// Two visual modes:
/// <list type="bullet">
///   <item><b>OCR word-level rect</b> – used when <see cref="HighlightRequest.MatchedOcrWords"/>
///   is non-null and confidence ≥ 0.5.  Draws an animated rectangle over exactly the
///   matched OCR words inside the image.</item>
///   <item><b>Laser dot</b> – fallback for whole-image matches, text elements, and
///   low-confidence image matches.</item>
/// </list>
/// </para>
/// Falls back to a COM freeform shape when not in slideshow mode (edit mode).
/// </summary>
public class SlideshowLaserRenderer : IHighlightRenderer
{
    private static readonly ILogger Log = Serilog.Log.ForContext<SlideshowLaserRenderer>();

    private const string LaserTag     = "PPTPOC_LASER";
    private const string TimestampTag = "PPTPOC_TS";

    // Minimum confidence required to show a word-level OCR highlight instead of
    // falling back to the whole-image dot.
    private const double OcrRectMinConfidence = 0.50;

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

    public void Highlight(HighlightRequest request, object slideComObject)
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
                return;
            }

            if (_active.ContainsKey(request.Element.ElementId))
                return;

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

                bool useOcrRect = request.MatchedOcrWords != null
                                  && request.MatchedOcrWords.Count > 0
                                  && request.Confidence >= OcrRectMinConfidence;

                if (useOcrRect)
                {
                    // Word-level bounding rectangle — request.Element is already the merged bbox proxy
                    Log.Information(
                        "OCR rect highlight: shape='{Shape}' words={Words} conf={Conf:F2} " +
                        "L={L:F0} T={T:F0} W={W:F0} H={H:F0} | " +
                        "render={RW:F0}x{RH:F0} off={OX:F0},{OY:F0}",
                        request.Element.ShapeName,
                        request.MatchedOcrWords!.Count,
                        request.Confidence,
                        request.Element.Left, request.Element.Top,
                        request.Element.Width, request.Element.Height,
                        renderW, renderH, offX, offY);

                    _overlay.AnimateOcrHighlight(
                        request.Element, slideW, slideH,
                        renderW, renderH, offX, offY,
                        request.Confidence);
                }
                else
                {
                    // Whole-image / text fallback dot
                    Log.Information(
                        "Laser dot highlight: shape='{Shape}' conf={Conf:F2} " +
                        "L={L:F0} T={T:F0} W={W:F0} H={H:F0} | " +
                        "render={RW:F0}x{RH:F0} off={OX:F0},{OY:F0} dpi={DPI:F2}",
                        request.Element.ShapeName,
                        request.Confidence,
                        request.Element.Left, request.Element.Top,
                        request.Element.Width, request.Element.Height,
                        renderW, renderH, offX, offY, dpiScale);

                    _overlay.AnimateLaserHighlight(
                        request.Element, slideW, slideH,
                        renderW, renderH, offX, offY);
                }
            }
            else
            {
                // ── Edit mode: static freeform shape fallback ────────────────
                RemoveAllLaserShapes(slide);
                DrawScribbleShape(slide, request.Element);
                Log.Debug("Edit-mode shape highlight → {Element}", request.Element.ShapeName);
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Laser highlight failed for {Id}", request.Element.ElementId);
        }
    }

    private static bool IsNativePowerPointPointerActive(Ppt.Application app)
    {
        try
        {
            if (app.SlideShowWindows.Count <= 0)
                return false;

            var pointerType = app.SlideShowWindows[1].View.PointerType;

            return pointerType != Ppt.PpSlideShowPointerType.ppSlideShowPointerNone
                   && pointerType != Ppt.PpSlideShowPointerType.ppSlideShowPointerArrow;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Edit-mode fallback: draws a horizontal underline below the element.
    /// When request.Element is an OCR proxy, this underline sits under the
    /// matched word(s) rather than the whole image.
    /// </summary>
    private static void DrawScribbleShape(Ppt.Slide slide, SlideElement element)
    {
        float pad    = 5f;
        float lineY  = element.Top + element.Height + pad;
        float lineL  = element.Left - pad;
        float lineR  = element.Left + element.Width + pad;

        var builder  = slide.Shapes.BuildFreeform(
            Office.MsoEditingType.msoEditingAuto, lineL, lineY);

        builder.AddNodes(
            Office.MsoSegmentType.msoSegmentLine,
            Office.MsoEditingType.msoEditingAuto, lineR, lineY);

        var scribble = builder.ConvertToShape();
        scribble.Fill.Visible = Office.MsoTriState.msoFalse;
        scribble.Line.Visible = Office.MsoTriState.msoTrue;
        scribble.Line.ForeColor.RGB  = ColorToRgb(0xFF, 0x22, 0x22);
        scribble.Line.Weight         = 3f;
        scribble.Line.Transparency   = 0.1f;
        scribble.Tags.Add(LaserTag,     "scribble");
        scribble.Tags.Add(TimestampTag, DateTime.UtcNow.Ticks.ToString());
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
                        if ((now - created).TotalMilliseconds > _config.HighlightDurationMs)
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
