using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windsock.Core.Formatting;
using Windsock.Core.Settings;

namespace Windsock.Core.Meter;

/// <summary>How a meter looks, apart from what it shows: the part of a layout file that is not places.</summary>
public sealed class MeterLook
{
    /// <summary>How large the figures are.</summary>
    public MeterTextSize TextSize { get; set; } = MeterTextSize.Normal;

    /// <summary>Whether the figures are bold.</summary>
    public bool Bold { get; set; } = true;

    /// <summary>Whether units are written.</summary>
    public bool ShowUnit { get; set; } = true;

    /// <summary>Whether marks are drawn.</summary>
    public bool ShowLabels { get; set; } = true;

    /// <summary>Bytes or bits.</summary>
    public RateFamily Family { get; set; } = RateFamily.Bytes;

    /// <summary>How far rates are scaled.</summary>
    public RateScale Scale { get; set; } = RateScale.Auto;

    /// <summary>A colour painted behind the readings, or nothing.</summary>
    public string? Background { get; set; }

    /// <summary>How rounded the meter is.</summary>
    public double Corner { get; set; } = TaskbarSettings.DefaultCorner;

    /// <summary>Whether the figures carry a soft outline.</summary>
    public bool Shadow { get; set; }

    /// <summary>Whether the meter fits its readings.</summary>
    public bool FitToContents { get; set; } = true;

    /// <summary>Whether a fitted meter keeps a steady width.</summary>
    public bool SteadyWidth { get; set; } = true;

    /// <summary>The fixed width, when the meter is not fitted.</summary>
    public double Width { get; set; } = TaskbarSettings.DefaultWidth;
}

/// <summary>A meter's layout read from a file: its places and its look.</summary>
/// <param name="Rows">Rows.</param>
/// <param name="Columns">Columns.</param>
/// <param name="Slots">What each place shows, row by row.</param>
/// <param name="Look">How it looks.</param>
public sealed record MeterLayout(int Rows, int Columns, IReadOnlyList<MeterSlot> Slots, MeterLook Look);

/// <summary>What reading a layout file found.</summary>
/// <param name="Layout">The layout, or nothing when the file could not be used.</param>
/// <param name="Problem">Why not, in a sentence.</param>
/// <param name="Addresses">
/// Every address the layout's JSON places would read, so the person sees them
/// before saying yes. They stay unread until the meter's feeds are switched
/// on, which a layout file can never do.
/// </param>
public sealed record LayoutRead(MeterLayout? Layout, string? Problem, IReadOnlyList<string> Addresses)
{
    internal static LayoutRead Refused(string problem) => new(null, problem, []);
}

/// <summary>
/// A meter's layout as a small file to share: what each place shows and how
/// the meter looks, nothing about this PC.
/// </summary>
/// <remarks>
/// Written with names rather than numbers, so a person can read it. Read
/// strictly, because it comes from someone else: an unknown name, a level that
/// is not a number, a colour that is not one, or an address a JSON place may
/// not read refuses the whole file and changes nothing. Where the meter sits,
/// what a click does, its shortcuts and the switch that lets JSON places reach
/// their addresses are this PC's own and never travel in a layout.
/// </remarks>
public static class MeterLayoutFile
{
    /// <summary>What the file says it is.</summary>
    public const string FormatName = "Windsock meter layout";

    /// <summary>The version this Windsock writes and the newest it reads.</summary>
    public const int FormatVersion = 1;

    /// <summary>The largest file read: a full layout is a few kilobytes.</summary>
    public const int Largest = 64 * 1024;

    /// <summary>The file name a layout is offered under.</summary>
    public const string Extension = ".windsock-meter";

    private const int MostRotated = MeterSlot.MostTurns;
    private const int LongestText = MeterFeed.LongestText;
    private const int LongestCard = MeterSlot.LongestCard;

    private static readonly JsonSerializerOptions Writing = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions Reading = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
    };

    /// <summary>A meter's layout, as the file's text.</summary>
    public static string Write(TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Document document = new()
        {
            Format = FormatName,
            Version = FormatVersion,
            Rows = settings.Rows,
            Columns = settings.Columns,
            Slots = [.. Enumerable.Range(0, settings.Rows * settings.Columns)
                .Select(index => (index < settings.Slots.Count ? settings.Slots[index] : MeterSlot.Empty()).Copy())],
            Look = new MeterLook
            {
                TextSize = settings.TextSize,
                Bold = settings.Bold,
                ShowUnit = settings.ShowUnit,
                ShowLabels = settings.ShowLabels,
                Family = settings.Family,
                Scale = settings.Scale,
                Background = settings.Background,
                Corner = settings.Corner,
                Shadow = settings.Shadow,
                FitToContents = settings.FitToContents,
                SteadyWidth = settings.SteadyWidth,
                Width = settings.Width,
            },
        };

        return JsonSerializer.Serialize(document, Writing);
    }

    /// <summary>Reads a layout file's text, refusing anything it cannot vouch for.</summary>
    public static LayoutRead Read(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return LayoutRead.Refused("The file is empty.");
        }

        if (text.Length > Largest)
        {
            return LayoutRead.Refused("The file is far larger than any meter layout.");
        }

        Document? document;

        try
        {
            document = JsonSerializer.Deserialize<Document>(text, Reading);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return LayoutRead.Refused("This is not a meter layout Windsock can read.");
        }

        if (document is null || document.Format != FormatName)
        {
            return LayoutRead.Refused("This is not a Windsock meter layout.");
        }

        if (document.Version > FormatVersion)
        {
            return LayoutRead.Refused("This layout was made by a newer Windsock. Update Windsock to use it.");
        }

        if (document.Version < 1)
        {
            return LayoutRead.Refused("This layout does not say which version it is.");
        }

        if (document.Rows is < 1 or > TaskbarSettings.MaximumRows || document.Columns is < 1 or > TaskbarSettings.MaximumColumns)
        {
            return LayoutRead.Refused(string.Create(
                CultureInfo.CurrentCulture,
                $"A meter has at most {TaskbarSettings.MaximumRows} rows and {TaskbarSettings.MaximumColumns} columns."));
        }

        List<MeterSlot> slots = document.Slots ?? [];
        int places = document.Rows * document.Columns;

        if (slots.Count > places)
        {
            return LayoutRead.Refused("The layout has more readings than places for them.");
        }

        List<string> addresses = [];

        foreach (MeterSlot? slot in slots)
        {
            if (Check(slot, addresses) is { } problem)
            {
                return LayoutRead.Refused(problem);
            }
        }

        MeterLook look = document.Look ?? new MeterLook();

        if (CheckLook(look) is { } wrong)
        {
            return LayoutRead.Refused(wrong);
        }

        while (slots.Count < places)
        {
            slots.Add(MeterSlot.Empty());
        }

        return new LayoutRead(new MeterLayout(document.Rows, document.Columns, slots, look), null, addresses);
    }

    /// <summary>
    /// Empties every place that shows a reading this machine or edition does
    /// not offer, and takes such readings out of the places that turn to
    /// them, so a file made elsewhere - a laptop's battery, the full edition's
    /// processor power - cannot put on this meter what this PC cannot read.
    /// Says which readings went, and how many places were left empty.
    /// </summary>
    public static (int Emptied, IReadOnlyList<MeterField> Missing) Fit(MeterLayout layout, Func<MeterField, bool> offered)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(offered);

        List<MeterField> missing = [];
        int emptied = 0;

        foreach (MeterSlot slot in layout.Slots)
        {
            if (slot.Rotate is { } turns)
            {
                foreach (MeterField turn in turns)
                {
                    if (!offered(turn) && !missing.Contains(turn))
                    {
                        missing.Add(turn);
                    }
                }

                turns.RemoveAll(turn => !offered(turn));

                if (turns.Count == 0)
                {
                    slot.Rotate = null;
                }
            }

            if (slot.Field == MeterField.Empty || offered(slot.Field))
            {
                continue;
            }

            if (!missing.Contains(slot.Field))
            {
                missing.Add(slot.Field);
            }

            emptied++;
            slot.Field = MeterField.Empty;
            slot.PlainValue();
            slot.Rotate = null;
            slot.Feed = null;
        }

        return (emptied, missing);
    }

    /// <summary>Puts a layout on a meter, leaving where it sits and what it does alone.</summary>
    public static void Apply(MeterLayout layout, TaskbarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(settings);

        MeterLook look = layout.Look;

        settings.Rows = layout.Rows;
        settings.Columns = layout.Columns;
        settings.Slots = [.. layout.Slots.Select(slot => slot.Copy())];
        settings.TextSize = look.TextSize;
        settings.Bold = look.Bold;
        settings.ShowUnit = look.ShowUnit;
        settings.ShowLabels = look.ShowLabels;
        settings.Family = look.Family;
        settings.Scale = look.Scale;
        settings.Background = look.Background;
        settings.Corner = look.Corner;
        settings.Shadow = look.Shadow;
        settings.FitToContents = look.FitToContents;
        settings.SteadyWidth = look.SteadyWidth;
        settings.Width = look.Width;
    }

    /// <summary>
    /// Whether a colour is one a place may take: nothing, the reading's own,
    /// a named level colour, or #rgb, #rrggbb or #aarrggbb.
    /// </summary>
    /// <remarks>
    /// Hex only, on purpose: WPF's own colour reader would also take
    /// "ContextColor &lt;address&gt; ..." and fetch a colour profile from that
    /// address, and throws on other forms ("sc#..."). Nothing that has not
    /// passed this test is ever read as a colour.
    /// </remarks>
    public static bool IsColour(string? colour)
    {
        if (colour is null || colour == MeterSlot.Own || MeterLevels.IsNamed(colour))
        {
            return true;
        }

        if (colour.Length is not (4 or 5 or 7 or 9) || colour[0] != '#')
        {
            return false;
        }

        foreach (char each in colour.AsSpan(1))
        {
            if (!char.IsAsciiHexDigit(each))
            {
                return false;
            }
        }

        return true;
    }

    private static string? Check(MeterSlot? slot, List<string> addresses)
    {
        if (slot is null)
        {
            return "The layout has a place with nothing written for it.";
        }

        if (!Enum.IsDefined(slot.Field) || !Defined(slot))
        {
            return "The layout names a reading this Windsock does not know.";
        }

        if (!IsColour(slot.LabelColour) || !IsColour(slot.ValueColour) || !IsColour(slot.UnitColour))
        {
            return "The layout gives a colour that is not a colour.";
        }

        if (slot.Levels is { } levels)
        {
            if (levels.Count > MeterLevels.Most)
            {
                return string.Create(CultureInfo.CurrentCulture, $"A place has at most {MeterLevels.Most} colour levels.");
            }

            if (levels.Exists(level => level is null || !double.IsFinite(level.At) || !IsColour(level.Colour) || level.Colour == MeterSlot.Own))
            {
                return "The layout has a colour level that is not a number and a colour.";
            }
        }

        if (slot.Rotate is { } rotate
            && (rotate.Count > MostRotated
                || rotate.Exists(field => !Enum.IsDefined(field) || field is MeterField.Empty or MeterField.Feed or MeterField.Pacer)))
        {
            return string.Create(CultureInfo.CurrentCulture, $"A place rotates through at most {MostRotated} other readings, none of them JSON or the windsock.");
        }

        if (slot.RotateSeconds is < 1 or > 60)
        {
            return "A rotating place changes every 1 to 60 seconds.";
        }

        if (slot.Card is { Length: > LongestCard })
        {
            return "The layout names a graphics card no card is called.";
        }

        if (slot.Feed is { } feed)
        {
            if (FeedAddress.Check(feed.Address).Problem is { } problem)
            {
                return string.Concat("A JSON place's address cannot be used: ", problem);
            }

            if (!JsonField.IsPath(feed.Path))
            {
                return "A JSON place's field is not written the way a field is.";
            }

            if (feed.Unit.Length > LongestText || feed.Label.Length > LongestText)
            {
                return string.Create(CultureInfo.CurrentCulture, $"A JSON place's unit and mark are {LongestText} letters at most.");
            }

            if (feed.Seconds is < MeterFeed.SoonestSeconds or > MeterFeed.LatestSeconds)
            {
                return "A JSON place asks every 15 seconds at the most often, and once a day at the least.";
            }

            addresses.Add(feed.Address.Trim());
        }

        return null;
    }

    private static bool Defined(MeterSlot slot) =>
        Enum.IsDefined(slot.MarkSide) && Enum.IsDefined(slot.ValueSide) && Enum.IsDefined(slot.UnitSide)
        && Enum.IsDefined(slot.MarkAs) && Enum.IsDefined(slot.Graph)
        && (slot.Family is null || Enum.IsDefined(slot.Family.Value))
        && (slot.Scale is null || Enum.IsDefined(slot.Scale.Value))
        && (slot.TextSize is null || Enum.IsDefined(slot.TextSize.Value));

    private static string? CheckLook(MeterLook look)
    {
        if (!Enum.IsDefined(look.TextSize) || !Enum.IsDefined(look.Family) || !Enum.IsDefined(look.Scale))
        {
            return "The layout's look names a choice this Windsock does not know.";
        }

        if (!IsColour(look.Background) || look.Background is MeterSlot.Own || MeterLevels.IsNamed(look.Background))
        {
            return "The layout's background is not a colour.";
        }

        if (!double.IsFinite(look.Corner) || look.Corner is < TaskbarSettings.MinimumCorner or > TaskbarSettings.MaximumCorner)
        {
            return "The layout's roundness is outside what a meter can have.";
        }

        if (!double.IsFinite(look.Width) || look.Width is < TaskbarSettings.MinimumWidth or > TaskbarSettings.MaximumWidth)
        {
            return "The layout's width is outside what a meter can have.";
        }

        return null;
    }

    private sealed class Document
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public int Rows { get; set; }

        public int Columns { get; set; }

        public List<MeterSlot>? Slots { get; set; }

        public MeterLook? Look { get; set; }
    }
}
