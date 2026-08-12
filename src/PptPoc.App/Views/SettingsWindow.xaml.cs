using System.Windows;
using System.Windows.Controls;

namespace PptPoc.App.Views;

public partial class SettingsWindow : Window
{
    public string GnaiToken => txtApiKey.Password;
    public string GlobalHotkey => txtHotkey.Text;
    public string ModelPath => txtModelPath.Text;

    public SettingsWindow(string currentToken, string currentHotkey, string currentModelPath)
    {
        InitializeComponent();
        txtApiKey.Password = currentToken;
        txtHotkey.Text = currentHotkey;
        txtModelPath.Text = currentModelPath;
    }

    private void btnBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Select the Parakeet-TDT OpenVINO Model Directory",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            txtModelPath.Text = dialog.SelectedPath;
        }
    }

    private void btnSave_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void btnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}