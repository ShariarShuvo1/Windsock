using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Windsock.App.Controls;

/// <summary>
/// A figure on its plate, as a named group: "Battery health", holding
/// "HEALTH", "90%" and "51.3 of 57.0 Wh new".
/// </summary>
/// <remarks>
/// A plate is a Border, and a Border has no automation peer, so the name a
/// page gave it - which is also what the search finds it by - was never read:
/// a screen reader met the caption in capitals and the figure loose on the
/// page. As a group it is announced by its name on the way in. It takes the
/// same plate styles a Border does.
/// </remarks>
public sealed class Plate : Border
{
    /// <summary>A group named by the page.</summary>
    protected override AutomationPeer OnCreateAutomationPeer() => new PlatePeer(this);

    private sealed class PlatePeer(Plate owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

        protected override string GetClassNameCore() => nameof(Plate);
    }
}
