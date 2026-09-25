namespace VisionInspection.UI.Services;

/// <summary>
/// SOP 运行日志的共享缓冲（单例）。
///
/// 背景：SOP 运行日志原先内嵌在右侧 SOP 面板底部，占掉了 SOP 流程本就不多的显示空间。
/// 现改为「独立运行日志窗口」显示，本类承担两件事：
///  1) 保留最近 <see cref="MaxLines"/> 行历史 —— 日志窗口后开也能回填，不会"窗口没开就丢日志"；
///  2) 提供 <see cref="LineAppended"/> 事件 —— 窗口打开时实时追加，无需轮询。
/// 写入方可来自任意线程，事件订阅方（窗口）自行 Dispatcher 切回 UI 线程。
/// </summary>
public sealed class SopRunLog
{
    /// <summary>历史保留上限（防止长时间运行无限增长）</summary>
    private const int MaxLines = 5000;

    private static readonly SopRunLog _instance = new();
    public static SopRunLog Instance => _instance;

    private readonly object _lock = new();
    private readonly Queue<string> _lines = new(MaxLines + 1);

    /// <summary>有新行追加，参数为完整行文本（含时间戳前缀）</summary>
    public event EventHandler<string>? LineAppended;

    /// <summary>日志被清空</summary>
    public event EventHandler? Cleared;

    private SopRunLog() { }

    /// <summary>追加一行日志</summary>
    public void Append(string line)
    {
        lock (_lock)
        {
            _lines.Enqueue(line);
            while (_lines.Count > MaxLines)
                _lines.Dequeue();
        }
        LineAppended?.Invoke(this, line);
    }

    /// <summary>取当前历史快照（用于窗口打开时回填）</summary>
    public string[] Snapshot()
    {
        lock (_lock) return _lines.ToArray();
    }

    /// <summary>当前历史行数</summary>
    public int Count
    {
        get { lock (_lock) return _lines.Count; }
    }

    /// <summary>清空历史（并通知已打开的窗口）</summary>
    public void Clear()
    {
        lock (_lock) _lines.Clear();
        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
