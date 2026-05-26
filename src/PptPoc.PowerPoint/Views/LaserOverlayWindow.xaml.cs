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

        // Make window cover the primary screen
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
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

                // Center of element in PPT points
                double cx = element.Left + element.Width / 2;
                double cy = element.Top + element.Height / 2;

                // Radius of orbit in PPT points (add padding)
                double rx = element.Width / 2 + 10;
                double ry = element.Height / 2 + 10;

                LaserDot.Visibility = Visibility.Visible;

                // Create a PathGeometry for the animation
                var path = new System.Windows.Media.PathGeometry();
                var figure = new System.Windows.Media.PathFigure();
                
                // Start angle at top (-90 degrees)
                double startPx = offsetX + (cx + rx * Math.Cos(-Math.PI / 2)) / slideWidthPoints * presentationScreenW;
                double startPy = offsetY + (cy + ry * Math.Sin(-Math.PI / 2)) / slideHeightPoints * presentationScreenH;
                figure.StartPoint = new Point(startPx, startPy);

                // Build a polyline roughly approximating 1.5 orbits
                var poly = new System.Windows.Media.PolyLineSegment();
                
                double orbits = 1.5;
                int segments = 60;
                for (int i = 1; i <= segments * orbits; i++)
                {
                    double angle = -Math.PI / 2 + (i * Math.PI * 2 / segments);
                    double px = offsetX + (cx + rx * Math.Cos(angle)) / slideWidthPoints * presentationScreenW;
                    double py = offsetY + (cy + ry * Math.Sin(angle)) / slideHeightPoints * presentationScreenH;
                    poly.Points.Add(new Point(px, py));
                }
                
                figure.Segments.Add(poly);
                path.Figures.Add(figure);

                // Set up animation
                var animX = new DoubleAnimationUsingPath
                {
                    PathGeometry = path,
                    Source = PathAnimationSource.X,
                    Duration = TimeSpan.FromMilliseconds(1500)
                };
                var animY = new DoubleAnimationUsingPath
                {
                    PathGeometry = path,
                    Source = PathAnimationSource.Y,
                    Duration = TimeSpan.FromMilliseconds(1500)
                };

                // Center the ellipse on the path points
                animX.FillBehavior = FillBehavior.HoldEnd;
                animY.FillBehavior = FillBehavior.HoldEnd;

                // Offset the path by the ellipse radius to actually center it
                var tf = new System.Windows.Media.TranslateTransform(-LaserDot.Width/2, -LaserDot.Height/2);
                LaserDot.RenderTransform = tf;

                // Apply animations directly to avoid Storyboard NameScope issues
                LaserDot.BeginAnimation(Canvas.LeftProperty, animX);
                LaserDot.BeginAnimation(Canvas.TopProperty, animY);

                // Hide dot after duration
                Task.Delay(_durationMs, ct).ContinueWith(t => 
                {
                    if (t.IsCanceled) return;
                    Dispatcher.Invoke(() => LaserDot.Visibility = Visibility.Collapsed);
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
