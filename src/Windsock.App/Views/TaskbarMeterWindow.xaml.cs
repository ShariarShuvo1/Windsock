using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Windsock.App.Services;
using Windsock.App.ViewModels;
using Windsock.Core.Docking;

namespace Windsock.App.Views;

/// <summary>A click on the meter: which button, and how many presses in quick succession.</summary>
/// <param name="Button">The button.</param>
/// <param name="Count">One for a click, two for a double click.</param>
public readonly record struct MeterPress(MouseButton Button, int Count);

/// <summary>
/// The window that shows the live readings inside the Windows taskbar.
/// </summary>
public partial class TaskbarMeterWindow : Window
{
    private double _rescale = 1;
    private ToolTip? _card;

    /// <summary>
    /// How many presses in quick succession the last left press was. Only
    /// the press knows: a button coming up always says one.
    /// </summary>
    private int _presses = 1;

    public TaskbarMeterWindow(TaskbarMeterViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        DataContext = viewModel;

        Root.MouseLeftButtonDown += OnPressing;
        Root.MouseLeftButtonUp += OnPressed;
        Root.MouseUp += OnOtherPressed;

        // A rotating place holds still on the reading being read.
        Root.MouseEnter += (_, _) => viewModel.Held = true;
        Root.MouseLeave += (_, _) => viewModel.Held = false;

        Root.ToolTipOpening += (_, _) => CardOpening?.Invoke(this, EventArgs.Empty);
        Root.ToolTipClosing += (_, _) => CardClosed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Raised when the reader clicks the meter, with which button and how many times.</summary>
    public event EventHandler<MeterPress>? Pressed;

    /// <summary>Raised when the reader right-clicks the meter, asking for its menu.</summary>
    public event EventHandler? MenuRequested;

    /// <summary>Raised when the reader has dragged a floating meter somewhere, with where it now is.</summary>
    public event EventHandler<PixelRect>? Dropped;

    /// <summary>Raised when WPF has started drawing the window at another DPI.</summary>
    public event EventHandler? ScaleChanged;

    /// <summary>Raised as the hover card is about to open.</summary>
    public event EventHandler? CardOpening;

    /// <summary>Raised when the hover card has closed.</summary>
    public event EventHandler? CardClosed;

    /// <summary>This window's handle, or zero before it has one.</summary>
    public nint Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// Whether a press may drag the window: a floating meter that is not
    /// locked. A docked meter never moves by hand.
    /// </summary>
    public bool CanDrag { get; set; }

    /// <summary>Whether a drag is under way, so nothing else moves the window meanwhile.</summary>
    public bool IsDragging { get; private set; }

    /// <summary>
    /// The size the readings take with all the room they want, in
    /// device-independent pixels, their margins included.
    /// </summary>
    public Size Natural()
    {
        Readings.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return Readings.DesiredSize;
    }

    /// <summary>
    /// The DPI WPF is drawing this window at, which can lag behind the taskbar
    /// it sits in until WPF hears that it changed.
    /// </summary>
    public double DrawnDpi => VisualTreeHelper.GetDpi(this).PixelsPerInchX;

    /// <summary>
    /// Puts the hover card on the meter in place of the readings' text,
    /// opening on the side away from the bar's edge.
    /// </summary>
    public void Card(FrameworkElement view, TaskbarEdge? edge)
    {
        ArgumentNullException.ThrowIfNull(view);

        _card ??= new ToolTip
        {
            Style = (Style)FindResource("MeterCardStyle"),
            Content = view,
        };

        _card.Placement = edge switch
        {
            TaskbarEdge.Top => PlacementMode.Bottom,
            TaskbarEdge.Left => PlacementMode.Right,
            TaskbarEdge.Right => PlacementMode.Left,
            _ => PlacementMode.Top,
        };

        _card.VerticalOffset = edge is TaskbarEdge.Top ? 6 : edge is null or TaskbarEdge.Bottom ? -6 : 0;
        _card.HorizontalOffset = edge switch
        {
            TaskbarEdge.Left => 6,
            TaskbarEdge.Right => -6,
            _ => 0,
        };

        if (!ReferenceEquals(Root.ToolTip, _card))
        {
            Root.ToolTip = _card;
        }
    }

    /// <summary>
    /// Scales everything in the window by a factor of its own: the last
    /// resort for a window WPF is drawing at a DPI other than its taskbar's.
    /// </summary>
    /// <remarks>
    /// The figures are sized from the taskbar's DPI. When WPF has not caught up
    /// with that - a child window is never sent WM_DPICHANGED, and the message
    /// it is sent instead may not cross from Explorer - the ratio between the
    /// two puts the drawing back to the size it was measured at. Text drawn
    /// this way is scaled rather than laid out at its size, which is a little
    /// softer, and the right size.
    /// </remarks>
    public void Rescale(double factor)
    {
        double wanted = double.IsFinite(factor) && factor > 0 ? factor : 1;

        if (Math.Abs(wanted - _rescale) < 0.001)
        {
            return;
        }

        _rescale = wanted;
        Root.LayoutTransform = Math.Abs(wanted - 1) < 0.001 ? Transform.Identity : new ScaleTransform(wanted, wanted);
    }

    /// <inheritdoc />
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPressing(object sender, MouseButtonEventArgs e)
    {
        _presses = Math.Max(1, e.ClickCount);

        if (!CanDrag)
        {
            return;
        }

        e.Handled = true;

        int count = e.ClickCount;
        PixelRect? before = Taskbars.Rect(Handle);
        IsDragging = true;

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // The button was already up by the time the drag would have
            // started, which makes it a click.
        }
        finally
        {
            IsDragging = false;
        }

        // The move loop swallows the button coming up, so a press that never
        // moved is answered here, as the click it was.
        if (Taskbars.Rect(Handle) is { } after && before is { } start
            && (Math.Abs(after.Left - start.Left) > 1 || Math.Abs(after.Top - start.Top) > 1))
        {
            Dropped?.Invoke(this, after);
            return;
        }

        Pressed?.Invoke(this, new MeterPress(MouseButton.Left, count));
    }

    private void OnPressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        e.Handled = true;

        if (!CanDrag)
        {
            Pressed?.Invoke(this, new MeterPress(MouseButton.Left, _presses));
        }
    }

    private void OnOtherPressed(object sender, MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.ChangedButton)
        {
            case MouseButton.Middle:
                e.Handled = true;
                Pressed?.Invoke(this, new MeterPress(MouseButton.Middle, 1));
                break;

            case MouseButton.Right:
                e.Handled = true;
                CloseCard();
                MenuRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    private void CloseCard()
    {
        if (_card is { IsOpen: true })
        {
            _card.IsOpen = false;
        }
    }
}
