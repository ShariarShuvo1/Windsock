using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;

namespace Windsock.App.Views;

/// <summary>
/// The compact window's frame: the surface it is dragged by, double-clicked
/// and right-clicked on, which a screen reader can also press.
/// </summary>
/// <remarks>
/// A plain Border has no automation peer, so with the keyboard's focus on it
/// Narrator had nothing to say and nothing to press. This one is a button to
/// UI Automation, named with the rates, whose Invoke does what a double-click
/// and Enter do: open Windsock.
/// </remarks>
public sealed class MiniFrame : Border
{
    /// <summary>Raised when UI Automation presses the frame.</summary>
    public event EventHandler? Invoked;

    /// <inheritdoc />
    protected override AutomationPeer OnCreateAutomationPeer() => new MiniFramePeer(this);

    private void Invoke() => Invoked?.Invoke(this, EventArgs.Empty);

    private sealed class MiniFramePeer(MiniFrame owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetClassNameCore() => nameof(MiniFrame);

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        void IInvokeProvider.Invoke()
        {
            if (!IsEnabled())
            {
                throw new ElementNotEnabledException();
            }

            ((MiniFrame)Owner).Invoke();
        }
    }
}
