using System.Windows;
using Point = System.Windows.Point;
using System.Windows.Controls;
using System.Windows.Media;
using RMMonitor.Models;

namespace RMMonitor.Controls;

/// <summary>
/// Renders CPU, GPU, and memory utilization samples. Polling, history retention,
/// and CSV export remain owned by MainWindow.
/// </summary>
public partial class UtilizationHistoryChart : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IReadOnlyList<SensorSnapshot>),
        typeof(UtilizationHistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<SensorSnapshot>(), OnChartPropertyChanged));

    public static readonly DependencyProperty ShowCpuProperty = DependencyProperty.Register(
        nameof(ShowCpu), typeof(bool), typeof(UtilizationHistoryChart),
        new FrameworkPropertyMetadata(true, OnChartPropertyChanged));

    public static readonly DependencyProperty ShowGpuProperty = DependencyProperty.Register(
        nameof(ShowGpu), typeof(bool), typeof(UtilizationHistoryChart),
        new FrameworkPropertyMetadata(true, OnChartPropertyChanged));

    public static readonly DependencyProperty ShowMemoryProperty = DependencyProperty.Register(
        nameof(ShowMemory), typeof(bool), typeof(UtilizationHistoryChart),
        new FrameworkPropertyMetadata(true, OnChartPropertyChanged));

    public UtilizationHistoryChart() => InitializeComponent();

    public IReadOnlyList<SensorSnapshot> Samples
    {
        get => (IReadOnlyList<SensorSnapshot>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value ?? Array.Empty<SensorSnapshot>());
    }

    public bool ShowCpu
    {
        get => (bool)GetValue(ShowCpuProperty);
        set => SetValue(ShowCpuProperty, value);
    }

    public bool ShowGpu
    {
        get => (bool)GetValue(ShowGpuProperty);
        set => SetValue(ShowGpuProperty, value);
    }

    public bool ShowMemory
    {
        get => (bool)GetValue(ShowMemoryProperty);
        set => SetValue(ShowMemoryProperty, value);
    }

    private static void OnChartPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is UtilizationHistoryChart chart)
            chart.DrawHistory();
    }

    private void DrawHistory()
    {
        if (CpuUtilizationHistoryPath is null ||
            GpuUtilizationHistoryPath is null ||
            MemoryUtilizationHistoryPath is null)
            return;

        CpuUtilizationHistoryPath.Visibility = ShowCpu ? Visibility.Visible : Visibility.Collapsed;
        GpuUtilizationHistoryPath.Visibility = ShowGpu ? Visibility.Visible : Visibility.Collapsed;
        MemoryUtilizationHistoryPath.Visibility = ShowMemory ? Visibility.Visible : Visibility.Collapsed;

        CpuUtilizationHistoryPath.Data = ShowCpu
            ? BuildGeometry(Samples.Select(sample => sample.CpuLoadPercent), Samples.Count)
            : Geometry.Empty;
        GpuUtilizationHistoryPath.Data = ShowGpu
            ? BuildGeometry(Samples.Select(sample => sample.GpuLoadPercent), Samples.Count)
            : Geometry.Empty;
        MemoryUtilizationHistoryPath.Data = ShowMemory
            ? BuildGeometry(Samples.Select(sample =>
                sample.MemoryUsedGiB.HasValue && sample.MemoryTotalGiB is > 0
                    ? (double?)(sample.MemoryUsedGiB.Value / sample.MemoryTotalGiB.Value * 100d)
                    : null), Samples.Count)
            : Geometry.Empty;
    }

    private static Geometry BuildGeometry(IEnumerable<double?> source, int sampleCount)
    {
        if (sampleCount == 0)
            return Geometry.Empty;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            var index = 0;
            var pointCount = 0;
            Point firstPoint = default;
            foreach (var value in source)
            {
                if (value is double number && double.IsFinite(number))
                {
                    var x = sampleCount <= 1 ? 0d : index * 900d / (sampleCount - 1);
                    var y = 128d - Math.Clamp(number, 0d, 100d) * 1.2d;
                    var point = new Point(x, y);
                    if (pointCount == 0)
                    {
                        firstPoint = point;
                        context.BeginFigure(point, isFilled: false, isClosed: false);
                    }
                    else
                    {
                        context.LineTo(point, isStroked: true, isSmoothJoin: true);
                    }

                    pointCount++;
                }

                index++;
            }

            if (pointCount == 0)
                return Geometry.Empty;

            if (pointCount == 1)
                context.LineTo(new Point(Math.Min(900d, firstPoint.X + 2d), firstPoint.Y),
                    isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }
}
