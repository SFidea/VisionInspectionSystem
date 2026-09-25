namespace VisionInspection.Core.Services;

/// <summary>
/// 通信设置总配置（当前含 Modbus）。持久化到 configs/communication_config.json。
/// </summary>
public class CommunicationConfig
{
    public ModbusConnectionConfig Modbus { get; set; } = new ModbusConnectionConfig();
}
