using System.Text.Json;
using System.Text.Json.Serialization;

using Windsock.Core.Formatting;

namespace Windsock.Core.Settings;

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

    /// <summary>Whether this place holds a reading at all.</summary>
    [JsonIgnore]
    public bool IsFilled => Field != MeterField.Empty;

    /// <summary>A place with nothing in it.</summary>
    public static MeterSlot Empty() => new() { Field = MeterField.Empty };

    /// <summary>A copy, so a reflow can move a cell without sharing it.</summary>
    public MeterSlot Copy() => (MeterSlot)MemberwiseClone();

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
        || Bold is not null || ValueSide != MeterSide.Right || ValueColour is not null;

    /// <summary>Whether anything at all was said about the unit.</summary>
    [JsonIgnore]
    public bool UnitIsChanged =>
        ShowUnit is not null || UnitSide != MeterSide.Right || UnitColour is not null;
}

internal sealed class MeterSlotsConverter : JsonConverter<List<MeterSlot>>
{
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
                case JsonTokenType.String when
                    Enum.TryParse(reader.GetString(), ignoreCase: true, out MeterField named):
                    slots.Add(new MeterSlot { Field = named });
                    break;

                case JsonTokenType.String:
                    slots.Add(MeterSlot.Empty());
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
