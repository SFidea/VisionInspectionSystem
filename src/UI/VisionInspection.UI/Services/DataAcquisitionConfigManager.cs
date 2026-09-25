using System;
using System.IO;
using System.Text.Json;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 数据采集（录像 + 抽帧 + 预标注）配置单例管理器。
    /// 持久化到 &lt;程序基目录&gt;/configs/data_acquisition.json。
    /// 写法沿用 SopCaptureConfigManager。
    /// </summary>
    public class DataAcquisitionConfigManager
    {
        private static readonly Lazy<DataAcquisitionConfigManager> _instance =
            new(() => new DataAcquisitionConfigManager());

        public static DataAcquisitionConfigManager Instance => _instance.Value;

        private readonly string _configPath;

        public VisionInspection.UI.Models.DataAcquisitionConfig Config { get; private set; } = new();

        private DataAcquisitionConfigManager()
        {
            _configPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs", "data_acquisition.json");
            Load();
        }

        public void Load()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    var cfg = JsonSerializer.Deserialize<VisionInspection.UI.Models.DataAcquisitionConfig>(json);
                    if (cfg != null) Config = cfg;
                }
            }
            catch
            {
                // 配置损坏则用默认值
                Config = new VisionInspection.UI.Models.DataAcquisitionConfig();
            }
        }

        public void Save()
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(_configPath);
                if (!string.IsNullOrEmpty(dir))
                    Directory.CreateDirectory(dir);

                var json = JsonSerializer.Serialize(Config,
                    new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(_configPath, json);
            }
            catch
            {
                // 保存失败静默忽略
            }
        }
    }
}
