using System.Windows;

namespace Windsock.App.Controls;

/// <summary>A view model that does something only while a part of the screen bound to it is visible.</summary>
public interface IFollowsVisibility
{
    /// <summary>Told once each time an element bound to it becomes visible, and once each time it stops being.</summary>
    void Showing(bool visible);
}

/// <summary>
/// Tells an element's view model when the element is on screen:
/// <c>controls:WhileVisible.Follows="True"</c> on any element whose data
/// context is an <see cref="IFollowsVisibility"/>.
/// </summary>
/// <remarks>
/// For a figure that lives inside another page's layout - a plate in a strip,
/// which has no code-behind of its own - so collection is still asked for, not
/// assumed: visibility rather than Loaded, because a minimised window's content
/// stays loaded. Each element says visible and not visible in turn, never twice
/// in a row, so a view model can count elements.
/// </remarks>
public static class WhileVisible
{
    public static readonly DependencyProperty FollowsProperty = DependencyProperty.RegisterAttached(
        "Follows",
        typeof(bool),
        typeof(WhileVisible),
        new PropertyMetadata(false, OnFollowsChanged));

    private static readonly DependencyProperty ToldProperty = DependencyProperty.RegisterAttached(
        "Told",
        typeof(IFollowsVisibility),
        typeof(WhileVisible),
        new PropertyMetadata(null));

    /// <summary>Whether the element tells its view model when it is visible.</summary>
    public static bool GetFollows(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(FollowsProperty);
    }

    /// <summary>Sets whether the element tells its view model when it is visible.</summary>
    public static void SetFollows(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(FollowsProperty, value);
    }

    private static void OnFollowsChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement framework)
        {
            return;
        }

        framework.IsVisibleChanged -= OnChanged;
        framework.DataContextChanged -= OnChanged;
        framework.Unloaded -= OnUnloaded;

        if (e.NewValue is true)
        {
            framework.IsVisibleChanged += OnChanged;
            framework.DataContextChanged += OnChanged;
            framework.Unloaded += OnUnloaded;
        }

        Update(framework);
    }

    private static void OnChanged(object sender, DependencyPropertyChangedEventArgs e) => Update((FrameworkElement)sender);

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Tell((FrameworkElement)sender, null);

    private static void Update(FrameworkElement element) =>
        Tell(element, GetFollows(element) && element.IsVisible ? element.DataContext as IFollowsVisibility : null);

    private static void Tell(FrameworkElement element, IFollowsVisibility? now)
    {
        IFollowsVisibility? before = (IFollowsVisibility?)element.GetValue(ToldProperty);

        if (ReferenceEquals(before, now))
        {
            return;
        }

        element.SetValue(ToldProperty, now);
        before?.Showing(false);
        now?.Showing(true);
    }
}
