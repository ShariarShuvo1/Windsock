using System.Windows;

namespace Windsock.App.Controls;

/// <summary>
/// Content with a point that should stay where it is on screen.
/// </summary>
public interface IViewAnchor
{
    Point Anchor { get; }
}
