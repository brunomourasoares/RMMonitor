using System.Windows.Controls;
using System.Windows;

namespace RMMonitor.Controls;

/// <summary>
/// Encapsulates the frequency panel presentation. Sensor polling and history retention
/// remain in MainWindow; this control only exposes the displayed values and chart.
/// </summary>
public partial class FrequencyPanelControl : System.Windows.Controls.UserControl
{
    public FrequencyPanelControl()
    {
        InitializeComponent();
    }

    public TextBlock CpuFrequencyText => CpuFrequencyTextInternal;
    public TextBlock GpuFrequencyText => GpuFrequencyTextInternal;
    public FrequencyHistoryChart FrequencyHistoryChart => FrequencyHistoryChartInternal;
}
