using System.Text.Json;
using System.Text.Json.Serialization;

using Windsock.Core.Formatting;

namespace Windsock.Core.Settings;

/// <summary>How a place draws its last minute, if at all.</summary>
public enum MeterGraph
{
    /// <summary>No graph.</summary>
    None,

    /// <summary>A line.</summary>
    Line,

    /// <summary>A bar a second.</summary>
    Bars,
}

/// <summary>
/// A level a reading can pass, and the colour it takes past it.
/// </summary>
public sealed class MeterLevel
{
    /// <summary>
    /// Where the level is, in the reading's own base unit: bytes a second for
    /// a rate, bytes for a total, percent, degrees Celsius, milliseconds,
    /// minutes for a battery's time.
    /// </summary>
    public double At { get; set; }

    /// <summary>
    /// The colour past it: #rrggbb, or one of the named colours that follow
    /// the taskbar's theme (good, fair, slow). A null written in a file
    /// becomes fair, so nothing downstream meets a level without a colour.
    /// </summary>
    public string Colour
    {
        get;
        set => field = string.IsNullOrWhiteSpace(value) ? "fair" : value;
    } = "fair";

    /// <summary>
    /// Whether the level is passed going down rather than up: a battery
    /// running low, a signal getting weak.
    /// </summary>
    public bool Below { get; set; }

    /// <summary>A copy, so a reflow can move a cell without sharing it.</summary>
    public MeterLevel Copy() => (MeterLevel)MemberwiseClone();
}

/// <summary>
/// Where a place's JSON value comes from: an address the person typed, and
/// the field in what it answers.
/// </summary>
/// <remarks>
/// Every text is kept as text: a null in a settings or layout file becomes
/// empty as it is read, so nothing that writes or keys a feed meets one.
/// </remarks>
public sealed class MeterFeed
{
    /// <summary>The address: https, http on this PC or the local network, or a file.</summary>
    public string Address
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>The field, written like data.items[0].price.</summary>
    public string Path
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>What to write after the value, if anything.</summary>
    public string Unit
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>The short mark written before the value, in place of a picture.</summary>
    public string Label
    {
        get;
        set => field = value ?? string.Empty;
    } = string.Empty;

    /// <summary>How often to ask, in seconds. Never more often than every fifteen.</summary>
    public int Seconds { get; set; } = 60;

    /// <summary>The most often a feed is asked, in seconds.</summary>
    public const int SoonestSeconds = 15;

    /// <summary>The least often a feed is asked, in seconds: once a day.</summary>
    public const int LatestSeconds = 86_400;

    /// <summary>The longest unit or mark a feed may write.</summary>
    public const int LongestText = 16;

    /// <summary>A copy, so a reflow can move a cell without sharing it.</summary>
    public MeterFeed Copy() => (MeterFeed)MemberwiseClone();
}

/// <summary>
/// One place on the meter: what it shows, and how it shows it.
/// </summary>
public sealed class MeterSlot
{
    /// <summary>What this place shows.</summary>
    public MeterField Field { get; set; }

    /// <summary>Whether this reading counts in bytes or bits.</summary>
    public RateFamily? Family { get; set; }

    /// <summary>How far this reading is scaled before it is shown.</summary>
    public RateScale? Scale { get; set; }

    /// <summary>Whether the unit is written after this figure.</summary>
    public bool? ShowUnit { get; set; }

    /// <summary>Whether this reading is marked with what it is.</summary>
    public bool? ShowLabel { get; set; }

    /// <summary>Which end of the place the mark sits at.</summary>
    public MeterSide MarkSide { get; set; } = MeterSide.Right;

    /// <summary>Which end of the place the figure sits at.</summary>
    public MeterSide ValueSide { get; set; } = MeterSide.Right;

    /// <summary>Which end of the place the unit sits at.</summary>
    public MeterSide UnitSide { get; set; } = MeterSide.Right;

    /// <summary>
    /// How many of the three parts are drawn against the left edge.
    /// </summary>
    [JsonIgnore]
    public int PartsOnTheLeft
    {
        get
        {
            int counted = 0;

            foreach (MeterSide side in (MeterSide[])[MarkSide, ValueSide, UnitSide])
            {
                if (side != MeterSide.Left)
                {
                    break;
                }

                counted++;
            }

            return counted;
        }
    }

    /// <summary>Whether the mark is a picture or the short name.</summary>
    public MeterMarkStyle MarkAs { get; set; } = MeterMarkStyle.Icon;

    /// <summary>How large this figure is.</summary>
    public MeterTextSize? TextSize { get; set; }

    /// <summary>Whether this figure carries extra weight.</summary>
    public bool? Bold { get; set; }

    /// <summary>
    /// The colour a part takes to be drawn in whatever colour its reading has
    /// everywhere else in Windsock.
    /// </summary>
    public const string Own = "own";

    /// <summary>The colour of the label, as #rrggbb or <see cref="Own"/>.</summary>
    public string? LabelColour { get; set; }

    /// <summary>The colour of the figure, as #rrggbb or <see cref="Own"/>.</summary>
    public string? ValueColour { get; set; }

    /// <summary>The colour of the unit, as #rrggbb or <see cref="Own"/>.</summary>
    public string? UnitColour { get; set; }

    /// <summary>
    /// Colours the figure takes past levels the person set, or nothing for
    /// the field's own levels (a response time has some; most have none).
    /// </summary>
    public List<MeterLevel>? Levels { get; set; }

    /// <summary>
    /// Which graphics card a graphics reading follows: nothing for the
    /// busiest, <see cref="EveryCard"/> for each in turn, otherwise a card's name.
    /// </summary>
    public string? Card { get; set; }

    /// <summary>The card choice that shows every card's figure side by side.</summary>
    public const string EveryCard = "*";

    /// <summary>The longest a card's name may be; no card is called anything longer.</summary>
    public const int LongestCard = 128;

    /// <summary>The most readings a place takes turns with, besides its own.</summary>
    public const int MostTurns = 6;

    /// <summary>Other readings this place turns to in turn, after its own.</summary>
    public List<MeterField>? Rotate { get; set; }

    /// <summary>How long each reading in a rotation stays, in seconds.</summary>
    public int RotateSeconds { get; set; } = 4;

    /// <summary>Whether this place draws its last minute as a graph, and how.</summary>
    public MeterGraph Graph { get; set; }

    /// <summary>Whether the graph takes the place alone, rather than sitting behind the figure.</summary>
    public bool GraphAlone { get; set; }

    /// <summary>Where a JSON value comes from, for a <see cref="MeterField.Feed"/> place.</summary>
    public MeterFeed? Feed { get; set; }

    /// <summary>Whether this place holds a reading at all.</summary>
    [JsonIgnore]
    public bool IsFilled => Field != MeterField.Empty;

    /// <summary>A place with nothing in it.</summary>
    public static MeterSlot Empty() => new() { Field = MeterField.Empty };

    /// <summary>A copy, so a reflow can move a cell without sharing it.</summary>
    public MeterSlot Copy()
    {
        var copy = (MeterSlot)MemberwiseClone();

        copy.Levels = Levels?.Select(level => level.Copy()).ToList();
        copy.Rotate = Rotate is null ? null : [.. Rotate];
        copy.Feed = Feed?.Copy();

        return copy;
    }

    /// <summary>Every reading this place shows: its own, then any it rotates through.</summary>
    public IEnumerable<MeterField> Fields()
    {
        yield return Field;

        if (Rotate is null)
        {
            yield break;
        }

        foreach (MeterField other in Rotate)
        {
            if (other != MeterField.Empty && other != Field)
            {
                yield return other;
            }
        }
    }

    /// <summary>Forgets everything said about the mark on this reading.</summary>
    public void PlainMark()
    {
        ShowLabel = null;
        MarkAs = MeterMarkStyle.Icon;
        MarkSide = MeterSide.Right;
        LabelColour = null;
    }

    /// <summary>Forgets everything said about the figure itself.</summary>
    public void PlainValue()
    {
        Family = null;
        Scale = null;
        TextSize = null;
        Bold = null;
        ValueSide = MeterSide.Right;
        ValueColour = null;
        Levels = null;
        Card = null;
        Graph = MeterGraph.None;
        GraphAlone = false;
    }

    /// <summary>Forgets everything said about the unit beside the figure.</summary>
    public void PlainUnit()
    {
        ShowUnit = null;
        UnitSide = MeterSide.Right;
        UnitColour = null;
    }

    /// <summary>Whether anything at all was said about the mark.</summary>
    [JsonIgnore]
    public bool MarkIsChanged =>
        ShowLabel is not null || MarkAs != MeterMarkStyle.Icon
        || MarkSide != MeterSide.Right || LabelColour is not null;

    /// <summary>Whether anything at all was said about the figure.</summary>
    [JsonIgnore]
    public bool ValueIsChanged =>
        Family is not null || Scale is not null || TextSize is not null
        || Bold is not null || ValueSide != MeterSide.Right || ValueColour is not null
        || Levels is not null || Card is not null || Graph != MeterGraph.None || GraphAlone;

    /// <summary>Whether this place rotates, or reads a feed: choices about what it shows beyond its field.</summary>
    [JsonIgnore]
    public bool ShowsMore => Rotate is { Count: > 0 } || Feed is not null;

    /// <summary>Whether anything at all was said about the unit.</summary>
    [JsonIgnore]
    public bool UnitIsChanged =>
        ShowUnit is not null || UnitSide != MeterSide.Right || UnitColour is not null;
}

internal sealed class MeterSlotsConverter : JsonConverter<List<MeterSlot>>
{
    /// <summary>A string token's text, or nothing for one that is not text (a lone surrogate).</summary>
    private static string? Text(ref Utf8JsonReader reader)
    {
        try
        {
            return reader.GetString();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    public override List<MeterSlot> Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        List<MeterSlot> slots = [];

        if (reader.TokenType != JsonTokenType.StartArray)
        {
            return slots;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.String:
                    slots.Add(Enum.TryParse(Text(ref reader), ignoreCase: true, out MeterField named) && Enum.IsDefined(named)
                        ? new MeterSlot { Field = named }
                        : MeterSlot.Empty());
                    break;

                // Older still, and harmless to allow: the number behind the name.
                case JsonTokenType.Number when reader.TryGetInt32(out int number):
                    slots.Add(new MeterSlot
                    {
                        Field = Enum.IsDefined((MeterField)number) ? (MeterField)number : MeterField.Empty,
                    });
                    break;

                case JsonTokenType.StartObject when
                    JsonSerializer.Deserialize<MeterSlot>(ref reader, options) is { } written:
                    slots.Add(written);
                    break;

                default:
                    reader.Skip();
                    slots.Add(MeterSlot.Empty());
                    break;
            }
        }

        return slots;
    }

    public override void Write(
        Utf8JsonWriter writer,
        List<MeterSlot> value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartArray();

        foreach (MeterSlot slot in value)
        {
            JsonSerializer.Serialize(writer, slot, options);
        }

        writer.WriteEndArray();
    }
}
