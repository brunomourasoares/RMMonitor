using RMMonitor.Controls;
using RMMonitor.Models;
using Xunit;

namespace RMMonitor.Tests;

public sealed class TemperatureHistoryChartTests
{
    [Theory]
    [InlineData(nameof(TemperatureHistoryChart.Samples), typeof(IReadOnlyList<SensorSnapshot>))]
    [InlineData(nameof(TemperatureHistoryChart.ShowCpu), typeof(bool))]
    [InlineData(nameof(TemperatureHistoryChart.ShowGpu), typeof(bool))]
    public void ExposesChartInputProperties(string propertyName, Type expectedType)
    {
        var property = typeof(TemperatureHistoryChart).GetProperty(propertyName);

        Assert.NotNull(property);
        Assert.Equal(expectedType, property!.PropertyType);
    }

    [Fact]
    public void ChartDefaultsToEmptyHistoryWithCpuAndGpuVisible()
    {
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<SensorSnapshot>>(
            TemperatureHistoryChart.SamplesProperty.GetMetadata(typeof(TemperatureHistoryChart)).DefaultValue));
        Assert.True((bool)TemperatureHistoryChart.ShowCpuProperty.GetMetadata(typeof(TemperatureHistoryChart)).DefaultValue);
        Assert.True((bool)TemperatureHistoryChart.ShowGpuProperty.GetMetadata(typeof(TemperatureHistoryChart)).DefaultValue);
    }

    [Theory]
    [InlineData(nameof(TemperatureHistoryChart.SamplesProperty))]
    [InlineData(nameof(TemperatureHistoryChart.ShowCpuProperty))]
    [InlineData(nameof(TemperatureHistoryChart.ShowGpuProperty))]
    public void ExposesDependencyPropertiesForBinding(string propertyName)
    {
        var field = typeof(TemperatureHistoryChart).GetField(propertyName);

        Assert.NotNull(field);
        Assert.Equal(typeof(System.Windows.DependencyProperty), field!.FieldType);
    }
}
