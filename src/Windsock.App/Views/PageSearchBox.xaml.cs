using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>
/// The search box over the sidebar, and the results that open under it.
/// </summary>
/// <remarks>
/// Below <see cref="CompactWidth"/> the sidebar shows icons only and the box
/// becomes a button; its box then comes up in the results flyout itself, beside
/// the sidebar. The flyout is a popup that stays open on its own terms - it
/// closes when the box loses focus, on Escape, or once a result is chosen - so a
/// click into the box is never taken for a click away from it.
/// </remarks>
public partial class PageSearchBox : UserControl
{
    /// <summary>Narrower than this, there is no room for the box.</summary>
    public const double CompactWidth = 120;

    private bool _compact;

    public PageSearchBox()
    {
        InitializeComponent();
        SizeChanged += (_, _) => Follow();
    }

    /// <summary>Raised when Escape closes the search with nothing chosen, so the window can put focus back.</summary>
    public event EventHandler? Dismissed;

    private PageSearchViewModel? ViewModel => DataContext as PageSearchViewModel;

    /// <summary>The box the caret belongs in, the inline one or the one in the flyout.</summary>
    private TextBox Box => _compact ? InPopup : Inline;

    /// <summary>Puts the caret in the box, opening the flyout first when the sidebar is folded.</summary>
    public void Begin()
    {
        if (_compact)
        {
            Results.Placement = PlacementMode.Right;
            Results.HorizontalOffset = 8;
            Results.VerticalOffset = 0;
            Results.IsOpen = true;
        }

        TextBox box = Box;

        // The flyout's box exists only once the popup has opened; focus it then.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            box.Focus();
            box.SelectAll();
        }));
    }

    /// <summary>Closes the results.</summary>
    public void Close() => Results.IsOpen = false;

    private void Follow()
    {
        bool compact = ActualWidth > 0 && ActualWidth < CompactWidth;

        if (compact == _compact)
        {
            return;
        }

        _compact = compact;
        Inline.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        InPopup.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;

        Results.Placement = compact ? PlacementMode.Right : PlacementMode.Bottom;
        Results.HorizontalOffset = compact ? 8 : 0;
        Results.VerticalOffset = compact ? 0 : 6;
        Close();
    }

    private void OnCompactClick(object sender, RoutedEventArgs e) => Begin();

    private void OnQueryChanged(object sender, TextChangedEventArgs e)
    {
        // Inline, the results open as soon as there is something to look for.
        if (!_compact)
        {
            Results.IsOpen = Inline.IsKeyboardFocusWithin && !string.IsNullOrWhiteSpace(Inline.Text);
        }
    }

    private void OnBoxKeyDown(object sender, KeyEventArgs e)
    {
        if (ViewModel is not { } search)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                search.Move(1);
                Reveal();
                e.Handled = true;
                break;

            case Key.Up:
                search.Move(-1);
                Reveal();
                e.Handled = true;
                break;

            case Key.Enter:
                if (search.Choose())
                {
                    Close();
                }

                e.Handled = true;
                break;

            case Key.Escape:
                if (search.HasQuery)
                {
                    search.Query = string.Empty;

                    if (!_compact)
                    {
                        Close();
                    }
                }
                else
                {
                    Close();
                    Dismissed?.Invoke(this, EventArgs.Empty);
                }

                e.Handled = true;
                break;
        }
    }

    /// <summary>Keeps the highlighted result in view in a long list.</summary>
    private void Reveal()
    {
        if (List.SelectedItem is { } item)
        {
            List.ScrollIntoView(item);
        }
    }

    private void OnBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Looked at once focus has settled: moving from the inline box to the
        // flyout's, or a click on a result, is not leaving the search.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!Inline.IsKeyboardFocusWithin && !InPopup.IsKeyboardFocusWithin && !Panel.IsMouseOver)
            {
                Close();
            }
        }));
    }

    private void OnResultClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement source || source.DataContext is not SearchHit hit || ViewModel is not { } search)
        {
            return;
        }

        e.Handled = true;

        if (search.Choose(hit))
        {
            Close();
        }
    }
}
