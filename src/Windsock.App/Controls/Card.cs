using System.Windows;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// One surface on a page, holding a whole subject: its figures, its chart and
/// its detail as parts of one thing, divided by hairlines rather than set out
/// as separate boxes.
/// </summary>
/// <remarks>
/// A page used to be a stack of small cards - a card per figure, a card for the
/// chart, a card per list - and read as a heap of unrelated pieces. A section
/// now has one card, and what is inside it is laid out edge to edge, so a strip
/// of figures, the chart it summarises and the rows under it touch the card's
/// sides and meet at a rule. The content is clipped to the card's rounded
/// corners for the same reason: it reaches them.
/// </remarks>
public sealed class Card : ContentControl
{
    static Card()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(typeof(Card)));
        FocusableProperty.OverrideMetadata(typeof(Card), new FrameworkPropertyMetadata(false));
    }
}
