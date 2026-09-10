using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Windsock.App.Controls;

/// <summary>
/// Walks up from whatever a mouse event says it landed on.
/// </summary>
public static class Ancestry
{
    /// <summary>Steps up one level, through whichever tree the element is in.</summary>
    public static DependencyObject? Above(DependencyObject? source) => source switch
    {
        null => null,
        Visual or Visual3D => VisualTreeHelper.GetParent(source),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(source),
    };

    /// <summary>The nearest thing of a given kind at or above the source.</summary>
    public static T? Find<T>(DependencyObject? source)
        where T : DependencyObject
    {
        while (source is not null and not T)
        {
            source = Above(source);
        }

        return source as T;
    }

    /// <summary>
    /// Whether something of one kind sits between the source and a boundary.
    /// </summary>
    public static bool Within<TWanted, TBoundary>(DependencyObject? source)
        where TWanted : DependencyObject
        where TBoundary : DependencyObject
    {
        while (source is not null and not TBoundary)
        {
            if (source is TWanted)
            {
                return true;
            }

            source = Above(source);
        }

        return false;
    }
}
