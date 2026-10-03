using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VisionInspection.Modules.SOP.Services;

/// <summary>
/// 黄金数据集配置管理器：读写 configs/golden_recorder.json（单例）
/// </summary>
public static class GoldenRecorderConfigManager
{
    private static readonly object _lock = new();
    private static GoldenRecorderConfig? _cached;

    private static string ConfigPath => System.IO.Path.Combine(
        AppContext.BaseDirectory, "configs", "golden_recorder.json");

    public static GoldenRecorderConfig Load()
    {
        lock (_lock)
        {
            if (_cached != null) return _cached;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath, Encoding.UTF8);
                    _cached = JsonSerializer.Deserialize<GoldenRecorderConfig>(json) ?? new();
                }
                else
                {
                    _cached = new GoldenRecorderConfig();
                }
            }
            catch
            {
                _cached = new GoldenRecorderConfig();
            }
            return _cached;
        }
    }

    public static void Save(GoldenRecorderConfig cfg)
    {
        lock (_lock)
        {
            _cached = cfg;
            try
            {
                var dir = System.IO.Path.GetDirectoryName(ConfigPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var json = JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json, Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GoldenCfg] 保存失败: {ex.Message}");
            }
        }
    }

    /// <summary>重置为默认（"结束录制"时用）</summary>
    public static GoldenRecorderConfig Reset()
    {
        var cfg = new GoldenRecorderConfig { Enabled = false };
        Save(cfg);
        return cfg;
    }
}
