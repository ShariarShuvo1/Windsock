using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Windsock.App.Controls;

namespace Windsock.App.Navigation;

/// <summary>
/// Finds a control on a page by the name a search entry gives it, and brings
/// it into view.
/// </summary>
/// <remarks>
/// <para>
/// A control is named by its automation name - what a screen reader says for
/// it, which most of Windsock's controls already carry - or by its
/// <c>x:Name</c>. Both trees are walked, the logical one for what the page
/// declares and the visual one for what templates draw.
/// </para>
/// <para>
/// A setting's row usually names it twice - the label (a
/// <see cref="SettingHead"/>) and the control - so a control wins over a label,
/// even a control switched off just now, and either over one hidden in a part
/// of the page that is not showing. Landing on a label, the control beside it
/// in its row is the one given focus, never the "About ..." mark inside
/// the label; a control switched off is brought into view without focus.
/// </para>
/// </remarks>
internal static class TargetFinder
{
    private const int MostVisited = 20_000;
    private const int MostVisitedInARow = 2_000;
    private const int MostClimbed = 3;

    /// <summary>The control named <paramref name="name"/> on a page, the one best to land on, or nothing.</summary>
    public static FrameworkElement? Find(DependencyObject page, string name)
    {
        ArgumentNullException.ThrowIfNull(page);

        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        FrameworkElement? switchedOff = null;
        FrameworkElement? shown = null;
        FrameworkElement? hidden = null;
        HashSet<DependencyObject> seen = [];
        Queue<DependencyObject> waiting = new();
        waiting.Enqueue(page);

        while (waiting.Count > 0 && seen.Count < MostVisited)
        {
            DependencyObject each = waiting.Dequeue();

            if (!seen.Add(each))
            {
                continue;
            }

            if (each is FrameworkElement element && Names(element, name))
            {
                if (element.IsVisible && element.Focusable && element.IsEnabled)
                {
                    return element;
                }

                if (element.IsVisible && element.Focusable)
                {
                    switchedOff ??= element;
                }
                else if (element.IsVisible)
                {
                    shown ??= element;
                }
                else
                {
                    hidden ??= element;
                }
            }

            Enqueue(each, waiting);
        }

        return switchedOff ?? shown ?? hidden;
    }

    /// <summary>
    /// Scrolls a control into view and gives it keyboard focus when it can take
    /// it. A setting's label or row passes focus to the setting's control; a
    /// control switched off, and a reading, are only scrolled to. A control in
    /// a part of the page not showing just now brings the nearest part that is
    /// into view. Returns whether the control itself was visible.
    /// </summary>
    /// <param name="target">What <see cref="Find"/> found.</param>
    /// <param name="setting">
    /// Whether the target is a setting, whose control is worth looking for
    /// when the target itself is a label or a row; a reading is never looked
    /// inside.
    /// </param>
    public static bool Reveal(FrameworkElement target, bool setting = true)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (!target.IsVisible)
        {
            for (DependencyObject? up = target; up is not null; up = Parent(up))
            {
                if (up is FrameworkElement { IsVisible: true } visible)
                {
                    visible.BringIntoView();
                    break;
                }
            }

            return false;
        }

        target.BringIntoView();

        if (target.Focusable)
        {
            Settle(target);
            return true;
        }

        if (setting && ControlOf(target) is { } control)
        {
            control.BringIntoView();
            Settle(control);
        }

        return true;
    }

    /// <summary>Focus for a control that is on; nothing for one switched off, which is only shown.</summary>
    private static void Settle(FrameworkElement control)
    {
        if (control.IsEnabled)
        {
            Keyboard.Focus(control);
        }
    }

    /// <summary>
    /// The control of a setting named by a label or a row: inside a row, or,
    /// for a label, beside it in the row it heads - looked for no further than
    /// the row, which ends where another setting's label is.
    /// </summary>
    private static FrameworkElement? ControlOf(FrameworkElement target)
    {
        if (target is not SettingHead head)
        {
            return FirstControl(target);
        }

        DependencyObject? up = Parent(head);

        for (int climbed = 0; up is not null && climbed < MostClimbed; climbed++, up = Parent(up))
        {
            if (HeadsBesides(up, head))
            {
                return null;
            }

            if (FirstControl(up) is { } control)
            {
                return control;
            }
        }

        return null;
    }

    /// <summary>
    /// The first control under <paramref name="root"/> that Tab could land on,
    /// switched on or not, never one inside a setting's label.
    /// </summary>
    private static FrameworkElement? FirstControl(DependencyObject root)
    {
        HashSet<DependencyObject> seen = [];
        Queue<DependencyObject> waiting = new();
        waiting.Enqueue(root);

        while (waiting.Count > 0 && seen.Count < MostVisitedInARow)
        {
            DependencyObject each = waiting.Dequeue();

            if (!seen.Add(each) || each is SettingHead)
            {
                continue;
            }

            if (!ReferenceEquals(each, root)
                && each is FrameworkElement { IsVisible: true, Focusable: true } element
                && KeyboardNavigation.GetIsTabStop(element))
            {
                return element;
            }

            Enqueue(each, waiting);
        }

        return null;
    }

    /// <summary>Whether a part of the page holds a setting's label other than this one.</summary>
    private static bool HeadsBesides(DependencyObject root, SettingHead head)
    {
        HashSet<DependencyObject> seen = [];
        Queue<DependencyObject> waiting = new();
        waiting.Enqueue(root);

        while (waiting.Count > 0 && seen.Count < MostVisitedInARow)
        {
            DependencyObject each = waiting.Dequeue();

            if (!seen.Add(each))
            {
                continue;
            }

            if (each is SettingHead other)
            {
                if (!ReferenceEquals(other, head))
                {
                    return true;
                }

                continue;
            }

            Enqueue(each, waiting);
        }

        return false;
    }

    private static void Enqueue(DependencyObject each, Queue<DependencyObject> waiting)
    {
        foreach (object child in LogicalTreeHelper.GetChildren(each))
        {
            if (child is DependencyObject node)
            {
                waiting.Enqueue(node);
            }
        }

        if (each is Visual or System.Windows.Media.Media3D.Visual3D)
        {
            int count = VisualTreeHelper.GetChildrenCount(each);

            for (int index = 0; index < count; index++)
            {
                waiting.Enqueue(VisualTreeHelper.GetChild(each, index));
            }
        }
    }

    private static DependencyObject? Parent(DependencyObject child) =>
        child is Visual or System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(child) ?? LogicalTreeHelper.GetParent(child)
            : LogicalTreeHelper.GetParent(child);

    private static bool Names(FrameworkElement element, string name) =>
        string.Equals(AutomationProperties.GetName(element), name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(element.Name, name, StringComparison.Ordinal);
}
