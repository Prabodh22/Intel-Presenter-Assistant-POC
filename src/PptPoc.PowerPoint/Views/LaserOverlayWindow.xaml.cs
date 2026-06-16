using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using PptPoc.Core.Models;
using Serilog;
using Point = System.Windows.Point;

namespace PptPoc.PowerPoint.Views;

public partial class LaserOverlayWindow : Window
{
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int GWL_EXSTYLE = -20;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    private CancellationTokenSource? _animCts;
    private readonly int _durationMs;

    public LaserOverlayWindow(int durationMs)
    {
        InitializeComponent();
        _durationMs = durationMs;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);

        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public void SetOverlayBounds(double left, double top, double width, double height)
    {
        Dispatcher.Invoke(() =>
        {
            Left   = left;
            Top    = top;
            Width  = Math.Max(1, width);
            Height = Math.Max(1, height);
        });
    }

    // ── ① Laser dot (whole-image / fallback) ─────────────────────────────────

    /// <summary>
    /// Animates the laser dot at the centre of <paramref name="element"/>.
    /// Call this when there are no OCR word bboxes or confidence is too low for
    /// a word-level rect highlight.
    /// </summary>
    public void AnimateLaserHighlight(
        SlideElement element,
        double slideWidthPoints, double slideHeightPoints,
        double presentationScreenW, double presentationScreenH,
        double offsetX, double offsetY)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _animCts?.Cancel();
                _animCts = new CancellationTokenSource();
                var ct = _animCts.Token;

                // Hide OCR rect if previously showing
                OcrHighlightRect.BeginAnimation(OpacityProperty, null);
                OcrHighlightRect.Visibility = Visibility.Collapsed;
                OcrHighlightRect.Opacity    = 1.0;

                double dotX = element.Left + (element.Width  / 2.0);
                double dotY = element.Top  + (element.Height / 2.0);

                double screenX = offsetX + dotX / slideWidthPoints  * presentationScreenW;
                double screenY = offsetY + dotY / slideHeightPoints * presentationScreenH;

                double canvasX = screenX - LaserDot.Width  / 2;
                double canvasY = screenY - LaserDot.Height / 2;

                Canvas.SetLeft(LaserDot, canvasX);
                Canvas.SetTop(LaserDot,  canvasY);
                LaserDot.Visibility    = Visibility.Visible;
                LaserDot.RenderTransform = null;
                LaserDot.BeginAnimation(Canvas.LeftProperty, null);
                LaserDot.BeginAnimation(Canvas.TopProperty,  null);

                Task.Delay(_durationMs, ct).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Dispatcher.Invoke(() =>
                    {
                        LaserDot.BeginAnimation(Canvas.LeftProperty, null);
                        LaserDot.Visibility = Visibility.Collapsed;
                    });
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to run WPF laser animation");
            }
        });
    }

    // ── ② OCR word-level rect highlight ──────────────────────────────────────

    /// <summary>
    /// Draws an animated bounding rectangle around the matched OCR word region.
    /// <para>
    /// <paramref name="element"/> is the MERGED proxy element whose Left/Top/Width/Height
    /// are already absolute slide-point coordinates of the matched word bbox.
    /// </para>
    /// Visual behaviour:
    /// <list type="bullet">
    ///   <item>Confidence ≥ 0.75 → deep-sky-blue solid border (certain match)</item>
    ///   <item>0.50 ≤ confidence &lt; 0.75 → orange dashed border (probable match)</item>
    /// </list>
    /// Animates: expand-in over 200 ms → hold → fade-out over 300 ms.
    /// </summary>
    public void AnimateOcrHighlight(
        SlideElement element,
        double slideWidthPoints, double slideHeightPoints,
        double presentationScreenW, double presentationScreenH,
        double offsetX, double offsetY,
        double confidence)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _animCts?.Cancel();
                _animCts = new CancellationTokenSource();
                var ct = _animCts.Token;

                // Hide laser dot if previously showing
                LaserDot.BeginAnimation(Canvas.LeftProperty, null);
                LaserDot.Visibility = Visibility.Collapsed;

                // ── Map slide-point rect → overlay canvas pixels ──────────────
                const double padding = 5.0;

                double screenLeft = offsetX + element.Left                    / slideWidthPoints  * presentationScreenW;
                double screenTop  = offsetY + element.Top                     / slideHeightPoints * presentationScreenH;
                double screenW    =           element.Width                   / slideWidthPoints  * presentationScreenW;
                double screenH    =           element.Height                  / slideHeightPoints * presentationScreenH;

                double rectW = Math.Max(24, screenW) + padding * 2;
                double rectH = Math.Max(14, screenH) + padding * 2;

                Canvas.SetLeft(OcrHighlightRect, screenLeft - padding);
                Canvas.SetTop(OcrHighlightRect,  screenTop  - padding);
                OcrHighlightRect.Width  = rectW;
                OcrHighlightRect.Height = rectH;

                // ── Colour + style based on confidence ───────────────────────
                Color borderColor;
                if (confidence >= 0.75)
                {
                    // Solid deep-sky-blue: high-confidence exact OCR word match
                    borderColor = Color.FromRgb(0x00, 0xBF, 0xFF);
                    OcrHighlightRect.StrokeThickness = 3;
                    OcrHighlightRect.StrokeDashArray = null;
                }
                else
                {
                    // Dashed orange: medium-confidence probable match
                    borderColor = Color.FromRgb(0xFF, 0xA5, 0x00);
                    OcrHighlightRect.StrokeThickness = 2;
                    OcrHighlightRect.StrokeDashArray = new DoubleCollection { 5.0, 3.5 };
                }

                OcrHighlightRect.Stroke = new SolidColorBrush(borderColor);

                // Update the glow to match
                if (OcrHighlightRect.Effect is DropShadowEffect glow)
                {
                    glow.Color   = borderColor;
                    glow.Opacity = confidence >= 0.75 ? 0.9 : 0.6;
                }

                // ── Expand-in animation (scale from 10 % → 100 % in 200 ms) ─
                OcrHighlightRect.RenderTransformOrigin = new Point(0.5, 0.5);
                OcrHighlightRect.Opacity = 1.0;
                OcrHighlightRect.BeginAnimation(OpacityProperty, null);

                var scale = new ScaleTransform(0.1, 0.1);
                OcrHighlightRect.RenderTransform = scale;
                OcrHighlightRect.Visibility = Visibility.Visible;

                var growX = new DoubleAnimation(0.1, 1.0, new Duration(TimeSpan.FromMilliseconds(200)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                var growY = new DoubleAnimation(0.1, 1.0, new Duration(TimeSpan.FromMilliseconds(200)))
                    { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, growX);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, growY);

                // ── Fade-out after hold ───────────────────────────────────────
                Task.Delay(_durationMs, ct).ContinueWith(t =>
                {
                    if (t.IsCanceled) return;
                    Dispatcher.Invoke(() =>
                    {
                        var fadeOut = new DoubleAnimation(1.0, 0.0, new Duration(TimeSpan.FromMilliseconds(300)));
                        fadeOut.Completed += (_, _) =>
                        {
                            OcrHighlightRect.Visibility = Visibility.Collapsed;
                            OcrHighlightRect.Opacity    = 1.0;
                        };
                        OcrHighlightRect.BeginAnimation(OpacityProperty, fadeOut);
                    });
                });
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to run OCR highlight animation");
            }
        });
    }

    // ── Shared clear ─────────────────────────────────────────────────────────

    public void ClearHighlight()
    {
        Dispatcher.Invoke(() =>
        {
            _animCts?.Cancel();

            LaserDot.BeginAnimation(Canvas.LeftProperty, null);
            LaserDot.Visibility = Visibility.Collapsed;

            OcrHighlightRect.BeginAnimation(OpacityProperty, null);
            OcrHighlightRect.Visibility = Visibility.Collapsed;
            OcrHighlightRect.Opacity    = 1.0;
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _animCts?.Cancel();
        base.OnClosed(e);
    }
}
