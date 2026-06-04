using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Animation;
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
        // Make the window click-through so we can still interact with PPT behind it
        var hwnd = new WindowInteropHelper(this).Handle;
        int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);

        // Default to virtual desktop; renderer can override to active slideshow screen.
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
    }

    public void SetOverlayBounds(double left, double top, double width, double height)
    {
        Dispatcher.Invoke(() =>
        {
            Left = left;
            Top = top;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
        });
    }

    /// <summary>
    /// Animates the laser dot in a circle around the element.
    /// Needs coordinate mapping: Slide Points -> Screen Pixels.
    /// </summary>
    public void AnimateLaserHighlight(SlideElement element, double slideWidthPoints, double slideHeightPoints, double presentationScreenW, double presentationScreenH, double offsetX, double offsetY)
    {
        Dispatcher.Invoke(() =>
        {
            try
            {
                _animCts?.Cancel();
                _animCts = new CancellationTokenSource();
                var ct = _animCts.Token;

                // Static attention dot at the center of the element.
                double dotX = element.Left + (element.Width / 2.0);
                double dotY = element.Top + (element.Height / 2.0);

                // Map PPT points to screen pixels
                double screenX = offsetX + dotX / slideWidthPoints * presentationScreenW;
                double screenY = offsetY + dotY / slideHeightPoints * presentationScreenH;

                // Convert absolute screen coordinates to overlay-canvas coordinates
                double canvasX = screenX - Left - LaserDot.Width / 2;
                double canvasY = screenY - Top - LaserDot.Height / 2;

                // Position dot and make visible
                Canvas.SetLeft(LaserDot, canvasX);
                Canvas.SetTop(LaserDot, canvasY);
                LaserDot.Visibility = Visibility.Visible;
                LaserDot.RenderTransform = null;
                LaserDot.BeginAnimation(Canvas.LeftProperty, null);
                LaserDot.BeginAnimation(Canvas.TopProperty, null);

                // Hide dot after highlight duration
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

    public void ClearHighlight()
    {
        Dispatcher.Invoke(() => 
        {
            _animCts?.Cancel();
            LaserDot.Visibility = Visibility.Collapsed; 
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _animCts?.Cancel();
        base.OnClosed(e);
    }
}
