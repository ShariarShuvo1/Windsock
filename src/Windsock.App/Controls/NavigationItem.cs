using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
/// Still a tab item underneath, so selection, focus and UI Automation keep
/// working the way the tab control expects. What a group changes is that it
/// takes focus without taking selection, and that pressing it is an action
/// rather than a choice.
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

        if (HasChildren && Pane is { } pane)
        {
            Focus();
            pane.Invoke(this);
            e.Handled = true;
            return;
        }

        base.OnMouseLeftButtonDown(e);
    }

    protected override void OnPreviewGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        // A tab item selects itself as it takes focus. A group only takes focus:
        // arrowing past it should not turn the page.
        if (HasChildren)
        {
            return;
        }

        base.OnPreviewGotKeyboardFocus(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Pane is not { } pane)
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
