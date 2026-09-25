using System.Diagnostics;
using System.IO.Ports;
using System.Net.Sockets;
using NModbus;

namespace VisionInspection.Core.Services;

/// <summary>
/// Modbus 通信状态
/// </summary>
public enum ModbusConnectionStatus
{
    Disconnected,
    Connecting,
    Connected,
    Error
}

/// <summary>
/// Modbus 通信服务（单例）。
/// 封装 NModbus 主站，支持：
///  - TCP（Modbus TCP）与 RTU（串口）两种物理层（由 CommunicationConfigManager 配置）
///  - 程序启动默认连接 + 断线低频自动重连（单发定时器，不重叠、不忙等，避免无谓资源消耗）
///  - 首次连接失败仅提示一次；后续持续静默重试
///  - 功能码 03：轮询保持寄存器 0-7（映射 X1-X8）用于实时输入监控
///  - 功能码 06：写单个寄存器 0-7（映射 Y1-Y8）用于 SOP 违规触发
///  - X1（寄存器 0）上升沿作为 SOP 采集触发信号
/// </summary>
public sealed class ModbusService
{
    private static readonly ModbusService _instance = new();
    public static ModbusService Instance => _instance;

    /// <summary>TCP 建链超时（毫秒）</summary>
    private const int TcpConnectTimeoutMs = 1500;

    /// <summary>单次 03 读 / 06 写的 I/O 超时（毫秒）。
    /// ⚠️ 必须显式设置：NModbus 默认 ReadTimeout/WriteTimeout 为 Infinite，
    /// 断线时读取会长时间阻塞，表现为状态卡在 Connecting / 轮询线程堆积。</summary>
    private const int IoTimeoutMs = 1000;

    /// <summary>03 轮询周期（毫秒）</summary>
    private const int PollIntervalMs = 250;

    private readonly object _lock = new();
    private IModbusMaster? _master;
    private TcpClient? _tcpClient;
    private SerialPort? _serialPort;
    private Timer? _reconnectTimer;
    private Timer? _pollTimer;
    private int _connecting;   // 重入保护：同一时刻只允许一次建链尝试

    private ModbusConnectionStatus _status = ModbusConnectionStatus.Disconnected;
    private bool _firstFailurePrompted;
    private readonly HashSet<ushort> _latchedOutputs = new();
    private ushort[] _inputs = new ushort[8];
    private bool _x1High;

    public ModbusConnectionStatus Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            StatusChanged?.Invoke(this, value);
        }
    }

    public bool IsConnected => _status == ModbusConnectionStatus.Connected;

    /// <summary>X1-X8 最新输入值（功能码 03 读取的保持寄存器 0-7）</summary>
    public IReadOnlyList<ushort> Inputs => _inputs;

    /// <summary>当前生效的 Modbus 连接配置（来自 CommunicationConfigManager）</summary>
    public ModbusConnectionConfig Config => CommunicationConfigManager.Instance.LoadConfig().Modbus;

    public event EventHandler<ModbusConnectionStatus>? StatusChanged;
    /// <summary>整个会话内首次连接失败触发一次（UI 据此弹一次提示）</summary>
    public event EventHandler? FirstConnectionFailed;
    /// <summary>输入寄存器刷新（X1-X8）</summary>
    public event EventHandler<ushort[]>? InputsUpdated;
    /// <summary>X1（寄存器 0）上升沿触发（SOP 采集触发）</summary>
    public event EventHandler? X1Trigger;

    private ModbusService() { }

    /// <summary>
    /// 程序启动调用：若配置为自动连接，则立即尝试连接并启动低频重连。
    /// </summary>
    public void Start()
    {
        if (!Config.AutoConnect) return;
        ConnectWithRetry();
    }

    /// <summary>立即连接，并启动断线后的低频自动重连。</summary>
    public void ConnectWithRetry()
    {
        TryConnect();
        if (Status != ModbusConnectionStatus.Connected) EnsureReconnectTimer();
    }

    /// <summary>仅尝试一次连接（用于配置窗"测试连接"按钮，不启动自动重连）。</summary>
    public bool TestConnect()
    {
        TryConnect();
        return IsConnected;
    }

    /// <summary>
    /// 建立连接。成功返回 true。
    /// 建链期间不持有 <see cref="_lock"/>（避免阻塞 06 写入），仅在替换主站对象时短暂加锁。
    /// </summary>
    private bool TryConnect()
    {
        // 重入保护：上一次建链还没结束（可能正卡在 TCP 超时）时直接返回，避免线程堆积
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            return IsConnected;

        try
        {
            lock (_lock) { DisconnectInternal(); }
            Status = ModbusConnectionStatus.Connecting;

            var cfg = Config;
            IModbusMaster master;
            TcpClient? tcp = null;
            SerialPort? serial = null;

            if (cfg.IsTcp)
            {
                tcp = new TcpClient();
                var ar = tcp.BeginConnect(cfg.IpAddress, cfg.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TcpConnectTimeoutMs))
                {
                    try { tcp.Close(); } catch { }
                    throw new TimeoutException($"Modbus TCP 连接超时 {cfg.IpAddress}:{cfg.Port}");
                }
                tcp.EndConnect(ar);
                master = new ModbusFactory().CreateMaster(tcp);
            }
            else
            {
                var parity = Enum.TryParse<Parity>(cfg.Parity, true, out var p) ? p : Parity.None;
                var stopBits = cfg.StopBits switch
                {
                    1.5 => StopBits.OnePointFive,
                    2 => StopBits.Two,
                    _ => StopBits.One
                };
                serial = new SerialPort(cfg.ComPort, cfg.BaudRate, parity, cfg.DataBits, stopBits)
                {
                    ReadTimeout = IoTimeoutMs,
                    WriteTimeout = IoTimeoutMs
                };
                serial.Open();
                master = new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(serial));
            }

            // 显式设置 I/O 超时与重试：保证断线时读写能在 1s 内失败返回，而不是长时间阻塞
            try
            {
                master.Transport.ReadTimeout = IoTimeoutMs;
                master.Transport.WriteTimeout = IoTimeoutMs;
                master.Transport.Retries = 1;
                master.Transport.WaitToRetryMilliseconds = 200;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Modbus] 设置传输超时失败（忽略）: {ex.Message}");
            }

            lock (_lock)
            {
                _master = master;
                _tcpClient = tcp;
                _serialPort = serial;
            }

            Status = ModbusConnectionStatus.Connected;
            _firstFailurePrompted = false;
            StopReconnectTimer();
            StartPolling();
            return true;
        }
        catch (Exception ex)
        {
            Status = ModbusConnectionStatus.Error;
            Debug.WriteLine($"[Modbus] 连接失败: {ex.Message}");
            if (!_firstFailurePrompted)
            {
                _firstFailurePrompted = true;   // 整个会话只提示一次，后续静默重试
                FirstConnectionFailed?.Invoke(this, EventArgs.Empty);
            }
            StopPolling();
            return false;
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>主动断开连接（并停止重连/轮询）。</summary>
    public void Disconnect()
    {
        lock (_lock)
        {
            StopReconnectTimer();
            StopPolling();
            DisconnectInternal();
            Status = ModbusConnectionStatus.Disconnected;
        }
    }

    private void DisconnectInternal()
    {
        try { _master?.Dispose(); } catch { }
        _master = null;
        try { _tcpClient?.Close(); } catch { }
        _tcpClient = null;
        try { if (_serialPort?.IsOpen == true) _serialPort.Close(); } catch { }
        _serialPort = null;
    }

    // ===== 低频自动重连 =====
    // 单发定时器：每次尝试结束后再排下一次，因此不会有重叠尝试（也就不会堆线程池）。
    // 连接成功后 StopReconnectTimer() 停表，连接态下不产生任何定时回调。
    private void EnsureReconnectTimer()
    {
        var interval = Math.Max(500, Config.ReconnectIntervalMs);
        if (_reconnectTimer == null)
            _reconnectTimer = new Timer(_ => ReconnectTick(), null, interval, Timeout.Infinite);
        else
            _reconnectTimer.Change(interval, Timeout.Infinite);
    }

    private void StopReconnectTimer()
    {
        _reconnectTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void ReconnectTick()
    {
        try
        {
            if (Status == ModbusConnectionStatus.Connected) return;
            if (!Config.AutoConnect) return;
            TryConnect();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Modbus] 重连异常: {ex.Message}");
        }
        finally
        {
            // 仍未连上 → 排下一次；已连上 → 保持停表（不空转）
            if (Status != ModbusConnectionStatus.Connected && Config.AutoConnect)
                EnsureReconnectTimer();
        }
    }

    // ===== 功能码 03：输入轮询（仅连接时以 250ms 低频刷新 X1-X8） =====
    private void StartPolling()
    {
        if (_pollTimer == null)
            _pollTimer = new Timer(_ => PollTick(), null, PollIntervalMs, PollIntervalMs);
        else
            _pollTimer.Change(PollIntervalMs, PollIntervalMs);
    }

    private void StopPolling()
    {
        _pollTimer?.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void PollTick()
    {
        if (_status != ModbusConnectionStatus.Connected) return;
        IModbusMaster? master;
        lock (_lock) { master = _master; }
        if (master == null) return;

        try
        {
            var regs = master.ReadHoldingRegisters(Config.SlaveId, 0, 8);
            if (regs.Length != 8) return;
            _inputs = regs;
            InputsUpdated?.Invoke(this, regs);

            bool x1 = regs[0] != 0;
            if (x1 && !_x1High)
            {
                _x1High = true;
                X1Trigger?.Invoke(this, EventArgs.Empty);
            }
            else if (!x1)
            {
                _x1High = false;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Modbus] 轮询失败，断开并重连: {ex.Message}");
            Status = ModbusConnectionStatus.Disconnected;
            StopPolling();
            if (Config.AutoConnect) EnsureReconnectTimer();
        }
    }

    // ===== 功能码 06：写单个寄存器（Y1-Y8，地址 0-7） =====

    /// <summary>
    /// SOP 违规触发：写单个寄存器。非锁存模式按 pulseMs 脉宽后自动复位为 0；
    /// 锁存模式保持为 1 直到管理员调用 ReleaseLatch 复位。
    /// </summary>
    /// <param name="address">输出地址 0-7（对应 Y1-Y8）</param>
    /// <param name="pulseMs">非锁存模式的脉宽（毫秒，100-5000）</param>
    /// <param name="latch">是否锁存（锁存需管理员复位）</param>
    public void TriggerOutput(int address, int pulseMs, bool latch)
    {
        if (address < 0 || address > 7) return;
        if (!IsConnected) return;

        var addr = (ushort)address;
        if (latch)
        {
            lock (_latchedOutputs) _latchedOutputs.Add(addr);
            WriteRegister(addr, 1);
        }
        else
        {
            var ms = Math.Clamp(pulseMs, 100, 5000);
            // 后台脉冲，不阻塞调用线程（状态机）
            _ = Task.Run(async () =>
            {
                WriteRegister(addr, 1);
                await Task.Delay(ms);
                WriteRegister(addr, 0);
            });
        }
    }

    /// <summary>管理员复位某个锁存输出（写 0 并移出锁存集合）。</summary>
    public void ReleaseLatch(int address)
    {
        if (address < 0 || address > 7) return;
        var addr = (ushort)address;
        lock (_latchedOutputs) _latchedOutputs.Remove(addr);
        WriteRegister(addr, 0);
    }

    /// <summary>管理员复位全部锁存输出。</summary>
    public void ReleaseAllLatches()
    {
        List<ushort> addrs;
        lock (_latchedOutputs) addrs = _latchedOutputs.ToList();
        foreach (var a in addrs)
        {
            WriteRegister(a, 0);
        }
        lock (_latchedOutputs) _latchedOutputs.Clear();
    }

    /// <summary>直接写单个保持寄存器（功能码 06）。返回是否成功。</summary>
    public bool WriteRegister(ushort address, ushort value)
    {
        IModbusMaster? master;
        byte slaveId;
        lock (_lock)
        {
            if (_master == null || _status != ModbusConnectionStatus.Connected) return false;
            master = _master;
            slaveId = Config.SlaveId;
        }

        try
        {
            master.WriteSingleRegister(slaveId, address, value);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Modbus] 写寄存器失败 addr={address} val={value}: {ex.Message}");
            return false;
        }
    }

    /// <summary>读取保持寄存器 0-7（功能码 03），返回 X1-X8；未连接时返回 null。</summary>
    public ushort[]? ReadInputs()
    {
        IModbusMaster? master;
        byte slaveId;
        lock (_lock)
        {
            if (_master == null || _status != ModbusConnectionStatus.Connected) return null;
            master = _master;
            slaveId = Config.SlaveId;
        }

        try
        {
            var regs = master.ReadHoldingRegisters(slaveId, 0, 8);
            if (regs.Length == 8) _inputs = regs;
            return regs;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Modbus] 读寄存器失败: {ex.Message}");
            return null;
        }
    }
}
