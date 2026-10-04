using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// The frame every section is drawn in: a heading that names it, a line saying
/// what it is looking at, the actions that belong to the whole page, and the
/// page itself under them.
/// </summary>
/// <remarks>
/// <para>
/// Every section used to open straight onto its first card, under an overview
/// that stayed the same whichever section was chosen. A reader landing on a
/// page had to work out from the cards which page it was. The heading answers
/// that before anything else is read, and it is the same shape on every page,
/// so the only thing that changes from one section to the next is the part
/// that should.
/// </para>
/// <para>
/// The heading does not scroll. The page under it does, inside its own
/// scroller, which the section supplies: two of the sections hold their scroller
/// to the viewport so a table can fill what is left, and that is theirs to
/// decide.
/// </para>
/// </remarks>
public sealed class SectionPage : ContentControl
{
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SectionPage),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty SubtitleProperty = DependencyProperty.Register(
        nameof(Subtitle),
        typeof(string),
        typeof(SectionPage),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ActionsProperty = DependencyProperty.Register(
        nameof(Actions),
        typeof(object),
        typeof(SectionPage),
        new PropertyMetadata(null));

    static SectionPage()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(SectionPage),
            new FrameworkPropertyMetadata(typeof(SectionPage)));

        FocusableProperty.OverrideMetadata(typeof(SectionPage), new FrameworkPropertyMetadata(false));
    }

    /// <summary>What the section is called.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>One line under the title: what the page is looking at.</summary>
    public string Subtitle
    {
        get => (string)GetValue(SubtitleProperty);
        set => SetValue(SubtitleProperty, value);
    }

    /// <summary>Controls that act on the whole page, set against the title.</summary>
    public object? Actions
    {
        get => GetValue(ActionsProperty);
        set => SetValue(ActionsProperty, value);
    }
}
