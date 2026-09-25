using System.Text;
using System.Windows;
using System.Windows.Threading;
using VisionInspection.UI.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// 独立运行日志窗口。
///
/// 设计要点：
///  - 常开可用：可同时开一个实例（<see cref="ShowWindow"/> 已做去重与激活）；
///  - 不丢日志：打开时回填 <see cref="SopRunLog"/> 的历史快照；
///  - 批量刷新：写入方可能来自推理/状态机线程且短时间成批到达，
///    用 120ms 节流合并成一次 <c>AppendText</c>，避免 UI 线程被逐行刷爆。
/// </summary>
public partial class SopLogWindow : Window
{
    private static SopLogWindow? _current;

    private readonly Queue<string> _pending = new();
    private readonly object _pendingLock = new();
    private readonly DispatcherTimer _flushTimer;
    private int _appendedSinceTrim;

    public SopLogWindow()
    {
        InitializeComponent();

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();

        SopRunLog.Instance.LineAppended += OnLineAppended;
        SopRunLog.Instance.Cleared += OnLogCleared;

        Loaded += (_, _) =>
        {
            BackfillFromSnapshot();
            LogTextBox.ScrollToEnd();
        };
        Closed += (_, _) =>
        {
            _flushTimer.Stop();
            SopRunLog.Instance.LineAppended -= OnLineAppended;
            SopRunLog.Instance.Cleared -= OnLogCleared;
        };
    }

    /// <summary>打开（或前置）唯一的日志窗口实例。</summary>
    public static void ShowWindow()
    {
        if (_current != null)
        {
            if (_current.WindowState == WindowState.Minimized)
                _current.WindowState = WindowState.Normal;
            _current.Activate();
            return;
        }

        var owner = Application.Current?.MainWindow;
        _current = new SopLogWindow();
        if (owner != null && !ReferenceEquals(owner, _current) && owner.IsVisible)
            _current.Owner = owner;   // 主窗口关闭时一并关闭

        _current.Closed += (_, _) => _current = null;
        _current.Show();
    }

    private void BackfillFromSnapshot()
    {
        var history = SopRunLog.Instance.Snapshot();
        if (history.Length == 0)
        {
            UpdateLineCount();
            return;
        }

        var sb = new StringBuilder();
        foreach (var line in history)
            sb.Append(line).Append('\n');
        LogTextBox.AppendText(sb.ToString());
        _appendedSinceTrim = history.Length;
        UpdateLineCount();
    }

    private void OnLineAppended(object? sender, string line)
    {
        lock (_pendingLock) _pending.Enqueue(line);
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            lock (_pendingLock) _pending.Clear();
            LogTextBox.Clear();
            _appendedSinceTrim = 0;
            UpdateLineCount();
        });
    }

    private void FlushPending()
    {
        string[] batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0) return;
            batch = _pending.ToArray();
            _pending.Clear();
        }

        var sb = new StringBuilder();
        foreach (var line in batch)
            sb.Append(line).Append('\n');

        bool atEnd = LogTextBox.VerticalOffset >= LogTextBox.ExtentHeight - LogTextBox.ViewportHeight - 2
                     || LogTextBox.ExtentHeight <= LogTextBox.ViewportHeight;

        LogTextBox.AppendText(sb.ToString());
        _appendedSinceTrim += batch.Length;

        // 长时间常开时防止 TextBox 无限膨胀：超过阈值就用服务端（有上限的）快照重建
        if (_appendedSinceTrim > 6000)
        {
            LogTextBox.Clear();
            BackfillFromSnapshot();
        }

        if (AutoScrollCheckBox.IsChecked == true || atEnd)
            LogTextBox.ScrollToEnd();

        UpdateLineCount();
    }

    private void UpdateLineCount() => LineCountText.Text = $"{SopRunLog.Instance.Count} 行";

    private void CopyAllButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (string.IsNullOrEmpty(LogTextBox.Text))
            {
                MessageBox.Show("当前没有日志内容。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Clipboard.SetText(LogTextBox.Text);
            MessageBox.Show("已复制全部日志到剪贴板。", "复制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"复制失败: {ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        SopRunLog.Instance.Clear();   // 会经 Cleared 事件清空本窗口
        UpdateLineCount();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
