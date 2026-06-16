using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace PptPoc.App;

public partial class StatusIndicatorWindow : Window
{
    private const int HOTKEY_ID = 9000;
    public event Action? ToggleLaserRequested;

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    public StatusIndicatorWindow(string hotkeyConfig)
    {
        InitializeComponent();
        
        // Position at bottom right corner
        var screen = System.Windows.SystemParameters.PrimaryScreenHeight;
        var screenW = System.Windows.SystemParameters.PrimaryScreenWidth;
        this.Left = screenW - 60;
        this.Top = screen - 100;
        this.Loaded += (s, e) => {
            // Keep window click-through

            // Register HotKey
            var helper = new WindowInteropHelper(this);
            // Default Ctrl+Shift+L
            uint modifiers = 0x0002 | 0x0004; // Control | Shift
            uint key = (uint)KeyInterop.VirtualKeyFromKey(Key.L);
            
            // Try very rudimenary parsing of hotkeyConfig if needed, but keeping it fixed internally is safer for this snippet
            if (hotkeyConfig.Contains("Ctrl") && hotkeyConfig.Contains("Shift") && hotkeyConfig.Contains("L")) {
                RegisterHotKey(helper.Handle, HOTKEY_ID, modifiers, key);
                HwndSource source = HwndSource.FromHwnd(helper.Handle);
                source?.AddHook(HwndHook);
            }
        };
    }

    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_HOTKEY = 0x0312;
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            ToggleLaserRequested?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        UnregisterHotKey(helper.Handle, HOTKEY_ID);
        base.OnClosed(e);
    }

    public void UpdateStatus(string state)
    {
        Dispatcher.Invoke(() =>
        {
            switch (state)
            {
                case "Listening":
                    StatusDot.Fill = System.Windows.Media.Brushes.Cyan;
                    StatusDot.Stroke = System.Windows.Media.Brushes.DarkCyan;
                    break;
                case "Laser Enabled":
                    StatusDot.Fill = System.Windows.Media.Brushes.LimeGreen;
                    StatusDot.Stroke = System.Windows.Media.Brushes.DarkGreen;
                    break;
                case "Laser Disabled":
                    StatusDot.Fill = System.Windows.Media.Brushes.Red;
                    StatusDot.Stroke = System.Windows.Media.Brushes.DarkRed;
                    break;
                case "Building KB":
                    StatusDot.Fill = System.Windows.Media.Brushes.Yellow;
                    StatusDot.Stroke = System.Windows.Media.Brushes.Goldenrod;
                    break;
                default:
                    StatusDot.Fill = System.Windows.Media.Brushes.Gray;
                    StatusDot.Stroke = System.Windows.Media.Brushes.DarkGray;
                    break;
            }
        });
    }
}