using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Windsock.Core;
using Windsock.Core.Feed;
using Windsock.Core.Formatting;
using Windsock.Core.History;
using Windsock.Core.Home;

namespace Windsock.Cli;

/// <summary>What a command ends with: the exit codes scripts can tell apart.</summary>
internal static class Exit
{
    /// <summary>Done.</summary>
    public const int Ok = 0;

    /// <summary>The command line could not be read (System.CommandLine's own code too).</summary>
    public const int Usage = 1;

    /// <summary>Windsock is not running, or its feed is off or has never been on.</summary>
    public const int NoFeed = 2;

    /// <summary>The feed refused: its token was reset.</summary>
    public const int Refused = 3;

    /// <summary>Nothing to read: no history, no device list, no such device.</summary>
    public const int Nothing = 4;

    /// <summary>Anything else that went wrong.</summary>
    public const int Failed = 5;

    /// <summary>A file of Windsock's is there but could not be read: busy, locked or damaged.</summary>
    public const int Unreadable = 6;
}

/// <summary>A period read from the options, or why it could not be.</summary>
internal sealed record PeriodChoice(UsagePeriod? Period, string? Problem);

/// <summary>The commands themselves.</summary>
internal static class Commands
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static PeriodChoice Period(string? name, string? month, string? day, string? from, string? until, string? by)
    {
        UsagePeriod? period = UsagePeriod.Read(name, month, day, from, until, by, DateTimeOffset.Now, TimeZoneInfo.Local, out string? problem);
        return new PeriodChoice(period, problem);
    }

    public static async Task<int> StatusAsync(bool json, CancellationToken cancellationToken)
    {
        using FeedClient client = FeedClient.FromDataFolder();
        FeedReply reply = await client.GetAsync("/v1/status", cancellationToken).ConfigureAwait(false);

        if (reply.Reach != FeedReach.Answered || reply.Body is null)
        {
            return Fail(reply);
        }

        if (json)
        {
            Console.Out.WriteLine(reply.Body);
            return Exit.Ok;
        }

        using JsonDocument document = JsonDocument.Parse(reply.Body);
        StatusText.Write(Console.Out, document.RootElement);
        return Exit.Ok;
    }

    public static async Task<int> WatchAsync(bool json, int count, CancellationToken cancellationToken)
    {
        using FeedClient client = FeedClient.FromDataFolder();
        StrongBox<FeedReply> ended = new();
        int shown = 0;

        try
        {
            await foreach (string line in client.FollowAsync(ended, cancellationToken).ConfigureAwait(false))
            {
                if (json)
                {
                    Console.Out.WriteLine(line);
                }
                else
                {
                    using JsonDocument document = JsonDocument.Parse(line);
                    Console.Out.WriteLine(StatusText.Line(document.RootElement));
                }

                if (count > 0 && ++shown >= count)
                {
                    return Exit.Ok;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Exit.Ok;
        }

        return ended.Value is { Reach: not FeedReach.Answered } reply ? Fail(reply) : Exit.Ok;
    }

    public static int Usage(PeriodChoice choice, bool json)
    {
        if (choice.Period is not { } period)
        {
            return Problem(choice.Problem);
        }

        using ReadOnlyHistory? history = ReadOnlyHistory.Open(WindsockPaths.Database, out string? problem, out bool unreadable);

        if (history is null)
        {
            Console.Error.WriteLine(problem);
            return unreadable ? Exit.Unreadable : Exit.Nothing;
        }

        IReadOnlyList<UsageBucket> buckets;

        try
        {
            buckets = history.Read(period with { Granularity = UsageGranularity.Day });
        }
        catch (SqliteException ex)
        {
            return Unreadable(ex);
        }

        long down = buckets.Sum(each => each.BytesDown);
        long up = buckets.Sum(each => each.BytesUp);
        int minutes = buckets.Sum(each => each.Minutes);

        if (json)
        {
            using Utf8JsonWriter writer = new(Console.OpenStandardOutput(), FeedJson.Options);
            writer.WriteStartObject();
            writer.WriteString("from", FeedJson.Time(period.From));
            writer.WriteString("until", FeedJson.Time(period.Until));
            writer.WriteNumber("bytesDown", down);
            writer.WriteNumber("bytesUp", up);
            writer.WriteNumber("bytesTotal", down + up);
            writer.WriteNumber("minutes", minutes);
            writer.WriteEndObject();
            writer.Flush();
            Console.Out.WriteLine();
            return Exit.Ok;
        }

        Console.Out.WriteLine(string.Concat(StatusText.DateAndTime(period.From), " to ", StatusText.DateAndTime(period.Until)));
        Console.Out.WriteLine(string.Concat("Total     ", SizeFormatter.Format(down + up)));
        Console.Out.WriteLine(string.Concat("Down      ", SizeFormatter.Format(down)));
        Console.Out.WriteLine(string.Concat("Up        ", SizeFormatter.Format(up)));
        Console.Out.WriteLine(string.Concat("Recorded  ", StatusText.Recorded(minutes)));
        return Exit.Ok;
    }

    public static int Export(PeriodChoice choice, string format, FileInfo? output)
    {
        if (choice.Period is not { } period)
        {
            return Problem(choice.Problem);
        }

        using ReadOnlyHistory? history = ReadOnlyHistory.Open(WindsockPaths.Database, out string? problem, out bool unreadable);

        if (history is null)
        {
            Console.Error.WriteLine(problem);
            return unreadable ? Exit.Unreadable : Exit.Nothing;
        }

        // A file is written beside the one asked for and put in its place only
        // once it is whole, so a failed export leaves no half a file behind
        // and does not spoil one that was already there.
        string? part = output is null
            ? null
            : string.Concat(output.FullName, ".", Guid.NewGuid().ToString("N")[..8], ".part");

        try
        {
            using (Stream stream = part is null
                ? Console.OpenStandardOutput()
                : new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                Write(history, stream, period, format);
            }

            if (part is not null)
            {
                File.Move(part, output!.FullName, overwrite: true);
                part = null;
            }
        }
        catch (SqliteException ex)
        {
            return Unreadable(ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(string.Concat("The export could not be written: ", ex.Message));
            return Exit.Failed;
        }
        finally
        {
            Discard(part);
        }

        if (output is not null)
        {
            Console.Error.WriteLine(string.Concat("Wrote ", output.FullName));
        }

        return Exit.Ok;
    }

    private static void Write(ReadOnlyHistory history, Stream stream, UsagePeriod period, string format)
    {
        if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
        {
            history.WriteJson(stream, period, DateTimeOffset.Now);
        }
        else
        {
            using StreamWriter writer = new(stream, Utf8, leaveOpen: true);
            history.WriteCsv(writer, period);
        }
    }

    private static void Discard(string? part)
    {
        if (part is null)
        {
            return;
        }

        try
        {
            File.Delete(part);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static int Unreadable(SqliteException exception)
    {
        Console.Error.WriteLine(string.Concat(
            "The history could not be read: ",
            exception.Message,
            " Windsock may be busy with it; try again in a moment."));
        return Exit.Unreadable;
    }

    public static int Devices(bool json)
    {
        IReadOnlyList<(string Network, HomeDevice Device)> devices;

        try
        {
            devices = SqliteHomeStore.ReadAllReadOnly(SqliteHomeStore.DefaultPath);
        }
        catch (Exception ex) when (HistoryFailure.IsStorage(ex))
        {
            Console.Error.WriteLine(string.Concat("The device list could not be read: ", ex.Message));
            return Exit.Unreadable;
        }

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No devices yet. Turn on looking for devices in Windsock, on a home network Windows calls private.");
            return Exit.Nothing;
        }

        MakerNames makers = new();

        if (json)
        {
            using Utf8JsonWriter writer = new(Console.OpenStandardOutput(), FeedJson.Options with { Indented = true });
            writer.WriteStartArray();

            foreach ((string network, HomeDevice device) in devices)
            {
                writer.WriteStartObject();
                writer.WriteString("network", network);
                writer.WriteString("mac", device.Mac.ToString());
                Text(writer, "name", device.Name);
                Text(writer, "maker", makers.Find(device.Mac));
                Text(writer, "address", device.Address?.ToString());
                Text(writer, "address6", device.Address6?.ToString());
                writer.WriteBoolean("router", device.IsRouter);
                writer.WriteBoolean("privateAddress", device.HasPrivateAddress);
                writer.WriteString("firstSeen", FeedJson.Time(device.FirstSeen));
                writer.WriteString("lastSeen", FeedJson.Time(device.LastSeen));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.Flush();
            Console.Out.WriteLine();
            return Exit.Ok;
        }

        foreach (IGrouping<string, (string Network, HomeDevice Device)> network in devices.GroupBy(each => each.Network, StringComparer.Ordinal))
        {
            Console.Out.WriteLine(network.Key.Length > 0 ? network.Key : "(a network)");

            foreach ((_, HomeDevice device) in network)
            {
                string name = device.Name ?? (device.IsRouter ? "Router" : "-");
                string maker = makers.Find(device.Mac) ?? (device.HasPrivateAddress ? "private address" : "-");

                Console.Out.WriteLine(string.Create(
                    CultureInfo.CurrentCulture,
                    $"  {Cut(name, 28),-28} {device.Address?.ToString() ?? "-",-15} {device.Mac}  {Cut(maker, 30),-30} last seen {StatusText.DateAndTime(device.LastSeen)}"));
            }
        }

        return Exit.Ok;
    }

    public static async Task<int> WakeAsync(string target, CancellationToken cancellationToken)
    {
        HomeLink? link = new HomeLinkReader().Read().Primary;

        if (link is null)
        {
            Console.Error.WriteLine("Not on a home network: Wake-on-LAN is sent only on a network Windows calls private, over a wired or Wi-Fi connection with its own router, never through a VPN.");
            return Exit.Failed;
        }

        MacAddress? mac = MacAddress.TryParse(target, out MacAddress typed) ? typed : null;
        string label = target;

        if (mac is null)
        {
            HomeDevice? named = null;

            try
            {
                named = SqliteHomeStore.ReadAllReadOnly(SqliteHomeStore.DefaultPath)
                    .Select(each => each.Device)
                    .Where(each => string.Equals(each.NetworkKey, link.NetworkKey, StringComparison.Ordinal))
                    .FirstOrDefault(each => string.Equals(each.Name, target, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex) when (HistoryFailure.IsStorage(ex))
            {
                Console.Error.WriteLine(string.Concat("The device list could not be read: ", ex.Message));
                return Exit.Unreadable;
            }

            if (named is null)
            {
                Console.Error.WriteLine(string.Concat("No device called \"", target, "\" on ", link.NetworkName, ". Give its hardware address instead, such as AA-BB-CC-DD-EE-FF."));
                return Exit.Nothing;
            }

            mac = named.Mac;
            label = named.Name ?? target;
        }

        if (!await WakeOnLan.SendAsync(mac.Value, link, cancellationToken).ConfigureAwait(false))
        {
            Console.Error.WriteLine("The wake-up packet could not be sent.");
            return Exit.Failed;
        }

        Console.Out.WriteLine(string.Concat("Sent a wake-up packet to ", label, " (", mac.Value.ToString(), ") on ", link.NetworkName, ". Whether it wakes is up to the device."));
        return Exit.Ok;
    }

    public static async Task<int> FeedAsync(bool showToken, CancellationToken cancellationToken)
    {
        FeedEndpoint? endpoint = FeedEndpoint.Read(FeedEndpoint.DefaultPath);

        if (endpoint is null)
        {
            Console.Error.WriteLine(new FeedReply(FeedReach.NeverSwitchedOn, 0, null).Problem);
            return Exit.NoFeed;
        }

        using FeedClient client = new(endpoint);
        FeedReply reply = await client.GetAsync("/v1", cancellationToken).ConfigureAwait(false);

        Console.Out.WriteLine(string.Concat("Address   ", endpoint.Address.ToString()));
        Console.Out.WriteLine(string.Concat("Running   ", reply.Reach == FeedReach.Answered ? "yes" : "no"));
        Console.Out.WriteLine(string.Concat("Token     ", showToken ? endpoint.Token : "(hidden; add --show-token to print it)"));
        Console.Out.WriteLine(string.Concat("File      ", FeedEndpoint.DefaultPath));
        Console.Out.WriteLine();
        Console.Out.WriteLine("Ask it with the token in a header:");
        Console.Out.WriteLine(string.Concat("  curl -H \"Authorization: Bearer <token>\" ", endpoint.Address.ToString(), "v1/status"));
        Console.Out.WriteLine("Stream overlay for OBS (a browser source):");
        Console.Out.WriteLine(string.Concat("  ", endpoint.Address.ToString(), "overlay?token=<token>"));

        return reply.Reach == FeedReach.Answered ? Exit.Ok : Exit.NoFeed;
    }

    private static int Fail(FeedReply reply)
    {
        Console.Error.WriteLine(reply.Problem);

        return reply.Reach switch
        {
            FeedReach.NeverSwitchedOn or FeedReach.NotRunning => Exit.NoFeed,
            FeedReach.Refused => Exit.Refused,
            _ => Exit.Failed,
        };
    }

    private static int Problem(string? problem)
    {
        Console.Error.WriteLine(problem ?? "That period cannot be read.");
        return Exit.Usage;
    }

    private static string Cut(string text, int width) => text.Length <= width ? text : string.Concat(text.AsSpan(0, width - 1), "~");

    private static void Text(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
