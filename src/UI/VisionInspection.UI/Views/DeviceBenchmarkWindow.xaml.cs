using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using SkiaSharp;
using VisionInspection.UI.Models;
using VisionInspection.UI.Services;
using VisionInspection.UI.ViewModels;

namespace VisionInspection.UI.Views
{
    /// <summary>
    /// 设备性能测试窗口：测量 MediaPipe 手部两阶段模型与 YOLO 检测模型在 CPU / GPU 上的推理帧率，
    /// 用于验证部署机是否满足性能要求。
    /// </summary>
    public partial class DeviceBenchmarkWindow : Window
    {
        private readonly ObservableCollection<BenchmarkResultItem> _results = new();
        private readonly DeviceBenchmarkService _service = new();
        private CancellationTokenSource? _cts;
        private BenchmarkReport? _lastReport;
        private MainViewModel? _mainVm;

        public DeviceBenchmarkWindow()
        {
            InitializeComponent();

            ResultGrid.ItemsSource = _results;
            InitScenarioCombo();
            InitResolutionCombo();
            LoadYoloModels();

            _mainVm = Application.Current?.MainWindow?.DataContext as MainViewModel;

            _ = LoadEnvironmentAsync();
        }

        // ==================== 初始化 ====================

        private void InitScenarioCombo()
        {
            ScenarioCombo.Items.Add(new ComboBoxItem { Content = BenchmarkScenario.Realtime.GetLabel(), Tag = BenchmarkScenario.Realtime });
            ScenarioCombo.Items.Add(new ComboBoxItem { Content = BenchmarkScenario.Sop.GetLabel(), Tag = BenchmarkScenario.Sop });
            ScenarioCombo.Items.Add(new ComboBoxItem { Content = BenchmarkScenario.Offline.GetLabel(), Tag = BenchmarkScenario.Offline });
            ScenarioCombo.SelectedIndex = 0;
        }

        private void InitResolutionCombo()
        {
            ResolutionCombo.Items.Add(new ComboBoxItem { Content = "640 × 480", Tag = "640x480" });
            ResolutionCombo.Items.Add(new ComboBoxItem { Content = "1280 × 720", Tag = "1280x720" });
            ResolutionCombo.Items.Add(new ComboBoxItem { Content = "1920 × 1080", Tag = "1920x1080" });
            ResolutionCombo.SelectedIndex = 0;
        }

        private void LoadYoloModels()
        {
            YoloModelList.Items.Clear();
            var models = DeviceBenchmarkService.ScanYoloModels();

            if (models.Count == 0)
            {
                YoloModelList.Items.Add(new ListBoxItem
                {
                    Content = "（未扫描到 yolo_models/*.onnx）",
                    IsEnabled = false
                });
                return;
            }

            foreach (var path in models)
            {
                YoloModelList.Items.Add(new ListBoxItem
                {
                    Content = Path.GetFileName(path),
                    Tag = path
                });
            }

            // 默认选中一个常用模型，避免一上来就跑十几个模型耗时过长
            var preferred = models.FirstOrDefault(p =>
                Path.GetFileName(p).Equals("yolov8s.onnx", StringComparison.OrdinalIgnoreCase))
                ?? models.FirstOrDefault(p => Path.GetFileName(p).Contains("yolov8", StringComparison.OrdinalIgnoreCase))
                ?? models.First();
            if (preferred != null)
            {
                var item = YoloModelList.Items.OfType<ListBoxItem>()
                    .FirstOrDefault(i => (i.Tag as string) == preferred);
                if (item != null) item.IsSelected = true;
            }
        }

        private async Task LoadEnvironmentAsync()
        {
            try
            {
                var env = await Task.Run(() => DeviceBenchmarkService.GetEnvironment());

                EnvOsText.Text = env.OS;
                EnvCpuText.Text = string.IsNullOrWhiteSpace(env.Cpu)
                    ? $"{env.CpuCores} 核"
                    : $"{env.Cpu}（{env.CpuCores} 核）";
                EnvMemText.Text = env.TotalMemoryMB > 0 ? $"{env.TotalMemoryMB} MB" : "-";
                EnvOrtText.Text = string.IsNullOrWhiteSpace(env.OnnxRuntimeVersion) ? "-" : env.OnnxRuntimeVersion;

                EnvCudaText.Text = env.CudaAvailable ? "可用" : "不可用";
                EnvCudaText.Foreground = env.CudaAvailable
                    ? new SolidColorBrush(Color.FromRgb(45, 138, 78))
                    : new SolidColorBrush(Color.FromRgb(190, 60, 60));
                EnvGpuText.Text = !string.IsNullOrWhiteSpace(env.GpuName)
                    ? env.GpuName
                    : (env.CudaAvailable ? "NVIDIA GPU（型号未获取）" : "-");

                if (!env.CudaAvailable && GpuOnlyRadio.IsChecked == true)
                {
                    BothDeviceRadio.IsChecked = true;
                    ProgressText.Text = "CUDA 不可用，已自动切换为 CPU 测试";
                }
            }
            catch (Exception ex)
            {
                EnvOsText.Text = "环境信息采集失败";
                ProgressText.Text = $"环境信息采集失败: {ex.Message}";
            }
        }

        // ==================== 测试执行 ====================

        private BenchmarkOptions BuildOptions()
        {
            var opt = new BenchmarkOptions
            {
                TestMediaPipe = MediaPipeCheckBox.IsChecked == true,
                RunCpu = BothDeviceRadio.IsChecked == true || CpuOnlyRadio.IsChecked == true,
                RunGpu = BothDeviceRadio.IsChecked == true || GpuOnlyRadio.IsChecked == true,
                UseRealFrame = RealFrameRadio.IsChecked == true,
                Scenario = (ScenarioCombo.SelectedItem as ComboBoxItem)?.Tag is BenchmarkScenario s
                    ? s
                    : BenchmarkScenario.Realtime
            };

            opt.YoloModelPaths = YoloModelList.Items
                .OfType<ListBoxItem>()
                .Where(i => i.IsSelected && i.Tag is string)
                .Select(i => (string)i.Tag!)
                .ToList();

            if (int.TryParse(WarmupTextBox.Text, out var warmup)) opt.WarmupFrames = Math.Clamp(warmup, 0, 200);
            if (int.TryParse(FramesTextBox.Text, out var frames)) opt.TestFrames = Math.Clamp(frames, 1, 2000);

            if (ResolutionCombo.SelectedItem is ComboBoxItem { Tag: string res })
            {
                var parts = res.Split('x');
                if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h))
                {
                    opt.Width = w;
                    opt.Height = h;
                }
            }

            return opt;
        }

        private SKBitmap? ProvideRealFrame()
        {
            try
            {
                var vm = _mainVm;
                if (vm == null) return null;
                return vm.CurrentImage ?? vm.RoiEditorViewModel?.CurrentImage;
            }
            catch
            {
                return null;
            }
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            var opt = BuildOptions();

            if (!opt.TestMediaPipe && opt.YoloModelPaths.Count == 0)
            {
                MessageBox.Show("请至少选择一个被测对象（MediaPipe 手部或 YOLO 模型）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!opt.RunCpu && !opt.RunGpu)
            {
                MessageBox.Show("请至少选择一种测试设备（CPU 或 GPU）。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (opt.UseRealFrame && ProvideRealFrame() == null)
            {
                var r = MessageBox.Show(
                    "当前没有可用的真实帧（相机未运行且未加载图像）。\n是否改用合成测试帧继续？",
                    "真实帧不可用", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (r != MessageBoxResult.Yes) return;
                opt.UseRealFrame = false;
            }

            _results.Clear();
            _lastReport = null;
            ExportButton.IsEnabled = false;
            SummaryText.Text = "";
            SetRunning(true);

            _cts = new CancellationTokenSource();

            try
            {
                var progress = new Progress<BenchmarkProgress>(p =>
                {
                    ProgressText.Text = p.Message;
                    if (p.Percent >= 0) ProgressBar.Value = p.Percent;
                });

                var report = await _service.RunAsync(opt, ProvideRealFrame, progress, _cts.Token);

                _lastReport = report;
                foreach (var item in report.Items) _results.Add(item);

                ExportButton.IsEnabled = _results.Count > 0;
                UpdateSummary(report);
                ProgressText.Text = "测试完成";
                ProgressBar.Value = 100;
            }
            catch (OperationCanceledException)
            {
                ProgressText.Text = "测试已取消";
            }
            catch (Exception ex)
            {
                ProgressText.Text = $"测试失败: {ex.Message}";
                MessageBox.Show($"性能测试失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                SetRunning(false);
                _cts?.Dispose();
                _cts = null;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            _cts?.Cancel();
            CancelButton.IsEnabled = false;
        }

        private void SetRunning(bool running)
        {
            StartButton.IsEnabled = !running;
            CancelButton.IsEnabled = running;
            MediaPipeCheckBox.IsEnabled = !running;
            YoloModelList.IsEnabled = !running;
            BothDeviceRadio.IsEnabled = !running;
            CpuOnlyRadio.IsEnabled = !running;
            GpuOnlyRadio.IsEnabled = !running;
            ScenarioCombo.IsEnabled = !running;
            SyntheticRadio.IsEnabled = !running;
            RealFrameRadio.IsEnabled = !running;
            ResolutionCombo.IsEnabled = !running;
            WarmupTextBox.IsEnabled = !running;
            FramesTextBox.IsEnabled = !running;
        }

        private void UpdateSummary(BenchmarkReport report)
        {
            int passed = report.Items.Count(i => i.Passed);
            int failed = report.Items.Count(i => i.Error == null && !i.Passed);
            int errored = report.Items.Count(i => i.Error != null);

            var sb = new StringBuilder();
            sb.Append($"达标 {passed} · 不达标 {failed} · 失败 {errored}");

            if (report.Speedups.Count > 0)
            {
                sb.Append("   |  加速比（GPU/CPU）：");
                sb.Append(string.Join("，", report.Speedups.Select(kv => $"{kv.Key} {kv.Value:F2}×")));
            }

            SummaryText.Text = sb.ToString();
        }

        // ==================== 导出 / 选择 ====================

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in YoloModelList.Items.OfType<ListBoxItem>())
            {
                if (item.IsEnabled) item.IsSelected = true;
            }
        }

        private void ClearSelectionButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in YoloModelList.Items.OfType<ListBoxItem>())
            {
                item.IsSelected = false;
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            if (_lastReport == null || _lastReport.Items.Count == 0)
            {
                MessageBox.Show("没有可导出的测试结果。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dlg = new SaveFileDialog
            {
                Title = "导出性能测试结果",
                Filter = "CSV 文件 (*.csv)|*.csv",
                FileName = $"benchmark_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
            };

            if (dlg.ShowDialog() != true) return;

            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("测试时间,被测对象,类别,请求设备,实际设备,回退CPU,平均FPS,平均延迟ms,P50ms,P95ms,最小ms,最大ms,palm_ms,landmark_ms,目标FPS,判定,错误信息");

                foreach (var it in _lastReport.Items)
                {
                    sb.AppendLine(string.Join(",", new[]
                    {
                        _lastReport.Timestamp.ToString("yyyy-MM-dd HH:mm:ss"),
                        Csv(it.Name), Csv(it.Category), Csv(it.RequestedDevice), Csv(it.ActualDevice),
                        it.FellBackToCpu ? "是" : "否",
                        it.AvgFps.ToString("F2", CultureInfo.InvariantCulture),
                        it.AvgMs.ToString("F2", CultureInfo.InvariantCulture),
                        it.P50Ms.ToString("F2", CultureInfo.InvariantCulture),
                        it.P95Ms.ToString("F2", CultureInfo.InvariantCulture),
                        it.MinMs.ToString("F2", CultureInfo.InvariantCulture),
                        it.MaxMs.ToString("F2", CultureInfo.InvariantCulture),
                        it.PalmMs.ToString("F2", CultureInfo.InvariantCulture),
                        it.LandmarkMs.ToString("F2", CultureInfo.InvariantCulture),
                        it.TargetFps.ToString(CultureInfo.InvariantCulture),
                        Csv(it.StatusText), Csv(it.Error ?? "")
                    }));
                }

                // 环境信息附在末尾，便于归档时追溯部署机配置
                sb.AppendLine();
                sb.AppendLine($"# 环境: OS={Csv(_lastReport.Environment.OS)}, CPU={Csv(_lastReport.Environment.Cpu)}, Cores={_lastReport.Environment.CpuCores}, MemMB={_lastReport.Environment.TotalMemoryMB}, ORT={Csv(_lastReport.Environment.OnnxRuntimeVersion)}, CUDA={(_lastReport.Environment.CudaAvailable ? "yes" : "no")}, GPU={Csv(_lastReport.Environment.GpuName)}");
                sb.AppendLine($"# 参数: 场景={_lastReport.Options.Scenario.GetLabel()}, 预热帧={_lastReport.Options.WarmupFrames}, 测试帧={_lastReport.Options.TestFrames}, 输入源={(_lastReport.Options.UseRealFrame ? "真实帧" : "合成帧")}, 分辨率={_lastReport.Options.Width}x{_lastReport.Options.Height}");

                File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
                MessageBox.Show($"已导出：{dlg.FileName}", "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private static string Csv(string value)
        {
            if (value == null) return "";
            return value.Contains(',') || value.Contains('"') || value.Contains('\n')
                ? $"\"{value.Replace("\"", "\"\"")}\""
                : value;
        }
    }
}
