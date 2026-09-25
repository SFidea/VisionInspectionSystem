using System.IO.Ports;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using VisionInspection.Core.Models;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Views;

/// <summary>
/// 通信设置窗口：配置 Modbus TCP / RTU，实时查看 X1-X8 输入，复位锁存输出。
/// </summary>
public partial class CommunicationConfigWindow : Window
{
    private readonly UserManager? _userManager;
    private readonly List<Ellipse> _inputDots = new();
    private readonly List<TextBlock> _inputLabels = new();

    public CommunicationConfigWindow(UserManager? userManager = null)
    {
        InitializeComponent();
        _userManager = userManager;

        BuildInputsUi();
        BuildLatchUi();
        ScanComPorts(keepCurrentText: false);   // 先扫描串口，供 LoadConfigToUi 回填/兜底
        LoadConfigToUi();
        UpdateStatus(ModbusService.Instance.Status);
        RefreshInputIndicators(ModbusService.Instance.Inputs);

        ModbusService.Instance.StatusChanged += OnModbusStatusChanged;
        ModbusService.Instance.InputsUpdated += OnInputsUpdated;
        Closed += (_, _) =>
        {
            ModbusService.Instance.StatusChanged -= OnModbusStatusChanged;
            ModbusService.Instance.InputsUpdated -= OnInputsUpdated;
        };
    }

    private void BuildInputsUi()
    {
        for (int i = 0; i < 8; i++)
        {
            var dot = new Ellipse { Width = 16, Height = 16, Fill = Brushes.Gray, Margin = new Thickness(0, 0, 6, 0) };
            var label = new TextBlock { Text = $"X{i + 1}: 0", VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
            _inputDots.Add(dot);
            _inputLabels.Add(label);

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(6, 6, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            panel.Children.Add(dot);
            panel.Children.Add(label);
            InputsGrid.Children.Add(panel);
        }
    }

    private void BuildLatchUi()
    {
        bool canReset = _userManager?.HasPermission(Permissions.PLCConfig) ?? false;
        for (int i = 0; i < 8; i++)
        {
            int addr = i;
            var btn = new Button
            {
                Content = $"Y{i + 1}",
                Width = 56,
                Height = 32,
                Margin = new Thickness(4),
                Padding = new Thickness(0),
                IsEnabled = canReset
            };
            btn.Click += (_, _) =>
            {
                if (!(_userManager?.HasPermission(Permissions.PLCConfig) ?? false))
                {
                    MessageBox.Show("锁存复位需要管理员权限。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                ModbusService.Instance.ReleaseLatch(addr);
                Log($"已复位锁存输出 Y{addr + 1}");
            };
            LatchGrid.Children.Add(btn);
        }
        if (!canReset)
        {
            var hint = new TextBlock
            {
                Text = "（当前账号无 PLCConfig 权限，锁存复位按钮已禁用；请用管理员登录后复位）",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
                Margin = new Thickness(4, 8, 0, 0)
            };
            LatchGrid.Children.Add(hint);
        }
    }

    /// <summary>
    /// 扫描本机可用串口并填充到下拉框。
    /// 用 <see cref="SerialPort.GetPortNames"/> 枚举设备（无需额外依赖），按端口号数字排序（COM2 排在 COM10 前面）。
    /// 下拉框是 IsEditable，因此即使配置里的端口当前不存在（设备被拔掉），也能保留原文本不被清掉 —— 这正是「记住上次连接的参数」需要的。
    /// </summary>
    /// <param name="keepCurrentText">true 时保留用户当前输入，仅刷新候选列表（用于展开下拉/手动扫描）</param>
    private void ScanComPorts(bool keepCurrentText)
    {
        var current = keepCurrentText ? ComComboBox.Text : null;

        string[] ports;
        try
        {
            ports = SerialPort.GetPortNames()
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(GetPortNumber)
                .ToArray();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CommConfig] 扫描串口失败: {ex.Message}");
            ports = Array.Empty<string>();
        }

        ComComboBox.ItemsSource = ports;

        if (keepCurrentText && !string.IsNullOrEmpty(current))
        {
            ComComboBox.Text = current;
        }

        var mode = CommunicationConfigManager.Instance.LoadConfig().Modbus;
        if (!keepCurrentText)
        {
            // 首次填充：优先回填已保存的端口；若从未配置过且检测到设备，则默认选第一个可用端口
            ComComboBox.Text = !string.IsNullOrWhiteSpace(mode.ComPort)
                ? mode.ComPort
                : ports.FirstOrDefault() ?? string.Empty;
        }

        ScanComButton.ToolTip = ports.Length > 0
            ? $"重新扫描串口设备（当前检测到 {ports.Length} 个：{string.Join("、", ports)}）"
            : "重新扫描串口设备（当前未检测到可用串口）";
    }

    /// <summary>取端口号数字用于排序；无法解析的排在最后</summary>
    private static int GetPortNumber(string portName)
    {
        var digits = new string(portName.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, out var n) ? n : int.MaxValue;
    }

    private void ComComboBox_DropDownOpened(object sender, EventArgs e) => ScanComPorts(keepCurrentText: true);

    private void ScanComButton_Click(object sender, RoutedEventArgs e)
    {
        ScanComPorts(keepCurrentText: true);
        ComComboBox.IsDropDownOpen = true;   // 扫描完直接展开，省一次点击
    }

    private void LoadConfigToUi()
    {
        var cfg = CommunicationConfigManager.Instance.LoadConfig().Modbus;
        if (cfg.IsTcp)
        {
            TcpRadio.IsChecked = true;
            RtuRadio.IsChecked = false;
        }
        else
        {
            TcpRadio.IsChecked = false;
            RtuRadio.IsChecked = true;
        }
        TcpPanel.Visibility = cfg.IsTcp ? Visibility.Visible : Visibility.Collapsed;
        RtuPanel.Visibility = cfg.IsTcp ? Visibility.Collapsed : Visibility.Visible;

        IpTextBox.Text = cfg.IpAddress;
        PortTextBox.Text = cfg.Port.ToString();
        // 端口用可编辑下拉：若已保存端口不在本机列表中（设备未插），保留原文本以便下次插上即用
        if (!string.IsNullOrWhiteSpace(cfg.ComPort))
            ComComboBox.Text = cfg.ComPort;
        BaudTextBox.Text = cfg.BaudRate.ToString();
        SelectComboByTag(ParityCombo, cfg.Parity);
        DataBitsTextBox.Text = cfg.DataBits.ToString();
        SelectComboByTag(StopBitsCombo, cfg.StopBits.ToString());
        SlaveTextBox.Text = cfg.SlaveId.ToString();
        ReconnectTextBox.Text = cfg.ReconnectIntervalMs.ToString();
        AutoConnectCheckBox.IsChecked = cfg.AutoConnect;
    }

    private ModbusConnectionConfig ReadConfigFromUi()
    {
        bool isTcp = TcpRadio.IsChecked == true;
        byte.TryParse(SlaveTextBox.Text, out var slave);
        int.TryParse(ReconnectTextBox.Text, out var reconnect);
        reconnect = reconnect <= 0 ? 3000 : reconnect;

        var cfg = new ModbusConnectionConfig
        {
            Mode = isTcp ? "Tcp" : "Rtu",
            IpAddress = IpTextBox.Text.Trim(),
            Port = int.TryParse(PortTextBox.Text, out var p) && p > 0 ? p : 502,
            ComPort = (ComComboBox.Text ?? string.Empty).Trim(),
            BaudRate = int.TryParse(BaudTextBox.Text, out var b) && b > 0 ? b : 9600,
            Parity = GetSelectedTag(ParityCombo) ?? "None",
            DataBits = int.TryParse(DataBitsTextBox.Text, out var db) && db > 0 ? db : 8,
            StopBits = double.TryParse(GetSelectedTag(StopBitsCombo) ?? "1", out var sb) ? sb : 1,
            SlaveId = slave == 0 ? (byte)1 : slave,
            AutoConnect = AutoConnectCheckBox.IsChecked == true,
            ReconnectIntervalMs = reconnect
        };
        return cfg;
    }

    private void ModeRadio_Changed(object sender, RoutedEventArgs e)
    {
        bool isTcp = TcpRadio.IsChecked == true;
        TcpPanel.Visibility = isTcp ? Visibility.Visible : Visibility.Collapsed;
        RtuPanel.Visibility = isTcp ? Visibility.Collapsed : Visibility.Visible;
    }

    private void TestButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ReadConfigFromUi();

        // RTU 缺串口时提前给出可读提示，避免走到底层抛"端口名无效"
        if (!cfg.IsTcp && string.IsNullOrWhiteSpace(cfg.ComPort))
        {
            MessageBox.Show("请选择或填写串口号（可点右侧 🔄 重新扫描）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ComComboBox.Focus();
            return;
        }

        // 测试需要 ModbusService 先读到待测配置，因此临时写入；
        // 若测试失败，则回滚为之前已保存（上次连接成功）的参数，避免一次误试把可用配置冲掉。
        var previous = CommunicationConfigManager.Instance.LoadConfig().Modbus;
        CommunicationConfigManager.Instance.SaveModbus(cfg);

        bool ok;
        try
        {
            ok = ModbusService.Instance.TestConnect();
        }
        catch (Exception ex)
        {
            ok = false;
            Log($"测试连接异常: {ex.Message}");
        }

        if (!ok)
            CommunicationConfigManager.Instance.SaveModbus(previous);

        MessageBox.Show(ok ? "连接成功！已记住本次连接参数。" : "连接失败，请检查配置与设备。\n（已保留上次连接成功的参数，未覆盖）",
            "测试连接", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var cfg = ReadConfigFromUi();

        if (!cfg.IsTcp && string.IsNullOrWhiteSpace(cfg.ComPort))
        {
            MessageBox.Show("请选择或填写串口号（可点右侧 🔄 重新扫描）。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            ComComboBox.Focus();
            return;
        }

        CommunicationConfigManager.Instance.SaveModbus(cfg);   // 持久化：下次启动按这些参数自动连接
        // 立即按新配置重连（含自动重连）
        ModbusService.Instance.ConnectWithRetry();
        MessageBox.Show("已保存通信设置并应用（下次启动按此参数自动连接）。", "保存",
            MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void ResetAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (!(_userManager?.HasPermission(Permissions.PLCConfig) ?? false))
        {
            MessageBox.Show("锁存复位需要管理员权限。", "权限不足", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        ModbusService.Instance.ReleaseAllLatches();
        Log("已复位全部锁存输出");
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ===== 状态 / 输入刷新 =====
    private void OnModbusStatusChanged(object? sender, ModbusConnectionStatus status) => UpdateStatus(status);

    private void UpdateStatus(ModbusConnectionStatus status)
    {
        Dispatcher.Invoke(() =>
        {
            (StatusDot.Fill, StatusText.Text) = status switch
            {
                ModbusConnectionStatus.Connected => (Brushes.LimeGreen, "已连接"),
                ModbusConnectionStatus.Connecting => (Brushes.Orange, "连接中..."),
                ModbusConnectionStatus.Error => (Brushes.Red, "连接错误/未连接"),
                _ => (Brushes.Gray, "未连接")
            };
            var cfg = CommunicationConfigManager.Instance.LoadConfig().Modbus;
            ModeText.Text = cfg.IsTcp ? $"TCP {cfg.IpAddress}:{cfg.Port}" : $"RTU {cfg.ComPort} {cfg.BaudRate}";
        });
    }

    private void OnInputsUpdated(object? sender, ushort[] inputs) => RefreshInputIndicators(inputs);

    private void RefreshInputIndicators(IReadOnlyList<ushort> inputs)
    {
        if (inputs == null || inputs.Count < 8) return;
        Dispatcher.Invoke(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                bool on = inputs[i] != 0;
                _inputDots[i].Fill = on ? Brushes.LimeGreen : Brushes.Gray;
                _inputLabels[i].Text = $"X{i + 1}: {(on ? 1 : 0)}";
            }
        });
    }

    private static void SelectComboByTag(ComboBox combo, string? tag)
    {
        foreach (var item in combo.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = cbi;
                return;
            }
        }
    }

    private static string? GetSelectedTag(ComboBox combo)
    {
        if (combo.SelectedItem is ComboBoxItem cbi) return cbi.Tag?.ToString();
        return null;
    }

    private void Log(string msg) => System.Diagnostics.Debug.WriteLine($"[CommConfig] {msg}");
}
