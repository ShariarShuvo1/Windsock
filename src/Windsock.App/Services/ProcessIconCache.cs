using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Windsock.App.Services;

/// <summary>
/// Finds the icon a program is drawn with, in the background and once each.
/// </summary>
public sealed class ProcessIconCache
{
    private const int Capacity = 512;

    private readonly ConcurrentDictionary<string, ImageSource?> _icons =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ConcurrentDictionary<string, byte> _asking =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The icon for a program if it is known, otherwise null and a read starts
    /// in the background.
    /// </summary>
    public ImageSource? Find(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        if (_icons.TryGetValue(path, out ImageSource? known))
        {
            return known;
        }

        if (_icons.Count >= Capacity)
        {
            return null;
        }

        // TryAdd is the whole guard: whoever wins starts the one read.
        if (_asking.TryAdd(path, 0))
        {
            _ = Task.Run(() => Load(path));
        }

        return null;
    }

    private void Load(string path)
    {
        ImageSource? icon = Extract(path);
        icon?.Freeze();

        _icons[path] = icon;
        _asking.TryRemove(path, out _);
    }

    private static unsafe BitmapSource? Extract(string path)
    {
        HICON icon = default;
        fixed (char* name = path)
        {
            if (PInvoke.ExtractIconEx(name, 0, null, &icon, 1) == 0 || icon.IsNull)
            {
                return null;
            }
        }

        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                icon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            PInvoke.DestroyIcon(icon);
        }
    }
}
