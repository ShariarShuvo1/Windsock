using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windsock.Core.Settings;

namespace Windsock.App.Theming;

/// <summary>
/// Puts the app in the chosen colour scheme, and keeps its own palette in step
/// with whatever is on screen.
/// </summary>
public sealed class ThemeManager
{
    private static readonly Uri DarkPalette = new("Styles/Palette.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightPalette = new("Styles/Palette.Light.xaml", UriKind.Relative);

    private readonly Application _application;
    private ResourceDictionary? _applied;
    private bool? _appliedIsDark;
    private bool _stopped;

    public ThemeManager(Application application) => _application = application;

    /// <summary>Raised on the UI thread after the scheme changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether what is on screen right now is the dark scheme.</summary>
    public bool IsDark => _appliedIsDark ?? IsDarkTheme();

    /// <summary>Applies a scheme and begins following system changes.</summary>
    public void Start(ThemePreference preference)
    {
        Apply(preference);
        ApplyPalette();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>Switches to a scheme.</summary>
    public void Apply(ThemePreference preference)
    {
#pragma warning disable WPF0001
        _application.ThemeMode = preference switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };
#pragma warning restore WPF0001
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyPalette));
    }

    /// <summary>Stops following system changes. Safe to call more than once.</summary>
    public void Stop()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_stopped || e.Category != UserPreferenceCategory.General)
        {
            return;
        }
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ApplyPalette));
    }

    private void ApplyPalette()
    {
        if (_stopped)
        {
            return;
        }

        bool isDark = IsDarkTheme();
        if (_appliedIsDark == isDark)
        {
            return;
        }

        var palette = new ResourceDictionary { Source = isDark ? DarkPalette : LightPalette };

        if (_applied is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_applied);
        }

        _application.Resources.MergedDictionaries.Add(palette);
        _applied = palette;
        _appliedIsDark = isDark;

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool IsDarkTheme()
    {
        if (_application.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush brush)
        {
            // Primary text is near-white on dark surfaces and near-black on light.
            double luminance =
                (0.2126 * brush.Color.R) + (0.7152 * brush.Color.G) + (0.0722 * brush.Color.B);
            return luminance > 128;
        }

        return true;
    }
}
