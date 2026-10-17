using System.Windows;
using Point = System.Windows.Point;
using System.Windows.Controls;
using System.Windows.Media;
using RMMonitor.Models;

namespace RMMonitor.Controls;

/// <summary>
/// Displays temperature history only. MainWindow remains responsible for polling,
/// retaining samples, filter interaction, and CSV export.
/// </summary>
public partial class TemperatureHistoryChart : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IReadOnlyList<SensorSnapshot>),
        typeof(TemperatureHistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<SensorSnapshot>(), OnChartPropertyChanged));

    public static readonly DependencyProperty ShowCpuProperty = DependencyProperty.Register(
        nameof(ShowCpu),
        typeof(bool),
        typeof(TemperatureHistoryChart),
        new FrameworkPropertyMetadata(true, OnChartPropertyChanged));

    public static readonly DependencyProperty ShowGpuProperty = DependencyProperty.Register(
        nameof(ShowGpu),
        typeof(bool),
        typeof(TemperatureHistoryChart),
        new FrameworkPropertyMetadata(true, OnChartPropertyChanged));

    public TemperatureHistoryChart() => InitializeComponent();

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

    private static void OnChartPropertyChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is TemperatureHistoryChart chart)
            chart.DrawHistory();
    }

    private void DrawHistory()
    {
        if (ThermalChartCanvas is null || CpuHistoryPath is null || GpuHistoryPath is null)
            return;

        CpuHistoryPath.Visibility = ShowCpu ? Visibility.Visible : Visibility.Collapsed;
        GpuHistoryPath.Visibility = ShowGpu ? Visibility.Visible : Visibility.Collapsed;
        CpuHistoryPath.Data = ShowCpu
            ? BuildTemperatureGeometry(Samples.Select(point => point.CpuTemperatureC), Samples.Count)
            : Geometry.Empty;
        GpuHistoryPath.Data = ShowGpu
            ? BuildTemperatureGeometry(Samples.Select(point => point.GpuTemperatureC), Samples.Count)
            : Geometry.Empty;
    }

    private static Geometry BuildTemperatureGeometry(IEnumerable<double?> source, int sampleCount) =>
        BuildHistoryGeometry(source, sampleCount, static temperature =>
        {
            var normalized = Math.Clamp((temperature - 30d) / 60d, 0d, 1d);
            return 128d - normalized * 120d;
        });

    private static Geometry BuildHistoryGeometry(
        IEnumerable<double?> source,
        int sampleCount,
        Func<double, double> getY)
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
                    var point = new Point(x, getY(number));
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
