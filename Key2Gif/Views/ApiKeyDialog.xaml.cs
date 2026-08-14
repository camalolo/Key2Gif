using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Key2Gif.Services;

namespace Key2Gif.Views;

public partial class ApiKeyDialog : Window
{
    public string ApiKey { get; private set; } = "";

    public ApiKeyDialog()
    {
        InitializeComponent();
        KeyInput.Focus();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var key = KeyInput.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            KeyInput.Focus();
            return;
        }
        ApiKey = key;
        DialogResult = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnLinkClick(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to open link: {ex.Message}", ex);
        }
    }
}
