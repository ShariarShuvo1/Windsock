using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;
using Windsock.App.ViewModels;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Networking;
using Windsock.Core.Settings;

namespace Windsock.App.Services;

/// <summary>
/// Paints the rates into the notification-area icon, once a second, for
/// people who keep the meter off.
/// </summary>
/// <remarks>
/// <para>
/// Two short lines - download over upload, each in its own colour - at the
/// size Windows draws a small icon at the taskbar's DPI
/// (GetSystemMetricsForDpi, SM_CXSMICON: 16 at 100%, 24 at 150%, 32 at 200%),
/// so the figures are drawn for the pixels they land on rather than scaled.
/// The picture is encoded as PNG and made an icon with
/// CreateIconFromResourceEx, which reads PNG icons since Vista; that keeps
/// the transparency WPF drew without handling device bitmaps by hand.
/// </para>
/// <para>
/// Only while the option is on and the icon is shown; nothing is painted when
/// the figures have not changed.
/// </para>
/// </remarks>
internal sealed class TrayFiguresPainter
{
    private const int SmallIcon = 49;
    private const uint IconVersion = 0x00030000;

    private static readonly FontFamily Face = new("Segoe UI");

    private readonly TrayIcon _tray;
    private readonly ThroughputMonitor _network;
    private readonly WindsockSettings _settings;
    private readonly Dispatcher _dispatcher;
    private readonly Func<string?> _lead;
    private string _drawn = string.Empty;
    private DateTimeOffset _paintedAt = DateTimeOffset.MinValue;
    private bool _running;
    private bool _queued;

    /// <param name="tray">The icon to draw into.</param>
    /// <param name="network">Where the rates come from.</param>
    /// <param name="settings">The meter's family and scale.</param>
    /// <param name="dispatcher">The interface thread.</param>
    /// <param name="lead">
    /// The line the tooltip leads with when there is one - "Internet
    /// unreachable since 21:04" - as the meter's own tooltip does, so the
    /// rates never push the news that the internet is gone out of the tip.
    /// </param>
    public TrayFiguresPainter(TrayIcon tray, ThroughputMonitor network, WindsockSettings settings, Dispatcher dispatcher, Func<string?> lead)
    {
        _tray = tray;
        _network = network;
        _settings = settings;
        _dispatcher = dispatcher;
        _lead = lead;
    }

    /// <summary>Starts or stops painting, as the setting says.</summary>
    public void Follow(bool wanted)
    {
        if (wanted == _running)
        {
            return;
        }

        _running = wanted;

        if (wanted)
        {
            _network.SampleTaken += OnSample;
            _drawn = string.Empty;
            Paint();
            return;
        }

        _network.SampleTaken -= OnSample;
        _drawn = string.Empty;
        _tray.SetFigures(0);
    }

    private void OnSample(object? sender, ThroughputSample sample)
    {
        if (_queued)
        {
            return;
        }

        _queued = true;
        _dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false;

            if (_running && DateTimeOffset.UtcNow - _paintedAt >= TimeSpan.FromSeconds(1))
            {
                Paint();
            }
        }));
    }

    private void Paint()
    {
        _paintedAt = DateTimeOffset.UtcNow;

        RateFamily family = _settings.Taskbar.Family;
        ThroughputSample latest = _network.Latest;
        string down = TrayFigures.Compact(latest.DownloadBytesPerSecond, family);
        string up = TrayFigures.Compact(latest.UploadBytesPerSecond, family);
        int dpi = Taskbars.Dpi((nint)PInvoke.FindWindow("Shell_TrayWnd", null));
        bool dark = TaskbarTheme.IsDark();
        string key = string.Concat(down, "\n", up, "\n", dpi.ToString(CultureInfo.InvariantCulture), dark ? "d" : "l");

        string rates = string.Concat("↓ ", RateFormatter.Format(latest.DownloadBytesPerSecond, family, _settings.Taskbar.Scale), "  ↑ ", RateFormatter.Format(latest.UploadBytesPerSecond, family, _settings.Taskbar.Scale));

        _tray.SetTooltip(_lead() is { Length: > 0 } lead
            ? string.Concat("Windsock · ", lead, "\n", rates)
            : string.Concat("Windsock · ", rates));

        if (key == _drawn)
        {
            return;
        }

        _drawn = key;
        _tray.SetFigures(Draw(down, up, dpi, dark));
    }

    /// <summary>Draws the two lines and makes them an icon, or zero when Windows will not.</summary>
    private static unsafe nint Draw(string down, string up, int dpi, bool dark)
    {
        int size = Math.Max(16, PInvoke.GetSystemMetricsForDpi((SYSTEM_METRICS_INDEX)SmallIcon, (uint)dpi));
        double line = size / 2.0;
        double font = Math.Round(line * 1.02, 1);
        Typeface typeface = new(Face, FontStyles.Normal, FontWeights.SemiBold, FontStretches.SemiCondensed);
        DrawingVisual visual = new();

        TextOptions.SetTextFormattingMode(visual, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(visual, TextRenderingMode.Grayscale);

        using (DrawingContext context = visual.RenderOpen())
        {
            Line(context, down, typeface, font, MeterPalette.Own(MeterField.Download, MeterGroup.Network, dark), size, 0, line);
            Line(context, up, typeface, font, MeterPalette.Own(MeterField.Upload, MeterGroup.Network, dark), size, line, line);
        }

        RenderTargetBitmap bitmap = new(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        using MemoryStream stream = new();
        encoder.Save(stream);
        byte[] png = stream.ToArray();

        fixed (byte* bits = png)
        {
            return PInvoke.CreateIconFromResourceEx(bits, (uint)png.Length, true, IconVersion, size, size, IMAGE_FLAGS.LR_DEFAULTCOLOR);
        }
    }

    /// <summary>One line, squeezed to the icon's width if it would not fit, centred in its half.</summary>
    private static void Line(DrawingContext context, string text, Typeface typeface, double font, Brush ink, int size, double top, double height)
    {
        FormattedText formatted = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, font, ink, 1.0);
        double squeeze = formatted.WidthIncludingTrailingWhitespace > size ? size / formatted.WidthIncludingTrailingWhitespace : 1;
        double left = (size - (formatted.WidthIncludingTrailingWhitespace * squeeze)) / 2;
        double y = top + ((height - formatted.Height) / 2);

        if (squeeze < 1)
        {
            context.PushTransform(new ScaleTransform(squeeze, 1, 0, 0));
            context.DrawText(formatted, new Point(left / squeeze, y));
            context.Pop();
            return;
        }

        context.DrawText(formatted, new Point(left, y));
    }
}
