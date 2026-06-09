using System.Windows;
using System;

namespace PptPoc.App;

public partial class TokenInputDialog : Window
{
    public string ApiKey { get; private set; } = string.Empty;

    public TokenInputDialog()
    {
        InitializeComponent();
        TokenPasswordBox.Focus();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = TokenPasswordBox.Password?.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            ApiKey = key;
            DialogResult = true;
            Environment.SetEnvironmentVariable("GNAI_TOKEN", key, EnvironmentVariableTarget.Process);
            try
            {
                Environment.SetEnvironmentVariable("GNAI_TOKEN", key, EnvironmentVariableTarget.User);
            }
            catch { /* Ignore registry failure if not admin */ }
            Close();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}