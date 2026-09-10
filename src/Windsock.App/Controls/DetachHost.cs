using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using Windsock.App.Services;

namespace Windsock.App.Controls;

/// <summary>
/// A piece of a window that can be lifted out into one of its own.
/// </summary>
public sealed class DetachHost : ContentControl
{
    private const double Cascade = 26;
    private static int _opened;
    private const double Gap = 80;
    private const double Caption = 40;
    private const double LeastWidth = 420;
    private const double LeastHeight = 260;
    private Window? _window;
    private object? _lifted;

    /// <summary>Lift the content out into a window of its own.</summary>
    public static readonly RoutedUICommand TakeOut =
        new("Open in its own window", nameof(TakeOut), typeof(DetachHost));

    /// <summary>Put it back where it came from.</summary>
    public static readonly RoutedUICommand BringBack =
        new("Bring it back", nameof(BringBack), typeof(DetachHost));

    static DetachHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DetachHost),
            new FrameworkPropertyMetadata(typeof(DetachHost)));

        CommandManager.RegisterClassCommandBinding(
            typeof(DetachHost),
            new CommandBinding(TakeOut, (sender, _) => ((DetachHost)sender).IsDetached = true));

        CommandManager.RegisterClassCommandBinding(
            typeof(DetachHost),
            new CommandBinding(BringBack, (sender, _) => ((DetachHost)sender).IsDetached = false));
    }

    /// <summary>
    /// Whether taking the content out is offered at all.
    /// </summary>
    public static readonly DependencyProperty CanDetachProperty = DependencyProperty.Register(
        nameof(CanDetach),
        typeof(bool),
        typeof(DetachHost),
        new PropertyMetadata(true));

    /// <summary>Whether the content is out in a window of its own.</summary>
    public static readonly DependencyProperty IsDetachedProperty = DependencyProperty.Register(
        nameof(IsDetached),
        typeof(bool),
        typeof(DetachHost),
        new FrameworkPropertyMetadata(
            false,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnDetachedChanged));

    /// <summary>What the window is called while the content is in it.</summary>
    public static readonly DependencyProperty WindowTitleProperty = DependencyProperty.Register(
        nameof(WindowTitle),
        typeof(string),
        typeof(DetachHost),
        new PropertyMetadata("Windsock"));

    /// <summary>What the space says while the content is somewhere else.</summary>
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note),
        typeof(string),
        typeof(DetachHost),
        new PropertyMetadata("This is open in a window of its own."));

    /// <summary>How large the window opens, when the panel gives no clue.</summary>
    public static readonly DependencyProperty WindowWidthProperty = DependencyProperty.Register(
        nameof(WindowWidth),
        typeof(double),
        typeof(DetachHost),
        new PropertyMetadata(960d));

    public static readonly DependencyProperty WindowHeightProperty = DependencyProperty.Register(
        nameof(WindowHeight),
        typeof(double),
        typeof(DetachHost),
        new PropertyMetadata(620d));

    /// <summary>Raised when the content leaves, and when it comes back.</summary>
    public event EventHandler? DetachedChanged;

    /// <summary>Whether taking the content out is offered at all.</summary>
    public bool CanDetach
    {
        get => (bool)GetValue(CanDetachProperty);
        set => SetValue(CanDetachProperty, value);
    }

    /// <summary>Whether the content is out in a window of its own.</summary>
    public bool IsDetached
    {
        get => (bool)GetValue(IsDetachedProperty);
        set => SetValue(IsDetachedProperty, value);
    }

    /// <summary>What the window is called while the content is in it.</summary>
    public string WindowTitle
    {
        get => (string)GetValue(WindowTitleProperty);
        set => SetValue(WindowTitleProperty, value);
    }

    /// <summary>What the space says while the content is somewhere else.</summary>
    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    /// <summary>How wide the window opens.</summary>
    public double WindowWidth
    {
        get => (double)GetValue(WindowWidthProperty);
        set => SetValue(WindowWidthProperty, value);
    }

    /// <summary>How tall the window opens.</summary>
    public double WindowHeight
    {
        get => (double)GetValue(WindowHeightProperty);
        set => SetValue(WindowHeightProperty, value);
    }

    private static void OnDetachedChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is DetachHost host)
        {
            if ((bool)e.NewValue)
            {
                host.Lift();
            }
            else
            {
                host.Land();
            }

            host.DetachedChanged?.Invoke(host, EventArgs.Empty);
        }
    }

    private void Lift()
    {
        if (_window is not null || Content is not { } content)
        {
            return;
        }

        Window? owner = Window.GetWindow(this);
        Rect room = ScreenFit.Room(owner);

        double width = Fits(Math.Max(ActualWidth, WindowWidth), room.Width, LeastWidth);
        double height = Fits(Math.Max(ActualHeight + Caption, WindowHeight), room.Height, LeastHeight);

        Window window = new()
        {
            Title = WindowTitle,
            Width = width,
            Height = height,
            MinWidth = LeastWidth,
            MinHeight = LeastHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            DataContext = DataContext,
        };

        if (owner is not null)
        {
            window.Icon = owner.Icon;
            window.Resources.MergedDictionaries.Add(owner.Resources);
            int step = _opened++ % 6;

            window.Left = Math.Max(room.Left, Math.Min(owner.Left + 40 + (step * Cascade), room.Right - width));
            window.Top = Math.Max(room.Top, Math.Min(owner.Top + 60 + (step * Cascade), room.Bottom - height));
            window.WindowStartupLocation = WindowStartupLocation.Manual;
        }

        _lifted = content;
        Content = null;
        window.Content = content;

        // Closing the window is the other way of saying "put it back".
        window.Closed += (_, _) =>
        {
            if (_window is not null)
            {
                _window = null;
                IsDetached = false;
            }
        };

        _window = window;
        window.Show();
    }

    private static double Fits(double wanted, double screen, double least) =>
        Math.Max(least, Math.Min(wanted, screen - Gap));

    private void Land()
    {
        if (_lifted is null)
        {
            return;
        }

        object content = _lifted;
        _lifted = null;

        if (_window is { } window)
        {
            _window = null;
            window.Content = null;
            window.Close();
        }

        Content = content;
    }

    /// <summary>Closes the window, if the content is out in one.</summary>
    public void Recall()
    {
        if (IsDetached)
        {
            IsDetached = false;
        }
    }
}
