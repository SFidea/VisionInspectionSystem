using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OpenCvSharp;
using VisionInspection.Core.Services;

namespace VisionInspection.UI.Services
{
    /// <summary>
    /// 网络相机服务：ONVIF 自动发现 + RTSP 拉流（OpenCV VideoCapture）。
    /// 兼容海康 / 大华 / 萤石等所有支持 ONVIF、RTSP 的 IP 相机，无需厂商 SDK。
    /// </summary>
    public class OnvifCameraService : ICameraService
    {
        private const string MulticastAddress = "239.255.255.250";
        private const int MulticastPort = 3702;

        #region 字段

        private VideoCapture? _capture;
        private bool _isConnected;
        private bool _isGrabbing;
        private CancellationTokenSource? _cts;
        private Task? _grabTask;

        private readonly object _captureLock = new();
        private int _readFailCount;
        private int _reconnectAttempts;
        private string _lastRtspUrl = "";

        #endregion

        #region 属性

        public bool IsConnected => _isConnected;
        public bool IsGrabbing => _isGrabbing;
        public CameraInfo CurrentCamera { get; private set; } = new CameraInfo();

        #endregion

        #region 事件

        public event EventHandler<byte[]>? ImageGrabbed;
        public event EventHandler<CameraImageData>? ImageDataGrabbed;
        public event EventHandler<bool>? ConnectionStatusChanged;
        public event EventHandler<string>? ErrorOccurred;

        #endregion

        #region 相机枚举（ONVIF WS-Discovery）

        public async Task<List<CameraInfo>> EnumCamerasAsync()
        {
            var devices = await Task.Run(() => DiscoverOnvifDevices(TimeSpan.FromSeconds(3)));
            var cameras = new List<CameraInfo>();

            foreach (var d in devices)
            {
                // 尽力匿名获取 RTSP 地址，失败则回退常见默认路径（可在界面手动修改）
                string rtsp = await TryGetRtspUrlAsync(d.DeviceServiceUrl) ?? BuildDefaultRtsp(d.Ip);
                cameras.Add(new CameraInfo
                {
                    Id = $"onvif_{d.Ip}",
                    Name = d.Name,
                    Model = d.Model,
                    SerialNumber = d.Ip,
                    InterfaceType = "ONVIF",
                    Index = (uint)cameras.Count,
                    DisplayName = $"🌐 {d.Name} ({d.Ip})",
                    ExtInfo = new OnvifCameraExt
                    {
                        Ip = d.Ip,
                        Name = d.Name,
                        Model = d.Model,
                        DeviceServiceUrl = d.DeviceServiceUrl,
                        RtspUrl = rtsp
                    }
                });
            }

            return cameras;
        }

        /// <summary>
        /// WS-Discovery 组播探测：向 239.255.255.250:3702 发送 Probe，收集支持 ONVIF 的相机
        /// </summary>
        private static List<OnvifDevice> DiscoverOnvifDevices(TimeSpan timeout)
        {
            var devices = new List<OnvifDevice>();
            try
            {
                using var udp = new UdpClient();
                udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
                udp.JoinMulticastGroup(IPAddress.Parse(MulticastAddress));
                udp.Client.ReceiveTimeout = 1000;

                string messageId = $"uuid:{Guid.NewGuid():N}";
                byte[] payload = Encoding.UTF8.GetBytes(BuildProbe(messageId));
                udp.Send(payload, payload.Length, MulticastAddress, MulticastPort);

                var sw = Stopwatch.StartNew();
                while (sw.Elapsed < timeout)
                {
                    try
                    {
                        var remote = new IPEndPoint(IPAddress.Any, 0);
                        byte[] data = udp.Receive(ref remote);
                        string xml = Encoding.UTF8.GetString(data);
                        var dev = ParseProbeMatch(xml);
                        if (dev != null && !devices.Any(x => x.Ip == dev.Ip))
                            devices.Add(dev);
                    }
                    catch (SocketException)
                    {
                        // 单次接收超时（ReceiveTimeout=1s）：不能 break，否则响应稍慢的相机会被整轮漏掉。
                        // 继续等，直到整个探测窗口（timeout）耗尽。
                        continue;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ONVIF 发现异常: {ex.Message}");
            }
            return devices;
        }

        private static string BuildProbe(string messageId) =>
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
            "<e:Envelope xmlns:e=\"http://www.w3.org/2003/05/soap-envelope\" " +
            "xmlns:w=\"http://schemas.xmlsoap.org/ws/2004/08/addressing\" " +
            "xmlns:d=\"http://schemas.xmlsoap.org/ws/2005/04/discovery\" " +
            "xmlns:dn=\"http://www.onvif.org/ver10/network/wsdl\">" +
            "<e:Header>" +
            $"<w:MessageID>{messageId}</w:MessageID>" +
            "<w:To e:mustUnderstand=\"true\">urn:schemas-xmlsoap-org:ws:2005:04:discovery</w:To>" +
            "<w:Action e:mustUnderstand=\"true\">http://schemas.xmlsoap.org/ws/2005/04/discovery/Probe</w:Action>" +
            "</e:Header>" +
            "<e:Body><d:Probe><d:Types>dn:NetworkVideoTransmitter</d:Types></d:Probe></e:Body></e:Envelope>";

        private static OnvifDevice? ParseProbeMatch(string xml)
        {
            try
            {
                var xaddrs = Regex.Match(xml, @"<d:XAddrs>(.*?)</d:XAddrs>", RegexOptions.Singleline).Groups[1].Value;
                string deviceUrl = xaddrs.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                                         .FirstOrDefault(u => u.StartsWith("http://")) ?? "";
                string ip = ExtractIpFromUrl(deviceUrl);
                if (string.IsNullOrEmpty(ip)) return null;

                var scopes = Regex.Match(xml, @"<d:Scopes>(.*?)</d:Scopes>", RegexOptions.Singleline).Groups[1].Value;
                string name = ExtractScopeValue(scopes, "name");
                string hardware = ExtractScopeValue(scopes, "hardware");

                return new OnvifDevice
                {
                    Ip = ip,
                    Name = string.IsNullOrEmpty(name) ? $"相机 {ip}" : name,
                    Model = hardware,
                    DeviceServiceUrl = deviceUrl
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractScopeValue(string scopes, string key)
        {
            var m = Regex.Match(scopes, $@"onvif://www\.onvif\.org/{key}/([^\s]+)");
            if (!m.Success) return "";
            try { return Uri.UnescapeDataString(m.Groups[1].Value.Trim()); }
            catch { return m.Groups[1].Value.Trim(); }
        }

        private static string ExtractIpFromUrl(string url)
        {
            try
            {
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                    return uri.Host;
            }
            catch { }
            return "";
        }

        #endregion

        #region RTSP 地址解析（匿名 GetCapabilities → GetProfiles → GetStreamUri）

        private static async Task<string?> TryGetRtspUrlAsync(string deviceServiceUrl, int timeoutMs = 2500,
            string? user = null, string? pass = null)
        {
            if (string.IsNullOrEmpty(deviceServiceUrl)) return null;
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromMilliseconds(timeoutMs) };

                // 1. 设备服务 GetCapabilities → 媒体服务地址
                string caps = await PostSoapAsync(client, deviceServiceUrl, BuildGetCapabilities(user, pass));
                if (string.IsNullOrEmpty(caps)) return null;
                string mediaUrl = ExtractMediaXAddr(caps);
                if (string.IsNullOrEmpty(mediaUrl)) return null;

                // 2. 媒体服务 GetProfiles → 首个 Profile token
                string profiles = await PostSoapAsync(client, mediaUrl, BuildGetProfiles(user, pass));
                if (string.IsNullOrEmpty(profiles)) return null;
                string token = Regex.Match(profiles, @"<[\w]+:Profiles[^>]*token=""([^""]*)""").Groups[1].Value;
                if (string.IsNullOrEmpty(token)) return null;

                // 3. GetStreamUri → RTSP 地址
                string stream = await PostSoapAsync(client, mediaUrl, BuildGetStreamUri(token, user, pass));
                if (string.IsNullOrEmpty(stream)) return null;
                string uri = Regex.Match(stream, @"<[\w]+:Uri>([^<]*)</[\w]+:Uri>").Groups[1].Value;
                return string.IsNullOrEmpty(uri) ? null : uri;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"获取 RTSP 地址失败: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// 提取媒体服务地址（XAddr）。
        /// ONVIF 标准返回 &lt;tt:Media&gt;&lt;tt:XAddr&gt;…&lt;/tt:XAddr&gt;&lt;/tt:Media&gt;；
        /// 只有少数厂商固件直接给 &lt;tt:MediaXAddr&gt;…&lt;/tt:MediaXAddr&gt;。
        /// 旧实现只认后者，导致标准设备一律解析失败、静默回退到硬编码 RTSP 路径
        /// （表现为"能发现相机但连不上"）。这里两种都兼容，且命名空间前缀不写死。
        /// </summary>
        private static string ExtractMediaXAddr(string caps)
        {
            var m = Regex.Match(caps, @"<[\w]+:MediaXAddr>([^<]*)</[\w]+:MediaXAddr>");
            if (m.Success) return m.Groups[1].Value.Trim();

            m = Regex.Match(caps, @"<[\w]+:Media>(.*?)</[\w]+:Media>", RegexOptions.Singleline);
            if (m.Success)
            {
                var inner = Regex.Match(m.Groups[1].Value, @"<[\w]+:XAddr>([^<]*)</[\w]+:XAddr>");
                if (inner.Success) return inner.Groups[1].Value.Trim();
            }
            return "";
        }

        private static async Task<string> PostSoapAsync(HttpClient client, string url, string envelope)
        {
            using var content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml");
            using var resp = await client.PostAsync(url, content);
            return await resp.Content.ReadAsStringAsync();
        }

        private static string BuildGetCapabilities(string? user, string? pass) =>
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            BuildSecurityHeader(user, pass) +
            "<s:Body><GetCapabilities xmlns=\"http://www.onvif.org/ver10/device/wsdl\"><Category>All</Category></GetCapabilities></s:Body></s:Envelope>";

        private static string BuildGetProfiles(string? user, string? pass) =>
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            BuildSecurityHeader(user, pass) +
            "<s:Body><GetProfiles xmlns=\"http://www.onvif.org/ver10/media/wsdl\"/></s:Body></s:Envelope>";

        private static string BuildGetStreamUri(string token, string? user, string? pass) =>
            "<s:Envelope xmlns:s=\"http://www.w3.org/2003/05/soap-envelope\">" +
            BuildSecurityHeader(user, pass) +
            "<s:Body><GetStreamUri xmlns=\"http://www.onvif.org/ver10/media/wsdl\">" +
            "<StreamSetup><Stream>RTP-Unicast</Stream><Transport><Protocol>RTSP</Protocol></Transport></StreamSetup>" +
            $"<ProfileToken>{token}</ProfileToken></GetStreamUri></s:Body></s:Envelope>";

        /// <summary>
        /// WS-Security UsernameToken（PasswordDigest）。
        /// 绝大多数 IP 相机的 GetCapabilities/GetProfiles 都要求鉴权，
        /// 不带上这段会直接返回 401，导致 RTSP 地址解析失败、静默回退到硬编码路径。
        /// 未配置用户名时返回空头（匿名访问）。
        /// </summary>
        private static string BuildSecurityHeader(string? user, string? pass)
        {
            if (string.IsNullOrWhiteSpace(user)) return "<s:Header/>";

            var nonceBytes = new byte[16];
            RandomNumberGenerator.Fill(nonceBytes);
            string nonceB64 = Convert.ToBase64String(nonceBytes);
            string created = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            // Digest = Base64( SHA1( nonceBytes + createdUTF8 + passwordUTF8 ) )
            var buf = new List<byte>();
            buf.AddRange(nonceBytes);
            buf.AddRange(Encoding.UTF8.GetBytes(created));
            buf.AddRange(Encoding.UTF8.GetBytes(pass ?? ""));
            string digestB64 = Convert.ToBase64String(SHA1.HashData(buf.ToArray()));

            return "<s:Header>" +
                   "<Security s:mustUnderstand=\"1\" xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">" +
                   "<UsernameToken>" +
                   $"<Username>{SecurityElement.Escape(user)}</Username>" +
                   "<Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" +
                   $"{digestB64}</Password>" +
                   $"<Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">{nonceB64}</Nonce>" +
                   "<Created xmlns=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd\">" +
                   $"{created}</Created>" +
                   "</UsernameToken></Security></s:Header>";
        }

        /// <summary>
        /// 常见默认 RTSP 路径（海康/大华 IP 相机主码流）；失败时作为兜底，可在界面手动修改
        /// </summary>
        private static string BuildDefaultRtsp(string ip) => $"rtsp://{ip}:554/Streaming/Channels/101";

        /// <summary>
        /// 把用户名/密码注入 RTSP 地址：rtsp://user:pass@host:port/path。
        /// 已自带凭据（UserInfo 非空）或未提供用户名时原样返回，避免重复注入。
        /// </summary>
        private static string InjectRtspCredentials(string rtsp, string? user, string? pass)
        {
            if (string.IsNullOrWhiteSpace(user)) return rtsp;
            if (!Uri.TryCreate(rtsp, UriKind.Absolute, out var u)) return rtsp;
            if (!string.IsNullOrEmpty(u.UserInfo)) return rtsp; // 用户已手写 user:pass@

            var creds = Uri.EscapeDataString(user) + ":" + Uri.EscapeDataString(pass ?? "");
            var port = u.IsDefaultPort ? "" : ":" + u.Port;
            return $"{u.Scheme}://{creds}@{u.Host}{port}{u.PathAndQuery}";
        }

        /// <summary>隐藏 URL 中的账号密码，避免把凭据打到日志/界面上</summary>
        private static string MaskCredentials(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || string.IsNullOrEmpty(u.UserInfo))
                return url;
            var port = u.IsDefaultPort ? "" : ":" + u.Port;
            return $"{u.Scheme}://***:***@{u.Host}{port}{u.PathAndQuery}";
        }

        #endregion

        #region 连接控制

        public Task<bool> ConnectAsync(CameraInfo camera)
        {
            return Task.Run(() =>
            {
                try
                {
                    Disconnect();

                    if (camera?.ExtInfo is not OnvifCameraExt ext || string.IsNullOrEmpty(ext.RtspUrl))
                    {
                        ErrorOccurred?.Invoke(this, "RTSP 地址无效，请检查");
                        return false;
                    }

                    // 配置了账号密码时：先用鉴权重新向 ONVIF 取一次真实 RTSP 地址
                    //（发现阶段是匿名的，鉴权相机会 401 而回退到硬编码路径）
                    var rtsp = ext.RtspUrl;
                    if (!string.IsNullOrWhiteSpace(ext.UserName) && !string.IsNullOrEmpty(ext.DeviceServiceUrl))
                    {
                        var authed = TryGetRtspUrlAsync(ext.DeviceServiceUrl, 3000, ext.UserName, ext.Password).Result;
                        if (!string.IsNullOrEmpty(authed)) rtsp = authed!;
                    }

                    // 把凭据注入 RTSP URL：rtsp://user:pass@host:port/path
                    rtsp = InjectRtspCredentials(rtsp, ext.UserName, ext.Password);

                    _capture = new VideoCapture(rtsp);
                    if (!_capture.IsOpened())
                    {
                        ErrorOccurred?.Invoke(this, $"无法打开网络相机: {MaskCredentials(rtsp)}");
                        return false;
                    }

                    // 降低解码缓冲，减少延迟
                    _capture.Set(VideoCaptureProperties.BufferSize, 1);

                    lock (_captureLock) { _lastRtspUrl = rtsp; }
                    _readFailCount = 0;
                    _reconnectAttempts = 0;

                    _isConnected = true;
                    CurrentCamera = camera;
                    ConnectionStatusChanged?.Invoke(this, true);
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"连接网络相机异常：{ex.Message}");
                    return false;
                }
            });
        }

        public void Disconnect()
        {
            try
            {
                StopGrabbing();

                lock (_captureLock)
                {
                    if (_isConnected && _capture != null)
                    {
                        _capture.Release();
                        _capture.Dispose();
                        _capture = null;
                        _isConnected = false;
                        _lastRtspUrl = "";
                        Debug.WriteLine("网络相机已断开");
                    }
                }

                CurrentCamera = new CameraInfo();
                ConnectionStatusChanged?.Invoke(this, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"断开连接异常：{ex.Message}");
            }
        }

        #endregion

        #region 采集控制

        public Task<bool> StartGrabbingAsync()
        {
            return Task.Run(() =>
            {
                if (!_isConnected || _capture == null)
                {
                    ErrorOccurred?.Invoke(this, "设备未连接");
                    return false;
                }

                try
                {
                    _cts = new CancellationTokenSource();
                    _isGrabbing = true;
                    _grabTask = Task.Run(() => GrabLoop(_cts.Token));
                    return true;
                }
                catch (Exception ex)
                {
                    ErrorOccurred?.Invoke(this, $"开始采集异常：{ex.Message}");
                    _isGrabbing = false;
                    return false;
                }
            });
        }

        private void GrabLoop(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && _isGrabbing)
            {
                try
                {
                    Mat? frame = null;
                    bool ok = false;
                    lock (_captureLock)
                    {
                        if (_capture != null)
                        {
                            frame = new Mat();
                            ok = _capture.Read(frame) && !frame.Empty();
                        }
                    }

                    if (!ok)
                    {
                        frame?.Dispose();
                        frame = null;
                        _readFailCount++;

                        // 连续约 1 秒取不到帧 → 判定掉线，尝试重连
                        if (_readFailCount >= 30)
                        {
                            if (_readFailCount == 30)
                                ErrorOccurred?.Invoke(this, "网络相机取帧中断，正在尝试重连...");

                            if (_readFailCount % 30 == 0)
                            {
                                _reconnectAttempts++;
                                if (TryReconnect())
                                {
                                    _readFailCount = 0;
                                    _reconnectAttempts = 0;
                                    ConnectionStatusChanged?.Invoke(this, true);
                                }
                                else if (_reconnectAttempts >= 5)
                                {
                                    ErrorOccurred?.Invoke(this, $"网络相机重连失败 {_reconnectAttempts} 次，已停止采集");
                                    _isGrabbing = false;
                                    ConnectionStatusChanged?.Invoke(this, false);
                                }
                            }
                        }

                        Thread.Sleep(100);
                        continue;
                    }

                    _readFailCount = 0;
                    _reconnectAttempts = 0;

                    using (frame)
                    {
                        using var rgbFrame = new Mat();
                        Cv2.CvtColor(frame, rgbFrame, ColorConversionCodes.BGR2RGB);

                        int width = rgbFrame.Width;
                        int height = rgbFrame.Height;
                        int channels = rgbFrame.Channels();
                        int totalBytes = width * height * channels;

                        byte[] data = new byte[totalBytes];
                        System.Runtime.InteropServices.Marshal.Copy(rgbFrame.Data, data, 0, totalBytes);

                        ImageGrabbed?.Invoke(this, data);
                        ImageDataGrabbed?.Invoke(this, new CameraImageData
                        {
                            Data = data,
                            Width = width,
                            Height = height,
                            IsColor = true,
                            Channels = channels
                        });
                    }

                    Thread.Sleep(33);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"采集异常：{ex.Message}");
                }
            }
        }

        /// <summary>
        /// 网络相机断流重连：释放旧 VideoCapture 并按原 RTSP 地址重新打开。
        /// RTSP 是长连接，网络抖动 / 相机重启都会导致 Read 一直失败，
        /// 不重连的话程序会静默无帧（界面停在最后一帧，看不出已掉线）。
        /// </summary>
        private bool TryReconnect()
        {
            string url;
            lock (_captureLock) { url = _lastRtspUrl; }
            if (string.IsNullOrEmpty(url)) return false;

            lock (_captureLock)
            {
                try
                {
                    _capture?.Release();
                    _capture?.Dispose();
                }
                catch { }
                _capture = null;

                try
                {
                    _capture = new VideoCapture(url);
                    if (_capture.IsOpened())
                    {
                        _capture.Set(VideoCaptureProperties.BufferSize, 1);
                        Debug.WriteLine("网络相机已重连");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"重连异常：{ex.Message}");
                }

                try { _capture?.Dispose(); } catch { }
                _capture = null;
            }
            return false;
        }

        public void StopGrabbing()
        {
            try
            {
                _isGrabbing = false;
                _cts?.Cancel();
                _grabTask?.Wait(1000);
                _grabTask = null;
                _cts?.Dispose();
                _cts = null;
                Debug.WriteLine("网络相机采集已停止");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"停止采集异常：{ex.Message}");
            }
        }

        #endregion

        #region 参数设置（RTSP 通过 OpenCV 不支持曝光/增益控制）

        public Task<bool> SetExposureTimeAsync(float exposureTime) => Task.FromResult(false);
        public Task<bool> SetGainAsync(float gain) => Task.FromResult(false);
        public Task<float> GetExposureTimeAsync() => Task.FromResult(0f);
        public Task<float> GetGainAsync() => Task.FromResult(0f);
        public Task<(float Min, float Max)> GetExposureTimeRangeAsync() => Task.FromResult((0f, 0f));
        public Task<(float Min, float Max)> GetGainRangeAsync() => Task.FromResult((0f, 0f));

        #endregion

        #region 资源释放

        public void Dispose() => Disconnect();

        #endregion

        private class OnvifDevice
        {
            public string Ip { get; set; } = "";
            public string Name { get; set; } = "";
            public string Model { get; set; } = "";
            public string DeviceServiceUrl { get; set; } = "";
        }
    }

    /// <summary>
    /// ONVIF 网络相机扩展信息（存放于 CameraInfo.ExtInfo）
    /// </summary>
    public class OnvifCameraExt
    {
        public string Ip { get; set; } = "";
        public string Name { get; set; } = "";
        public string Model { get; set; } = "";
        public string DeviceServiceUrl { get; set; } = "";
        public string RtspUrl { get; set; } = "";

        /// <summary>ONVIF / RTSP 用户名。绝大多数 IP 相机默认要求鉴权，留空则按匿名访问。</summary>
        public string UserName { get; set; } = "";

        /// <summary>ONVIF / RTSP 密码。</summary>
        public string Password { get; set; } = "";
    }
}
