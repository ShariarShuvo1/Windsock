using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Windsock.App.Controls;

/// <summary>
/// A line a screen reader reads out again whenever its text changes: the
/// sentence under a chart saying the bar the keyboard is on.
/// </summary>
/// <remarks>
/// <para>
/// WPF reads <see cref="AutomationProperties.LiveSettingProperty"/> but never
/// raises the live-region event itself when a text block's text changes; an
/// app has to. So <c>AutomationProperties.LiveSetting="Polite"</c> alone left
/// those lines silent: Narrator heard nothing as the arrows moved along a
/// chart. <c>controls:LiveRegion.Speaks="True"</c> on a <see cref="TextBlock"/>
/// sets it polite (unless it already says assertive) and raises the event
/// after each change, once the new text is laid out. It follows the block's
/// <c>Text</c>, so a block built of runs needs its text bound whole.
/// </para>
/// <para>
/// The text is followed only while the block is loaded, so a page's view
/// that is thrown away is not held by the property's change list.
/// </para>
/// </remarks>
public static class LiveRegion
{
    public static readonly DependencyProperty SpeaksProperty =
        DependencyProperty.RegisterAttached(
            "Speaks",
            typeof(bool),
            typeof(LiveRegion),
            new PropertyMetadata(false, OnSpeaksChanged));

    private static readonly DependencyPropertyDescriptor TextDescriptor =
        DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));

    /// <summary>Whether the block is read out again when its text changes.</summary>
    public static bool GetSpeaks(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (bool)element.GetValue(SpeaksProperty);
    }

    /// <summary>Sets whether the block is read out again when its text changes.</summary>
    public static void SetSpeaks(DependencyObject element, bool value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(SpeaksProperty, value);
    }

    private static void OnSpeaksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock block)
        {
            return;
        }

        block.Loaded -= OnLoaded;
        block.Unloaded -= OnUnloaded;
        TextDescriptor.RemoveValueChanged(block, OnTextChanged);

        if (e.NewValue is not true)
        {
            return;
        }

        if (AutomationProperties.GetLiveSetting(block) == AutomationLiveSetting.Off)
        {
            AutomationProperties.SetLiveSetting(block, AutomationLiveSetting.Polite);
        }

        block.Loaded += OnLoaded;
        block.Unloaded += OnUnloaded;

        if (block.IsLoaded)
        {
            TextDescriptor.AddValueChanged(block, OnTextChanged);
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        TextDescriptor.RemoveValueChanged(sender, OnTextChanged);
        TextDescriptor.AddValueChanged(sender, OnTextChanged);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) =>
        TextDescriptor.RemoveValueChanged(sender, OnTextChanged);

    private static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is not TextBlock { Text.Length: > 0 } block || !AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            return;
        }

        // After layout: a line that was collapsed while empty has no peer to speak from until it shows.
        block.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (block.IsVisible && (UIElementAutomationPeer.FromElement(block) ?? UIElementAutomationPeer.CreatePeerForElement(block)) is { } peer)
            {
                peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            }
        }));
    }
}
