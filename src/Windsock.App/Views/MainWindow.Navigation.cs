using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Windsock.App.Controls;
using Windsock.App.Navigation;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.Core.Docking;
using Windsock.Core.Navigation;
using PageLink = Windsock.Core.Navigation.PageLink;

namespace Windsock.App.Views;

/// <summary>
/// Getting around the window: the search box, the page the window opens on,
/// and where on screen it opens.
/// </summary>
public partial class MainWindow
{
    /// <summary>Settings' pages as the search names them, by link name.</summary>
    private static readonly Dictionary<string, string> SettingsLabels = new(StringComparer.Ordinal)
    {
        ["general"] = "General",
        ["appearance"] = "Appearance",
        ["privacy"] = "Data and privacy",
        ["advanced"] = "Advanced",
        ["about"] = "About",
    };

    private readonly PageSearchViewModel _search = new();
    private PageSearchBox? _searchBox;
    private (PixelRect Bounds, bool Maximised)? _planned;

    /// <summary>The page on screen, by its link name: a settings page by its own.</summary>
    public string CurrentLink
    {
        get
        {
            if (Sections.SelectedItem is not NavigationItem entry || !_pages.TryGetValue(entry, out Page? page))
            {
                return string.Empty;
            }

            if (entry.Content is SettingsPanelView settings)
            {
                foreach ((string link, SettingsPage each) in SettingsPages)
                {
                    if (each == settings.ViewModel.SelectedPage)
                    {
                        return link;
                    }
                }
            }

            return page.Key;
        }
    }

    /// <summary>
    /// Puts the caret in the search box. What Ctrl+F and Ctrl+K do from
    /// anywhere in the window.
    /// </summary>
    public void FindPage() => _searchBox?.Begin();

    /// <summary>
    /// Goes to something the search found: its page, then the control it
    /// names, brought into view and given focus once the page is laid out.
    /// </summary>
    public void Go(SearchEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        ShowSection(entry.Link);

        if (string.IsNullOrEmpty(entry.Target))
        {
            if (Sections.SelectedItem is UIElement chosen)
            {
                chosen.Focus();
            }

            return;
        }

        // After layout, so a page made or a tab shown just now has its controls
        // where BringIntoView can find them; once more when the dispatcher is
        // idle, for a part of a page whose visibility settles a pass later.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!RevealTarget(entry))
            {
                Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => RevealTarget(entry)));
            }
        }));
    }

    /// <summary>Brings a control on the page on screen into view; false when it is not there or not showing.</summary>
    private bool RevealTarget(SearchEntry entry) =>
        WorkspaceHost.Content is DependencyObject page
        && TargetFinder.Find(page, entry.Target) is { } target
        && TargetFinder.Reveal(target, setting: entry.Kind == SearchKind.Setting);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        if (_planned is { } planned)
        {
            WindowPlacement.Restore(this, planned.Bounds);

            if (planned.Maximised)
            {
                WindowState = WindowState.Maximized;
            }
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.F or Key.K)
        {
            FindPage();
            e.Handled = true;
            return;
        }

        base.OnPreviewKeyDown(e);
    }

    /// <summary>
    /// Sets up the search box, turns to the page the window should open on,
    /// and plans where it opens. Called once, at the end of the constructor.
    /// </summary>
    private void StartNavigation()
    {
        Debug.Assert(Pages.All(page => PageLink.IsName(page.Key) && page.Aliases.All(PageLink.IsName)), "Every page's link name is lowercase letters and hyphens.");
        Debug.Assert(Pages.Select(page => page.Key).Concat(Pages.SelectMany(page => page.Aliases)).Concat(SettingsPages.Keys).Distinct(StringComparer.Ordinal).Count()
            == Pages.Length + Pages.Sum(page => page.Aliases.Count) + SettingsPages.Count, "No two pages share a link name.");

        _searchBox = new PageSearchBox { DataContext = _search };
        _searchBox.Dismissed += OnSearchDismissed;
        _search.Chosen += OnSearchChosen;
        _search.Use(BuildIndex());
        Sections.Search = _searchBox;

        // A page offered later - the battery's - becomes findable when it is listed.
        ((INotifyCollectionChanged)Sections.Items).CollectionChanged += OnEntriesChanged;

        if (_settings.Window.PageToOpen() is { } open)
        {
            ShowSection(open);
        }

        _planned = WindowPlacement.Planned(_settings.Window, MinWidth, MinHeight);

        if (_planned is not null)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
        }

        Closing += OnClosingRemember;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => _search.Use(BuildIndex());

    private void OnSearchChosen(object? sender, SearchEntry entry) => Go(entry);

    // The list's Tab stop rather than the page's own entry, which cannot take
    // focus while its group or the sidebar is folded.
    private void OnSearchDismissed(object? sender, EventArgs e) => Sections.FocusList();

    /// <summary>Keeps the page on screen and where the window stands, for the next opening.</summary>
    private void OnClosingRemember(object? sender, CancelEventArgs e)
    {
        Closing -= OnClosingRemember;
        ((INotifyCollectionChanged)Sections.Items).CollectionChanged -= OnEntriesChanged;
        _search.Chosen -= OnSearchChosen;

        if (_searchBox is not null)
        {
            _searchBox.Dismissed -= OnSearchDismissed;
        }

        string link = CurrentLink;

        if (PageLink.IsName(link))
        {
            _settings.Window.LastPage = link;
        }

        WindowPlacement.Remember(this, _settings.Window);
        _store.Save(_settings);
    }

    /// <summary>
    /// Everything the search can find: every page listed in the sidebar by its
    /// name, Settings' own pages, and what <see cref="SearchCatalogue"/> lists
    /// on them, each with where it is as the sidebar names it.
    /// </summary>
    private SearchIndex BuildIndex()
    {
        SearchIndex index = new();
        HashSet<string> groups = [.. Pages.Where(page => page.Parent is not null).Select(page => page.Parent!)];

        foreach (Page page in Pages)
        {
            if (groups.Contains(page.Key) || !Listed(page.Key))
            {
                continue;
            }

            index.Add(new SearchEntry(page.Header, page.Key)
            {
                Kind = SearchKind.Page,
                Where = page.Parent is { } parent ? HeaderOf(parent) : string.Empty,
                Terms = string.Join(' ', SearchCatalogue.PageTerms.GetValueOrDefault(page.Key, string.Empty), page.Automation ?? string.Empty),
            });
        }

        foreach ((string link, string label) in SettingsLabels)
        {
            index.Add(new SearchEntry(label, link)
            {
                Kind = SearchKind.Page,
                Where = "Settings",
                Terms = SearchCatalogue.PageTerms.GetValueOrDefault(link, string.Empty),
            });
        }

        foreach (SearchEntry entry in SearchCatalogue.Entries)
        {
            if (WhereOf(entry.Link) is { } where)
            {
                index.Add(entry with { Where = where });
            }
        }

        return index;
    }

    /// <summary>Where a page is, as the sidebar names it, or nothing for a page this window does not list.</summary>
    private string? WhereOf(string link)
    {
        if (SettingsLabels.TryGetValue(link, out string? label))
        {
            return string.Concat("Settings › ", label);
        }

        if (Pages.FirstOrDefault(page => string.Equals(page.Key, link, StringComparison.Ordinal)) is not { } found || !Listed(found.Key))
        {
            return null;
        }

        return found.Parent is { } parent ? string.Concat(HeaderOf(parent), " › ", found.Header) : found.Header;
    }

    private bool Listed(string key) => _entries.TryGetValue(key, out NavigationItem? entry) && Sections.Items.Contains(entry);

    private static string HeaderOf(string key) =>
        Pages.FirstOrDefault(page => string.Equals(page.Key, key, StringComparison.Ordinal))?.Header ?? key;
}
