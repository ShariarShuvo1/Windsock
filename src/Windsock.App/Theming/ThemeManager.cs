using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.UI.ViewManagement;
using Windsock.Core.Appearance;
using Windsock.Core.Formatting;
using Windsock.Core.Settings;
using WinColor = Windows.UI.Color;

namespace Windsock.App.Theming;

/// <summary>
/// Puts the app in the chosen colour scheme, and keeps its own palette in step
/// with whatever is on screen: light or dark, a Windows contrast theme, the
/// Windows accent colour when asked for, the density chosen in Settings, and
/// Windows' Text size.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is written into the application's resources, never into a
/// page: the palettes are merged dictionaries swapped whole, and the accent,
/// the type ramp and the density are keys put in the application's own
/// dictionary, which wins over the merged ones. Pages take them all with
/// DynamicResource, so a change reaches an open window without reopening it.
/// </para>
/// <para>
/// A contrast theme wins over everything: its palette maps every key to a
/// system colour (Styles/Palette.Contrast.xaml), the accent override is taken
/// off, and WPF's own theme follows the system so its controls take the
/// contrast colours too, whatever light or dark choice was pinned.
/// </para>
/// <para>
/// Windows says what changed in more than one way and none of them covers
/// everything, so three are heard: <see cref="SystemEvents.UserPreferenceChanged"/>
/// (light and dark, colours, contrast, region), <see cref="SystemParameters.StaticPropertyChanged"/>
/// (contrast), and the WinRT <see cref="UISettings"/> events (accent colour and
/// Text size), which arrive off the UI thread. Each only schedules a re-read on
/// the dispatcher; re-reading what has not changed changes nothing.
/// </para>
/// </remarks>
public sealed class ThemeManager
{
    private static readonly Uri DarkPalette = new("Styles/Palette.Dark.xaml", UriKind.Relative);
    private static readonly Uri LightPalette = new("Styles/Palette.Light.xaml", UriKind.Relative);
    private static readonly Uri ContrastPalette = new("Styles/Palette.Contrast.xaml", UriKind.Relative);

    /// <summary>The sizes of the type ramp in Styles/TextStyles.xaml, at 100 percent.</summary>
    private static readonly double[] Ramp = [9, 10, 10.5, 11, 11.5, 12, 12.5, 13, 13.5, 14, 15, 16, 17, 18, 19, 20, 22, 24, 28, 32, 40, 46];

    /// <summary>WPF's own Fluent font sizes, grown with the ramp so its controls keep pace with ours.</summary>
    private static readonly string[] FluentSizes =
    [
        "ControlContentThemeFontSize",
        "ContentControlFontSize",
        "BodyTextBlockFontSize",
        "CaptionTextBlockFontSize",
        "DefaultDataGridFontSize",
        "SubtitleTextBlockFontSize",
        "TitleTextBlockFontSize",
    ];

    private static readonly string[] AccentKeys = ["AccentColor", "AccentBrush", "AccentSoftBrush"];

    private readonly Application _application;
    private readonly Dictionary<string, double> _fluentBase = new(StringComparer.Ordinal);
    private ThemePreference _preference = ThemePreference.System;
    private AppearanceSettings _appearance = new();
    private UISettings? _ui;
    private ResourceDictionary? _applied;
    private Palette? _appliedPalette;
    private bool _pending;
    private bool _started;
    private bool _stopped;

    public ThemeManager(Application application) => _application = application;

    /// <summary>Raised on the UI thread after the scheme, the accent, the density or the text size changes.</summary>
    public event EventHandler? Changed;

    private enum Palette
    {
        Dark,
        Light,
        Contrast,
    }

    /// <summary>
    /// A Text size factor to draw with instead of Windows' own, for renders
    /// that check the pages at a size this machine is not set to. Read when the
    /// manager starts; null in the app.
    /// </summary>
    internal static double? TextScaleOverride { get; set; }

    /// <summary>
    /// Whether to draw as if a contrast theme were on, for renders that check
    /// the contrast palette on a machine without one. Read when the manager
    /// starts; null in the app.
    /// </summary>
    internal static bool? ContrastOverride { get; set; }

    /// <summary>Whether what is on screen right now is a dark scheme. A dark contrast theme counts.</summary>
    public bool IsDark => _appliedPalette switch
    {
        Palette.Dark => true,
        Palette.Light => false,
        _ => IsDarkTheme(),
    };

    /// <summary>Whether a Windows contrast theme is being followed.</summary>
    public bool IsHighContrast => _appliedPalette == Palette.Contrast;

    /// <summary>Windows' Text size, from 1 to 2.25, as the type ramp is drawn now.</summary>
    public double TextScale { get; private set; } = TextScaling.Normal;

    /// <summary>The density the window is drawn at.</summary>
    public Density Density => _appearance.Density;

    /// <summary>Applies a scheme and begins following system changes.</summary>
    public void Start(ThemePreference preference)
    {
        _preference = preference;
        _started = true;

        Listen();
        ReadFluentSizes();
        ApplyThemeMode();
        ApplyPalette(force: true);
        ApplyTextScale();
        ApplyDensity();

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;

        // WPF's own theme settles a dispatcher turn after it is set; look again then.
        Schedule();
    }

    /// <summary>Switches to a scheme.</summary>
    public void Apply(ThemePreference preference)
    {
        _preference = preference;
        ApplyThemeMode();
        Schedule();
    }

    /// <summary>Takes the accent and density choices, and keeps following them.</summary>
    public void ApplyAppearance(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);

        _appearance = appearance;

        if (!_started)
        {
            return;
        }

        ApplyDensity();
        ApplyAccent();
        Changed?.Invoke(this, EventArgs.Empty);
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
        SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;

        if (_ui is not null)
        {
            _ui.ColorValuesChanged -= OnColourValuesChanged;
            _ui.TextScaleFactorChanged -= OnTextScaleFactorChanged;
            _ui = null;
        }
    }

    /// <summary>Whether a contrast theme is on, or is being drawn as if it were.</summary>
    private static bool HighContrast => ContrastOverride ?? SystemParameters.HighContrast;

    private void Listen()
    {
        try
        {
            _ui = new UISettings();
            _ui.ColorValuesChanged += OnColourValuesChanged;
            _ui.TextScaleFactorChanged += OnTextScaleFactorChanged;
        }
        catch (Exception error) when (error is COMException or TypeLoadException or PlatformNotSupportedException or InvalidCastException)
        {
            // No WinRT settings object: the accent stays Windsock's own and text
            // stays its usual size, which is what Windsock did before either was
            // followed.
            _ui = null;
        }
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_stopped)
        {
            return;
        }

        switch (e.Category)
        {
            case UserPreferenceCategory.Locale:
                // WM_SETTINGCHANGE "intl": the 12 or 24-hour clock or the date
                // format may have changed. .NET keeps what it read until told.
                _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => ClockFormat.Refresh()));
                break;

            case UserPreferenceCategory.General:
            case UserPreferenceCategory.Color:
            case UserPreferenceCategory.Accessibility:
            case UserPreferenceCategory.VisualStyle:
                Schedule();
                break;
        }
    }

    private void OnSystemParameterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_stopped && string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            Schedule();
        }
    }

    private void OnColourValuesChanged(UISettings sender, object args) => Schedule();

    private void OnTextScaleFactorChanged(UISettings sender, object args)
    {
        if (!_stopped)
        {
            _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                ApplyTextScale();
                ApplyDensity();
                Changed?.Invoke(this, EventArgs.Empty);
            }));
        }
    }

    /// <summary>Re-reads the scheme on the dispatcher, once however many notices arrive together.</summary>
    private void Schedule()
    {
        if (_stopped || _pending)
        {
            return;
        }

        _pending = true;
        _application.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _pending = false;
            ApplyThemeMode();
            ApplyPalette(force: HighContrast);
        }));
    }

    private void ApplyThemeMode()
    {
        // Under a contrast theme WPF's controls must follow the system, or a
        // pinned light or dark keeps them in colours the theme overrides.
        ThemePreference wanted = HighContrast ? ThemePreference.System : _preference;

#pragma warning disable WPF0001
        ThemeMode mode = wanted switch
        {
            ThemePreference.Light => ThemeMode.Light,
            ThemePreference.Dark => ThemeMode.Dark,
            _ => ThemeMode.System,
        };

        if (_application.ThemeMode != mode)
        {
            _application.ThemeMode = mode;
        }
#pragma warning restore WPF0001
    }

    /// <summary>
    /// Merges the palette for what is on screen. A contrast palette is merged
    /// again even when it is already the one in use, since the theme's colours
    /// may be what changed and the palette reads them as it loads.
    /// </summary>
    private void ApplyPalette(bool force)
    {
        if (_stopped)
        {
            return;
        }

        bool contrast = HighContrast;

        // The contrast palette sets WPF's text colour too, which is what says
        // light from dark; leaving it, take it away before asking.
        if (!contrast && _appliedPalette == Palette.Contrast && _applied is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_applied);
            _applied = null;
            _appliedPalette = null;
        }

        Palette wanted = contrast ? Palette.Contrast : IsDarkTheme() ? Palette.Dark : Palette.Light;

        if (_appliedPalette == wanted && !(force && wanted == Palette.Contrast))
        {
            // The accent may still have changed, in Windows or in what it is
            // drawn against.
            ApplyAccent();
            return;
        }

        var palette = new ResourceDictionary
        {
            Source = wanted switch
            {
                Palette.Contrast => ContrastPalette,
                Palette.Dark => DarkPalette,
                _ => LightPalette,
            },
        };

        if (_applied is not null)
        {
            _application.Resources.MergedDictionaries.Remove(_applied);
        }

        _application.Resources.MergedDictionaries.Add(palette);
        _applied = palette;
        _appliedPalette = wanted;

        ApplyAccent();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Puts the Windows accent in place of Windsock's cyan when asked for, in
    /// the shade of it that stands out from the page; takes it away otherwise,
    /// and always under a contrast theme. Only the accent: download, upload,
    /// the parts of the PC and the states keep their own colours.
    /// </summary>
    private void ApplyAccent()
    {
        ResourceDictionary resources = _application.Resources;

        if (!_appearance.UseWindowsAccent || IsHighContrast || _ui is null)
        {
            foreach (string key in AccentKeys)
            {
                resources.Remove(key);
            }

            return;
        }

        Rgb ground = Ground();
        bool dark = ColourVision.Luminance(ground) < 0.2;

        // Lighter shades first on a dark page, darker ones on a light page, and
        // the first that reads as text (4.5:1) wins; the accent itself is only
        // the fallback, since Windows' default blue is too dark for a dark page.
        UIColorType[] order = dark
            ? [UIColorType.AccentLight2, UIColorType.AccentLight1, UIColorType.AccentLight3, UIColorType.Accent]
            : [UIColorType.AccentDark1, UIColorType.Accent, UIColorType.AccentDark2, UIColorType.AccentDark3];

        List<Rgb> shades = [];

        try
        {
            foreach (UIColorType type in order)
            {
                WinColor shade = _ui.GetColorValue(type);
                shades.Add(new Rgb(shade.R, shade.G, shade.B));
            }
        }
        catch (COMException)
        {
            return;
        }

        Rgb chosen = ColourVision.FirstReadable(shades, ground, 4.5);
        Color colour = Color.FromRgb(chosen.R, chosen.G, chosen.B);

        if (resources["AccentColor"] is Color current && current == colour && resources.Contains("AccentBrush"))
        {
            return;
        }

        SolidColorBrush brush = new(colour);
        brush.Freeze();

        // The soft wash behind a chosen row or pill, as the palettes mix their
        // own: a little over a fifth of the accent on dark, a little under on light.
        SolidColorBrush soft = new(Color.FromArgb(dark ? (byte)0x38 : (byte)0x26, colour.R, colour.G, colour.B));
        soft.Freeze();

        resources["AccentColor"] = colour;
        resources["AccentBrush"] = brush;
        resources["AccentSoftBrush"] = soft;
    }

    /// <summary>The page's colour in the palette in use: what the accent has to stand out from.</summary>
    private Rgb Ground() =>
        _applied?["SurfaceBrush"] is SolidColorBrush surface
            ? new Rgb(surface.Color.R, surface.Color.G, surface.Color.B)
            : IsDarkTheme() ? new Rgb(0x15, 0x18, 0x1C) : new Rgb(0xF2, 0xF4, 0xF7);

    /// <summary>
    /// Grows the type ramp, and WPF's own font sizes, for Windows' Text size.
    /// At normal size the ramp's own values stand, so nothing is written.
    /// </summary>
    private void ApplyTextScale()
    {
        double factor = TextScaleOverride ?? ReadTextScaleFactor();
        factor = TextScaling.Settle(factor);

        ResourceDictionary resources = _application.Resources;
        TextScale = factor;

        // Read by FlowGrid, whose columns widen with the text they hold.
        if (factor <= TextScaling.Normal)
        {
            resources.Remove("TextScale");
        }
        else
        {
            resources["TextScale"] = factor;
        }

        foreach (double size in Ramp)
        {
            string key = RampKey(size);

            if (factor <= TextScaling.Normal)
            {
                resources.Remove(key);
            }
            else
            {
                resources[key] = TextScaling.Scale(size, factor);
            }
        }

        foreach ((string key, double size) in _fluentBase)
        {
            if (factor <= TextScaling.Normal)
            {
                resources.Remove(key);
            }
            else
            {
                resources[key] = TextScaling.Scale(size, factor);
            }
        }
    }

    private double ReadTextScaleFactor()
    {
        try
        {
            return _ui?.TextScaleFactor ?? new UISettings().TextScaleFactor;
        }
        catch (Exception error) when (error is COMException or TypeLoadException or PlatformNotSupportedException or InvalidCastException)
        {
            return TextScaling.Normal;
        }
    }

    /// <summary>
    /// Writes the compact values over the comfortable ones, or takes them away,
    /// and lifts the heights that hold a line of text to fit it at the current
    /// Text size.
    /// </summary>
    private void ApplyDensity()
    {
        ResourceDictionary resources = _application.Resources;
        bool compact = _appearance.Density == Density.Compact;

        // A row holds a line of 12-point text with a little air; it never gets
        // shorter than its density's own height, and grows when the text does.
        double line = Math.Ceiling(TextScaling.Scale(12, TextScale) * 1.34);

        Set(resources, "ControlHeight", Math.Max(compact ? 26 : 30, line + 8));
        Set(resources, "TableRowHeight", Math.Max(compact ? 24 : 30, line + 6));
        Set(resources, "TableHeaderHeight", Math.Max(compact ? 30 : 36, line + 12));

        if (compact)
        {
            resources["PlatePadding"] = new Thickness(13, 8, 11, 9);
            resources["InsetPlatePadding"] = new Thickness(11, 7, 9, 8);
            resources["BandPadding"] = new Thickness(15, 11, 15, 12);
            resources["BandRowPadding"] = new Thickness(0, 5, 0, 5);
            resources["BandRowMinHeight"] = 40.0;
            resources["TilePadding"] = new Thickness(11, 6, 11, 7);
        }
        else
        {
            foreach (string key in new[] { "PlatePadding", "InsetPlatePadding", "BandPadding", "BandRowPadding", "BandRowMinHeight", "TilePadding" })
            {
                resources.Remove(key);
            }
        }

        static void Set(ResourceDictionary resources, string key, double value) => resources[key] = value;
    }

    /// <summary>Notes WPF's own font sizes before anything grows them, as the base to grow from.</summary>
    private void ReadFluentSizes()
    {
        if (_fluentBase.Count > 0)
        {
            return;
        }

        foreach (string key in FluentSizes)
        {
            if (_application.TryFindResource(key) is double size && size > 0)
            {
                _fluentBase[key] = size;
            }
        }
    }

    /// <summary>The resource key of a size on the ramp: <c>TextSize12_5</c> for 12.5.</summary>
    internal static string RampKey(double size) =>
        string.Concat("TextSize", size.ToString(CultureInfo.InvariantCulture).Replace('.', '_'));

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
