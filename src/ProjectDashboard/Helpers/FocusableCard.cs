using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace ProjectDashboard.Helpers;

/// <summary>
/// A <see cref="Border"/> that appears in the automation tree. A plain Border builds no peer, so
/// it holds keyboard focus while reporting nothing: focus resolves to no automation element and a
/// reader announces the window instead. Any element that takes focus and carries a name needs one.
/// </summary>
public sealed class FocusableCard : Border
{
    protected override AutomationPeer OnCreateAutomationPeer() => new FrameworkElementAutomationPeer(this);
}
