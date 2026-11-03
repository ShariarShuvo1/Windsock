using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
/// <remarks>
/// Two Windsocks can have the file at once: a second launch reads it while
/// the running one may be saving (2026-10-05). So a read holds the file only
/// for as long as it takes to copy it, and a save that finds the file held -
/// by that copy, an older Windsock, an editor or a scanner - tries again a few
/// times before it gives up, and says so. Holding it any longer, through a
/// first deserialise of a few hundred milliseconds, made a save in that window
/// fail outright, silently.
/// </remarks>
public sealed partial class SettingsStore(string path, ILogger<SettingsStore>? logger = null) : ISettingsStore
{
    /// <summary>How many more times a save tries to put its file in place.</summary>
    internal const int Retries = 3;

    /// <summary>How long a save waits between tries.</summary>
    internal static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private static readonly JsonSerializerOptions Format = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;

    /// <summary>Called before each retry of a save with its number, from 1; for tests.</summary>
    internal Action<int>? BeforeRetry { get; set; }

    /// <inheritdoc />
    public WindsockSettings Load()
    {
        try
        {
            if (!File.Exists(path))
            {
                return new WindsockSettings();
            }

            // Read as a stream, as it always was, so a byte order mark a hand
            // edit left at the front is still passed over.
            using MemoryStream copy = new(ReadShared(path), writable: false);
            WindsockSettings settings =
                JsonSerializer.Deserialize<WindsockSettings>(copy, Format) ?? new WindsockSettings();

            return Settled(settings);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
            or InvalidOperationException or ArgumentException or FormatException or OverflowException)
        {
            return new WindsockSettings();
        }
    }

    /// <summary>
    /// The file's bytes, copied out at once so the file is held only for the
    /// copy, never through the deserialise behind it.
    /// </summary>
    /// <remarks>
    /// Deserialising the first time in a process takes a few hundred
    /// milliseconds of the JIT's, and reading straight from the file held it
    /// all that while. Measured 2026-10-05: no sharing mode lets
    /// <see cref="File.Move(string, string, bool)"/> replace a file someone has
    /// open, so a short hold and the save's retries are what keep a save safe.
    /// </remarks>
    internal static byte[] ReadShared(string path)
    {
        using FileStream stream = OpenShared(path);
        using MemoryStream copy = new();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>
    /// Opens the file to read while letting anyone else have it for writing or
    /// deleting too, so an editor or a backup holding it open does not make the
    /// settings read as the defaults.
    /// </summary>
    internal static FileStream OpenShared(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// Settles what was read. A file whose contents nothing can settle is a
    /// bad file, whatever settling throws, and is treated as one: the app
    /// starts from the defaults rather than failing at every start.
    /// </summary>
    private static WindsockSettings Settled(WindsockSettings settings)
    {
        try
        {
            settings.Taskbar.Settle();

            // A plan with no id, or another's, from a file edited by hand. The
            // id given is the same at every start until the plans are saved,
            // so a level already told is not told again.
            Plans.DataPlan.Tidy(
                settings.Usage.Plans,
                index => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"plan-{index + 1}"));

            return settings;
        }
#pragma warning disable CA1031 // Any failure here means a file nobody can use; the defaults are the answer.
        catch (Exception)
#pragma warning restore CA1031
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

            Replace(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(_logger, path, ex);
        }
    }

    /// <summary>
    /// Puts the written copy in place of the file. Anyone else holding the
    /// file open - another Windsock reading it, an editor, a backup or a virus
    /// scanner - makes that fail for a moment, so it is tried again before the
    /// failure is let through.
    /// </summary>
    private void Replace(string temporary)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(temporary, path, overwrite: true);
                return;
            }
            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < Retries)
            {
                BeforeRetry?.Invoke(attempt + 1);
                Thread.Sleep(RetryDelay);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The settings could not be saved to {Path}")]
    private static partial void LogSaveFailed(ILogger logger, string path, Exception error);
}
