using System.ComponentModel;
using System.Windows;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The first-run welcome, in a window of its own over the main one.
/// </summary>
public partial class WelcomeDialog : Window
{
    private readonly WelcomeViewModel _viewModel;
    private bool _finished;

    public WelcomeDialog(WelcomeViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        viewModel.Finished += OnFinished;

        // The way forward starts with the keyboard on it, so Enter walks
        // through the steps and Escape leaves them.
        Loaded += (_, _) => Forward.Focus();
    }

    /// <inheritdoc />
    protected override void OnClosing(CancelEventArgs e)
    {
        // Closed by its own button: the same as Skip, so it is not asked again.
        if (!_finished)
        {
            _finished = true;
            _viewModel.SkipCommand.Execute(null);
        }

        base.OnClosing(e);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Finished -= OnFinished;
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnFinished(object? sender, EventArgs e)
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        Close();
    }
}
