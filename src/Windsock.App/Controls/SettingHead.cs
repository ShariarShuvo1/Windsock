using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Windsock.App.Controls;

/// <summary>
/// The name of one setting, with the detail behind a mark beside it.
/// </summary>
public sealed class SettingHead : Control
{
    /// <summary>What the setting is called.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SettingHead),
        new PropertyMetadata(string.Empty));

    /// <summary>The detail, shown when the mark beside the name is asked.</summary>
    public static readonly DependencyProperty HintProperty = DependencyProperty.Register(
        nameof(Hint),
        typeof(string),
        typeof(SettingHead),
        new PropertyMetadata(string.Empty));

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>A second thing worth saying, under the first with a gap.</summary>
    public static readonly DependencyProperty NoteProperty = DependencyProperty.Register(
        nameof(Note),
        typeof(string),
        typeof(SettingHead),
        new PropertyMetadata(string.Empty));

    public string Hint
    {
        get => (string)GetValue(HintProperty);
        set => SetValue(HintProperty, value);
    }

    public string Note
    {
        get => (string)GetValue(NoteProperty);
        set => SetValue(NoteProperty, value);
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        if (GetTemplateChild("PART_Mark") is not FrameworkElement mark)
        {
            return;
        }

        mark.PreviewMouseLeftButtonUp += (_, args) =>
        {
            Show(mark);
            args.Handled = true;
        };
        mark.GotKeyboardFocus += (_, _) =>
        {
            if (InputManager.Current.MostRecentInputDevice is KeyboardDevice)
            {
                Show(mark);
            }
        };

        mark.LostKeyboardFocus += (_, _) => Hide(mark);

        mark.KeyDown += (_, args) =>
        {
            if (args.Key is Key.Enter or Key.Space)
            {
                Show(mark);
                args.Handled = true;
            }
        };
    }

    private static void Show(FrameworkElement mark)
    {
        if (ToolTipService.GetToolTip(mark) is not ToolTip tip)
        {
            return;
        }

        tip.PlacementTarget = mark;
        tip.IsOpen = true;
    }

    private static void Hide(FrameworkElement mark)
    {
        if (ToolTipService.GetToolTip(mark) is ToolTip tip)
        {
            tip.IsOpen = false;
        }
    }
}
