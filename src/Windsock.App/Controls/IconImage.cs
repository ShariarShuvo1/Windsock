using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// A program's icon beside its name: an <see cref="Image"/> that says nothing
/// to UI Automation.
/// </summary>
/// <remarks>
/// Every row of the Apps tables, the Activity page's lists and the nodes of
/// the traffic graph draw the program's own icon next to the program's name.
/// As a plain Image each one was an unnamed image to a screen reader, forty
/// of them on a busy page, and naming them would only say each name twice.
/// With no peer of its own it is left out of the tree, the way a Border or a
/// Grid is; the name beside it is what is read.
/// </remarks>
public sealed class IconImage : Image
{
    /// <summary>No peer: the icon is decoration for the name beside it.</summary>
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
