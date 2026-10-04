using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windsock.Core.Apps;

namespace Windsock.App.Services;

/// <summary>
/// Finds the picture an app Windows counted is drawn with: a program's own
/// icon, or the logo a packaged app's manifest names.
/// </summary>
public sealed class AppIconCache : IDisposable
{
    private const int Capacity = 256;
    private const int LogoPixels = 32;

    private readonly ProcessIconCache _programs;
    private readonly ConcurrentDictionary<string, ImageSource?> _logos = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _asking = new(StringComparer.OrdinalIgnoreCase);

    public AppIconCache(ProcessIconCache programs)
    {
        ArgumentNullException.ThrowIfNull(programs);
        _programs = programs;
        _programs.Loaded += OnProgramIconLoaded;
    }

    /// <summary>Raised on a background thread when a picture has been read.</summary>
    public event EventHandler? Loaded;

    /// <summary>
    /// The picture for an app if it is known, otherwise null and a read starts
    /// in the background.
    /// </summary>
    public ImageSource? Find(AppDescription app)
    {
        ArgumentNullException.ThrowIfNull(app);

        if (app.IconPath is not { } path)
        {
            return null;
        }

        if (app.Kind == AppKind.Program)
        {
            return _programs.Find(path);
        }

        if (_logos.TryGetValue(path, out ImageSource? known))
        {
            return known;
        }

        if (_logos.Count < Capacity && _asking.TryAdd(path, 0))
        {
            _ = Task.Run(() => LoadLogo(path));
        }

        return null;
    }

    public void Dispose() => _programs.Loaded -= OnProgramIconLoaded;

    private void OnProgramIconLoaded(object? sender, EventArgs e) => Loaded?.Invoke(this, EventArgs.Empty);

    private void LoadLogo(string path)
    {
        ImageSource? logo = null;

        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            BitmapImage image = new();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = LogoPixels;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            logo = image;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException or System.Runtime.InteropServices.COMException or OverflowException)
        {
            // An unreadable logo is drawn as no logo. The imaging codecs report
            // a damaged file as a COM error.
        }

        _logos[path] = logo;
        _asking.TryRemove(path, out _);

        if (logo is not null)
        {
            Loaded?.Invoke(this, EventArgs.Empty);
        }
    }
}
