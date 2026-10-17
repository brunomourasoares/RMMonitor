using System.Windows.Controls;
using WpfProgressBar = System.Windows.Controls.ProgressBar;
using WpfTextBlock = System.Windows.Controls.TextBlock;
using WpfUserControl = System.Windows.Controls.UserControl;

namespace RMMonitor.Controls;

/// <summary>
/// Owns the dashboard's top-level CPU, GPU, memory, and storage metric cards.
/// Sensor acquisition remains in HardwareMonitoringService; MainWindow supplies rendered values.
/// </summary>
public partial class DashboardMetricsControl : WpfUserControl
{
    public DashboardMetricsControl() => InitializeComponent();
    public WpfTextBlock CpuModelText => CpuModelTextElement;
    public WpfTextBlock CpuTempText => CpuTempTextElement;
    public WpfTextBlock CpuLoadText => CpuLoadTextElement;
    public WpfTextBlock GpuModelText => GpuModelTextElement;
    public WpfTextBlock GpuTempText => GpuTempTextElement;
    public WpfTextBlock GpuLoadText => GpuLoadTextElement;
    public WpfTextBlock MemoryText => MemoryTextElement;
    public WpfTextBlock MemoryPercentText => MemoryPercentTextElement;
    public WpfProgressBar MemoryUsageBar => MemoryUsageBarElement;
    public WpfTextBlock StorageVolumesText => StorageVolumesTextElement;
    public WpfTextBlock StorageModelText => StorageModelTextElement;
    public WpfTextBlock StorageUsedPercentText => StorageUsedPercentTextElement;
    public WpfTextBlock StorageCapacityText => StorageCapacityTextElement;
    public WpfProgressBar StorageUsageBar => StorageUsageBarElement;
}
