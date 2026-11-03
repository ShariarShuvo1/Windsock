using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>The Alerts page: whether Windsock may interrupt, and for what.</summary>
public partial class AlertsPanelView : UserControl
{
    private readonly AlertsPanelViewModel _viewModel;

    public AlertsPanelView(AlertsPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;

        // The list marks alerts read only while it can be seen, and Windows'
        // own notification switch can change while the page is away.
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is true)
            {
                viewModel.Show();
            }
            else
            {
                viewModel.Hide();
            }
        };

        // A token is a secret: kept in a password box, which binds to nothing.
        // The view model lives exactly as long as this view (one window's
        // scope), so the subscription needs no undoing; undoing it on Unloaded
        // would lose it the first time another page was chosen.
        TokenBox.Password = viewModel.Forwarding.NtfyToken;
        viewModel.Forwarding.PropertyChanged += OnForwardingChanged;
    }

    /// <summary>A pressed alert opens what it was about: the Status page for an outage, Storage for a full drive.</summary>
    private void OnAlertClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: AlertRowViewModel { Opens: { Length: > 0 } section } }
            && Window.GetWindow(this) is MainWindow window)
        {
            window.ShowSection(section);
        }
    }

    private void OnTokenChanged(object sender, RoutedEventArgs e) =>
        _viewModel.Forwarding.SetToken(TokenBox.Password);

    private void OnForwardingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ForwardingViewModel.NtfyToken)
            && !string.Equals(TokenBox.Password, _viewModel.Forwarding.NtfyToken, StringComparison.Ordinal))
        {
            TokenBox.Password = _viewModel.Forwarding.NtfyToken;
        }
    }
}
