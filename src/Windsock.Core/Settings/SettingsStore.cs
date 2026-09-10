using System.Text.Json;
using System.Text.Json.Serialization;

namespace Windsock.Core.Settings;

/// <summary>Loads and saves the user's choices.</summary>
public interface ISettingsStore
{
    WindsockSettings Load();

    void Save(WindsockSettings settings);
}

/// <summary>
/// Keeps the settings in a JSON file beside the logs.
/// </summary>
public sealed class SettingsStore(string path) : ISettingsStore
{
    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <inheritdoc />
    public WindsockSettings Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new WindsockSettings();
            }

            using FileStream stream = File.OpenRead(path);
            WindsockSettings settings =
                JsonSerializer.Deserialize<WindsockSettings>(stream, Format) ?? new WindsockSettings();
            settings.Taskbar.Settle();

            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new WindsockSettings();
        }
    }

    /// <inheritdoc />
    public void Save(WindsockSettings settings)
    {
        try
        {
            string? directory = Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string temporary = path + ".tmp";

            using (FileStream stream = File.Create(temporary))
            {
                JsonSerializer.Serialize(stream, settings, Format);
            }

            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
