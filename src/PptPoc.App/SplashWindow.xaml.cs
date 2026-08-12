using System.Windows;
using System.Windows.Threading;

namespace PptPoc.App;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
    }

    public void UpdateProgress(double percent, string message)
    {
        Dispatcher.Invoke(() =>
        {
            DownloadProgressBar.Value = percent;
            StatusText.Text = message;
        });
    }
}