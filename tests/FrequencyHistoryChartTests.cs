using RMMonitor.Controls;
using RMMonitor.Models;
using Xunit;

namespace RMMonitor.Tests;

public sealed class FrequencyHistoryChartTests
{
    [Fact]
    public void ExposesSamplesAsReadOnlySensorSnapshotCollection()
    {
        var property = typeof(FrequencyHistoryChart).GetProperty(nameof(FrequencyHistoryChart.Samples));

        Assert.NotNull(property);
        Assert.Equal(typeof(IReadOnlyList<SensorSnapshot>), property!.PropertyType);
    }

    [Fact]
    public void SamplesDependencyPropertyDefaultsToEmptyHistory()
    {
        var metadata = FrequencyHistoryChart.SamplesProperty.GetMetadata(typeof(FrequencyHistoryChart));

        var samples = Assert.IsAssignableFrom<IReadOnlyList<SensorSnapshot>>(metadata.DefaultValue);
        Assert.Empty(samples);
    }

    [Fact]
    public void ExposesDependencyPropertyForSamplesBinding()
    {
        var field = typeof(FrequencyHistoryChart).GetField(nameof(FrequencyHistoryChart.SamplesProperty));

        Assert.NotNull(field);
        Assert.Equal(typeof(System.Windows.DependencyProperty), field!.FieldType);
    }
}
