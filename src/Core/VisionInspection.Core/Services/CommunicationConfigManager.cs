using System.Text.Json;

namespace VisionInspection.Core.Services;

/// <summary>
/// 通信设置配置管理器（Modbus 等） - 单例，持久化到 configs/communication_config.json。
/// 模式与 CameraConfigManager 一致：配置文件路径位于程序运行目录下的 configs/。
/// </summary>
public class CommunicationConfigManager
{
    private static CommunicationConfigManager? _instance;
    private static readonly object _lock = new object();
    private readonly string _configFilePath;
    private CommunicationConfig? _cachedConfig;

    public static CommunicationConfigManager Instance
    {
        get
        {
            if (_instance == null)
            {
                lock (_lock)
                {
                    _instance ??= new CommunicationConfigManager();
                }
            }
            return _instance;
        }
    }

    private CommunicationConfigManager()
    {
        var appDirectory = AppDomain.CurrentDomain.BaseDirectory;
        var configDirectory = Path.Combine(appDirectory, "configs");
        if (!Directory.Exists(configDirectory))
        {
            Directory.CreateDirectory(configDirectory);
        }
        _configFilePath = Path.Combine(configDirectory, "communication_config.json");
    }

    public CommunicationConfig LoadConfig()
    {
        if (_cachedConfig != null)
            return _cachedConfig;

        try
        {
            if (File.Exists(_configFilePath))
            {
                var json = File.ReadAllText(_configFilePath);
                var config = JsonSerializer.Deserialize<CommunicationConfig>(json);
                if (config != null)
                {
                    config.Modbus ??= new ModbusConnectionConfig();
                    _cachedConfig = config;
                    return config;
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"加载通信配置失败: {ex.Message}");
        }

        _cachedConfig = new CommunicationConfig();
        return _cachedConfig;
    }

    public void SaveConfig(CommunicationConfig config)
    {
        try
        {
            _cachedConfig = config;
            var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_configFilePath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存通信配置失败: {ex.Message}");
        }
    }

    /// <summary>仅保存 Modbus 子配置</summary>
    public void SaveModbus(ModbusConnectionConfig modbus)
    {
        var config = LoadConfig();
        config.Modbus = modbus;
        SaveConfig(config);
    }

    /// <summary>清除缓存，强制下次从文件重新加载</summary>
    public void ClearCache()
    {
        _cachedConfig = null;
    }
}
