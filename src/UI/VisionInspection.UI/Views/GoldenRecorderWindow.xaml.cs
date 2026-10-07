using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using VisionInspection.Modules.SOP;
using VisionInspection.Modules.SOP.Models;
using VisionInspection.Modules.SOP.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// 黄金数据集录制窗口（P0 / RK3588 移植对拍基线采集）。
///
/// 职责：配置案例 → 启停录制器 → 显示实时统计。
/// 铁律：本窗口【不参与任何检测逻辑】，仅调用 SOPModule 的 Start/StopGoldenRecording。
/// </summary>
public partial class GoldenRecorderWindow : Window
{
    private readonly SOPModule? _sopModule;
    private DispatcherTimer? _statusTimer;
    private DateTime _startedAt;

    public GoldenRecorderWindow(SOPModule? sopModule)
    {
        InitializeComponent();
        _sopModule = sopModule;

        var cfg = GoldenRecorderConfigManager.Load();
        CaseNameBox.Text = cfg.CaseName;
        OutputRootBox.Text = string.IsNullOrWhiteSpace(cfg.OutputRoot) ? "golden" : cfg.OutputRoot;
        SaveFramesCheck.IsChecked = cfg.SaveFrames;
        JpegQualityBox.Text = cfg.JpegQuality.ToString();
        FrameStrideBox.Text = cfg.FrameStride.ToString();
        MaxFramesBox.Text = cfg.MaxFrames.ToString();

        Log("就绪。填写案例名称后点「开始录制」，SOP 检测照常运行。");
        if (_sopModule == null)
            Log("⚠️ 未找到 SOP 模块，无法录制。请先启动 SOP 检测。");
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_sopModule == null)
        {
            MessageBox.Show(this, "未找到 SOP 模块，无法开始录制。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var name = CaseNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name))
        {
            MessageBox.Show(this, "请填写案例名称。\n\n命名建议：配方名_场景，如 cup_usage_normal / cup_usage_skip。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var cfg = new GoldenRecorderConfig
        {
            Enabled = true,
            CaseName = name,
            CaseNote = CaseNoteBox.Text.Trim(),
            OutputRoot = OutputRootBox.Text.Trim(),
            SaveFrames = SaveFramesCheck.IsChecked == true,
            JpegQuality = ParseInt(JpegQualityBox.Text, 90, 50, 100),
            FrameStride = ParseInt(FrameStrideBox.Text, 1, 1, 100),
            MaxFrames = ParseInt(MaxFramesBox.Text, 2000, 0, 100000),
            QueueCapacity = 64
        };

        if (!_sopModule.StartGoldenRecording(cfg))
        {
            if (_sopModule.IsGoldenRecording)
            {
                MessageBox.Show(this,
                    "录制已在进行中。请先点「结束录制」再开始新案例。\n\n" +
                    $"当前正在录制：{_sopModule.GoldenStatus}",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                Log("⚠️ 已在录制中，未重复启动。请先结束当前录制。");
            }
            else
            {
                MessageBox.Show(this, "启动录制失败，详情见窗口日志。", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                Log("✗ 启动失败（案例名非法或目录不可写）");
            }
            return;
        }

        _startedAt = DateTime.Now;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = true;
        SetRecordingUi(true);
        Log($"▶ 开始录制：{name}");
        Log($"  输出目录：{ResolveOutputPreview()}");

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _statusTimer.Tick += StatusTimer_Tick;
        _statusTimer.Start();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        _statusTimer?.Stop();
        _sopModule?.StopGoldenRecording();

        var elapsed = (DateTime.Now - _startedAt).TotalSeconds;
        StartButton.IsEnabled = true;
        StopButton.IsEnabled = false;
        SetRecordingUi(false);

        Log($"■ 已结束，耗时 {elapsed:F1}s");
        Log($"  数据集目录：{ResolveOutputPreview()}");
        Log("  下一步：换下一个场景继续录，全部录完后交由 P1 引擎离线对拍使用。");
    }

    private void StatusTimer_Tick(object? sender, EventArgs e)
    {
        if (_sopModule == null) return;
        var el = (DateTime.Now - _startedAt).TotalSeconds;
        StatusText.Text = $"录制中 {_sopModule.GoldenStatus}　|　已 {el:F0}s";
    }

    private void SetRecordingUi(bool recording)
    {
        StatusDot.Fill = new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                recording ? "#1D9E75" : "#9CA3AF"));
        if (!recording)
            StatusText.Text = "未录制";
        CaseNameBox.IsEnabled = !recording;
    }

    private string ResolveOutputPreview()
    {
        var root = OutputRootBox.Text.Trim();
        if (string.IsNullOrEmpty(root)) root = "golden";
        if (!Path.IsPathRooted(root))
            root = Path.Combine(AppContext.BaseDirectory, root);
        return Path.Combine(root, "cases", CaseNameBox.Text.Trim());
    }

    private static int ParseInt(string s, int def, int min, int max)
        => int.TryParse(s, out var v) ? Math.Clamp(v, min, max) : def;

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    protected override void OnClosed(EventArgs e)
    {
        _statusTimer?.Stop();
        base.OnClosed(e);

        // 关闭窗口不停止录制（用户要切回主界面实际操作动作），
        // 但必须明确告知——否则用户以为在录，实际已停录，会录出空目录。
        if (_sopModule?.IsGoldenRecording == true)
        {
            var result = MessageBox.Show(this,
                "录制仍在进行中。\n\n关闭窗口不会停止录制，但要结束录制请重新打开此窗口" +
                "（配置 → 🗂 黄金数据集录制）。\n\n现在要停止录制吗？",
                "录制进行中", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _sopModule.StopGoldenRecording();
            }
        }
    }
}
