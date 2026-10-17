using System.Windows;
using Point = System.Windows.Point;
using System.Windows.Controls;
using System.Windows.Media;
using RMMonitor.Models;

namespace RMMonitor.Controls;

/// <summary>
/// Renders CPU and GPU clock samples only. Sample acquisition, retention, and polling
/// remain owned by MainWindow so chart rendering cannot alter sensor monitoring.
/// </summary>
public partial class FrequencyHistoryChart : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty SamplesProperty = DependencyProperty.Register(
        nameof(Samples),
        typeof(IReadOnlyList<SensorSnapshot>),
        typeof(FrequencyHistoryChart),
        new FrameworkPropertyMetadata(Array.Empty<SensorSnapshot>(), OnSamplesChanged));

    public FrequencyHistoryChart()
    {
        InitializeComponent();
    }

    public IReadOnlyList<SensorSnapshot> Samples
    {
        get => (IReadOnlyList<SensorSnapshot>)GetValue(SamplesProperty);
        set => SetValue(SamplesProperty, value ?? Array.Empty<SensorSnapshot>());
    }

    private static void OnSamplesChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is FrequencyHistoryChart chart)
            chart.DrawHistory();
    }

    private void ChartCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHistory();

    private void DrawHistory()
    {
        if (ChartCanvas is null || CpuFrequencyLine is null || GpuFrequencyLine is null)
            return;

        var width = ChartCanvas.ActualWidth;
        var height = ChartCanvas.ActualHeight;
        if (width <= 1 || height <= 1)
            return;

        var samples = Samples;
        var validValues = samples
            .SelectMany(sample => new[] { sample.CpuFrequencyMHz, sample.GpuFrequencyMHz })
            .Where(value => value.HasValue && double.IsFinite(value.Value) && value.Value >= 100)
            .Select(value => value!.Value)
            .ToArray();

        if (validValues.Length == 0)
        {
            CpuFrequencyLine.Data = Geometry.Empty;
            GpuFrequencyLine.Data = Geometry.Empty;
            return;
        }

        var min = Math.Max(0, Math.Floor(validValues.Min() / 500d) * 500d);
        var max = Math.Ceiling(validValues.Max() / 500d) * 500d;
        if (max - min < 500)
            max = min + 500;

        var cpuGeometry = new PathGeometry();
        var gpuGeometry = new PathGeometry();
        PathFigure? cpuFigure = null;
        PathFigure? gpuFigure = null;
        var newest = samples.Count > 0 ? samples[^1].UpdatedAt : DateTime.MinValue;
        DateTime? previousSampleAt = null;

        foreach (var sample in samples)
        {
            // A polling gap breaks the line instead of implying continuous telemetry.
            if (previousSampleAt is DateTime previous &&
                (sample.UpdatedAt - previous).TotalSeconds > 2.5)
            {
                cpuFigure = null;
                gpuFigure = null;
            }

            // Fixed 60-second horizontal scale keeps the graph stable while polling.
            var ageSeconds = Math.Clamp((newest - sample.UpdatedAt).TotalSeconds, 0, 60);
            var x = width * (1 - ageSeconds / 60d);
            AddPoint(cpuGeometry, ref cpuFigure, sample.CpuFrequencyMHz, x, height, min, max);
            AddPoint(gpuGeometry, ref gpuFigure, sample.GpuFrequencyMHz, x, height, min, max);
            previousSampleAt = sample.UpdatedAt;
        }

        CpuFrequencyLine.Data = cpuGeometry;
        GpuFrequencyLine.Data = gpuGeometry;
    }

    private static void AddPoint(
        PathGeometry geometry,
        ref PathFigure? currentFigure,
        double? value,
        double x,
        double height,
        double min,
        double max)
    {
        if (value is not double frequency || !double.IsFinite(frequency) || frequency < 100)
        {
            // Missing samples break the path so the chart does not bridge invalid data.
            currentFigure = null;
            return;
        }

        var y = height - 4 - ((frequency - min) / (max - min)) * (height - 8);
        var point = new Point(x, Math.Clamp(y, 2, height - 2));
        if (currentFigure is null)
        {
            currentFigure = new PathFigure { StartPoint = point, IsFilled = false, IsClosed = false };
            geometry.Figures.Add(currentFigure);
        }
        else
        {
            currentFigure.Segments.Add(new LineSegment(point, isStroked: true));
        }
    }
}
