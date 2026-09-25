using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using SkiaSharp;
using VisionInspection.Core.Services;
using VisionInspection.UI.Models;
using VisionInspection.UI.Services;
using VisionInspection.Modules.Detection;

namespace VisionInspection.UI.Views;

/// <summary>
/// 数据采集窗口：录像（ffmpeg 管道）→ 抽帧（ffmpeg 粗抽 + OpenCV 精筛）→ 自动预标注（YOLO .txt）
/// </summary>
public partial class DataAcquisitionWindow : Window
{
    private readonly VideoRecorderService _recorder = new();
    private readonly FrameExtractorService _extractor = new();
    private readonly DispatcherTimer _recordTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private CancellationTokenSource? _cts;
    private string _lastVideoPath = "";
    private string _lastSessionDir = "";

    private DataAcquisitionConfig Config => DataAcquisitionConfigManager.Instance.Config;

    public DataAcquisitionWindow()
    {
        InitializeComponent();

        InitCombos();
        LoadConfigToUi();

        _recordTimer.Tick += (_, _) => UpdateRecordStatus();
        _recorder.RecordingStopped += OnRecordingStopped;

        txtSessionName.Text = NewSessionName();

        Closed += (_, _) =>
        {
            try
            {
                CameraManager.Instance.FrameGrabbed -= OnFrameGrabbed;
                ReadUiToConfig();
                DataAcquisitionConfigManager.Instance.Save();
                _recorder.Dispose();
            }
            catch { }
        };
    }

    // ==================== 初始化 ====================

    private sealed class OptionItem
    {
        public string Text { get; set; } = "";
        public string Value { get; set; } = "";
        public override string ToString() => Text;
    }

    private static void AddItem(ComboBox combo, string text, string value)
        => combo.Items.Add(new OptionItem { Text = text, Value = value });

    private static string GetValue(ComboBox combo)
        => (combo.SelectedItem as OptionItem)?.Value ?? "";

    private static void SelectByValue(ComboBox combo, string value)
    {
        foreach (var item in combo.Items)
        {
            if (item is OptionItem o && o.Value == value)
            {
                combo.SelectedItem = item;
                return;
            }
        }

        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private void InitCombos()
    {
        AddItem(cmbCodec, "H.264（推荐）", "libx264");
        AddItem(cmbCodec, "H.265（更小体积）", "libx265");
        AddItem(cmbCodec, "MJPEG（画质优先）", "mjpeg");
        cmbCodec.SelectedIndex = 0;

        foreach (var p in new[] { "ultrafast", "veryfast", "fast", "medium" }) AddItem(cmbPreset, p, p);
        cmbPreset.SelectedIndex = 1;

        AddItem(cmbSrcPixFmt, "RGB24（默认，相机帧原生）", "rgb24");
        AddItem(cmbSrcPixFmt, "BGR24（画面偏色时切换）", "bgr24");
        AddItem(cmbSrcPixFmt, "Gray8（黑白相机）", "gray");
        AddItem(cmbSrcPixFmt, "自动（按通道数推断）", "auto");
        cmbSrcPixFmt.SelectedIndex = 0;

        AddItem(cmbRecordFps, "跟随相机", "0");
        foreach (var f in new[] { "10", "15", "20", "25", "30" }) AddItem(cmbRecordFps, f + " fps", f);
        cmbRecordFps.SelectedIndex = 0;

        AddItem(cmbStrategy, "目标总数（均匀抽取）★推荐", "TargetCount");
        AddItem(cmbStrategy, "按帧间隔", "IntervalFrames");
        AddItem(cmbStrategy, "按时间间隔(ms)", "IntervalMs");
        AddItem(cmbStrategy, "目标帧率(fps)", "TargetFps");
        AddItem(cmbStrategy, "场景变化触发", "SceneChange");
        cmbStrategy.SelectedIndex = 0;

        AddItem(cmbDedupMethod, "pHash 感知哈希", "PHash");
        AddItem(cmbDedupMethod, "dHash 差值哈希", "DHash");
        AddItem(cmbDedupMethod, "灰度直方图", "Histogram");
        cmbDedupMethod.SelectedIndex = 0;

        AddItem(cmbDedupScope, "只比上一张保留帧（快）", "PreviousKept");
        AddItem(cmbDedupScope, "比全部保留帧（彻底）", "AllKept");
        cmbDedupScope.SelectedIndex = 0;

        AddItem(cmbFormat, "JPG", "jpg");
        AddItem(cmbFormat, "PNG", "png");
        AddItem(cmbFormat, "BMP", "bmp");
        cmbFormat.SelectedIndex = 0;

        AddItem(cmbResize, "不缩放", "None");
        AddItem(cmbResize, "限制长边", "LimitLongEdge");
        AddItem(cmbResize, "固定尺寸", "FixedSize");
        AddItem(cmbResize, "按百分比", "Percent");
        cmbResize.SelectedIndex = 1;

        CmbStrategy_SelectionChanged(this, EventArgs.Empty);
        CmbResize_SelectionChanged(this, EventArgs.Empty);
    }

    private static string NewSessionName() => "session_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");

    // ==================== 参数读写 ====================

    private void LoadConfigToUi()
    {
        var c = Config;

        txtOutputRoot.Text = string.IsNullOrWhiteSpace(c.Record.OutputRoot) ? "dataset" : c.Record.OutputRoot;
        SelectByValue(cmbCodec, c.Record.VideoCodec);
        sldCrf.Value = c.Record.Crf;
        txtCrf.Text = c.Record.Crf.ToString(CultureInfo.InvariantCulture);
        SelectByValue(cmbPreset, c.Record.Preset);
        SelectByValue(cmbSrcPixFmt, c.Record.SourcePixelFormat);
        SelectByValue(cmbRecordFps, ((int)c.Record.RecordFps).ToString(CultureInfo.InvariantCulture));
        txtMaxDuration.Text = c.Record.MaxDurationSec.ToString(CultureInfo.InvariantCulture);
        txtMaxSize.Text = c.Record.MaxFileSizeMB.ToString(CultureInfo.InvariantCulture);

        SelectByValue(cmbStrategy, c.Extract.Strategy.ToString());
        txtTargetCount.Text = c.Extract.TargetCount.ToString(CultureInfo.InvariantCulture);
        txtIntervalFrames.Text = c.Extract.IntervalFrames.ToString(CultureInfo.InvariantCulture);
        txtIntervalMs.Text = c.Extract.IntervalMs.ToString(CultureInfo.InvariantCulture);
        txtTargetFps.Text = c.Extract.TargetFps.ToString(CultureInfo.InvariantCulture);
        txtSceneThreshold.Text = c.Extract.SceneThreshold.ToString(CultureInfo.InvariantCulture);
        txtStartSec.Text = c.Extract.StartTimeSec.ToString(CultureInfo.InvariantCulture);
        txtEndSec.Text = c.Extract.EndTimeSec.ToString(CultureInfo.InvariantCulture);

        chkQuality.IsChecked = c.Extract.EnableQualityFilter;
        txtMinSharpness.Text = c.Extract.MinSharpness.ToString(CultureInfo.InvariantCulture);
        txtMinBrightness.Text = c.Extract.MinBrightness.ToString(CultureInfo.InvariantCulture);
        txtMaxBrightness.Text = c.Extract.MaxBrightness.ToString(CultureInfo.InvariantCulture);
        txtMinContrast.Text = c.Extract.MinContrast.ToString(CultureInfo.InvariantCulture);

        chkDedup.IsChecked = c.Extract.EnableDedup;
        SelectByValue(cmbDedupMethod, c.Extract.Method.ToString());
        txtHamming.Text = c.Extract.HammingThreshold.ToString(CultureInfo.InvariantCulture);
        txtHistTh.Text = c.Extract.HistogramThreshold.ToString(CultureInfo.InvariantCulture);
        SelectByValue(cmbDedupScope, c.Extract.Scope.ToString());
        chkCrossSession.IsChecked = c.Extract.CrossSessionDedup;

        SelectByValue(cmbFormat, c.Extract.OutputFormat);
        txtJpegQuality.Text = c.Extract.JpegQuality.ToString(CultureInfo.InvariantCulture);
        SelectByValue(cmbResize, c.Extract.Resize.ToString());
        chkGrayscale.IsChecked = c.Extract.Grayscale;
        ApplyResizeEditors();

        txtPattern.Text = c.Naming.FilePattern;
        txtStartIndex.Text = c.Naming.StartIndex.ToString(CultureInfo.InvariantCulture);
        txtMaxOutput.Text = c.Extract.MaxOutputCount.ToString(CultureInfo.InvariantCulture);

        chkAutoLabel.IsChecked = c.AutoLabel.EnableAutoLabel;
        txtModelPath.Text = c.AutoLabel.ModelPath;
        txtConf.Text = c.AutoLabel.ConfThreshold.ToString(CultureInfo.InvariantCulture);
        txtIou.Text = c.AutoLabel.IouThreshold.ToString(CultureInfo.InvariantCulture);
        chkKeepNegative.IsChecked = c.AutoLabel.KeepNegativeFrames;
    }

    private void ReadUiToConfig()
    {
        var c = Config;

        c.Record.OutputRoot = string.IsNullOrWhiteSpace(txtOutputRoot.Text) ? "dataset" : txtOutputRoot.Text.Trim();
        c.Record.VideoCodec = GetValue(cmbCodec);
        c.Record.Crf = (int)sldCrf.Value;
        c.Record.Preset = GetValue(cmbPreset);
        c.Record.SourcePixelFormat = GetValue(cmbSrcPixFmt);
        c.Record.RecordFps = Num(cmbRecordFps, 0);
        c.Record.MaxDurationSec = Num(txtMaxDuration, 0);
        c.Record.MaxFileSizeMB = (long)Num(txtMaxSize, 0);

        c.Extract.Strategy = Enum.TryParse<ExtractStrategy>(GetValue(cmbStrategy), out var st) ? st : ExtractStrategy.TargetCount;
        c.Extract.TargetCount = (int)Num(txtTargetCount, 200);
        c.Extract.IntervalFrames = (int)Num(txtIntervalFrames, 10);
        c.Extract.IntervalMs = Num(txtIntervalMs, 500);
        c.Extract.TargetFps = Num(txtTargetFps, 2);
        c.Extract.SceneThreshold = Num(txtSceneThreshold, 0.3);
        c.Extract.StartTimeSec = Num(txtStartSec, 0);
        c.Extract.EndTimeSec = Num(txtEndSec, 0);

        c.Extract.EnableQualityFilter = chkQuality.IsChecked == true;
        c.Extract.MinSharpness = Num(txtMinSharpness, 80);
        c.Extract.MinBrightness = Num(txtMinBrightness, 30);
        c.Extract.MaxBrightness = Num(txtMaxBrightness, 225);
        c.Extract.MinContrast = Num(txtMinContrast, 15);

        c.Extract.EnableDedup = chkDedup.IsChecked == true;
        c.Extract.Method = Enum.TryParse<DedupMethod>(GetValue(cmbDedupMethod), out var dm) ? dm : DedupMethod.PHash;
        c.Extract.HammingThreshold = (int)Num(txtHamming, 8);
        c.Extract.HistogramThreshold = Num(txtHistTh, 0.985);
        c.Extract.Scope = Enum.TryParse<DedupScope>(GetValue(cmbDedupScope), out var ds) ? ds : DedupScope.PreviousKept;
        c.Extract.CrossSessionDedup = chkCrossSession.IsChecked == true;

        c.Extract.OutputFormat = GetValue(cmbFormat);
        c.Extract.JpegQuality = (int)Num(txtJpegQuality, 95);
        c.Extract.Resize = Enum.TryParse<AutoResizeMode>(GetValue(cmbResize), out var rm) ? rm : AutoResizeMode.LimitLongEdge;
        c.Extract.Grayscale = chkGrayscale.IsChecked == true;

        switch (c.Extract.Resize)
        {
            case AutoResizeMode.LimitLongEdge:
                c.Extract.MaxLongEdge = (int)Num(txtResize1, 1920);
                break;
            case AutoResizeMode.FixedSize:
                c.Extract.FixedWidth = (int)Num(txtResize1, 1280);
                c.Extract.FixedHeight = (int)Num(txtResize2, 720);
                break;
            case AutoResizeMode.Percent:
                c.Extract.PercentScale = (int)Num(txtResize1, 50);
                break;
        }

        c.Naming.FilePattern = string.IsNullOrWhiteSpace(txtPattern.Text) ? "{session}_{index:D6}" : txtPattern.Text.Trim();
        c.Naming.StartIndex = (int)Num(txtStartIndex, 0);
        c.Extract.MaxOutputCount = (int)Num(txtMaxOutput, 2000);

        c.AutoLabel.EnableAutoLabel = chkAutoLabel.IsChecked == true;
        c.AutoLabel.ModelPath = txtModelPath.Text.Trim();
        c.AutoLabel.ConfThreshold = (float)Num(txtConf, 0.25);
        c.AutoLabel.IouThreshold = (float)Num(txtIou, 0.45);
        c.AutoLabel.KeepNegativeFrames = chkKeepNegative.IsChecked == true;
    }

    private static double Num(TextBox box, double fallback)
    {
        var s = box.Text?.Trim();
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    private static double Num(ComboBox combo, double fallback)
    {
        var s = GetValue(combo);
        return double.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var v) ? v : fallback;
    }

    // ==================== UI 联动 ====================

    private void CmbStrategy_SelectionChanged(object sender, EventArgs e)
    {
        if (panTargetCount == null) return;

        var v = cmbStrategy.SelectedItem is OptionItem o ? o.Value : "TargetCount";
        HideAll(panTargetCount, panIntervalFrames, panIntervalMs, panTargetFps, panScene);

        switch (v)
        {
            case "TargetCount": panTargetCount.Visibility = Visibility.Visible; break;
            case "IntervalFrames": panIntervalFrames.Visibility = Visibility.Visible; break;
            case "IntervalMs": panIntervalMs.Visibility = Visibility.Visible; break;
            case "TargetFps": panTargetFps.Visibility = Visibility.Visible; break;
            case "SceneChange": panScene.Visibility = Visibility.Visible; break;
        }
    }

    private void CmbResize_SelectionChanged(object sender, EventArgs e)
    {
        if (cmbResize == null || lblResize1 == null) return;
        ApplyResizeEditors();
    }

    private void ApplyResizeEditors()
    {
        var v = cmbResize.SelectedItem is OptionItem o ? o.Value : "LimitLongEdge";
        var c = Config.Extract;

        switch (v)
        {
            case "None":
                lblResize1.Text = ""; txtResize1.Text = "0";
                lblResize2.Text = ""; txtResize2.Text = "0";
                break;
            case "LimitLongEdge":
                lblResize1.Text = "长边上限"; txtResize1.Text = c.MaxLongEdge.ToString(CultureInfo.InvariantCulture);
                lblResize2.Text = ""; txtResize2.Text = "0";
                break;
            case "FixedSize":
                lblResize1.Text = "宽"; txtResize1.Text = c.FixedWidth.ToString(CultureInfo.InvariantCulture);
                lblResize2.Text = "高"; txtResize2.Text = c.FixedHeight.ToString(CultureInfo.InvariantCulture);
                break;
            case "Percent":
                lblResize1.Text = "百分比%"; txtResize1.Text = c.PercentScale.ToString(CultureInfo.InvariantCulture);
                lblResize2.Text = ""; txtResize2.Text = "0";
                break;
        }
    }

    private static void HideAll(params StackPanel[] panels)
    {
        foreach (var p in panels) p.Visibility = Visibility.Collapsed;
    }

    private void BtnBrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        // WPF 无内置目录对话框：借 OpenFileDialog 选任一文件后取其所在目录
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择输出根目录（选中其中任意文件即可）",
            CheckFileExists = false,
            ValidateNames = false,
            Filter = "任意文件|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            var dir = System.IO.Path.GetDirectoryName(dlg.FileName);
            if (!string.IsNullOrEmpty(dir)) txtOutputRoot.Text = dir;
        }
    }

    private void BtnBrowseVideo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "视频文件|*.mp4;*.avi;*.mkv;*.mov;*.flv;*.wmv|所有文件|*.*"
        };

        if (dlg.ShowDialog(this) == true) txtSourceVideo.Text = dlg.FileName;
    }

    private void BtnBrowseModel_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "模型文件|*.onnx;*.pt|所有文件|*.*"
        };

        var yoloDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "yolo_models");
        if (Directory.Exists(yoloDir)) dlg.InitialDirectory = yoloDir;

        if (dlg.ShowDialog(this) == true) txtModelPath.Text = dlg.FileName;
    }

    private void BtnUseLastVideo_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_lastVideoPath) || !File.Exists(_lastVideoPath))
        {
            MessageBox.Show(this, "还没有录制过视频", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        txtSourceVideo.Text = _lastVideoPath;
    }

    // ==================== 录像 ====================

    private void BtnRecordStart_Click(object sender, RoutedEventArgs e)
    {
        ReadUiToConfig();

        if (_recorder.IsRecording) return;

        var session = string.IsNullOrWhiteSpace(txtSessionName.Text) ? NewSessionName() : txtSessionName.Text.Trim();
        var root = ResolveRoot(txtOutputRoot.Text);
        var sessionDir = System.IO.Path.Combine(root, session);

        try
        {
            Directory.CreateDirectory(sessionDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "无法创建会话目录：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        var videoFile = System.IO.Path.Combine(sessionDir, "session.mp4");

        try
        {
            _recorder.Start(videoFile, Config.Record);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法开始录制", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _lastVideoPath = videoFile;
        _lastSessionDir = sessionDir;

        CameraManager.Instance.FrameGrabbed -= OnFrameGrabbed;
        CameraManager.Instance.FrameGrabbed += OnFrameGrabbed;

        btnRecordStart.IsEnabled = false;
        btnRecordStop.IsEnabled = true;
        txtRecordStatus.Text = "录制中...（等待相机帧）";
        SetStatus("录制中：" + videoFile);
        _recordTimer.Start();
    }

    private async void BtnRecordStop_Click(object sender, RoutedEventArgs e)
    {
        SetStatus("正在结束录制...");
        await _recorder.StopAsync("手动停止");
    }

    private void OnFrameGrabbed(object? sender, CameraFrameEventArgs? e)
    {
        if (e?.ImageData == null) return;
        _recorder.EnqueueFrame(e.ImageData);
    }

    private void UpdateRecordStatus()
    {
        if (!_recorder.IsRecording) return;

        var mb = _recorder.FileBytes / 1024.0 / 1024.0;
        txtRecordStatus.Text =
            $"录制中：{_recorder.Elapsed.TotalSeconds:F1}s | 写入 {_recorder.FramesWritten} 帧 | 丢帧 {_recorder.FramesDropped} | {mb:F1} MB";
    }

    private void OnRecordingStopped(string reason)
    {
        Dispatcher.Invoke(() =>
        {
            _recordTimer.Stop();
            btnRecordStart.IsEnabled = true;
            btnRecordStop.IsEnabled = false;
            try { CameraManager.Instance.FrameGrabbed -= OnFrameGrabbed; } catch { }

            var mb = _recorder.FileBytes / 1024.0 / 1024.0;
            txtRecordStatus.Text = $"已停止（{reason}）：{_recorder.FramesWritten} 帧, {mb:F1} MB";
            SetStatus("录制结束：" + _lastVideoPath);

            if (!string.IsNullOrEmpty(_lastVideoPath) && File.Exists(_lastVideoPath))
                txtSourceVideo.Text = _lastVideoPath;
        });
    }

    // ==================== 抽帧 ====================

    private async void BtnEstimate_Click(object sender, RoutedEventArgs e)
    {
        var video = txtSourceVideo.Text.Trim();
        if (!File.Exists(video))
        {
            MessageBox.Show(this, "请先选择源视频", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!FfmpegLocator.EnsureAvailable(out _, out _, out var err))
        {
            MessageBox.Show(this, err, "缺少 ffmpeg", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ReadUiToConfig();

        try
        {
            var n = await _extractor.EstimateAsync(video, Config.Extract);
            if (n < 0)
            {
                MessageBox.Show(this, "「场景变化」策略无法预估数量（取决于画面变化），直接执行即可。",
                    "预估", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var msg = $"当前策略预计粗抽出 {n} 张候选帧。";
            if (Config.Extract.MaxOutputCount > 0 && n > Config.Extract.MaxOutputCount)
                msg += $"\n超过上限 {Config.Extract.MaxOutputCount}，实际会被截断，建议调整参数。";

            MessageBox.Show(this, msg, "抽帧预估", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "预估失败：" + ex.Message, "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void BtnExtract_Click(object sender, RoutedEventArgs e)
    {
        var video = txtSourceVideo.Text.Trim();
        if (!File.Exists(video))
        {
            MessageBox.Show(this, "请先选择源视频", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!FfmpegLocator.EnsureAvailable(out _, out _, out var err))
        {
            MessageBox.Show(this, err, "缺少 ffmpeg", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        ReadUiToConfig();

        var sessionDir = ResolveSessionDir(video);
        _lastSessionDir = sessionDir;

        btnExtract.IsEnabled = false;
        btnCancel.IsEnabled = true;
        _cts = new CancellationTokenSource();

        var progress = new Progress<AcquireProgress>(p =>
        {
            SetStatus($"[{p.Phase}] {p.Message}");
            pbProgress.Value = Math.Clamp(p.Percent, 0, 100);
        });

        try
        {
            var summary = await _extractor.ExtractAsync(video, sessionDir, Config.Extract, Config.Naming, progress, _cts.Token);

            // 自动预标注
            if (Config.AutoLabel.EnableAutoLabel && summary.KeptCount > 0)
            {
                try
                {
                    var (labeled, negative, removed) = await RunAutoLabelAsync(summary, summary.ImageDir, progress);
                    summary.LabeledCount = labeled;
                    summary.NegativeCount = negative;
                    if (removed.Count > 0)
                        summary.Records.RemoveAll(r => removed.Contains(r.FileName));
                    summary.KeptCount = summary.Records.Count;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "自动预标注失败（抽帧结果已保留）：" + ex.Message, "预标注",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }

            ShowSummary(summary);
        }
        catch (OperationCanceledException)
        {
            SetStatus("已取消");
        }
        catch (Exception ex)
        {
            SetStatus("抽帧失败：" + ex.Message);
            MessageBox.Show(this, ex.Message, "抽帧失败", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            btnExtract.IsEnabled = true;
            btnCancel.IsEnabled = false;
            pbProgress.Value = 0;
            ReadUiToConfig();
            DataAcquisitionConfigManager.Instance.Save();
        }
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        SetStatus("正在取消...");
    }

    private async Task<(int, int, List<string>)> RunAutoLabelAsync(ExtractSummary summary, string imageDir,
        IProgress<AcquireProgress> progress)
    {
        var modelPath = Config.AutoLabel.ModelPath;
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
        {
            MessageBox.Show(this, "未选择有效的模型文件，已跳过自动预标注（图片已保留）。",
                "预标注", MessageBoxButton.OK, MessageBoxImage.Information);
            return (0, 0, new List<string>());
        }

        var detector = new YoloDetectionService();
        var info = new ModelInfo
        {
            Id = "autolabel_" + Guid.NewGuid().ToString("N")[..8],
            Name = System.IO.Path.GetFileNameWithoutExtension(modelPath),
            ModelPath = modelPath,
            Type = ModelType.ObjectDetection,
            UseGpu = false,
            ConfidenceThreshold = Config.AutoLabel.ConfThreshold,
            IouThreshold = Config.AutoLabel.IouThreshold
        };

        try
        {
            var ok = await detector.InitializeAsync(info);
            if (!ok) throw new InvalidOperationException("模型初始化失败：" + modelPath);

            detector.SetConfidenceThreshold(Config.AutoLabel.ConfThreshold);
            detector.SetIouThreshold(Config.AutoLabel.IouThreshold);

            var exporter = new AutoLabelExporter(detector);
            var labelDir = System.IO.Path.Combine(summary.SessionDir, "labels");

            return await exporter.ExportAsync(summary.Records, imageDir, labelDir, Config.AutoLabel, progress, _cts?.Token ?? CancellationToken.None);
        }
        finally
        {
            // YoloDetectionService 有 Dispose 方法但类型未实现 IDisposable，故手动释放
            try { detector.Dispose(); } catch { }
        }
    }

    private void ShowSummary(ExtractSummary s)
    {
        var lines = new List<string>
        {
            $"候选帧 {s.CandidateCount} → 保留 {s.KeptCount} 张",
            $"剔除：模糊 {s.RejectedBlur} / 亮度 {s.RejectedBrightness} / 低对比度 {s.RejectedContrast} / 重复 {s.RejectedDuplicate}",
            $"耗时 {s.ElapsedSec:F1} 秒"
        };

        if (Config.AutoLabel.EnableAutoLabel)
            lines.Add($"预标注：有目标 {s.LabeledCount} 张，负样本 {s.NegativeCount} 张");

        lines.Add("输出目录：" + s.SessionDir);

        txtSummary.Text = string.Join(Environment.NewLine, lines);
        listResults.ItemsSource = s.Records;
        SetStatus(s.Message);
    }

    private void BtnOpenFolder_Click(object sender, RoutedEventArgs e)
    {
        var dir = !string.IsNullOrEmpty(_lastSessionDir) && Directory.Exists(_lastSessionDir)
            ? _lastSessionDir
            : ResolveRoot(txtOutputRoot.Text);

        try
        {
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start("explorer.exe", dir);
        }
        catch { }
    }

    // ==================== 辅助 ====================

    private static string ResolveRoot(string configured)
    {
        var root = string.IsNullOrWhiteSpace(configured) ? "dataset" : configured.Trim();
        return System.IO.Path.IsPathRooted(root)
            ? root
            : System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, root);
    }

    private string ResolveSessionDir(string videoPath)
    {
        var parent = System.IO.Path.GetDirectoryName(videoPath);
        var name = !string.IsNullOrEmpty(parent) ? System.IO.Path.GetFileName(parent) : "";

        // 源视频已在一个会话目录里 → 复用
        if (!string.IsNullOrEmpty(name) && name.StartsWith("session_", StringComparison.OrdinalIgnoreCase))
            return parent!;

        var session = string.IsNullOrWhiteSpace(txtSessionName.Text) ? NewSessionName() : txtSessionName.Text.Trim();
        if (session.Equals("session", StringComparison.OrdinalIgnoreCase)) session = NewSessionName();

        var dir = System.IO.Path.Combine(ResolveRoot(txtOutputRoot.Text), session);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private void SetStatus(string text) => txtStatus.Text = text;
}
