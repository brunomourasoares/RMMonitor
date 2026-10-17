using System.Windows.Controls;
using RMMonitor.Controls;
using Xunit;

namespace RMMonitor.Tests;

public sealed class DashboardMetricsControlTests
{
    [Theory]
    [InlineData("CpuModelText", typeof(TextBlock))]
    [InlineData("CpuTempText", typeof(TextBlock))]
    [InlineData("CpuLoadText", typeof(TextBlock))]
    [InlineData("GpuModelText", typeof(TextBlock))]
    [InlineData("GpuTempText", typeof(TextBlock))]
    [InlineData("GpuLoadText", typeof(TextBlock))]
    [InlineData("MemoryText", typeof(TextBlock))]
    [InlineData("MemoryPercentText", typeof(TextBlock))]
    [InlineData("MemoryUsageBar", typeof(ProgressBar))]
    [InlineData("StorageVolumesText", typeof(TextBlock))]
    [InlineData("StorageModelText", typeof(TextBlock))]
    [InlineData("StorageUsedPercentText", typeof(TextBlock))]
    [InlineData("StorageCapacityText", typeof(TextBlock))]
    [InlineData("StorageUsageBar", typeof(ProgressBar))]
    public void ExposesExpectedDashboardMetricElement(string propertyName, Type expectedType)
    {
        var property = typeof(DashboardMetricsControl).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(expectedType, property!.PropertyType);
    }
}