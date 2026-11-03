using CommunityToolkit.Mvvm.ComponentModel;
using Windsock.Core.Alerts;
using Windsock.Core.Notifications;
using Windsock.Core.Settings;

namespace Windsock.App.ViewModels;

/// <summary>One value a level picker offers.</summary>
/// <param name="Value">What is stored.</param>
/// <param name="Label">What is shown.</param>
public sealed record LevelOption(int Value, string Label)
{
    /// <summary>The label, which UI Automation reads for the item.</summary>
    public override string ToString() => Label;
}

/// <summary>A sound on offer for a kind of alert.</summary>
/// <param name="Sound">The sound.</param>
/// <param name="Label">Its name.</param>
public sealed record SoundOption(NoticeSound Sound, string Label)
{
    /// <summary>The label, which UI Automation reads for the item.</summary>
    public override string ToString() => Label;
}

/// <summary>One level of an alert - a temperature, a speed, a stretch - with the values it may take.</summary>
/// <remarks>
/// The values and their words are worked out again by <see cref="Refresh"/>,
/// so a picker of temperatures follows Celsius or Fahrenheit, and one of
/// speeds follows bytes or bits, as they are changed elsewhere.
/// </remarks>
public sealed partial class AlertLevelChoice : ObservableObject
{
    private readonly Func<IReadOnlyList<int>> _values;
    private readonly Func<int> _current;
    private readonly Func<int, string> _words;
    private readonly Action<int> _apply;
    private bool _loaded;

    /// <param name="label">What the level is, over its picker: "Above", "For".</param>
    /// <param name="automationName">The picker's name for a screen reader.</param>
    /// <param name="values">The values offered, as things stand.</param>
    /// <param name="current">The value stored now; offered too when it is not among them.</param>
    /// <param name="words">How to show a value, as things stand.</param>
    /// <param name="apply">Stores a chosen value, and saves.</param>
    public AlertLevelChoice(string label, string automationName, Func<IReadOnlyList<int>> values, Func<int> current, Func<int, string> words, Action<int> apply)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(apply);

        Label = label;
        AutomationName = automationName;
        _values = values;
        _current = current;
        _words = words;
        _apply = apply;

        Refresh();
        _loaded = true;
    }

    /// <summary>What the level is.</summary>
    public string Label { get; }

    /// <summary>The picker's name for a screen reader.</summary>
    public string AutomationName { get; }

    /// <summary>What a screen reader says for the level's place in its list.</summary>
    public override string ToString() => AutomationName;

    /// <summary>The values offered.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<LevelOption> Options { get; private set; } = [];

    /// <summary>The value chosen.</summary>
    [ObservableProperty]
    public partial int Selected { get; set; }

    /// <summary>
    /// Works the values and their words out again, and shows the stored value,
    /// without counting it as a choice.
    /// </summary>
    public void Refresh()
    {
        bool loaded = _loaded;
        _loaded = false;

        int now = _current();
        IReadOnlyList<int> offered = _values();

        // A value from an imported file or an older build is shown as it is, not lost to the nearest offered.
        int[] all = offered.Contains(now) ? [.. offered] : [.. offered.Append(now).Order()];

        Options = [.. all.Select(value => new LevelOption(value, _words(value)))];
        Selected = now;

        // New options are new objects: the picker is told the choice again.
        OnPropertyChanged(nameof(Selected));
        _loaded = loaded;
    }

    partial void OnSelectedChanged(int value)
    {
        if (_loaded)
        {
            _apply(value);
        }
    }
}

/// <summary>
/// One kind of alert on the Alerts page: its switch, and once on, its levels,
/// its sound, whether it breaks quiet hours and whether it is forwarded.
/// </summary>
/// <remarks>
/// The switch is the kind's own setting wherever it lives - the connection's in
/// <see cref="ConnectionSettings"/>, the digest's with the usage settings - reached
/// through <see cref="AlertKind.SetOn"/>, so it is one setting with whatever else
/// shows it. The sound, quiet hours and forwarding are kept per kind in
/// <see cref="AlertSettings.Choices"/>, replaced whole on each change.
/// </remarks>
public sealed partial class AlertKindViewModel : ObservableObject
{
    private readonly AlertKind _kind;
    private readonly WindsockSettings _settings;
    private readonly Action _changed;
    private bool _loaded;

    public AlertKindViewModel(
        AlertKind kind,
        WindsockSettings settings,
        IReadOnlyList<AlertLevelChoice> levels,
        string note,
        Action changed)
    {
        ArgumentNullException.ThrowIfNull(kind);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(levels);
        ArgumentNullException.ThrowIfNull(changed);

        _kind = kind;
        _settings = settings;
        _changed = changed;
        Levels = levels;
        Note = note;

        Read();
    }

    /// <summary>The sounds on offer, the same for every kind.</summary>
    public static IReadOnlyList<SoundOption> Sounds { get; } =
    [
        new(NoticeSound.Default, "Standard"),
        new(NoticeSound.Silent, "Silent"),
        new(NoticeSound.Reminder, "Reminder"),
        new(NoticeSound.Message, "Message"),
        new(NoticeSound.Mail, "Mail"),
        new(NoticeSound.Text, "Text message"),
    ];

    /// <summary>The kind's topic.</summary>
    public string Topic => _kind.Topic;

    /// <summary>Its name.</summary>
    public string Name => _kind.Name;

    /// <summary>What it is for.</summary>
    public string Description => _kind.Description;

    /// <summary>How much it matters, for its mark.</summary>
    public AlertLevel Level => _kind.Level;

    /// <summary>A line more, such as where a plan's levels are set; empty for none.</summary>
    public string Note { get; }

    /// <summary>Its levels, each a picker; empty for a kind with none.</summary>
    public IReadOnlyList<AlertLevelChoice> Levels { get; }

    /// <summary>Whether it has levels to show.</summary>
    public bool HasLevels => Levels.Count > 0;

    /// <summary>The switch's name for a screen reader.</summary>
    public string SwitchName => string.Concat("Alert: ", Name);

    /// <summary>The sound picker's name, with the kind in it: every kind has one.</summary>
    public string SoundName => string.Concat("Sound for ", Name);

    /// <summary>The quiet-hours box's name, with the kind in it: every kind has one.</summary>
    public string QuietName => string.Concat("Show ", Name, " even during quiet hours");

    /// <summary>The forwarding box's name, with the kind in it: every kind has one.</summary>
    public string ForwardName => string.Concat("Forward ", Name);

    /// <summary>
    /// What a screen reader says for the kind's place in its group's list: the
    /// tile's words. Without it the item was read by its type's name.
    /// </summary>
    public override string ToString() =>
        string.Join(". ", new[] { Name, Description, Note }.Where(part => part.Length > 0));

    /// <summary>Whether it is switched on.</summary>
    [ObservableProperty]
    public partial bool IsOn { get; set; }

    /// <summary>The sound it makes.</summary>
    [ObservableProperty]
    public partial NoticeSound Sound { get; set; }

    /// <summary>Whether it is shown during quiet hours and a snooze.</summary>
    [ObservableProperty]
    public partial bool BreaksQuiet { get; set; }

    /// <summary>Whether it is forwarded when forwarding is set up.</summary>
    [ObservableProperty]
    public partial bool Forwards { get; set; }

    /// <summary>Whether forwarding is on, so the forward choice is worth showing.</summary>
    [ObservableProperty]
    public partial bool IsForwardingOn { get; set; }

    /// <summary>Reads the settings again, without saving: after another place changed them.</summary>
    public void Read()
    {
        _loaded = false;

        AlertChoice choice = _settings.Alerts.ChoiceFor(_kind.Topic);
        IsOn = _kind.IsOn?.Invoke(_settings) ?? false;
        Sound = choice.Sound;
        BreaksQuiet = choice.BreaksQuiet;
        Forwards = choice.Forward;

        _loaded = true;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loaded || _kind.SetOn is null)
        {
            return;
        }

        _kind.SetOn(_settings, value);
        _changed();
    }

    partial void OnSoundChanged(NoticeSound value) => Choose(choice => choice.Sound = value);

    partial void OnBreaksQuietChanged(bool value) => Choose(choice => choice.BreaksQuiet = value);

    partial void OnForwardsChanged(bool value) => Choose(choice => choice.Forward = value);

    private void Choose(Action<AlertChoice> change)
    {
        if (!_loaded)
        {
            return;
        }

        AlertChoice was = _settings.Alerts.ChoiceFor(_kind.Topic);
        AlertChoice next = new() { Sound = was.Sound, BreaksQuiet = was.BreaksQuiet, Forward = was.Forward };
        change(next);

        // Replaced whole, never changed in place: the desk reads it as alerts arrive.
        Dictionary<string, AlertChoice> choices = new(_settings.Alerts.Choices, StringComparer.Ordinal)
        {
            [_kind.Topic] = next,
        };

        // The kinds sharing a switch share their choices too: "back online" sounds as "lost" was set.
        if (_kind.SharesSwitchWith is { } partner)
        {
            choices[partner] = next;
        }

        _settings.Alerts.Choices = choices;
        _changed();
    }
}

/// <summary>A group of kinds on the Alerts page: a band of its own.</summary>
/// <param name="group">Which group.</param>
/// <param name="title">Its heading.</param>
/// <param name="note">One line on what it holds.</param>
/// <param name="kinds">Its kinds, as tiles.</param>
public sealed partial class AlertGroupViewModel(AlertGroup group, string title, string note, IReadOnlyList<AlertKindViewModel> kinds) : ObservableObject
{
    /// <summary>Which group.</summary>
    public AlertGroup Group { get; } = group;

    /// <summary>Its heading.</summary>
    public string Title { get; } = title;

    /// <summary>One line on what it holds.</summary>
    public string Note { get; } = note;

    /// <summary>Its kinds, as tiles.</summary>
    public IReadOnlyList<AlertKindViewModel> Kinds { get; } = kinds;

    /// <summary>
    /// Why the group's alerts may not fire as expected - the connection's, while
    /// nothing watches it in the background - or empty.
    /// </summary>
    [ObservableProperty]
    public partial string Warning { get; set; } = string.Empty;

    /// <summary>What a screen reader says for the group's band in the page's list of groups.</summary>
    public override string ToString() =>
        string.Join(". ", new[] { Title, Note, Warning }.Where(part => part.Length > 0));
}
