using System.Windows.Controls;

namespace Windsock.App.Views;

/// <summary>
/// The tabbed lower section: a panel per tab, opening on the machine as a whole
/// and narrowing from there to one process, one period, one setting.
/// </summary>
public partial class WorkspaceView : UserControl
{
    public WorkspaceView(
        SystemPanelView system,
#if !STORE
        ProcessPanelView processes,
#endif
        HistoryPanelView history,
        TaskbarPanelView taskbar,
        SettingsPanelView settings)
    {
        InitializeComponent();
        SystemTab.Content = system;
        HistoryTab.Content = history;
        TaskbarTab.Content = taskbar;
        SettingsTab.Content = settings;

#if STORE
        Tabs.Items.Remove(ProcessTab);
#else
        ProcessTab.Content = processes;
#endif
    }
}
