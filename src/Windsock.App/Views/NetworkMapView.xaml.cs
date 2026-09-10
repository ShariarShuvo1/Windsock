using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Windsock.App.Controls;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The whole machine's network, as a piece of a window.
/// </summary>
public partial class NetworkMapView : UserControl
{
    private NetworkMapViewModel? _viewModel;
    private Spotlight? _filling;
    private bool _fitting;

    public NetworkMapView()
    {
        InitializeComponent();

        Graph.HoverChanged += (_, hovered) => _viewModel?.Highlight(hovered.Item);
        Graph.Activated += (_, chosen) => _viewModel?.Choose(chosen.Item);

        // Clicking the map itself rather than anything on it stops following.
        Viewport.Cleared += (_, _) => _viewModel?.Choose(null);
        FitButton.Click += (_, _) =>
        {
            Viewport.FitOrReturn();
            Keyboard.ClearFocus();
        };
        Viewport.ViewChanged += (_, _) => Graph.Scale = Viewport.Zoom;

        DataContextChanged += OnSubjectChanged;
        PreviewKeyDown += (_, key) =>
        {
            if (key.Key != Key.Escape)
            {
                return;
            }

            if (_viewModel is { IsSettingsOpen: true } open)
            {
                open.IsSettingsOpen = false;
                key.Handled = true;
                return;
            }

            if (_viewModel is { IsFullScreen: true } filling)
            {
                filling.IsFullScreen = false;
                key.Handled = true;
            }
        };
        Unloaded += (_, _) =>
        {
            if (_viewModel is { IsFullScreen: true } filling)
            {
                filling.IsFullScreen = false;
            }

            Release();
        };
    }

    private void ApplyFullScreen()
    {
        if (_viewModel?.IsFullScreen != true)
        {
            Release();
            return;
        }

        _filling ??= Spotlight.Give(this);
        Viewport.Focus();
    }

    private void Release()
    {
        _filling?.Dispose();
        _filling = null;
    }

    private void OnSubjectChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is { } was)
        {
            was.PropertyChanged -= OnViewModelChanged;
        }

        Release();

        _viewModel = e.NewValue as NetworkMapViewModel;
        _fitting = false;

        if (_viewModel is { } now)
        {
            now.PropertyChanged += OnViewModelChanged;
        }
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (e.PropertyName == nameof(NetworkMapViewModel.IsFullScreen))
        {
            ApplyFullScreen();
            return;
        }

        if (e.PropertyName != nameof(NetworkMapViewModel.Graph)
            || _fitting
            || Viewport.IsMoved
            || _viewModel?.Graph is not { Children.Count: > 0 })
        {
            return;
        }

        _fitting = true;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _fitting = false;

                if (!Viewport.IsMoved)
                {
                    Viewport.Fit();
                }
            }));
    }
}
