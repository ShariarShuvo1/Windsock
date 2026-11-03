using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Windsock.App.Controls;

/// <summary>
/// One entry in a <see cref="NavigationPane"/>: a section, or a group of them.
/// </summary>
/// <remarks>
/// <para>
/// A section is a page, and choosing it shows the page. An entry becomes a group
/// when others name it as their <see cref="ParentName"/>; a group is never a page
/// of its own. Pressing it opens it and goes to its first section, or, when one
/// of its sections is already showing, folds or unfolds it. Its sections are
/// listed under it while it is open and the sidebar is showing names.
/// </para>
/// <para>
/// Still a tab item underneath, so selection and UI Automation keep working the
/// way the tab control expects. What changes is focus: a tab item chooses
/// itself as it takes focus, and here nothing does. The arrow keys move the
/// keyboard through the list and Enter or Space chooses, as in Windows' own
/// navigation, so walking the list never turns the page; a press with the
/// pointer focuses and chooses at once. A group is never chosen at all:
/// pressing it is an action.
/// </para>
/// <para>
/// A section's page is its content, so the page's routed events - keys,
/// presses, focus - pass through the section on their way up, though the page
/// is drawn beside the list rather than inside the entry. Every handler here
/// first asks whether the event is the entry's own (<see cref="IsOwn"/>):
/// without that, Up on a button in the page moved the keyboard into the list,
/// and a press on the page's background chose the entry. The tab item's own
/// mouse handler asks the same in its own way.
/// </para>
/// </remarks>
public sealed class NavigationItem : TabItem
{
    public static readonly DependencyProperty ParentNameProperty =
        DependencyProperty.Register(
            nameof(ParentName),
            typeof(string),
            typeof(NavigationItem),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(
            nameof(IsExpanded),
            typeof(bool),
            typeof(NavigationItem),
            new FrameworkPropertyMetadata(false, OnIsExpandedChanged));

    private static readonly DependencyPropertyKey HasChildrenPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasChildren),
            typeof(bool),
            typeof(NavigationItem),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsChildPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsChild),
            typeof(bool),
            typeof(NavigationItem),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey HasSelectedChildPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(HasSelectedChild),
            typeof(bool),
            typeof(NavigationItem),
            new PropertyMetadata(false));

    public static readonly DependencyProperty HasChildrenProperty = HasChildrenPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsChildProperty = IsChildPropertyKey.DependencyProperty;

    public static readonly DependencyProperty HasSelectedChildProperty = HasSelectedChildPropertyKey.DependencyProperty;

    private readonly List<NavigationItem> _children = [];

    /// <summary>The name of the group this section belongs to, or empty for a top-level entry.</summary>
    public string ParentName
    {
        get => (string)GetValue(ParentNameProperty);
        set => SetValue(ParentNameProperty, value);
    }

    /// <summary>Whether a group's sections are listed under it.</summary>
    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    /// <summary>Whether this entry is a group.</summary>
    public bool HasChildren
    {
        get => (bool)GetValue(HasChildrenProperty);
        private set => SetValue(HasChildrenPropertyKey, value);
    }

    /// <summary>Whether this entry is a section inside a group.</summary>
    public bool IsChild
    {
        get => (bool)GetValue(IsChildProperty);
        private set => SetValue(IsChildPropertyKey, value);
    }

    /// <summary>Whether the section showing is one of this group's.</summary>
    public bool HasSelectedChild
    {
        get => (bool)GetValue(HasSelectedChildProperty);
        internal set => SetValue(HasSelectedChildPropertyKey, value);
    }

    /// <summary>The group this section is in, if any.</summary>
    public NavigationItem? ParentItem { get; private set; }

    /// <summary>This group's sections, in the order they are listed.</summary>
    public IReadOnlyList<NavigationItem> Children => _children;

    private NavigationPane? Pane => ItemsControl.ItemsControlFromItemContainer(this) as NavigationPane;

    /// <summary>Forgets where this entry sits, before the pane works it out again.</summary>
    internal void Unlink()
    {
        _children.Clear();
        ParentItem = null;
        HasChildren = false;
        IsChild = false;
    }

    /// <summary>Puts a section under this group.</summary>
    internal void Adopt(NavigationItem child)
    {
        _children.Add(child);
        child.ParentItem = this;
        child.IsChild = true;
        HasChildren = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Pane is not { } pane || !IsEnabled || !IsOwn(e))
        {
            base.OnMouseLeftButtonDown(e);
            return;
        }

        Focus();

        if (HasChildren)
        {
            pane.Invoke(this);
        }
        else
        {
            // What the tab item did by taking focus, now that focus alone chooses nothing.
            SetCurrentValue(IsSelectedProperty, true);
        }

        e.Handled = true;
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        // A tab item selects itself as it takes focus. Here focus only moves:
        // arrowing or tabbing through the list must not turn the page.
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnGotKeyboardFocus(e);

        // The entry with the keyboard is the list's one Tab stop while it has it,
        // so Tab goes on to the page from here rather than to the next stop.
        if (IsOwn(e))
        {
            Pane?.KeepTabStop();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Pane is not { } pane || !IsOwn(e))
        {
            base.OnKeyDown(e);
            return;
        }

        switch (e.Key)
        {
            case Key.Enter or Key.Space when HasChildren:
                pane.Invoke(this);
                e.Handled = true;
                return;

            case Key.Enter or Key.Space:
                SetCurrentValue(IsSelectedProperty, true);
                e.Handled = true;
                return;

            case Key.Home or Key.End:
                pane.FocusEnd(e.Key == Key.End);
                e.Handled = true;
                return;

            case Key.Up or Key.Down:
                pane.FocusNext(this, e.Key == Key.Down ? 1 : -1);
                e.Handled = true;
                return;

            case Key.Right when HasChildren && !IsExpanded && pane.IsPaneShown:
                IsExpanded = true;
                e.Handled = true;
                return;

            case Key.Left when HasChildren && IsExpanded && pane.IsPaneShown:
                IsExpanded = false;
                e.Handled = true;
                return;

            case Key.Left when ParentItem is { } parent:
                parent.Focus();
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Whether an event began on the entry itself, as drawn, rather than on
    /// the page it shows, whose events pass through it by the logical tree.
    /// </summary>
    private bool IsOwn(RoutedEventArgs e)
    {
        // A press can land on text inside a text block, which is no visual.
        DependencyObject? source = e.OriginalSource as DependencyObject;

        while (source is not null and not Visual)
        {
            source = LogicalTreeHelper.GetParent(source);
        }

        return source is Visual visual && (ReferenceEquals(visual, this) || IsAncestorOf(visual));
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var item = (NavigationItem)d;
        item.Pane?.FollowGroups();

        // Folding a group with the keyboard inside it hands the keyboard to the group.
        if (e.NewValue is false && item._children.Any(child => child.IsKeyboardFocusWithin))
        {
            item.Focus();
        }
    }
}
