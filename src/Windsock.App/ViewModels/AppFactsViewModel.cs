using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.App.Services;
using Windsock.Core.Asn;
using Windsock.Core.Insight;
using Windsock.Core.Processes;
using Windsock.Core.Security;

namespace Windsock.App.ViewModels;

/// <summary>
/// What a details window says about who an app is - who signed it, which
/// program started it, which services it runs - and what can be done about it,
/// and the notes on each of its connections: who runs the far end's network,
/// whether a blocklist names it, and which service opened it.
/// </summary>
/// <remarks>
/// Kept beside the window's own view model rather than in it: it reads its
/// processes and connection rows from there and adds to them, every few
/// seconds and off the window's thread, and stops when the window closes.
/// </remarks>
public sealed partial class AppFactsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Step = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly string? _path;
    private readonly Func<IReadOnlyList<ProcessUsage>> _processes;
    private readonly ObservableCollection<ConnectionRowViewModel> _rows;
    private readonly AppFacts _facts;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;
    private Dictionary<string, SocketOwner> _owners = new(StringComparer.Ordinal);
    private int _reading;
    private bool _disposed;

    internal AppFactsViewModel(
        string name,
        string? path,
        Func<IReadOnlyList<ProcessUsage>> processes,
        ObservableCollection<ConnectionRowViewModel> rows,
        AppFacts facts,
        Dispatcher dispatcher)
    {
        _name = name;
        _path = path;
        _processes = processes;
        _rows = rows;
        _facts = facts;
        _dispatcher = dispatcher;

        NetworkColumn = Column.Text<ConnectionRowViewModel>("Network", row => row.Network);
        ServiceColumn = Column.Text<ConnectionRowViewModel>("Service", row => row.Service);
        ServiceColumn.IsOffered = false;

        _rows.CollectionChanged += OnRowsChanged;
        _facts.Blocklists.Changed += OnListsChanged;
        _facts.Signatures.Checked += OnSignatureChecked;

        _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Step };
        _timer.Tick += (_, _) => Read();
        _timer.Start();

        Describe(_rows);
        Sign();
        Read();
    }

    /// <summary>Who runs each far end's network.</summary>
    public TableColumn<ConnectionRowViewModel> NetworkColumn { get; }

    /// <summary>Which service opened each socket; offered only for a service host.</summary>
    public TableColumn<ConnectionRowViewModel> ServiceColumn { get; }

    /// <summary>Raised when a column's filter or choice changed, so the table filters again.</summary>
    public event EventHandler? FilterChanged
    {
        add
        {
            NetworkColumn.Changed += value;
            ServiceColumn.Changed += value;
        }
        remove
        {
            NetworkColumn.Changed -= value;
            ServiceColumn.Changed -= value;
        }
    }

    /// <summary>Who signed the program, in a sentence.</summary>
    [ObservableProperty]
    public partial string Publisher { get; private set; } = "Checking the signature…";

    /// <summary>Whether it is unsigned, or signed in a way that does not hold.</summary>
    [ObservableProperty]
    public partial bool IsFlagged { get; private set; }

    /// <summary>The program that started it, or that the one that did has gone.</summary>
    [ObservableProperty]
    public partial string StartedBy { get; private set; } = string.Empty;

    /// <summary>The services it runs, for a service host; empty otherwise.</summary>
    [ObservableProperty]
    public partial string Services { get; private set; } = string.Empty;

    /// <summary>Whether Windows Firewall blocks it with a Windsock rule (full edition).</summary>
    [ObservableProperty]
    public partial string Firewall { get; private set; } = string.Empty;

    /// <summary>Whether a row of the table may offer to close its connection: the full edition.</summary>
#if STORE
    public static Visibility CloseVisibility => Visibility.Collapsed;
#else
    public static Visibility CloseVisibility => Visibility.Visible;
#endif

    /// <summary>Whether a column's filter keeps a row.</summary>
    public bool Matches(ConnectionRowViewModel row) => NetworkColumn.Matches(row) && ServiceColumn.Matches(row);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _rows.CollectionChanged -= OnRowsChanged;
        _facts.Blocklists.Changed -= OnListsChanged;
        _facts.Signatures.Checked -= OnSignatureChecked;
    }

    [RelayCommand]
    private async Task End()
    {
        if (Target() is { } target)
        {
            await InsightActions.EndAsync(Owner(), target).ConfigureAwait(true);
        }
    }

    [RelayCommand]
    private void CheckReputation()
    {
        if (Target() is { } target)
        {
            InsightActions.CheckReputation(Owner(), target, _facts.Reputation);
        }
    }

#if !STORE
    [RelayCommand]
    private void ToggleBlock()
    {
        if (Target() is { } target)
        {
            _facts.Actions.ToggleBlock(Owner(), target);
            Read();
        }
    }

    [RelayCommand]
    private static void CloseConnection(ConnectionRowViewModel? row)
    {
        if (row is not null)
        {
            InsightActions.Close(Owner(), row.Source, row.Host);
        }
    }
#endif

    private AppTarget? Target()
    {
        // The processes come from the window, filtered by the start time it
        // opened with, so a reused id is never offered as this app.
        IReadOnlyList<ProcessUsage> processes = _processes();

        return processes.Count == 0
            ? null
            : AppTarget.For(_name, _path, [.. processes], _facts.Hosts.Current);
    }

    private static Window? Owner() =>
        Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive);

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is { } added)
        {
            Describe(added.OfType<ConnectionRowViewModel>());
        }
    }

    private void OnListsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() =>
        {
            if (!_disposed)
            {
                Describe(_rows);
            }
        });

    private void OnSignatureChecked(object? sender, SignatureCheckedEventArgs e)
    {
        if (string.Equals(e.Path, _path, StringComparison.OrdinalIgnoreCase))
        {
            _dispatcher.BeginInvoke(Sign);
        }
    }

    /// <summary>The far end's network and blocklist, and the service that opened it, on each row.</summary>
    private void Describe(IEnumerable<ConnectionRowViewModel> rows)
    {
        foreach (ConnectionRowViewModel row in rows)
        {
            if (IPAddress.TryParse(row.RemoteAddress, out IPAddress? address))
            {
                AutonomousSystem? system = _facts.Networks.Find(address);
                row.Network = system is null ? "—" : string.Concat(system.Organisation, " · ", system.Label);

                BlocklistHit? hit = _facts.Blocklists.Check(address);
                row.IsListed = hit is not null;
                row.Listed = hit?.Text ?? string.Empty;
            }

#if !STORE
            row.CloseCommand ??= CloseConnectionCommand;

            // Offered only where Windows can do it: a talking IPv4 TCP connection.
            row.CanClose = Windsock.Core.Control.TcpConnectionCloser.CannotClose(row.Source) is null;
#endif

            if (_owners.TryGetValue(row.Key, out SocketOwner owner))
            {
                row.Service = owner.Refused ? "Windows would not say" : owner.Module ?? string.Empty;
            }
        }
    }

    private void Sign()
    {
        if (_path is null)
        {
            Publisher = "Windows will not say which file this is, so its signature cannot be checked.";
            return;
        }

        if (_facts.Signatures.Find(_path) is { } signature)
        {
            Publisher = signature.Summary;
            IsFlagged = signature.State is SignatureState.NoSignature or SignatureState.NotTrusted;
        }
    }

    /// <summary>The parent, the services and the firewall, read off the window's thread.</summary>
    private void Read()
    {
        if (_disposed || Interlocked.CompareExchange(ref _reading, 1, 0) != 0)
        {
            return;
        }

        IReadOnlyList<ProcessUsage> processes = _processes();

        _ = Task.Run(() =>
        {
            try
            {
                ProcessTree tree = ProcessTree.Read();
                ServiceHostMap map = _facts.Hosts.Current;
                string startedBy = Parent(tree, processes);
                string services = processes.Count == 1 && map.Describe(processes[0].ProcessId) is { } label
                    ? label.IsShared
                        ? string.Concat("Runs ", label.Text, ". Its traffic is counted for the whole host; the Service column says which one opened each connection.")
                        : string.Concat("Runs ", label.Text, ".")
                    : string.Empty;

                Dictionary<string, SocketOwner> owners = new(StringComparer.Ordinal);

                if (services.Length > 0)
                {
                    foreach (ProcessUsage usage in processes)
                    {
                        foreach ((string key, SocketOwner owner) in _facts.Owners.Read(usage.ProcessId))
                        {
                            owners[key] = owner;
                        }
                    }
                }

                string firewall = string.Empty;
#if !STORE
                AppTarget? target = Target();

                if (target is not null && InsightActions.RuleFor(target) is { } rule)
                {
                    firewall = string.Concat("Blocked in Windows Firewall (", rule.Name, ").");
                }
#endif

                _dispatcher.BeginInvoke(() =>
                {
                    if (_disposed)
                    {
                        return;
                    }

                    StartedBy = startedBy;
                    Services = services;
                    Firewall = firewall;
                    _owners = owners;
                    ServiceColumn.IsOffered = services.Length > 0;
                    Describe(_rows);
                });
            }
            finally
            {
                Interlocked.Exchange(ref _reading, 0);
            }
        });
    }

    /// <summary>"Started by Explorer (PID 4120)", or that the parent has since ended.</summary>
    private static string Parent(ProcessTree tree, IReadOnlyList<ProcessUsage> processes)
    {
        if (processes.Count == 0)
        {
            return string.Empty;
        }

        HashSet<int> own = [.. processes.Select(usage => usage.ProcessId)];

        // An app's processes start one another; its parent is the first one
        // outside the app.
        foreach (ProcessUsage usage in processes.OrderBy(each => each.Identity.StartedAt ?? DateTimeOffset.MaxValue))
        {
            foreach (ParentProcess parent in tree.AncestorsOf(usage.ProcessId))
            {
                if (own.Contains(parent.ProcessId))
                {
                    continue;
                }

                return parent.Entry is { } entry
                    ? string.Format(CultureInfo.CurrentCulture, "Started by {0} (PID {1})", System.IO.Path.GetFileNameWithoutExtension(entry.ImageName), entry.ProcessId)
                    : string.Format(CultureInfo.CurrentCulture, "Started by a process that has since ended (PID {0})", parent.ProcessId);
            }
        }

        return string.Empty;
    }
}

/// <summary>
/// What the details windows need to describe an app and act on it, in one
/// place, so the launcher hands over one thing.
/// </summary>
public sealed class AppFacts(
    SignatureBook signatures,
    ServiceHosts hosts,
    SocketOwners owners,
    Blocklists blocklists,
    AsnDirectory networks,
    ReputationService reputation,
    InsightActions actions,
    Dispatcher dispatcher)
{
    public SignatureBook Signatures { get; } = signatures;

    public ServiceHosts Hosts { get; } = hosts;

    public SocketOwners Owners { get; } = owners;

    public Blocklists Blocklists { get; } = blocklists;

    public AsnDirectory Networks { get; } = networks;

    public ReputationService Reputation { get; } = reputation;

    public InsightActions Actions { get; } = actions;

    /// <summary>The facts for one window.</summary>
    /// <param name="name">What the window calls the app.</param>
    /// <param name="path">The program's file, when Windows would say.</param>
    /// <param name="processes">The app's processes now, asked each time.</param>
    /// <param name="rows">The window's connection rows, to add notes to.</param>
    public AppFactsViewModel Create(string name, string? path, Func<IReadOnlyList<ProcessUsage>> processes, ObservableCollection<ConnectionRowViewModel> rows) =>
        new(name, path, processes, rows, this, dispatcher);
}
