using System.Windows.Controls;
using System.Windows.Input;
using Windsock.App.ViewModels;

namespace Windsock.App.Views;

/// <summary>The bar under a long table that moves between its pages; see <see cref="PagerViewModel"/>.</summary>
public partial class PagerBar : UserControl
{
    public PagerBar()
    {
        InitializeComponent();

        // A page typed into the box is gone to on Enter, or on leaving the box;
        // Escape puts the box back to the page on screen.
        PageBox.KeyDown += (_, e) =>
        {
            if (DataContext is not PagerViewModel pager)
            {
                return;
            }

            if (e.Key == Key.Enter)
            {
                pager.GoToTypedCommand.Execute(null);
                PageBox.SelectAll();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                pager.PageText = pager.Page.ToString(System.Globalization.CultureInfo.CurrentCulture);
                e.Handled = true;
            }
        };

        PageBox.LostKeyboardFocus += (_, _) => (DataContext as PagerViewModel)?.GoToTypedCommand.Execute(null);
        PageBox.GotKeyboardFocus += (_, _) => PageBox.SelectAll();
    }

    /// <summary>
    /// Moves a page for Page Up and Page Down pressed in the table the bar
    /// belongs to. Returns whether the key was taken.
    /// </summary>
    public static bool TakeKey(PagerViewModel pager, KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(pager);
        ArgumentNullException.ThrowIfNull(e);

        if (e.Key == Key.PageDown && pager.NextCommand.CanExecute(null))
        {
            pager.NextCommand.Execute(null);
            return true;
        }

        if (e.Key == Key.PageUp && pager.PreviousCommand.CanExecute(null))
        {
            pager.PreviousCommand.Execute(null);
            return true;
        }

        return false;
    }
}
