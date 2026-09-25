using System;
using System.IO;
using System.Text.Json;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// SOP 缩略图 / 违规存图 配置单例管理器。
    /// 持久化到 &lt;程序基目录&gt;/configs/sop_capture.json。
    /// </summary>
    public class SopCaptureConfigManager
    {
        private static readonly Lazy<SopCaptureConfigManager> _instance =
            new(() => new SopCaptureConfigManager());

        public static SopCaptureConfigManager Instance => _instance.Value;

        private readonly string _configPath;

        public SopCaptureConfig Config { get; private set; } = new();

        private SopCaptureConfigManager()
        {
            _configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs", "sop_capture.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<SopCaptureConfig>(json);
                    if (cfg != null) Config = cfg;
                }
            }
            catch
            {
                // 配置文件损坏则用默认配置
                Config = new SopCaptureConfig();
            }
        }

        public void Save()
        {
            try
            {
                var dir = Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(Config,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch
            {
                // 保存失败（权限/路径）静默忽略，不影响检测主流程
            }
        }
    }
}
