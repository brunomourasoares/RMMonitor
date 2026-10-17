using RMMonitor.Controls;
using Xunit;

namespace RMMonitor.Tests;

public sealed class FrequencyPanelControlTests
{
    [Fact]
    public void ExposesCpuAndGpuFrequencyTextBlocks()
    {
        Assert.Equal(typeof(System.Windows.Controls.TextBlock),
            typeof(FrequencyPanelControl).GetProperty(nameof(FrequencyPanelControl.CpuFrequencyText))!.PropertyType);
        Assert.Equal(typeof(System.Windows.Controls.TextBlock),
            typeof(FrequencyPanelControl).GetProperty(nameof(FrequencyPanelControl.GpuFrequencyText))!.PropertyType);
    }

    [Fact]
    public void ExposesFrequencyHistoryChart()
    {
        Assert.Equal(typeof(FrequencyHistoryChart),
            typeof(FrequencyPanelControl).GetProperty(nameof(FrequencyPanelControl.FrequencyHistoryChart))!.PropertyType);
    }
}
