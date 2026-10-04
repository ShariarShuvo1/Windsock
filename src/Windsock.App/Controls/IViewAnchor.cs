using System.Windows;

namespace Windsock.App.Controls;

/// <summary>
/// Content with a point that should stay where it is on screen.
/// </summary>
public interface IViewAnchor
{
    Point Anchor { get; }

    /// <summary>
    /// Whether a fit puts the anchor in the middle of the view, rather than
    /// the middle of the whole picture.
    /// </summary>
    bool IsFramedOnAnchor { get; }
}
