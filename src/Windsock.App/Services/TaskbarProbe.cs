using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Threading;
using Microsoft.Extensions.Logging;
using Windsock.Core.Docking;

namespace Windsock.App.Services;

/// <summary>What UI Automation found on one taskbar.</summary>
/// <param name="Bar">The taskbar window that was looked at.</param>
/// <param name="Obstacles">
/// Its buttons, each in the bar's own coordinates so the answer still holds
/// after an auto-hiding bar has slid.
/// </param>
internal sealed record ProbeAnswer(nint Bar, IReadOnlyList<TaskbarObstacle> Obstacles);

/// <summary>
/// Looks at a Windows 11 taskbar's buttons through UI Automation, off the UI
/// thread, and only when asked.
/// </summary>
/// <remarks>
/// The XAML taskbar draws its buttons inside one window, so nothing but UI
/// Automation can say where Widgets or the last app button is. Asking is a
/// call into Explorer that took 70 to 140 ms on the development machine, and
/// a hung Explorer could hold it far longer, so it never runs on the UI
/// thread, never more than one at a time, and only when the meter has reason
/// to think the bar changed - never on the patrol's every tick. All
/// properties come back in one cached request, and nothing live is kept.
/// </remarks>
internal sealed partial class TaskbarProbe : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly List<nint> _asked = [];
    private readonly AutoResetEvent _wake = new(false);
    private Thread? _thread;
    private bool _stopping;
    private bool _told;
    private bool _failed;

    public TaskbarProbe(Dispatcher dispatcher, ILogger logger)
    {
        _dispatcher = dispatcher;
        _logger = logger;
    }

    /// <summary>Raised on the UI thread with what a probe found.</summary>
    public event EventHandler<ProbeAnswer>? Answered;

    /// <summary>Asks for a bar to be looked at, unless it is already waiting its turn.</summary>
    public void Ask(nint bar)
    {
        if (bar == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_stopping || _asked.Contains(bar))
            {
                return;
            }

            _asked.Add(bar);

            if (_thread is null)
            {
                _thread = new Thread(Run)
                {
                    IsBackground = true,
                    Name = "Windsock taskbar probe",
                };
                _thread.SetApartmentState(ApartmentState.MTA);
                _thread.Start();
            }
        }

        _wake.Set();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_gate)
        {
            _stopping = true;
            _asked.Clear();
        }

        // Not joined: a probe stuck in a hung Explorer must not hold up the
        // app closing, and the thread is a background one.
        _wake.Set();
    }

    private void Run()
    {
        while (true)
        {
            _wake.WaitOne();

            while (true)
            {
                nint bar;

                lock (_gate)
                {
                    if (_stopping)
                    {
                        return;
                    }

                    if (_asked.Count == 0)
                    {
                        break;
                    }

                    bar = _asked[0];
                    _asked.RemoveAt(0);
                }

                if (Look(bar) is { } answer)
                {
                    _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => Answered?.Invoke(this, answer)));
                }
            }
        }
    }

    private ProbeAnswer? Look(nint bar)
    {
        Stopwatch watch = Stopwatch.StartNew();

        try
        {
            CacheRequest cache = new()
            {
                AutomationElementMode = AutomationElementMode.None,
                TreeScope = TreeScope.Element,
            };

            cache.Add(AutomationElement.BoundingRectangleProperty);
            cache.Add(AutomationElement.AutomationIdProperty);
            cache.Add(AutomationElement.ClassNameProperty);

            // The root is fetched before the cache request is active: under
            // it, elements come back with no live reference, and searching
            // from such an element throws.
            AutomationElement root = AutomationElement.FromHandle(bar);
            AutomationElementCollection buttons;

            using (cache.Activate())
            {
                buttons = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
            }

            if (Taskbars.Rect(bar) is not { } frame)
            {
                return null;
            }

            List<TaskbarObstacle> found = new(buttons.Count);

            foreach (AutomationElement button in buttons)
            {
                if (button.Cached.BoundingRectangle is not { IsEmpty: false } box
                    || !double.IsFinite(box.Width) || !double.IsFinite(box.Height))
                {
                    continue;
                }

                PixelRect rect = Round(box).RelativeTo(frame);

                if (rect.IsEmpty)
                {
                    continue;
                }

                found.Add(new TaskbarObstacle(
                    rect,
                    TaskbarButtons.Classify(button.Cached.AutomationId, button.Cached.ClassName)));
            }

            if (!_told)
            {
                _told = true;
                LogFirstProbe(found.Count, watch.ElapsedMilliseconds);
            }

            return new ProbeAnswer(bar, found);
        }
#pragma warning disable CA1031 // UI Automation can fail in more ways than it
        // documents - a bar mid-rebuild, a hung Explorer, a COM error - and
        // every one means the same: nothing new is known about the buttons,
        // and what was known before stands.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            if (_failed)
            {
                LogProbeFailedAgain(ex);
            }
            else
            {
                _failed = true;
                LogProbeFailed(ex);
            }

            return null;
        }
    }

    private static PixelRect Round(Rect box) => new(
        (int)Math.Round(box.Left),
        (int)Math.Round(box.Top),
        (int)Math.Round(box.Right),
        (int)Math.Round(box.Bottom));

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Taskbar buttons read through UI Automation: {Count} in {Milliseconds} ms")]
    private partial void LogFirstProbe(int count, long milliseconds);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Taskbar buttons could not be read through UI Automation; the meter places itself without them")]
    private partial void LogProbeFailed(Exception ex);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message = "Taskbar buttons could not be read through UI Automation")]
    private partial void LogProbeFailedAgain(Exception ex);
}
