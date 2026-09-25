using System.IO.Ports;
using NModbus.IO;

namespace VisionInspection.Core.Services;

/// <summary>
/// 将 System.IO.Ports.SerialPort 适配为 NModbus 的 IStreamResource，
/// 供 ModbusFactory.CreateRtuMaster 创建 Modbus RTU 主站使用。
/// （NModbus 3.x 程序集不再自带 SerialPortAdapter，需自行实现此薄适配层。）
/// </summary>
public class SerialPortAdapter : IStreamResource
{
    private readonly SerialPort _serialPort;

    public SerialPortAdapter(SerialPort serialPort)
    {
        _serialPort = serialPort ?? throw new ArgumentNullException(nameof(serialPort));
    }

    public int InfiniteTimeout => SerialPort.InfiniteTimeout;

    public int ReadTimeout
    {
        get => _serialPort.ReadTimeout;
        set => _serialPort.ReadTimeout = value;
    }

    public int WriteTimeout
    {
        get => _serialPort.WriteTimeout;
        set => _serialPort.WriteTimeout = value;
    }

    public void DiscardInBuffer() => _serialPort.DiscardInBuffer();

    public int Read(byte[] buffer, int offset, int count) => _serialPort.Read(buffer, offset, count);

    public void Write(byte[] buffer, int offset, int count) => _serialPort.Write(buffer, offset, count);

    public void Dispose()
    {
        // 串口的生命周期由 ModbusService 统一管理（DisconnectInternal 中关闭），此处不释放。
    }
}
