using System.Windows;
using System.Windows.Controls.Primitives;

namespace Windsock.App.Controls;

/// <summary>
/// Makes a button turn the main window to another page by its link name:
/// <c>controls:PageLink.To="adapters"</c>. The window decides what the name
/// means, as it does for a notification's link, so a page that moves keeps
/// every link to it working.
/// </summary>
public static class PageLink
{
    public static readonly DependencyProperty ToProperty = DependencyProperty.RegisterAttached(
        "To",
        typeof(string),
        typeof(PageLink),
        new PropertyMetadata(null, OnToChanged));

    /// <summary>The page a button opens.</summary>
    public static string? GetTo(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string?)element.GetValue(ToProperty);
    }

    /// <summary>Sets the page a button opens.</summary>
    public static void SetTo(DependencyObject element, string? value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ToProperty, value);
    }

    private static void OnToChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not ButtonBase button)
        {
            return;
        }

        button.Click -= OnClick;

        if (e.NewValue is string { Length: > 0 })
        {
            button.Click += OnClick;
        }
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not DependencyObject element || GetTo(element) is not { Length: > 0 } page)
        {
            return;
        }

        if (Window.GetWindow(element) is Views.MainWindow window)
        {
            window.ShowSection(page);
        }
    }
}
