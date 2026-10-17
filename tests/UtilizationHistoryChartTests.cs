using RMMonitor.Controls;
using RMMonitor.Models;
using Xunit;

namespace RMMonitor.Tests;

public sealed class UtilizationHistoryChartTests
{
    [Theory]
    [InlineData(nameof(UtilizationHistoryChart.Samples), typeof(IReadOnlyList<SensorSnapshot>))]
    [InlineData(nameof(UtilizationHistoryChart.ShowCpu), typeof(bool))]
    [InlineData(nameof(UtilizationHistoryChart.ShowGpu), typeof(bool))]
    [InlineData(nameof(UtilizationHistoryChart.ShowMemory), typeof(bool))]
    public void ExposesChartInputProperties(string propertyName, Type expectedType)
    {
        var property = typeof(UtilizationHistoryChart).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(expectedType, property!.PropertyType);
    }

    [Fact]
    public void ChartDefaultsToEmptyHistoryWithAllSeriesVisible()
    {
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<SensorSnapshot>>(
            UtilizationHistoryChart.SamplesProperty.GetMetadata(typeof(UtilizationHistoryChart)).DefaultValue));
        Assert.True((bool)UtilizationHistoryChart.ShowCpuProperty.GetMetadata(typeof(UtilizationHistoryChart)).DefaultValue);
        Assert.True((bool)UtilizationHistoryChart.ShowGpuProperty.GetMetadata(typeof(UtilizationHistoryChart)).DefaultValue);
        Assert.True((bool)UtilizationHistoryChart.ShowMemoryProperty.GetMetadata(typeof(UtilizationHistoryChart)).DefaultValue);
    }

    [Theory]
    [InlineData(nameof(UtilizationHistoryChart.SamplesProperty))]
    [InlineData(nameof(UtilizationHistoryChart.ShowCpuProperty))]
    [InlineData(nameof(UtilizationHistoryChart.ShowGpuProperty))]
    [InlineData(nameof(UtilizationHistoryChart.ShowMemoryProperty))]
    public void ExposesDependencyPropertiesForBinding(string propertyName)
    {
        var field = typeof(UtilizationHistoryChart).GetField(propertyName);

        Assert.NotNull(field);
        Assert.Equal(typeof(System.Windows.DependencyProperty), field!.FieldType);
    }
}
