using System.Collections.Specialized;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using FluentIcons.Common;

namespace Windsock.App.Controls;

/// <summary>
/// The column down the left of the window that chooses a panel, and the page
/// beside it.
/// </summary>
/// <remarks>
/// <para>
/// A tab control underneath, so selection, keyboard focus and UI Automation
/// behave the way they did when the sections were tabs: each section is still a
/// <see cref="TabItem"/>, and only the selected one's panel is in the tree.
/// What changed is where the tabs are drawn. A strip across the top took a
/// band of height from every panel for four words; a column down the side
/// takes width, which the panels have more of.
/// </para>
/// <para>
/// The list is one Tab stop. Tab lands on the entry showing the page (or its
/// group, folded), the arrows move from there without choosing anything, and
/// Enter or Space chooses - Windows' own navigation pattern. The next Tab goes
/// on to the page. Every entry used to be a stop that chose itself as it took
/// focus, so tabbing from the search box to the page turned the page twenty
/// times and ended on Settings. Only one entry at a time is a Tab stop
/// (<see cref="KeepTabStop"/>): the one with the keyboard while it is in the
/// list, and the page's own once it leaves. WPF's TabOnceActiveElement would
/// say the same, but it is not public.
/// </para>
/// <para>
/// Two widths: open, naming each section beside its icon, and compact, icons
/// only. The column glides between them and the page beside it gives way as it
/// goes. Which one the user wants is <see cref="IsPaneOpen"/>; which one is on
/// screen is <see cref="IsPaneShown"/>, and the two differ only when the window
/// is too narrow for the names. Then the column stays compact beside the page,
/// and opening it lays it over the page instead of squeezing the page into a
/// sliver, until a section is chosen or the pointer goes elsewhere.
/// </para>
/// </remarks>
[TemplatePart(Name = PanePart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = TogglePart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = BodyPart, Type = typeof(FrameworkElement))]
[TemplatePart(Name = SearchPart, Type = typeof(FrameworkElement))]
public sealed class NavigationPane : TabControl
{
    private const string PanePart = "PART_Pane";
    private const string TogglePart = "PART_Toggle";
    private const string BodyPart = "PART_Body";
    private const string SearchPart = "PART_Search";

    private static readonly Duration GlideDuration = new(TimeSpan.FromMilliseconds(220));

    public static readonly DependencyProperty IconProperty =
        DependencyProperty.RegisterAttached(
            "Icon",
            typeof(Symbol),
            typeof(NavigationPane),
            new PropertyMetadata(default(Symbol)));

    public static readonly DependencyProperty ReadingProperty =
        DependencyProperty.RegisterAttached(
            "Reading",
            typeof(string),
            typeof(NavigationPane),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.RegisterAttached(
            "Badge",
            typeof(string),
            typeof(NavigationPane),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(
            nameof(Body),
            typeof(object),
            typeof(NavigationPane),
            new PropertyMetadata(null));

    public static readonly DependencyProperty SearchProperty =
        DependencyProperty.Register(
            nameof(Search),
            typeof(object),
            typeof(NavigationPane),
            new PropertyMetadata(null));

    public static readonly DependencyProperty IsPaneOpenProperty =
        DependencyProperty.Register(
            nameof(IsPaneOpen),
            typeof(bool),
            typeof(NavigationPane),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsPaneOpenChanged));

    public static readonly DependencyProperty IsPaneShownProperty =
        DependencyProperty.Register(
            nameof(IsPaneShown),
            typeof(bool),
            typeof(NavigationPane),
            new FrameworkPropertyMetadata(
                true,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnIsPaneShownChanged));

    public static readonly DependencyProperty CompactPaneWidthProperty =
        DependencyProperty.Register(
            nameof(CompactPaneWidth),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(52d, OnGeometryChanged));

    public static readonly DependencyProperty OpenPaneWidthProperty =
        DependencyProperty.Register(
            nameof(OpenPaneWidth),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(200d, OnGeometryChanged));

    public static readonly DependencyProperty InlineMinimumWidthProperty =
        DependencyProperty.Register(
            nameof(InlineMinimumWidth),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(820d, OnGeometryChanged));

    /// <summary>
    /// The width the column is drawn at this instant, which is what the glide
    /// animates. Not for setting: the control drives it.
    /// </summary>
    public static readonly DependencyProperty PaneWidthProperty =
        DependencyProperty.Register(
            nameof(PaneWidth),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(200d, OnPaneWidthChanged));

    private static readonly DependencyPropertyKey ReservedWidthPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(ReservedWidth),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(200d));

    private static readonly DependencyPropertyKey OpenFractionPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(OpenFraction),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(1d));

    private static readonly DependencyPropertyKey NameOpacityPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(NameOpacity),
            typeof(double),
            typeof(NavigationPane),
            new PropertyMetadata(1d));

    private static readonly DependencyPropertyKey IsNarrowPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsNarrow),
            typeof(bool),
            typeof(NavigationPane),
            new PropertyMetadata(false));

    private static readonly DependencyPropertyKey IsOverlayPropertyKey =
        DependencyProperty.RegisterReadOnly(
            nameof(IsOverlay),
            typeof(bool),
            typeof(NavigationPane),
            new PropertyMetadata(false));

    public static readonly DependencyProperty ReservedWidthProperty = ReservedWidthPropertyKey.DependencyProperty;

    public static readonly DependencyProperty OpenFractionProperty = OpenFractionPropertyKey.DependencyProperty;

    public static readonly DependencyProperty NameOpacityProperty = NameOpacityPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsNarrowProperty = IsNarrowPropertyKey.DependencyProperty;

    public static readonly DependencyProperty IsOverlayProperty = IsOverlayPropertyKey.DependencyProperty;

    private FrameworkElement? _pane;
    private FrameworkElement? _toggle;
    private FrameworkElement? _body;
    private FrameworkElement? _search;
    private bool _sized;
    private bool _syncing;
    private Popup? _flyout;

    // Set by Ctrl+Tab from the page for the selection it makes: the new page
    // takes the keyboard, not the list.
    private bool _keyboardToPage;

    public NavigationPane() => Loaded += (_, _) => KeepTabStop();

    /// <summary>Raised when the user opens or folds the column.</summary>
    public event EventHandler? PaneOpenChanged;

    /// <summary>The icon a section shows in the column.</summary>
    public static Symbol GetIcon(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (Symbol)element.GetValue(IconProperty);
    }

    /// <summary>Sets the icon a section shows in the column.</summary>
    public static void SetIcon(DependencyObject element, Symbol value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(IconProperty, value);
    }

    /// <summary>
    /// A live figure set against a section's name - the processor's load, the
    /// download rate - so the column is a summary as well as a way in.
    /// </summary>
    public static string GetReading(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(ReadingProperty);
    }

    /// <summary>Sets the live figure beside a section's name.</summary>
    public static void SetReading(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ReadingProperty, value);
    }

    /// <summary>
    /// A count worth acting on, drawn on the entry's icon so it shows with the
    /// names folded away too - the alerts not yet read. Empty for none.
    /// </summary>
    public static string GetBadge(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (string)element.GetValue(BadgeProperty);
    }

    /// <summary>Sets the count on an entry's icon.</summary>
    public static void SetBadge(DependencyObject element, string value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(BadgeProperty, value);
    }

    /// <summary>
    /// The page beside the column. Whatever is in it shows the selected section
    /// by binding to <see cref="TabControl.SelectedContent"/>, so it can put
    /// something above the panel that stays whichever section is chosen.
    /// </summary>
    public object? Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    /// <summary>What sits at the top of the column, under its button: the search box.</summary>
    public object? Search
    {
        get => GetValue(SearchProperty);
        set => SetValue(SearchProperty, value);
    }

    /// <summary>Whether the user wants the sections named.</summary>
    public bool IsPaneOpen
    {
        get => (bool)GetValue(IsPaneOpenProperty);
        set => SetValue(IsPaneOpenProperty, value);
    }

    /// <summary>
    /// Whether the sections are named on screen now. Setting it is what the
    /// toggle does: it changes the preference while there is room, and only
    /// opens the column over the page while there is not.
    /// </summary>
    public bool IsPaneShown
    {
        get => (bool)GetValue(IsPaneShownProperty);
        set => SetValue(IsPaneShownProperty, value);
    }

    /// <summary>How wide the column is with icons only.</summary>
    public double CompactPaneWidth
    {
        get => (double)GetValue(CompactPaneWidthProperty);
        set => SetValue(CompactPaneWidthProperty, value);
    }

    /// <summary>How wide the column is with names.</summary>
    public double OpenPaneWidth
    {
        get => (double)GetValue(OpenPaneWidthProperty);
        set => SetValue(OpenPaneWidthProperty, value);
    }

    /// <summary>
    /// The narrowest this control may be and still set an open column beside
    /// the page rather than over it.
    /// </summary>
    public double InlineMinimumWidth
    {
        get => (double)GetValue(InlineMinimumWidthProperty);
        set => SetValue(InlineMinimumWidthProperty, value);
    }

    /// <summary>The column's width at this instant.</summary>
    public double PaneWidth
    {
        get => (double)GetValue(PaneWidthProperty);
        set => SetValue(PaneWidthProperty, value);
    }

    /// <summary>The width kept clear for the column beside the page.</summary>
    public double ReservedWidth
    {
        get => (double)GetValue(ReservedWidthProperty);
        private set => SetValue(ReservedWidthPropertyKey, value);
    }

    /// <summary>How far the column is through opening, from nothing to one.</summary>
    public double OpenFraction
    {
        get => (double)GetValue(OpenFractionProperty);
        private set => SetValue(OpenFractionPropertyKey, value);
    }

    /// <summary>
    /// How visible the section names are. The square of
    /// <see cref="OpenFraction"/> rather than the fraction itself: a name the
    /// edge is still cutting in half should be all but invisible, and at a
    /// straight line a quarter-open column showed a quarter-visible "Syst".
    /// </summary>
    public double NameOpacity
    {
        get => (double)GetValue(NameOpacityProperty);
        private set => SetValue(NameOpacityPropertyKey, value);
    }

    /// <summary>Whether the window is too narrow to set the names beside the page.</summary>
    public bool IsNarrow
    {
        get => (bool)GetValue(IsNarrowProperty);
        private set => SetValue(IsNarrowPropertyKey, value);
    }

    /// <summary>Whether the column is open over the page rather than beside it.</summary>
    public bool IsOverlay
    {
        get => (bool)GetValue(IsOverlayProperty);
        private set => SetValue(IsOverlayPropertyKey, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _pane = GetTemplateChild(PanePart) as FrameworkElement;
        _toggle = GetTemplateChild(TogglePart) as FrameworkElement;
        _body = GetTemplateChild(BodyPart) as FrameworkElement;
        _search = GetTemplateChild(SearchPart) as FrameworkElement;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new NavigationPaneAutomationPeer(this);

    /// <summary>
    /// What pressing a group does. A group not holding the page opens and goes
    /// to its first section, which is how a person asks for it; a group already
    /// holding the page only folds or unfolds. With the names folded away there
    /// is nowhere to list its sections, so they come up beside it instead.
    /// </summary>
    internal void Invoke(NavigationItem group)
    {
        if (!group.HasChildren)
        {
            return;
        }

        if (!IsPaneShown)
        {
            OpenFlyout(group);
            return;
        }

        if (group.HasSelectedChild)
        {
            group.IsExpanded = !group.IsExpanded;
            return;
        }

        group.IsExpanded = true;
        group.Children[0].IsSelected = true;
    }

    /// <summary>
    /// Lists each group's sections while it is open and the sidebar is naming
    /// things, and hides them otherwise, so the arrow keys only ever reach
    /// entries that can be seen.
    /// </summary>
    internal void FollowGroups()
    {
        foreach (NavigationItem item in Items.OfType<NavigationItem>())
        {
            if (item.ParentItem is { } parent)
            {
                item.Visibility = parent.IsExpanded && IsPaneShown ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        KeepTabStop();
    }

    /// <summary>Puts the keyboard on the first entry that can be seen, or the last: Home and End in the list.</summary>
    internal void FocusEnd(bool last)
    {
        List<NavigationItem> shown = Shown();

        if (shown.Count > 0)
        {
            shown[last ? ^1 : 0].Focus();
        }
    }

    /// <summary>Puts the keyboard on the entry above or below one, round the ends: Up and Down in the list.</summary>
    internal void FocusNext(NavigationItem from, int direction)
    {
        List<NavigationItem> shown = Shown();
        int at = shown.IndexOf(from);

        if (shown.Count > 0)
        {
            shown[at < 0 ? 0 : (at + direction + shown.Count) % shown.Count].Focus();
        }
    }

    /// <summary>
    /// Puts the keyboard on the list where Tab would: the entry showing the
    /// page, or its group while the entry is folded away and cannot take it.
    /// </summary>
    public void FocusList()
    {
        KeepTabStop();
        Items.OfType<NavigationItem>().FirstOrDefault(item => KeyboardNavigation.GetIsTabStop(item))?.Focus();
    }

    /// <summary>
    /// Makes one entry the list's only Tab stop: the one with the keyboard
    /// while it is in the list, otherwise the one showing the page - or its
    /// group, while the group or the whole sidebar is folded and the entry
    /// cannot be seen.
    /// </summary>
    internal void KeepTabStop()
    {
        List<NavigationItem> items = [.. Items.OfType<NavigationItem>()];

        NavigationItem? stop = Keyboard.FocusedElement is NavigationItem { IsVisible: true } focused && items.Contains(focused)
            ? focused
            : SelectedItem is NavigationItem chosen
                ? chosen.Visibility != Visibility.Visible && chosen.ParentItem is { } group ? group : chosen
                : items.FirstOrDefault(item => item.Visibility == Visibility.Visible);

        foreach (NavigationItem item in items)
        {
            KeyboardNavigation.SetIsTabStop(item, ReferenceEquals(item, stop));
        }
    }

    protected override void OnItemsChanged(NotifyCollectionChangedEventArgs e)
    {
        base.OnItemsChanged(e);
        Regroup();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        ArgumentNullException.ThrowIfNull(sizeInfo);
        base.OnRenderSizeChanged(sizeInfo);

        if (!sizeInfo.WidthChanged || sizeInfo.NewSize.Width <= 0)
        {
            return;
        }

        bool narrow = sizeInfo.NewSize.Width < InlineMinimumWidth;

        // The first real size is where the column starts, not somewhere it
        // travels to: a window should open already laid out.
        bool animate = _sized;
        _sized = true;

        if (narrow == IsNarrow && animate)
        {
            return;
        }

        IsNarrow = narrow;

        // Room again means the column goes back beside the page, at whatever
        // the user last chose; losing room folds it, over nothing.
        Show(narrow ? false : IsPaneOpen, animate);
    }

    protected override void OnSelectionChanged(SelectionChangedEventArgs e)
    {
        // A group is not a page. However it came to be chosen - the first item
        // at start, Home, automation - it means the group's first section.
        if (SelectedItem is NavigationItem { HasChildren: true } group)
        {
            group.IsExpanded = true;
            SetCurrentValue(SelectedItemProperty, group.Children[0]);
            return;
        }

        bool hadKeyboard = IsKeyboardFocusWithin || _keyboardToPage;
        bool toPage = _keyboardToPage;
        _keyboardToPage = false;

        base.OnSelectionChanged(e);
        FollowSelection();
        KeepTabStop();

        // The tab control puts the keyboard on the chosen entry, which cannot
        // take it while it is folded away; the old page then goes with the
        // keyboard still in it, leaving it nowhere - where even Ctrl+Tab no
        // longer reaches the window. Once the new page is laid out, the
        // keyboard is put somewhere it can be seen.
        if (hadKeyboard)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => SettleKeyboard(toPage)));
        }

        if (IsOverlay)
        {
            Show(false, animate: true);
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Key)
        {
            // A tab control answers Home and End from anywhere inside it by
            // going to its first or last tab - here from anywhere on the page,
            // which turned it to the Taskbar or Settings. The entries answer
            // them in the list (NavigationItem); nothing else here should.
            case Key.Home or Key.End:
                return;

            // Ctrl+Tab still turns the page, as in any tab control, now that
            // putting the keyboard on an entry no longer does.
            case Key.Tab when (e.KeyboardDevice.Modifiers & ModifierKeys.Control) != 0:
                Step((e.KeyboardDevice.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1);
                e.Handled = true;
                return;
        }

        base.OnKeyDown(e);
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnLostKeyboardFocus(e);

        // Leaving the list after arrowing about in it without choosing, the
        // keyboard should come back to the page showing, not where it left.
        if (e.OldFocus is NavigationItem && e.NewFocus is not NavigationItem)
        {
            KeepTabStop();
        }
    }

    protected override void OnPreviewMouseDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseDown(e);

        // Pressing anywhere off an open column puts it away. The press still
        // goes where it was aimed; dismissing is not a reason to eat it.
        if (IsOverlay && _pane is { IsMouseOver: false })
        {
            Show(false, animate: true);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnPreviewKeyDown(e);

        if (e.Key == Key.Escape && IsOverlay)
        {
            Show(false, animate: true);
            e.Handled = true;
        }
    }

    private static void OnIsPaneOpenChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        NavigationPane pane = (NavigationPane)d;

        if (!pane.IsNarrow)
        {
            pane.Show((bool)e.NewValue, animate: pane._sized);
        }

        pane.PaneOpenChanged?.Invoke(pane, EventArgs.Empty);
    }

    private static void OnIsPaneShownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        NavigationPane pane = (NavigationPane)d;

        if (pane._syncing)
        {
            return;
        }

        bool shown = (bool)e.NewValue;

        if (pane.IsNarrow)
        {
            pane.Show(shown, animate: pane._sized);
            return;
        }

        // Beside the page, the toggle is the preference itself.
        pane.IsPaneOpen = shown;
    }

    private static void OnGeometryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        NavigationPane pane = (NavigationPane)d;
        pane.Show(pane.IsPaneShown, animate: false);
    }

    private static void OnPaneWidthChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((NavigationPane)d).Follow();

    /// <summary>Works out which entries are groups, from the sections that name them.</summary>
    private void Regroup()
    {
        List<NavigationItem> items = [.. Items.OfType<NavigationItem>()];

        foreach (NavigationItem item in items)
        {
            item.Unlink();
        }

        foreach (NavigationItem item in items)
        {
            if (item.ParentName.Length > 0 && items.FirstOrDefault(other => other.Name == item.ParentName) is { } parent)
            {
                parent.Adopt(item);
            }
        }

        FollowGroups();
        FollowSelection();
    }

    /// <summary>
    /// Marks the group holding the page, and opens it: a section reached from
    /// anywhere else - a notification, the keyboard - is shown in its place.
    /// </summary>
    private void FollowSelection()
    {
        foreach (NavigationItem item in Items.OfType<NavigationItem>())
        {
            item.HasSelectedChild = item.HasChildren && item.Children.Any(child => child.IsSelected);

            if (item.HasSelectedChild)
            {
                item.IsExpanded = true;
            }
        }
    }

    /// <summary>The entries that can be seen and used, top to bottom.</summary>
    private List<NavigationItem> Shown() =>
        [.. Items.OfType<NavigationItem>().Where(item => item.IsVisible && item.IsEnabled)];

    /// <summary>
    /// Chooses the section before or after the one showing, round the end:
    /// Ctrl+Tab. Sections folded away count, since they are pages too. From the
    /// page the keyboard stays in the page area, as Windows' navigation keeps
    /// it in the content; from the list it stays in the list.
    /// </summary>
    private void Step(int direction)
    {
        List<NavigationItem> sections = [.. Items.OfType<NavigationItem>().Where(item => !item.HasChildren && item.IsEnabled)];

        if (sections.Count == 0)
        {
            return;
        }

        int at = SelectedItem is NavigationItem chosen ? sections.IndexOf(chosen) : -1;
        int next = at < 0 ? 0 : (at + direction + sections.Count) % sections.Count;

        _keyboardToPage = !(Keyboard.FocusedElement is NavigationItem focused && Items.Contains(focused));
        sections[next].IsSelected = true;
        _keyboardToPage = false;
    }

    /// <summary>
    /// Where the keyboard goes after the page changed with it inside the pane:
    /// into the new page after a Ctrl+Tab from a page, otherwise left where the
    /// tab control put it unless that is nowhere to be seen, when the list's
    /// Tab stop takes it.
    /// </summary>
    private void SettleKeyboard(bool toPage)
    {
        if (toPage)
        {
            if (_body is null
                || !_body.MoveFocus(new TraversalRequest(FocusNavigationDirection.First))
                || Keyboard.FocusedElement is not Visual into
                || !_body.IsAncestorOf(into))
            {
                // A page with nothing to focus: the list rather than nowhere.
                FocusList();
            }

            return;
        }

        if (Keyboard.FocusedElement is not UIElement { IsVisible: true } focused
            || focused is Window
            || PresentationSource.FromVisual(focused) is null)
        {
            FocusList();
        }
    }

    /// <summary>A group's sections, beside it, for the folded sidebar.</summary>
    private void OpenFlyout(NavigationItem group)
    {
        var list = new StackPanel { MinWidth = 180 };

        var title = new TextBlock
        {
            Text = group.Header as string,
            Margin = new Thickness(10, 4, 10, 6),
            FontWeight = FontWeights.SemiBold,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");

        // On the type ramp, so Windows' Text size grows it with everything else.
        title.SetResourceReference(TextBlock.FontSizeProperty, "TextSize11_5");
        list.Children.Add(title);

        foreach (NavigationItem section in group.Children)
        {
            var icon = new FluentIcons.Wpf.SymbolIcon
            {
                Symbol = GetIcon(section),
                FontSize = 14,
                Margin = new Thickness(0, 0, 9, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };

            var label = new TextBlock
            {
                Text = section.Header as string,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var entry = new Button
            {
                Content = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
                Style = TryFindResource("MenuItemStyle") as Style,
                Tag = section,
            };

            icon.SetBinding(ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = entry });

            if (section.IsSelected)
            {
                entry.FontWeight = FontWeights.SemiBold;
                entry.SetResourceReference(ForegroundProperty, "AccentBrush");
                icon.IconVariant = FluentIcons.Common.IconVariant.Filled;
            }

            AutomationProperties.SetName(entry, AutomationProperties.GetName(section) is { Length: > 0 } name ? name : section.Header as string);
            entry.Click += (_, _) =>
            {
                _flyout!.IsOpen = false;
                section.IsSelected = true;

                // The section is hidden while the names are folded; its group holds the page now.
                group.Focus();
            };
            list.Children.Add(entry);
        }

        var frame = new Border
        {
            Style = TryFindResource("FlyoutStyle") as Style,
            Padding = new Thickness(4),
            Child = list,
        };

        frame.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                _flyout!.IsOpen = false;
                group.Focus();
                e.Handled = true;
            }
        };

        _flyout ??= new Popup
        {
            StaysOpen = false,
            AllowsTransparency = true,
            Placement = PlacementMode.Right,
            HorizontalOffset = 8,
            PopupAnimation = PopupAnimation.Fade,
        };

        _flyout.PlacementTarget = group;
        _flyout.Child = frame;
        _flyout.IsOpen = true;

        // The keyboard goes to the section showing, or the first, so a folded
        // sidebar can still be worked without the pointer.
        Button? start = list.Children.OfType<Button>().FirstOrDefault(button => button.FontWeight == FontWeights.SemiBold)
            ?? list.Children.OfType<Button>().FirstOrDefault();

        if (start is not null)
        {
            frame.Dispatcher.BeginInvoke(() => start.Focus());
        }
    }

    /// <summary>Puts the column at one width or the other.</summary>
    private void Show(bool shown, bool animate)
    {
        _syncing = true;
        SetCurrentValue(IsPaneShownProperty, shown);
        _syncing = false;

        FollowGroups();

        IsOverlay = IsNarrow && shown;

        double target = shown ? OpenPaneWidth : CompactPaneWidth;

        if (!animate || !SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(PaneWidthProperty, null);
            PaneWidth = target;
            Follow();
            return;
        }

        DoubleAnimation glide = new(target, GlideDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        BeginAnimation(PaneWidthProperty, glide, HandoffBehavior.SnapshotAndReplace);

        // Narrowness can change without the width moving at all, and then no
        // animation tick would ever come to update the page's room.
        Follow();
    }

    /// <summary>Keeps the page's margin and the names' fade in step with the column.</summary>
    private void Follow()
    {
        double compact = CompactPaneWidth;
        double open = Math.Max(compact + 1, OpenPaneWidth);
        double width = PaneWidth;

        OpenFraction = Math.Clamp((width - compact) / (open - compact), 0, 1);
        NameOpacity = OpenFraction * OpenFraction;

        // Over the page, the page keeps the compact column's room and nothing
        // beneath moves.
        ReservedWidth = IsNarrow ? compact : width;
    }

    /// <summary>
    /// Tells UI Automation about everything the control draws, not only its
    /// sections.
    /// </summary>
    /// <remarks>
    /// A tab control's own peer reports its items and, through the selected
    /// item, the part of its template named for selected content - nothing
    /// else in the template. Here that would hide the fold button and the
    /// whole page, the overview included, from screen readers and from every
    /// automation that drives the window. So the order is the reading order:
    /// the button, the search box, the sections, then the page.
    /// </remarks>
    private sealed class NavigationPaneAutomationPeer(NavigationPane owner) : TabControlAutomationPeer(owner)
    {
        protected override List<AutomationPeer> GetChildrenCore()
        {
            NavigationPane pane = (NavigationPane)Owner;
            List<AutomationPeer> children = [];

            if (pane._toggle is not null && UIElementAutomationPeer.CreatePeerForElement(pane._toggle) is { } toggle)
            {
                children.Add(toggle);
            }

            if (pane._search is not null && new FrameworkElementAutomationPeer(pane._search).GetChildren() is { } search)
            {
                children.AddRange(search);
            }

            if (base.GetChildrenCore() is { } sections)
            {
                children.AddRange(sections);
            }

            if (pane._body is not null && new FrameworkElementAutomationPeer(pane._body).GetChildren() is { } page)
            {
                children.AddRange(page);
            }

            return children;
        }

        protected override ItemAutomationPeer CreateItemAutomationPeer(object item) =>
            new NavigationItemAutomationPeer(item, this);
    }

    /// <summary>
    /// A section as automation sees it, and for a group also the one thing a
    /// group can do beyond being chosen: fold and unfold.
    /// </summary>
    private sealed class NavigationItemAutomationPeer(object owner, TabControlAutomationPeer pane)
        : TabItemAutomationPeer(owner, pane), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => Item is NavigationItem { HasChildren: true } group
            ? group.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed
            : ExpandCollapseState.LeafNode;

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.ExpandCollapse && Item is NavigationItem { HasChildren: true }
                ? this
                : base.GetPattern(patternInterface);

        public void Expand()
        {
            if (Item is NavigationItem { HasChildren: true } group)
            {
                group.IsExpanded = true;
            }
        }

        public void Collapse()
        {
            if (Item is NavigationItem { HasChildren: true } group)
            {
                group.IsExpanded = false;
            }
        }
    }
}
