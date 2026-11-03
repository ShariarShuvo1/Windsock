using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Windsock.Core.Formatting;
using Windsock.Core.Meter;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>
/// What every slot on the page shares: the taskbar's theme, which readings this
/// machine and edition can offer, its graphics cards and its temperature unit.
/// </summary>
/// <param name="Dark">Whether the taskbar is dark, which the colours follow.</param>
/// <param name="Offered">Whether a reading is offered here at all.</param>
/// <param name="Cards">The graphics cards by name, read when a slot is built.</param>
/// <param name="Temperature">The unit temperatures are shown in.</param>
/// <param name="FeedsAllowed">Whether places may read web and local feeds at all.</param>
/// <param name="AllowFeeds">Allows them, or stops them, as the page's own switch does.</param>
public sealed record MeterSlotContext(
    Func<bool> Dark,
    Func<MeterField, bool> Offered,
    Func<IReadOnlyList<string>> Cards,
    Func<TemperatureUnit> Temperature,
    Func<bool> FeedsAllowed,
    Action<bool> AllowFeeds);

/// <summary>A colour a level may turn a figure, by name or written down.</summary>
/// <param name="Value">How it is written down: good, fair, slow, or #rrggbb.</param>
/// <param name="Label">What it is called.</param>
public sealed record LevelColour(string Value, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One level on one slot: past this, the figure turns this colour.</summary>
public sealed partial class MeterLevelRowViewModel : ObservableObject
{
    private readonly MeterLevel _level;
    private readonly LevelUnit _unit;
    private readonly Func<bool> _dark;
    private readonly Action _changed;
    private readonly Action<MeterLevelRowViewModel> _remove;

    public MeterLevelRowViewModel(MeterLevel level, LevelUnit unit, Func<bool> dark, Action changed, Action<MeterLevelRowViewModel> remove)
    {
        _level = level;
        _unit = unit;
        _dark = dark;
        _changed = changed;
        _remove = remove;
    }

    /// <summary>The level as kept, in the reading's base unit.</summary>
    public MeterLevel Level => _level;

    /// <summary>Whether it is passed going down, as a battery runs low.</summary>
    public bool Below
    {
        get => _level.Below;
        set
        {
            if (_level.Below == value)
            {
                return;
            }

            _level.Below = value;
            Told();
        }
    }

    /// <summary>The level in the unit it is shown in, as typed.</summary>
    public string Figure
    {
        get => Math.Round(_unit.Show(_level.At), 2).ToString("0.##", CultureInfo.CurrentCulture);
        set
        {
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double shown) || !double.IsFinite(shown))
            {
                return;
            }

            _level.At = _unit.Keep(shown);
            Told();
        }
    }

    /// <summary>The unit beside the figure.</summary>
    public string UnitLabel => _unit.Label;

    /// <summary>The colour past it.</summary>
    public string Colour
    {
        get => _level.Colour;
        set
        {
            if (string.IsNullOrWhiteSpace(value) || value == _level.Colour)
            {
                return;
            }

            _level.Colour = value;
            Told();
        }
    }

    /// <summary>
    /// The level in words, as a screen reader names it and the button that
    /// removes it: "at or above 50 ms, Amber".
    /// </summary>
    public string Summary => string.Format(
        CultureInfo.CurrentCulture,
        "{0} {1}, {2}",
        Below ? "at or below" : "at or above",
        string.Concat(Figure, " ", UnitLabel).Trim(),
        Colours.FirstOrDefault(each => string.Equals(each.Value, Colour, StringComparison.OrdinalIgnoreCase))?.Label ?? Colour);

    /// <inheritdoc />
    public override string ToString() => string.Concat("Level ", Summary);

    /// <summary>The colour as the taskbar would draw it now.</summary>
    public Brush Swatch => MeterPalette.Ink(_level.Colour, MeterField.Empty, MeterGroup.Network, _dark()) ?? MeterPalette.Level(MeterLevels.Fair, _dark());

    /// <summary>Which ways a level can be passed.</summary>
    public static IReadOnlyList<MeterOption<bool>> Directions { get; } =
    [
        new(false, "At or above"),
        new(true, "At or below"),
    ];

    /// <summary>The colours a level offers.</summary>
    public static IReadOnlyList<LevelColour> Colours { get; } =
    [
        new(MeterLevels.Good, "Green"),
        new(MeterLevels.Fair, "Amber"),
        new(MeterLevels.Slow, "Red"),
        new("#4C9AFF", "Blue"),
        new("#C77DFF", "Violet"),
        new("#FFFFFF", "White"),
    ];

    [RelayCommand]
    private void Remove() => _remove(this);

    private void Told()
    {
        OnPropertyChanged(string.Empty);
        _changed();
    }
}

/// <summary>A reading a slot takes turns with.</summary>
public sealed partial class MeterTurnViewModel : ObservableObject
{
    private readonly Action<MeterTurnViewModel> _remove;

    public MeterTurnViewModel(MeterField field, Action<MeterTurnViewModel> remove)
    {
        Field = field;
        _remove = remove;
    }

    /// <summary>The reading.</summary>
    public MeterField Field { get; }

    /// <summary>What it is called.</summary>
    public string Name => MeterReadout.Name(Field);

    /// <summary>Returns <see cref="Name"/>, which is what a screen reader names the row by.</summary>
    public override string ToString() => Name;

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>
/// The slot's further choices: a graph of its last minute, which graphics
/// card it follows, the levels that colour it, the readings it takes turns
/// with, and where a JSON reading comes from.
/// </summary>
public sealed partial class MeterSlotChoiceViewModel
{
    private const int MostTurns = 6;

    /// <summary>How long each reading stays, in seconds, as the picker offers it.</summary>
    public static IReadOnlyList<MeterOption<int>> TurnLengths { get; } =
        [.. new[] { 2, 3, 4, 5, 6, 8, 10, 15, 20, 30 }.Select(seconds => new MeterOption<int>(seconds, string.Concat(seconds.ToString(CultureInfo.CurrentCulture), " seconds")))];

    /// <summary>How often a JSON reading is asked for, as the picker offers it.</summary>
    public static IReadOnlyList<MeterOption<int>> FeedIntervals { get; } =
    [
        new(15, "Every 15 seconds"),
        new(30, "Every 30 seconds"),
        new(60, "Every minute"),
        new(300, "Every 5 minutes"),
        new(900, "Every 15 minutes"),
        new(3600, "Every hour"),
    ];

    /// <summary>How the last minute can be drawn.</summary>
    public static IReadOnlyList<MeterOption<MeterGraph>> Graphs { get; } =
    [
        new(MeterGraph.None, "No graph"),
        new(MeterGraph.Line, "A line"),
        new(MeterGraph.Bars, "Bars"),
    ];

    /// <summary>The levels this slot colours its figure by, in the unit they are set in.</summary>
    public ObservableCollection<MeterLevelRowViewModel> Levels { get; } = [];

    /// <summary>The readings this slot takes turns with, after its own.</summary>
    public ObservableCollection<MeterTurnViewModel> Turns { get; } = [];

    /// <summary>The readings that can be added to the turns.</summary>
    public IReadOnlyList<MeterOption<MeterField>> TurnChoices =>
        [.. Fields.Where(option => option.Value != Field && option.Value != MeterField.Feed && option.Value != MeterField.Pacer && Turns.All(turn => turn.Field != option.Value))];

    /// <summary>The graphics cards a graphics reading can follow.</summary>
    public IReadOnlyList<MeterOption<string>> CardChoices { get; private set; } = [];

    // ------------------------------------------------------------ the graph

    /// <summary>Whether this reading is a number, which a graph and levels need.</summary>
    public bool HasNumber => Unit() is not null;

    /// <summary>How the last minute is drawn, if it is.</summary>
    public MeterGraph Graph
    {
        get => Cell.Graph;
        set => Set(() => Cell.Graph = value);
    }

    /// <summary>Whether the graph takes the place alone, without the figure.</summary>
    public bool GraphAlone
    {
        get => Cell.GraphAlone;
        set => Set(() => Cell.GraphAlone = value);
    }

    /// <summary>Whether there is a graph to put alone.</summary>
    public bool HasGraph => Cell.Graph != MeterGraph.None;

    // ------------------------------------------------------------- the card

    /// <summary>Whether to offer a choice of card: a graphics reading on a machine with more than one.</summary>
    public bool OffersCard => IsGraphicsField(Field) && CardChoices.Count > 2;

    /// <summary>Which card it follows: empty for the busiest, every card, or one by name.</summary>
    public string CardChoice
    {
        get => Cell.Card ?? string.Empty;
        set => Set(() => Cell.Card = string.IsNullOrEmpty(value) ? null : value);
    }

    // ----------------------------------------------------------- the levels

    /// <summary>Whether levels can be set on this reading.</summary>
    public bool OffersLevels => HasNumber;

    /// <summary>Whether the levels are this slot's own rather than the reading's defaults.</summary>
    public bool LevelsAreOwn => Cell.Levels is not null;

    /// <summary>What the levels are, in a line, when there are none to list.</summary>
    public string LevelsNote => Levels.Count == 0
        ? "None: the figure keeps its colour whatever it reads."
        : LevelsAreOwn
            ? "The figure takes the colour of the furthest level it has passed."
            : "The reading's own levels. Change any of them to make them this slot's.";

    /// <summary>Whether another level can be added.</summary>
    public bool CanAddLevel => HasNumber && Levels.Count < MeterLevels.Most;

    [RelayCommand(CanExecute = nameof(CanAddLevel))]
    private void AddLevel()
    {
        if (Unit() is not { } unit)
        {
            return;
        }

        List<MeterLevel> levels = OwnLevels();
        double at = levels.Count == 0 ? 0 : levels.Max(level => level.At) * 2;

        if (at <= 0)
        {
            at = unit.Keep(Field switch
            {
                MeterField.Download or MeterField.Upload or MeterField.NetworkTotal => 10,
                MeterField.ProcessorTemperature or MeterField.GraphicsTemperature or MeterField.StorageTemperature
                    or MeterField.BoardTemperature => unit.Fahrenheit ? 176 : 80,
                _ => 80,
            });
        }

        levels.Add(new MeterLevel { At = at, Colour = MeterLevels.Fair });
        Set(() => Cell.Levels = levels);
        ReadLevels();
    }

    [RelayCommand(CanExecute = nameof(LevelsAreOwn))]
    private void ResetLevels()
    {
        Set(() => Cell.Levels = null);
        ReadLevels();
    }

    // ------------------------------------------------------------ the turns

    /// <summary>Whether this slot takes turns with other readings.</summary>
    public bool TakesTurns => Turns.Count > 0;

    /// <summary>Whether another reading can join the turns.</summary>
    public bool CanAddTurn => Field != MeterField.Feed && Field != MeterField.Pacer && Turns.Count < MostTurns;

    /// <summary>How long each reading stays.</summary>
    public int TurnSeconds
    {
        get => Cell.RotateSeconds;
        set => Set(() => Cell.RotateSeconds = Math.Clamp(value, 1, 60));
    }

    /// <summary>The reading the picker holds, which joins the turns as it is chosen.</summary>
    public MeterField? TurnToAdd
    {
        get => null;
        set
        {
            if (value is not { } chosen || chosen == MeterField.Empty || !CanAddTurn)
            {
                return;
            }

            Set(() => Cell.Rotate = [.. Cell.Rotate ?? [], chosen]);
            ReadTurns();
        }
    }

    // ------------------------------------------------------------- the feed

    /// <summary>Whether this slot reads a JSON field.</summary>
    public bool IsFeed => Field == MeterField.Feed;

    /// <summary>Whether this slot shows the public address, which says where it comes from.</summary>
    public bool IsPublicAddress => Cell.Fields().Contains(MeterField.PublicAddress);

    /// <summary>
    /// Whether places may read web and local feeds at all: one switch for
    /// every feed, the same one Settings › Data and privacy lists.
    /// </summary>
    public bool FeedsAllowed
    {
        get => _context.FeedsAllowed();
        set => _context.AllowFeeds(value);
    }

    /// <summary>The address it reads from.</summary>
    public string FeedAddress
    {
        get => Cell.Feed?.Address ?? string.Empty;
        set => Feed(feed => feed.Address = value.Trim());
    }

    /// <summary>The field in what that address answers, as a path such as <c>$.data.price</c>.</summary>
    public string FeedPath
    {
        get => Cell.Feed?.Path ?? string.Empty;
        set => Feed(feed => feed.Path = value.Trim());
    }

    /// <summary>The few letters that mark it on the bar.</summary>
    public string FeedLabel
    {
        get => Cell.Feed?.Label ?? string.Empty;
        set => Feed(feed => feed.Label = value.Trim());
    }

    /// <summary>The unit written after it.</summary>
    public string FeedUnit
    {
        get => Cell.Feed?.Unit ?? string.Empty;
        set => Feed(feed => feed.Unit = value.Trim());
    }

    /// <summary>How often it is asked for, in seconds.</summary>
    public int FeedSeconds
    {
        get => Cell.Feed?.Seconds ?? 60;
        set => Feed(feed => feed.Seconds = Math.Max(15, value));
    }

    /// <summary>What is wrong with the address or the path, or nothing.</summary>
    public string FeedProblem
    {
        get
        {
            if (Cell.Feed is not { } feed || feed.Address.Length == 0)
            {
                return "Type the address of a JSON document: https anywhere, http only on this PC or your own network, or a file on this PC's own drives (never a share).";
            }

            if (Windsock.Core.Meter.FeedAddress.Check(feed.Address).Problem is { } problem)
            {
                return problem;
            }

            return JsonField.IsPath(feed.Path)
                ? string.Empty
                : "Name the field as a path from the top of the document: $.price, or $.items[0].name.";
        }
    }

    // ------------------------------------------------------------- in a line

    /// <summary>What the cell does beyond its three parts, in a line under them, or nothing.</summary>
    public string Extras
    {
        get
        {
            List<string> parts = [];

            if (Cell.Graph != MeterGraph.None)
            {
                parts.Add(Cell.GraphAlone ? "Graph alone" : Cell.Graph == MeterGraph.Line ? "Line graph" : "Bar graph");
            }

            if (Cell.Levels is { } own)
            {
                parts.Add(own.Count == 1 ? "1 level" : string.Concat(own.Count.ToString(CultureInfo.CurrentCulture), " levels"));
            }

            if (Turns.Count > 0)
            {
                parts.Add(string.Concat("Takes turns with ", Turns.Count.ToString(CultureInfo.CurrentCulture)));
            }

            if (IsGraphicsField(Field) && Cell.Card is { Length: > 0 } card)
            {
                parts.Add(card == MeterSlot.EveryCard ? "Every card" : card);
            }

            if (IsFeed)
            {
                parts.Add(Cell.Feed is { Address.Length: > 0 } feed ? Host(feed.Address) : "No address yet");
            }

            return string.Join(" · ", parts);
        }
    }

    /// <summary>Says everything about the cell again, after something off the cell changed.</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);

    private static string Host(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) && !uri.IsFile ? uri.Host : Path.GetFileName(address);

    // ------------------------------------------------------------- the reading

    /// <summary>Reads the levels, turns and cards again, after the reading changed.</summary>
    private void ReadMore()
    {
        CardChoices = Cards();
        ReadLevels();
        ReadTurns();
        OnPropertyChanged(nameof(CardChoices));
    }

    private void ReadLevels()
    {
        Levels.Clear();

        if (Unit() is { } unit)
        {
            IReadOnlyList<MeterLevel> levels = MeterLevels.For(Cell, Field);

            foreach (MeterLevel level in levels)
            {
                // The defaults are shown as they are; the first change copies
                // them into the slot (Own), so a default is never edited in place.
                MeterLevel shown = LevelsAreOwn ? level : level.Copy();
                Levels.Add(new MeterLevelRowViewModel(shown, unit, _context.Dark, () => LevelChanged(), RemoveLevel));
            }
        }

        OnPropertyChanged(nameof(LevelsNote));
        OnPropertyChanged(nameof(LevelsAreOwn));
        OnPropertyChanged(nameof(CanAddLevel));
        OnPropertyChanged(nameof(Extras));
        AddLevelCommand.NotifyCanExecuteChanged();
        ResetLevelsCommand.NotifyCanExecuteChanged();
    }

    private void ReadTurns()
    {
        Turns.Clear();

        foreach (MeterField field in Cell.Rotate ?? [])
        {
            if (field != MeterField.Empty && field != Field)
            {
                Turns.Add(new MeterTurnViewModel(field, RemoveTurn));
            }
        }

        OnPropertyChanged(nameof(TakesTurns));
        OnPropertyChanged(nameof(CanAddTurn));
        OnPropertyChanged(nameof(TurnChoices));
        OnPropertyChanged(nameof(Extras));
    }

    /// <summary>A level was edited: the rows on show become the slot's own levels.</summary>
    private void LevelChanged()
    {
        List<MeterLevel> levels = [.. Levels.Select(row => row.Level)];

        Set(() => Cell.Levels = levels);
        OnPropertyChanged(nameof(LevelsNote));
        ResetLevelsCommand.NotifyCanExecuteChanged();
    }

    private void RemoveLevel(MeterLevelRowViewModel row)
    {
        List<MeterLevel> levels = [.. Levels.Where(each => !ReferenceEquals(each, row)).Select(each => each.Level)];

        Set(() => Cell.Levels = levels);
        ReadLevels();
    }

    private void RemoveTurn(MeterTurnViewModel turn)
    {
        List<MeterField> left = [.. Turns.Where(each => !ReferenceEquals(each, turn)).Select(each => each.Field)];

        Set(() => Cell.Rotate = left.Count == 0 ? null : left);
        ReadTurns();
    }

    /// <summary>The slot's own levels, copied out of the defaults the first time.</summary>
    private List<MeterLevel> OwnLevels() => [.. MeterLevels.For(Cell, Field).Select(level => level.Copy())];

    private void Feed(Action<MeterFeed> edit) => Set(() =>
    {
        Cell.Feed ??= new MeterFeed();
        edit(Cell.Feed);
    });

    private LevelUnit? Unit() => MeterLevels.UnitOf(Field, Family, _context.Temperature());

    private List<MeterOption<string>> Cards()
    {
        IReadOnlyList<string> cards = _context.Cards();
        List<MeterOption<string>> choices =
        [
            new(string.Empty, "The busiest card"),
            new(MeterSlot.EveryCard, "Every card, side by side"),
        ];

        choices.AddRange(cards.Select(card => new MeterOption<string>(card, card)));

        if (Cell.Card is { Length: > 0 } chosen && chosen != MeterSlot.EveryCard && !cards.Contains(chosen, StringComparer.Ordinal))
        {
            choices.Add(new MeterOption<string>(chosen, string.Concat(chosen, ", not found")));
        }

        return choices;
    }

    private static bool IsGraphicsField(MeterField field) =>
        field is MeterField.GraphicsLoad or MeterField.GraphicsTemperature or MeterField.GraphicsMemory or MeterField.GraphicsPower;
}
