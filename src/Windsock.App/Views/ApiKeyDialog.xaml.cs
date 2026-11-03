using System.Windows;
using Windsock.Core.Security;

namespace Windsock.App.Views;

/// <summary>Takes the person's own VirusTotal key.</summary>
public partial class ApiKeyDialog : Window
{
    private string? _key;

    private ApiKeyDialog() => InitializeComponent();

    /// <summary>The key typed, or null when the person cancelled.</summary>
    public static string? Ask(Window? owner)
    {
        ApiKeyDialog dialog = new() { Owner = owner is { IsVisible: true } ? owner : null };
        dialog.ShowDialog();
        return dialog._key;
    }

    // A VirusTotal key is 64 hexadecimal characters; nothing else is kept.
    private void OnTyped(object sender, RoutedEventArgs e) =>
        Save.IsEnabled = ApiKeyVault.IsWellFormed(Key.Password);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _key = Key.Password.Trim();
        DialogResult = true;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _key = null;
        DialogResult = false;
    }
}
