namespace VisionInspection.Core.Services;

/// <summary>
/// Modbus 连接配置（TCP / RTU 二选一）。
/// 由 CommunicationConfigManager 持久化到 configs/communication_config.json。
/// </summary>
public class ModbusConnectionConfig
{
    /// <summary>连接模式：Tcp（Modbus TCP）或 Rtu（串口 RTU）</summary>
    public string Mode { get; set; } = "Tcp";

    // ===== TCP 模式参数 =====
    public string IpAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 502;

    // ===== RTU（串口）模式参数 =====
    public string ComPort { get; set; } = "COM1";
    public int BaudRate { get; set; } = 9600;
    public string Parity { get; set; } = "None";   // None / Odd / Even
    public int DataBits { get; set; } = 8;
    public double StopBits { get; set; } = 1;      // 1 / 1.5 / 2

    // ===== 通用参数 =====
    /// <summary>从站地址（功能码 03/06 的 unit id）</summary>
    public byte SlaveId { get; set; } = 1;

    /// <summary>程序启动后是否默认连接并自动重连</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>断线后重连尝试间隔（毫秒）。低频重试，避免无谓资源消耗。</summary>
    public int ReconnectIntervalMs { get; set; } = 3000;

    public bool IsTcp => string.Equals(Mode, "Tcp", StringComparison.OrdinalIgnoreCase);
}
