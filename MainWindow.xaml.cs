using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.IO;
using System.Diagnostics;
using System.Text.Json;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using Button = System.Windows.Controls.Button;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Control = System.Windows.Controls.Control;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using RMMonitor.Controls;
using RMMonitor.Models;
using RMMonitor.Services;

namespace RMMonitor;

public partial class MainWindow : Window
{
    private static readonly TimeSpan HistoryWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HistoryPersistenceInterval = TimeSpan.FromSeconds(30);
    private const int WmActivateExistingInstance = 0x8001;
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left; public int Top; public int Right; public int Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeMinMaxInfo
    {
        public NativePoint Reserved;
        public NativePoint MaxSize;
        public NativePoint MaxPosition;
        public NativePoint MinTrackSize;
        public NativePoint MaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct NativeMonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint MonitorFromWindow(nint hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(nint monitor, ref NativeMonitorInfo info);
    private readonly DispatcherTimer _pollTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _storageCycleTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly Queue<SensorSnapshot> _history = new();
    private readonly Queue<SensorSnapshot> _utilizationHistory = new();
    private readonly Queue<SensorSnapshot> _frequencyHistory = new();
    private readonly Queue<SensorSnapshot> _storedHistory = new();
    private readonly TelemetryHistoryStore _historyStore = new();
    private readonly TelemetryHistoryWriter _historyWriter;
    private DateTime _lastHistorySaveAt = DateTime.MinValue;
    private DateTime _lastPersistedSnapshotAt = DateTime.MinValue;
    private TimeSpan _historyRetention = TelemetryHistoryStore.DefaultRetention;
    private HardwareMonitoringService? _monitor;
    private readonly DisplayTransportService _displayTransport = new();
    private bool _reading;
    private bool _closing;
    private string _language = "en";
    private DisplayTransportStatus? _lastDisplayStatus;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<DependencyObject, TranslationState> _translationStates = new();
    private SensorSnapshot? _lastSnapshot;
    private int _storageDriveIndex = -1;
    private string? _selectedStorageDriveName;
    private readonly string _preferencesPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RM Monitor", "preferences.json");
    private readonly string[] _legacyPreferencesPaths = { Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AquaCore", "preferences.json"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HWMetrics", "preferences.json"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WaterCoolerApp", "preferences.json") };
    private bool _loadingPreferences;
    private NotifyIcon? _trayIcon;
    private System.Drawing.Icon? _trayApplicationIcon;
    private bool _allowExit;
    private bool _closePromptAnswered;
    private bool _startHidden;

    // One profile per pump-block series/lighting family. Keeping palettes and timing here
    // makes user-selectable ARGB colors a future extension rather than a visual rewrite.
    private enum CoolerLightingMode { None, Rgb, Argb, Rainbow }
    private sealed record CoolerVisualProfile(
        string Family,
        bool IsBlack,
        bool HasNumericDisplay,
        CoolerLightingMode LightingMode,
        double RingSeconds,
        double OuterMirrorSeconds,
        double InnerMirrorSeconds,
        Color[] Palette);

    private static readonly Color[] DefaultArgbPalette =
    [
        Color.FromRgb(255, 42, 104),
        Color.FromRgb(255, 194, 54),
        Color.FromRgb(68, 255, 190),
        Color.FromRgb(45, 150, 255),
        Color.FromRgb(190, 74, 255)
    ];

    private static readonly Color[] RainbowPalette =
    [
        Color.FromRgb(255, 0, 0),
        Color.FromRgb(255, 150, 0),
        Color.FromRgb(255, 255, 0),
        Color.FromRgb(0, 255, 80),
        Color.FromRgb(0, 150, 255),
        Color.FromRgb(90, 0, 255),
        Color.FromRgb(255, 0, 180)
    ];

    private static CoolerVisualProfile CreateCoolerProfile(string[] parts, string modelCode)
    {
        var family = parts.Length > 3 ? parts[3] : "classic";
        var finish = parts.Length > 1 ? parts[1] : "white";
        var display = parts.Length > 2 && parts[2].Equals("display", StringComparison.OrdinalIgnoreCase);
        var lighting = parts.Length > 4 && Enum.TryParse<CoolerLightingMode>(parts[4], true, out var parsed)
            ? parsed
            : modelCode.Contains("WC-AB-", StringComparison.OrdinalIgnoreCase)
                ? CoolerLightingMode.None
                : modelCode.Contains("-RBW", StringComparison.OrdinalIgnoreCase)
                    ? CoolerLightingMode.Rainbow
                    : modelCode.Contains("-RGB", StringComparison.OrdinalIgnoreCase)
                        ? CoolerLightingMode.Rgb
                        : CoolerLightingMode.Argb;

        // Profiles are intentionally series-specific; model size (120/240/360) does not
        // alter the pump face, while finish/display and lighting technology do.
        return family switch
        {
            "auraice" => new(family, finish.Equals("black", StringComparison.OrdinalIgnoreCase), display,
                CoolerLightingMode.Argb, 7.5, 0, 0, DefaultArgbPalette),
            "aurafrostpro" => new(family, finish.Equals("black", StringComparison.OrdinalIgnoreCase), false,
                CoolerLightingMode.Argb, 6.5, 10.5, 5.8, DefaultArgbPalette),
            "square" => new(family, finish.Equals("black", StringComparison.OrdinalIgnoreCase), false,
                CoolerLightingMode.Rainbow, 9.0, 0, 0, RainbowPalette),
            "allblack" => new(family, true, false, CoolerLightingMode.None, 0, 0, 0, Array.Empty<Color>()),
            _ => new("classic", finish.Equals("black", StringComparison.OrdinalIgnoreCase), false,
                lighting, 9.0, 0, 0,
                lighting == CoolerLightingMode.Rainbow ? RainbowPalette :
                lighting == CoolerLightingMode.Rgb ? [Color.FromRgb(255, 0, 0), Color.FromRgb(0, 255, 0), Color.FromRgb(0, 0, 255)] :
                lighting == CoolerLightingMode.None ? Array.Empty<Color>() : DefaultArgbPalette)
        };
    }

    private static LinearGradientBrush CreatePaletteBrush(IReadOnlyList<Color> palette)
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        if (palette.Count == 0)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(45, 55, 65), 0));
            brush.GradientStops.Add(new GradientStop(Color.FromRgb(45, 55, 65), 1));
            return brush;
        }

        for (var i = 0; i < palette.Count; i++)
            brush.GradientStops.Add(new GradientStop(palette[i], palette.Count == 1 ? 0 : (double)i / (palette.Count - 1)));
        return brush;
    }

    private sealed class AppPreferences
    {
        public int ThemeIndex { get; set; }
        public int PollIntervalIndex { get; set; } = 1;
        public int LanguageIndex { get; set; } = 1;
        public int CoolerModelIndex { get; set; }
        public int HistoryRetentionIndex { get; set; }
        public bool MinimizeToTrayOnClose { get; set; }
        public bool StartWithWindows { get; set; }
        public bool ClosePromptAnswered { get; set; }
    }

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += MainWindow_SourceInitialized;
        _historyWriter = new TelemetryHistoryWriter(_historyStore);
        ApplyApplicationIcon();
        LoadPreferences();
        ApplyLanguage(Localizer.CodeAt(LanguageSelector.SelectedIndex));
        _pollTimer.Tick += async (_, _) => await RefreshSensorsAsync();
        _storageCycleTimer.Tick += (_, _) => RotateStorageDrive();
        InitializeTrayIcon();
    }

    private void ApplyApplicationIcon()
    {
        try
        {
            using var bitmap = LoadApplicationBitmap();
            var handle = bitmap.GetHicon();
            try
            {
                Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                    handle, Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[RM Monitor] Could not apply window icon: {ex}");
        }
    }

    private void LoadPreferences()
    {
        _loadingPreferences = true;
        try
        {
            var preferencesFile = File.Exists(_preferencesPath)
                ? _preferencesPath
                : _legacyPreferencesPaths.FirstOrDefault(File.Exists) ?? _preferencesPath;
            var preferences = File.Exists(preferencesFile)
                ? JsonSerializer.Deserialize<AppPreferences>(File.ReadAllText(preferencesFile)) ?? new AppPreferences()
                : new AppPreferences { LanguageIndex = Localizer.DetectSystemLanguageIndex() };
            ThemeSelector.SelectedIndex = Math.Clamp(preferences.ThemeIndex, 0, 1);
            PollIntervalSelector.SelectedIndex = Math.Clamp(preferences.PollIntervalIndex, 0, 2);
            _pollTimer.Interval = GetSelectedPollInterval();
            LanguageSelector.SelectedIndex = Math.Clamp(preferences.LanguageIndex, 0, Localizer.Languages.Count - 1);
            CoolerModelSelector.SelectedIndex = Math.Clamp(preferences.CoolerModelIndex, 0, Math.Max(0, CoolerModelSelector.Items.Count - 1));
            HistoryRetentionSelector.SelectedIndex = Math.Clamp(preferences.HistoryRetentionIndex, 0, HistoryRetentionSelector.Items.Count - 1);
            CloseToTrayCheckBox.IsChecked = preferences.MinimizeToTrayOnClose;
            StartWithWindowsCheckBox.IsChecked = preferences.StartWithWindows;
            _closePromptAnswered = preferences.ClosePromptAnswered;
        }
        catch (Exception)
        {
            ThemeSelector.SelectedIndex = 0;
            PollIntervalSelector.SelectedIndex = 1;
            LanguageSelector.SelectedIndex = Localizer.DetectSystemLanguageIndex();
            CoolerModelSelector.SelectedIndex = 0;
            HistoryRetentionSelector.SelectedIndex = 0;
            CloseToTrayCheckBox.IsChecked = false;
            StartWithWindowsCheckBox.IsChecked = false;
            _closePromptAnswered = false;
        }
        finally
        {
            _loadingPreferences = false;
        }
    }

    private void SavePreferences()
    {
        if (_loadingPreferences || !IsLoaded) return;
        try
        {
            var preferences = new AppPreferences
            {
                ThemeIndex = Math.Max(0, ThemeSelector.SelectedIndex),
                PollIntervalIndex = Math.Max(0, PollIntervalSelector.SelectedIndex),
                LanguageIndex = Math.Max(0, LanguageSelector.SelectedIndex),
                CoolerModelIndex = Math.Max(0, CoolerModelSelector.SelectedIndex),
                HistoryRetentionIndex = Math.Max(0, HistoryRetentionSelector.SelectedIndex),
                MinimizeToTrayOnClose = CloseToTrayCheckBox.IsChecked == true,
                StartWithWindows = StartWithWindowsCheckBox.IsChecked == true,
                ClosePromptAnswered = _closePromptAnswered
            };
            var directory = Path.GetDirectoryName(_preferencesPath)!;
            Directory.CreateDirectory(directory);
            var temporaryPath = _preferencesPath + ".tmp";
            try
            {
                File.WriteAllText(temporaryPath, JsonSerializer.Serialize(preferences, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporaryPath, _preferencesPath, overwrite: true);
            }
            finally
            {
                try
                {
                    if (File.Exists(temporaryPath))
                        File.Delete(temporaryPath);
                }
                catch (IOException)
                {
                    // Cleanup is best-effort; preference persistence must not affect UI availability.
                }
                catch (UnauthorizedAccessException)
                {
                    // Cleanup is best-effort; preference persistence must not affect UI availability.
                }
            }
        }
        catch (Exception)
        {
            // Preference persistence is best-effort; UI interaction should remain available.
        }
    }

    public bool StartHidden { get => _startHidden; set => _startHidden = value; }

    private static void FillRoundedRectangle(System.Drawing.Graphics graphics, System.Drawing.Brush brush, System.Drawing.Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    private static System.Drawing.Bitmap LoadApplicationBitmap()
    {
        var resource = Application.GetResourceStream(new Uri("Resources/Images/rise-mode.png", UriKind.Relative))
            ?? throw new InvalidOperationException("The application icon resource is unavailable.");
        using var stream = resource.Stream;
        using var bitmap = new System.Drawing.Bitmap(stream);
        return new System.Drawing.Bitmap(bitmap);
    }

    private System.Drawing.Icon CreateTrayIcon()
    {
        using var bitmap = LoadApplicationBitmap();
        using var scaled = new System.Drawing.Bitmap(bitmap, new System.Drawing.Size(32, 32));
        var handle = scaled.GetHicon();
        try
        {
            return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(handle).Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint handle);

    private void InitializeTrayIcon()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(T("Open RM Monitor"), null, (_, _) => Dispatcher.Invoke(RestoreFromTray));
        menu.Items.Add(T("Exit"), null, (_, _) => Dispatcher.Invoke(() => { _allowExit = true; Close(); }));
        _trayApplicationIcon = CreateTrayIcon();
        _trayIcon = new NotifyIcon
        {
            Icon = _trayApplicationIcon,
            Text = T("RM Monitor"),
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WindowMessageHook);
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == WmGetMinMaxInfo)
        {
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor != 0)
            {
                var monitorInfo = new NativeMonitorInfo { Size = Marshal.SizeOf<NativeMonitorInfo>() };
                if (GetMonitorInfo(monitor, ref monitorInfo))
                {
                    var info = Marshal.PtrToStructure<NativeMinMaxInfo>(lParam);
                    info.MaxPosition = new NativePoint
                    {
                        X = monitorInfo.Work.Left - monitorInfo.Monitor.Left,
                        Y = monitorInfo.Work.Top - monitorInfo.Monitor.Top
                    };
                    info.MaxSize = new NativePoint
                    {
                        X = monitorInfo.Work.Right - monitorInfo.Work.Left,
                        Y = monitorInfo.Work.Bottom - monitorInfo.Work.Top
                    };
                    Marshal.StructureToPtr(info, lParam, false);
                    handled = true;
                }
            }
            return 0;
        }

        if (message != WmActivateExistingInstance) return 0;

        RestoreFromTray();
        handled = true;
        return 0;
    }

    private void RestoreFromTray()
    {
        Opacity = 1;
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void HideToTray()
    {
        if (_trayIcon is not null) _trayIcon.Visible = true;
        ShowInTaskbar = false;
        Hide();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Apply the default model after InitializeComponent has created every named control.
        CoolerModelSelector_SelectionChanged(CoolerModelSelector, null!);
        if (FindResource("IntroStoryboard") is Storyboard intro) intro.Begin(this, true);
        if (_startHidden) HideToTray();
        try
        {
            _historyRetention = GetSelectedHistoryRetention();
            var now = DateTime.Now;
            var persistedHistory = _historyStore.Load(now, _historyRetention);
            foreach (var point in persistedHistory)
            {
                _storedHistory.Enqueue(point);
                _lastPersistedSnapshotAt = point.UpdatedAt;
                if (point.UpdatedAt >= now - HistoryWindow)
                {
                    _history.Enqueue(point);
                    _utilizationHistory.Enqueue(point);
                }
            }

            var monitor = await Task.Run(() => new HardwareMonitoringService());
            // The window can close while the hardware service is opening on a worker thread.
            // Dispose the newly created service instead of attaching it to a closed window.
            if (_closing)
            {
                monitor.Dispose();
                return;
            }

            _monitor = monitor;
            UpdateDisplayStatus(DisplayTransportStatus.DeviceNotFound);
            _pollTimer.Start();
            _storageCycleTimer.Start();
            await RefreshSensorsAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Sensor initialization failed: {ex}");
        }
    }

    private async Task RefreshSensorsAsync()
    {
        var monitor = _monitor;
        if (monitor is null || _reading || _closing) return;
        _reading = true;
        try
        {
            var snapshot = await Task.Run(monitor.Read);
            if (_closing) return;
            Render(snapshot);
            var displayStatus = await Task.Run(() => _displayTransport.Send(snapshot));
            if (!_closing) UpdateDisplayStatus(displayStatus);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] Sensor refresh failed: {ex}");
        }
        finally
        {
            _reading = false;
        }
    }

    private void UpdateDisplayStatus(DisplayTransportStatus status)
    {
        _lastDisplayStatus = status;
        var (title, detail, connected) = status switch
        {
            DisplayTransportStatus.Sent => (
                T("HID display connected"),
                T("Telemetry sent to physical display"),
                true),
            DisplayTransportStatus.InvalidTelemetry => (
                T("Waiting for device"),
                T("CPU temperature unavailable · not sent"),
                false),
            DisplayTransportStatus.IncompatibleReport => (
                T("HID display not found"),
                T("HID report incompatible"),
                false),
            DisplayTransportStatus.OpenFailed => (
                T("HID display not found"),
                T("Could not open HID display"),
                false),
            DisplayTransportStatus.Error => (
                T("HID display not found"),
                T("HID write failed"),
                false),
            _ => (
                T("Waiting for device"),
                T("HID display not found"),
                false)
        };

        DisplayConnectionTitleText.Text = title;
        DisplayConnectionStatusText.Text = detail;
        // SetResourceReference mantém a ligação com o tema: a cor acompanha a troca claro/escuro.
        DisplayConnectionIndicator.SetResourceReference(
            System.Windows.Shapes.Shape.FillProperty,
            connected ? "AccentBrush" : "WarningBrush");
    }

    private void RotateStorageDrive()
    {
        var volumes = _lastSnapshot?.StorageVolumes;
        if (volumes is null || volumes.Count < 2) return;

        _storageDriveIndex = (_storageDriveIndex + 1) % volumes.Count;
        RenderStorageVolume(volumes[_storageDriveIndex]);
    }

    private void RenderStorageVolume(StorageVolumeSnapshot volume)
    {
        _selectedStorageDriveName = volume.Name;
        // ProductModel identifies the physical disk, not the filesystem volume label.
        DashboardMetrics.StorageModelText.Text = string.IsNullOrWhiteSpace(volume.ProductModel)
            ? T("Drive model unavailable")
            : volume.ProductModel;
        DashboardMetrics.StorageUsedPercentText.Text = $"{volume.UsedPercent:0}%";
        DashboardMetrics.StorageCapacityText.Text =
            $"{FormatCapacity(volume.UsedGiB)} / {FormatCapacity(volume.TotalGiB)} {T("total")} - " +
            $"{FormatCapacity(volume.FreeGiB)} {T("free")}";
        AnimateProgress(DashboardMetrics.StorageUsageBar, volume.UsedPercent);
    }

    private static string FormatFrequency(double? frequencyMHz) =>
        frequencyMHz is double value && double.IsFinite(value) && value >= 100
            ? $"{value / 1000d:0.00} GHz"
            : "-- GHz";

    private void Render(SensorSnapshot s)
    {
        _lastSnapshot = s;
        _frequencyHistory.Enqueue(s);
        while (_frequencyHistory.Count > 0 && s.UpdatedAt - _frequencyHistory.Peek().UpdatedAt > TimeSpan.FromSeconds(60))
            _frequencyHistory.Dequeue();
        FrequencyPanel.CpuFrequencyText.Text = FormatFrequency(s.CpuFrequencyMHz);
        FrequencyPanel.GpuFrequencyText.Text = FormatFrequency(s.GpuFrequencyMHz);
        FrequencyPanel.FrequencyHistoryChart.Samples = _frequencyHistory.ToArray();
        DashboardMetrics.CpuModelText.Text = string.IsNullOrWhiteSpace(s.CpuModel) ? T("Processor model unavailable") : s.CpuModel;
        DashboardMetrics.GpuModelText.Text = string.IsNullOrWhiteSpace(s.GpuModel) ? T("Graphics model unavailable") : s.GpuModel;
        DashboardMetrics.StorageModelText.Text = string.IsNullOrWhiteSpace(s.StorageModel) ? T("Drive model unavailable") : s.StorageModel;
        DashboardMetrics.CpuTempText.Text = ShortTemp(s.CpuTemperatureC);
        CoolerPreview.SetTemperature(s.CpuTemperatureC.HasValue ? $"{s.CpuTemperatureC.Value:0}" : "--");
        DashboardMetrics.GpuTempText.Text = ShortTemp(s.GpuTemperatureC);
        if (s.StorageVolumes.Count > 0)
        {
            // Select the Windows system volume on first render; subsequent reads keep the
            // selected volume stable instead of snapping back to C: every polling cycle.
            var selectedIndex = -1;
            if (_selectedStorageDriveName is not null)
            {
                for (var i = 0; i < s.StorageVolumes.Count; i++)
                {
                    if (!string.Equals(s.StorageVolumes[i].Name, _selectedStorageDriveName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    selectedIndex = i;
                    break;
                }
            }
            if (selectedIndex >= 0)
            {
                _storageDriveIndex = selectedIndex;
            }
            else if (_storageDriveIndex < 0 || _storageDriveIndex >= s.StorageVolumes.Count ||
                     _selectedStorageDriveName is not null)
            {
                var systemIndex = -1;
                for (var i = 0; i < s.StorageVolumes.Count; i++)
                {
                    if (!string.Equals(s.StorageVolumes[i].Name, s.SystemDriveName,
                            StringComparison.OrdinalIgnoreCase))
                        continue;

                    systemIndex = i;
                    break;
                }
                _storageDriveIndex = systemIndex >= 0 ? systemIndex : 0;
            }
            RenderStorageVolume(s.StorageVolumes[_storageDriveIndex]);
            DashboardMetrics.StorageVolumesText.Text = Localizer.Get(s.StorageVolumesSummary);
        }
        else if (s.StorageUsedPercent.HasValue && s.StorageUsedGiB.HasValue && s.StorageTotalGiB.HasValue)
        {
            // Keep a safe fallback for snapshots with legacy aggregate storage data.
            DashboardMetrics.StorageUsedPercentText.Text = $"{s.StorageUsedPercent.Value:0}%";
            DashboardMetrics.StorageCapacityText.Text = $"{FormatCapacity(s.StorageUsedGiB.Value)} / {FormatCapacity(s.StorageTotalGiB.Value)} {T("total")}" +
                (s.StorageFreeGiB.HasValue ? $" - {FormatCapacity(s.StorageFreeGiB.Value)} {T("free")}" : string.Empty);
            AnimateProgress(DashboardMetrics.StorageUsageBar, s.StorageUsedPercent.Value);
            DashboardMetrics.StorageVolumesText.Text = Localizer.Get(s.StorageVolumesSummary);
        }
        else
        {
            DashboardMetrics.StorageUsedPercentText.Text = "N/D";
            DashboardMetrics.StorageCapacityText.Text = T("Storage capacity unavailable");
            DashboardMetrics.StorageUsageBar.Value = 0;
            DashboardMetrics.StorageVolumesText.Text = T("No local volumes detected");
        }

        DashboardMetrics.CpuLoadText.Text = T("Utilization ") + Percent(s.CpuLoadPercent);
        DashboardMetrics.GpuLoadText.Text = T("Utilization ") + Percent(s.GpuLoadPercent);

        if (s.MemoryUsedGiB.HasValue && s.MemoryTotalGiB.HasValue && s.MemoryTotalGiB.Value > 0)
        {
            var total = s.MemoryTotalGiB.Value;
            var used = Math.Clamp(s.MemoryUsedGiB.Value, 0, total);
            var free = Math.Max(0, total - used);
            var pct = Math.Clamp(used / total * 100, 0, 100);
            DashboardMetrics.MemoryText.Text = $"{pct:0}%";
                        DashboardMetrics.MemoryPercentText.Text = string.Format(T("{0} used · {1} free of {2}"), FormatCapacity(used), FormatCapacity(free), FormatCapacity(total));
            AnimateProgress(DashboardMetrics.MemoryUsageBar, pct);
        }
        else
        {
            DashboardMetrics.MemoryText.Text = "N/D";
            DashboardMetrics.MemoryPercentText.Text = T("Capacity unavailable");
            AnimateProgress(DashboardMetrics.MemoryUsageBar, 0);
        }

        _history.Enqueue(s);
        var cutoff = s.UpdatedAt - HistoryWindow;
        while (_history.Count > 0 && _history.Peek().UpdatedAt < cutoff) _history.Dequeue();
        UpdateTemperatureHistoryChart(s);

        _utilizationHistory.Enqueue(s);
        while (_utilizationHistory.Count > 0 && _utilizationHistory.Peek().UpdatedAt < cutoff) _utilizationHistory.Dequeue();
        UpdateUtilizationHistoryChart(s);

        if (_storedHistory.Count == 0 || s.UpdatedAt - _lastPersistedSnapshotAt >= TimeSpan.FromSeconds(10))
        {
            _storedHistory.Enqueue(s);
            _lastPersistedSnapshotAt = s.UpdatedAt;
        }

        var retentionCutoff = s.UpdatedAt - _historyRetention;
        while (_storedHistory.Count > 0 && _storedHistory.Peek().UpdatedAt < retentionCutoff)
            _storedHistory.Dequeue();

        if (s.UpdatedAt - _lastHistorySaveAt >= HistoryPersistenceInterval)
        {
            _historyWriter.Enqueue(_storedHistory, s.UpdatedAt, _historyRetention);
            _lastHistorySaveAt = s.UpdatedAt;
        }
    }

    private async void SaveHistory_Click(object sender, RoutedEventArgs e)
    {
        bool exportTemperature = sender is Button button &&
            button.Name == "SaveTemperatureHistoryButton";
        bool showCpu;
        bool showGpu;
        bool showRam = false;

        if (exportTemperature)
        {
            showCpu = TemperatureCpuFilter.IsChecked == true;
            showGpu = TemperatureGpuFilter.IsChecked == true;
        }
        else
        {
            showCpu = UtilizationCpuFilter.IsChecked == true;
            showGpu = UtilizationGpuFilter.IsChecked == true;
            showRam = UtilizationRamFilter.IsChecked == true;
        }

        if (!showCpu && !showGpu && !showRam)
        {
            MessageBox.Show(
                T("Select at least one series to export."),
                T("Nothing to export"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = T("Export history to CSV"),
            Filter = "CSV files (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = $"hw-metrics-history-{(exportTemperature ? "temperature" : "utilization")}-{DateTime.Now:yyyyMMdd-HHmmss}.csv"
        };

        if (dialog.ShowDialog(this) != true) return;

        // Export only the metric family and series selected in the corresponding chart.
        var snapshots = _storedHistory.ToArray();
        try
        {
            var csv = await Task.Run(() => TelemetryHistoryCsvExporter.Build(
                snapshots, exportTemperature, showCpu, showGpu, showRam));
            await Task.Run(() => File.WriteAllText(
                dialog.FileName,
                csv,
                new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true)));
            MessageBox.Show(
                T("History exported successfully."),
                T("Export complete"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[MainWindow] History export failed: {ex}");
            MessageBox.Show(
                T("Could not export history."),
                T("Export failed"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private TimeSpan GetSelectedHistoryRetention() => HistoryRetentionSelector.SelectedIndex switch
    {
        1 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(6),
        3 => TimeSpan.FromHours(24),
        _ => TimeSpan.FromMinutes(30)
    };

    private void HistoryRetentionSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || HistoryRetentionSelector.SelectedIndex < 0) return;
        _historyRetention = GetSelectedHistoryRetention();
        SavePreferences();

        var now = DateTime.Now;
        var cutoff = now - _historyRetention;
        var retained = _storedHistory.Where(snapshot => snapshot.UpdatedAt >= cutoff && snapshot.UpdatedAt <= now).ToArray();
        _storedHistory.Clear();
        foreach (var snapshot in retained) _storedHistory.Enqueue(snapshot);
        _historyWriter.Enqueue(_storedHistory, now, _historyRetention);
        _lastHistorySaveAt = now;
    }

    private void HistoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized || _lastSnapshot is null) return;
        UpdateTemperatureHistoryChart(_lastSnapshot);
        UpdateUtilizationHistoryChart(_lastSnapshot);
    }

    private void UpdateTemperatureHistoryChart(SensorSnapshot current)
    {
        TemperatureHistoryChart.Samples = _history.ToArray();
        TemperatureHistoryChart.ShowCpu = TemperatureCpuFilter.IsChecked == true;
        TemperatureHistoryChart.ShowGpu = TemperatureGpuFilter.IsChecked == true;
    }

    private void UpdateUtilizationHistoryChart(SensorSnapshot current)
    {
        UtilizationHistoryChart.Samples = _utilizationHistory.ToArray();
        UtilizationHistoryChart.ShowCpu = UtilizationCpuFilter.IsChecked == true;
        UtilizationHistoryChart.ShowGpu = UtilizationGpuFilter.IsChecked == true;
        UtilizationHistoryChart.ShowMemory = UtilizationRamFilter.IsChecked == true;
    }

    private static void AnimateProgress(ProgressBar bar, double target)
    {
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        bar.BeginAnimation(ProgressBar.ValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void ThemeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || ThemeSelector.SelectedIndex < 0 || Application.Current is null) return;
        var source = ThemeSelector.SelectedIndex == 1 ? "Themes/Light.xaml" : "Themes/Dark.xaml";
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        dictionaries.Clear();
        dictionaries.Add(new ResourceDictionary { Source = new Uri(source, UriKind.Relative) });
        SavePreferences();
    }

    private static TimeSpan GetSelectedPollInterval(int selectedIndex) => selectedIndex switch
    {
        0 => TimeSpan.FromSeconds(1),
        2 => TimeSpan.FromSeconds(5),
        _ => TimeSpan.FromSeconds(2)
    };

    private TimeSpan GetSelectedPollInterval() => GetSelectedPollInterval(PollIntervalSelector.SelectedIndex);

    private void PollIntervalSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PollIntervalSelector.SelectedIndex < 0) return;
        _pollTimer.Interval = GetSelectedPollInterval();
        if (_monitor is not null && !_pollTimer.IsEnabled) _pollTimer.Start();
        SavePreferences();
    }

    private void HideSettingsPanel()
    {
        SettingsDialogOverlay.Visibility = Visibility.Collapsed;
    }

    private void NavOverview_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        SetActiveNavigation(NavOverviewButton);
        MainScrollViewer.ScrollToTop();
    }

    private void NavCooling_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        SetActiveNavigation(NavCoolingButton);
        CoolerCard.BringIntoView();
    }

    private void NavHardware_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
        SetActiveNavigation(NavHardwareButton);
        ThermalComponentsCard.BringIntoView();
    }

    private void NavSettings_Click(object sender, RoutedEventArgs e)
    {
        SetActiveNavigation(NavSettingsButton);
        SettingsDialogOverlay.Visibility = Visibility.Visible;
        ThemeSelector.Focus();
    }

    private void CloseSettingsDialog_Click(object sender, RoutedEventArgs e)
    {
        HideSettingsPanel();
    }

    private static LinearGradientBrush CreateArgbBrush()
    {
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox
        };
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 42, 104), 0.0));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(255, 194, 54), 0.22));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(68, 255, 190), 0.48));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(45, 150, 255), 0.72));
        brush.GradientStops.Add(new GradientStop(Color.FromRgb(190, 74, 255), 1.0));
        return brush;
    }

    private static void StartRingAnimation(RotateTransform transform, double seconds)
    {
        // Replace the previous clock when switching models so each series applies its own timing.
        transform.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(seconds))
        {
            RepeatBehavior = RepeatBehavior.Forever
        }, HandoffBehavior.SnapshotAndReplace);
    }

    private static void StopRingAnimation(RotateTransform transform)
    {
        // Non-addressable 12 V RGB is shown as a static color blend, not a moving ARGB effect.
        transform.BeginAnimation(RotateTransform.AngleProperty, null, HandoffBehavior.SnapshotAndReplace);
        transform.Angle = 0;
    }

    private void CoolerModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized || CoolerModelSelector.SelectedIndex < 0) return;

        // The selected profile only changes the visual preview; it never controls hardware.
        var selectedItem = CoolerModelSelector.SelectedItem as ComboBoxItem;
        var tag = selectedItem?.Tag?.ToString() ?? string.Empty;
        var parts = tag.Split('|');
        var modelCode = parts.Length > 0 && !string.IsNullOrWhiteSpace(parts[0])
            ? parts[0]
            : selectedItem?.Content?.ToString() ?? "Rise Mode";
        var profile = CreateCoolerProfile(parts, modelCode);

        var displayName = selectedItem?.Content?.ToString();
        CoolerModelBadge.Text = string.IsNullOrWhiteSpace(displayName) ? modelCode : displayName;
        CoolerPreview.Apply(new CoolerBlockStyle(
            profile.Family,
            profile.IsBlack,
            profile.HasNumericDisplay,
            Lit: profile.LightingMode != CoolerLightingMode.None,
            Animated: profile.Family switch
            {
                "auraice" or "aurafrostpro" => true,
                "classic" => profile.LightingMode is CoolerLightingMode.Argb or CoolerLightingMode.Rainbow,
                "square" => profile.LightingMode == CoolerLightingMode.Rainbow,
                _ => false
            },
            RingSeconds: profile.RingSeconds,
            Palette: profile.Palette));
        CoolerModelBadge.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "AccentBrush");
        SavePreferences();
    }

    private void LanguageSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LanguageSelector.SelectedIndex < 0) return;
        ApplyLanguage(Localizer.CodeAt(LanguageSelector.SelectedIndex));
        SavePreferences();
    }

    private void ApplyLanguage(string language)
    {
        Localizer.SetLanguage(language);
        _language = Localizer.Current;
        TranslateTree(this);
        Title = "RM Monitor";
        if (_trayIcon is not null)
        {
            _trayIcon.Text = "RM Monitor";
            if (_trayIcon.ContextMenuStrip is ContextMenuStrip trayMenu && trayMenu.Items.Count >= 2)
            {
                trayMenu.Items[0].Text = T("Open RM Monitor");
                trayMenu.Items[1].Text = T("Exit");
            }
        }

        // Textos montados em código (status do display etc.) são refeitos já no novo idioma.
        if (_lastDisplayStatus is { } status) UpdateDisplayStatus(status);
    }

    // Guarda o texto original (em inglês) de cada elemento para poder trocar entre QUALQUER idioma,
    // não só entre inglês e um idioma. Se o código mudar o texto depois, o novo texto vira a origem.
    private sealed class TranslationState
    {
        public string? TextSource, TextOutput, ContentSource, ContentOutput, ToolTipSource, ToolTipOutput;
    }

    private static string Retranslate(string current, ref string? source, ref string? output)
    {
        var key = source is not null && current == output
            ? source
            : Localizer.GetEnglishKey(current) ?? current;
        var translated = Localizer.Get(key);
        source = key;
        output = translated;
        return translated;
    }

    private void TranslateTree(DependencyObject parent)
    {
        if (parent is TextBlock textBlock && !string.IsNullOrEmpty(textBlock.Text))
        {
            var state = _translationStates.GetOrCreateValue(textBlock);
            var translated = Retranslate(textBlock.Text, ref state.TextSource, ref state.TextOutput);
            if (translated != textBlock.Text) textBlock.Text = translated;
        }

        if (parent is ContentControl contentControl && contentControl.Content is string content && content.Length > 0)
        {
            var state = _translationStates.GetOrCreateValue(contentControl);
            var translated = Retranslate(content, ref state.ContentSource, ref state.ContentOutput);
            if (translated != content) contentControl.Content = translated;
        }

        if (parent is FrameworkElement element && element.ToolTip is string toolTip && toolTip.Length > 0)
        {
            var state = _translationStates.GetOrCreateValue(element);
            var translated = Retranslate(toolTip, ref state.ToolTipSource, ref state.ToolTipOutput);
            if (translated != toolTip) element.ToolTip = translated;
        }

        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>().ToArray())
            TranslateTree(child);
    }

    private string T(string english) => Localizer.Get(english);

    private void SetActiveNavigation(Button active)
    {
        foreach (var button in new[] { NavOverviewButton, NavCoolingButton, NavHardwareButton, NavSettingsButton })
        {
            button.Background = Brushes.Transparent;
            button.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
        }
        active.SetResourceReference(Control.BackgroundProperty, "PanelRaisedBrush");
        active.SetResourceReference(Control.ForegroundProperty, "TextPrimaryBrush");
    }

    private static string ShortTemp(double? value) => value.HasValue ? $"{value.Value:0}°" : "--°";
    private static string FullTemp(double? value) => value.HasValue ? $"{value.Value:0}°C" : "N/D";
    private static string FormatCapacity(double gib)
    {
        if (!double.IsFinite(gib) || gib < 0) return "N/D";
        return gib >= 1024d
            ? $"{gib / 1024d:0.0} TB"
            : $"{gib:0.0} GB";
    }

    private static string Percent(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? $"{Math.Clamp(value.Value, 0d, 100d):0}%" : "N/D";
    private static double Clamp(double? value) => value.HasValue ? Math.Clamp(value.Value, 0, 100) : 0;

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        UpdateWindowControlGlyphs();
    }

    private void UpdateWindowControlGlyphs()
    {
        MaximizeGlyph.Visibility = WindowState == WindowState.Maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = WindowState == WindowState.Maximized ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_allowExit) return;
        e.Cancel = true;

        if (!_closePromptAnswered)
        {
            var choice = MessageBox.Show(
                T("Would you like to exit RM Monitor or keep it running in the system tray?"),
                T("Close RM Monitor"),
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question,
                MessageBoxResult.Cancel);
            if (choice == MessageBoxResult.Cancel) return;

            _closePromptAnswered = true;
            CloseToTrayCheckBox.IsChecked = choice == MessageBoxResult.No;
            SavePreferences();
            if (choice == MessageBoxResult.No)
            {
                HideToTray();
                return;
            }

            _allowExit = true;
            e.Cancel = false;
            Close();
            return;
        }

        if (CloseToTrayCheckBox.IsChecked == true)
        {
            HideToTray();
            return;
        }

        _allowExit = true;
        e.Cancel = false;
        Close();
    }

    private void CloseToTrayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        SavePreferences();
    }

    internal static string BuildWindowsStartupCommand(string executable, string? assemblyPath)
    {
        if (string.IsNullOrWhiteSpace(executable))
            throw new ArgumentException("An executable path is required.", nameof(executable));

        // When launched through the .NET host, Windows must run dotnet.exe with
        // the app assembly before passing the application-specific --tray argument.
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(assemblyPath))
                throw new ArgumentException("An assembly path is required when launching through dotnet.", nameof(assemblyPath));

            return $"\"{executable}\" \"{assemblyPath}\" --tray";
        }

        return $"\"{executable}\" --tray";
    }

    private const string StartupTaskName = "RM Monitor";

    private static string QuoteWindowsCommandLineArgument(string argument)
    {
        var builder = new System.Text.StringBuilder("\"");
        var backslashes = 0;

        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }

            if (character == '\"')
            {
                builder.Append('\\', backslashes * 2 + 1);
                builder.Append('\"');
                backslashes = 0;
                continue;
            }

            builder.Append('\\', backslashes);
            backslashes = 0;
            builder.Append(character);
        }

        builder.Append('\\', backslashes * 2);
        builder.Append('\"');
        return builder.ToString();
    }

    private static bool ScheduledTaskExists()
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = $"/Query /TN {QuoteWindowsCommandLineArgument(StartupTaskName)}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };

        if (!process.Start())
            throw new InvalidOperationException("Could not query the Windows Task Scheduler.");
        process.WaitForExit();
        return process.ExitCode == 0;
    }

    private static void RunElevatedScheduledTaskCommand(bool enable)
    {
        // Disabling startup is idempotent: if the task was already removed,
        // continue to clean up legacy Run entries without requesting UAC.
        if (!enable && !ScheduledTaskExists())
            return;

        var executable = Environment.ProcessPath;
        if (enable && string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("Could not determine the current executable path.");

        var arguments = new List<string>
        {
            enable ? "/Create" : "/Delete",
            "/TN",
            StartupTaskName
        };

        if (enable)
        {
            arguments.Add("/SC");
            arguments.Add("ONLOGON");
            arguments.Add("/RL");
            arguments.Add("HIGHEST");
            arguments.Add("/TR");
            arguments.Add(BuildWindowsStartupCommand(executable!, typeof(App).Assembly.Location));
        }

        arguments.Add("/F");

        // ShellExecute (required for the UAC "runas" verb) uses Arguments rather
        // than ArgumentList. Quote each argument and escape embedded quotes so
        // paths containing spaces survive both schtasks.exe and its /TR command.
        var commandLine = string.Join(" ", arguments.Select(QuoteWindowsCommandLineArgument));
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                Arguments = commandLine,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }
        };
        if (!process.Start())
            throw new InvalidOperationException("Could not start the Windows Task Scheduler command.");

        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"Windows Task Scheduler failed with exit code {process.ExitCode}.");
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPreferences) return;
        try
        {
            RunElevatedScheduledTaskCommand(StartWithWindowsCheckBox.IsChecked == true);

            // Remove older per-user startup entries so the app cannot start twice.
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key is not null)
            {
                key.DeleteValue("RM Monitor", throwOnMissingValue: false);
                key.DeleteValue("HWMetrics", throwOnMissingValue: false);
                key.DeleteValue("WaterCoolerApp", throwOnMissingValue: false);
                key.DeleteValue("RMMonitor", throwOnMissingValue: false);
                key.DeleteValue("AquaCore", throwOnMissingValue: false);
            }
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // The user cancelled the UAC prompt; keep the saved preference unchanged.
            _loadingPreferences = true;
            StartWithWindowsCheckBox.IsChecked = !StartWithWindowsCheckBox.IsChecked;
            _loadingPreferences = false;
            return;
        }
        catch (Exception ex)
        {
            _loadingPreferences = true;
            StartWithWindowsCheckBox.IsChecked = !StartWithWindowsCheckBox.IsChecked;
            _loadingPreferences = false;
            MessageBox.Show(
                T("Could not update Windows startup settings.") + Environment.NewLine + ex.Message,
                T("Startup setting failed"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        SavePreferences();
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SettingsDialogOverlay.Visibility == Visibility.Visible)
        {
            HideSettingsPanel();
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { }
        }
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _pollTimer.Stop();
        _storageCycleTimer.Stop();

        try
        {
            _historyWriter.Enqueue(_storedHistory, DateTime.Now, _historyRetention);
            await _historyWriter.DisposeAsync();
        }
        finally
        {
            // Isolate cleanup failures so one provider cannot prevent releasing later resources.
            try { _monitor?.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] Hardware cleanup failed: {ex}"); }

            try { _displayTransport.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] HID cleanup failed: {ex}"); }

            if (_trayIcon is not null)
            {
                try
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[MainWindow] Tray icon cleanup failed: {ex}");
                }
                finally
                {
                    _trayIcon = null;
                }
            }

            try { _trayApplicationIcon?.Dispose(); }
            catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[MainWindow] Application icon cleanup failed: {ex}"); }
            finally { _trayApplicationIcon = null; }
        }
    }
}
