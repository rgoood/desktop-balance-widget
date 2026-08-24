using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;

namespace DesktopWidget
{
    public class WidgetConfig
    {
        [JsonPropertyName("openrouter_api_key")]
        public string OpenRouterApiKey { get; set; } = "";

        [JsonPropertyName("airport_api_url")]
        public string AirportApiUrl { get; set; } = "";

        [JsonPropertyName("airport_username")]
        public string AirportUsername { get; set; } = "";

        [JsonPropertyName("airport_password")]
        public string AirportPassword { get; set; } = "";

        [JsonPropertyName("refresh_interval_hours")]
        public double RefreshIntervalHours { get; set; } = 2.0;

        [JsonPropertyName("embed_desktop")]
        public bool EmbedDesktop { get; set; } = false;
    }

    public partial class MainWindow : Window
    {
        private static readonly HttpClient Http = CreateHttpClient();

        private readonly string _configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DesktopWidget");
        private readonly string _configPath;

        private WidgetConfig _config = new();
        private DateTime _nextRefresh = DateTime.Now;
        private bool _topmost = true;
        private bool _embedded;
        private System.Windows.Forms.NotifyIcon? _trayIcon;
        private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromSeconds(30) };

        // ---------- Win32：伪桌面模式（窗口沉底） ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string cls, string? title);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const uint GW_HWNDPREV = 3; // Z 序中位于指定窗口上方的窗口
        private const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010, SWP_SHOWWINDOW = 0x0040;

        private static int GetWindowLong(IntPtr h, int i) => GetWindowLong32(h, i);
        private static int SetWindowLong(IntPtr h, int i, int v) => SetWindowLong32(h, i, v);

        private DispatcherTimer? _pinTimer;
        private IntPtr _progman;

        public MainWindow()
        {
            InitializeComponent();
            _configPath = Path.Combine(_configDir, "config.json");

            LoadConfig();
            InitTrayIcon();

            PositionWindow();
            StateChanged += (_, _) =>
            {
                if (WindowState != WindowState.Minimized) return;
                if (_embedded) { WindowState = WindowState.Normal; PinAboveDesktop(); }
                else Hide();
            };

            _clock.Tick += async (_, _) => await OnTickAsync();
            _clock.Start();

            Loaded += (_, _) =>
            {
                if (_config.EmbedDesktop)
                    Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(EmbedInDesktop));
            };

            _ = RefreshAllAsync();
        }

        private static HttpClient CreateHttpClient()
        {
            var handler = new SocketsHttpHandler
            {
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            };
            return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        }

        // ---------- 配置 ----------
        private void LoadConfig()
        {
            try
            {
                if (File.Exists(_configPath))
                {
                    var json = File.ReadAllText(_configPath);
                    _config = JsonSerializer.Deserialize<WidgetConfig>(json) ?? new WidgetConfig();
                }
                else SaveConfig();
            }
            catch { _config = new WidgetConfig(); }
        }

        public void SaveConfig()
        {
            Directory.CreateDirectory(_configDir);
            var options = new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config, options));
        }

        private void PositionWindow()
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - 24;
            Top = area.Top + 24;
        }

        private static System.Drawing.Icon LoadAppIcon()
        {
            try
            {
                using var s = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico")).Stream;
                return new System.Drawing.Icon(s);
            }
            catch { return System.Drawing.SystemIcons.Shield; }
        }

        // ---------- 托盘 ----------
        private void InitTrayIcon()
        {
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = LoadAppIcon(),
                Text = "桌面余额小组件",
                Visible = true,
            };
            var menu = new System.Windows.Forms.ContextMenuStrip();
            var autostart = new System.Windows.Forms.ToolStripMenuItem("开机自启")
            {
                Checked = IsAutoStartEnabled(),
            };
            autostart.Click += (_, _) =>
            {
                SetAutoStart(!IsAutoStartEnabled());
                autostart.Checked = IsAutoStartEnabled();
            };
            menu.Items.Add(autostart);
            menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
            menu.Items.Add("显示面板", null, (_, _) => ShowPanel());
            menu.Items.Add("立即刷新", null, (_, _) => Dispatcher.InvokeAsync(async () => await RefreshAllAsync()));
            menu.Items.Add("退出", null, (_, _) => ExitApp());
            _trayIcon.ContextMenuStrip = menu;
            _trayIcon.DoubleClick += (_, _) => ShowPanel();
        }

        private void ShowPanel()
        {
            Show();
            WindowState = WindowState.Normal;
            if (_embedded) PositionWindow();
            try { Activate(); } catch { }
        }

        private void ExitApp()
        {
            _trayIcon?.Dispose();
            _clock.Stop();
            System.Windows.Application.Current.Shutdown();
        }

        // ---------- 定时逻辑 ----------
        private async Task OnTickAsync()
        {
            if (DateTime.Now >= _nextRefresh)
                await RefreshAllAsync();
        }

        private async Task RefreshAllAsync()
        {
            TxtStatus.Text = "正在刷新...";
            var bal = await Task.Run(() => QueryOpenRouterBalanceAsync());
            SetBalanceUi(bal);

            // 机场流量：已配置 API 则同时刷新（猫猫云后端 API，轻量可定时）
            var traffic = await Task.Run(() => QueryAirportTrafficAsync());
            SetTrafficUi(traffic);

            var ok = bal.Ok && traffic.Ok;
            TxtStatus.Text = ok
                ? $"刷新于 {DateTime.Now:HH:mm:ss}"
                : (bal.Ok ? $"{Truncate(traffic.Detail, 46)}" : Truncate(bal.Detail, 46));

            var interval = TimeSpan.FromHours(Math.Max(0.1, _config.RefreshIntervalHours));
            if (interval.TotalMinutes < 1) interval = TimeSpan.FromMinutes(1);
            _nextRefresh = DateTime.Now.Add(interval);
            TxtNext.Text = $"下次更新 {_nextRefresh:HH:mm}";
        }

        // ---------- 机场流量：手动获取 ----------
        private bool _trafficBusy;

        public async Task RefreshTrafficAsync()
        {
            if (_trafficBusy) return;
            _trafficBusy = true;
            BtnTraffic.IsEnabled = false;
            try
            {
                TxtStatus.Text = "正在获取流量...";
                var r = await Task.Run(() => QueryAirportTrafficAsync());
                SetTrafficUi(r);
                if (r.Ok)
                    TxtStatus.Text = $"流量更新于 {DateTime.Now:HH:mm:ss}";
            }
            finally
            {
                _trafficBusy = false;
                BtnTraffic.IsEnabled = true;
            }
        }

        // ---------- OpenRouter ----------
        private record BalanceResult(bool Ok, string Text, string Detail);

        private async Task<BalanceResult> QueryOpenRouterBalanceAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.OpenRouterApiKey))
                return new BalanceResult(false, "--", "未配置 API Key，请点齿轮设置");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, "https://openrouter.ai/api/v1/credits");
                req.Headers.Authorization = new("Bearer", _config.OpenRouterApiKey.Trim());
                using var resp = await Http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return new BalanceResult(false, "错误", $"HTTP {(int)resp.StatusCode}：{Truncate(body, 120)}");

                using var doc = JsonDocument.Parse(body);
                var data = doc.RootElement.GetProperty("data");
                var total = data.GetProperty("total_credits").GetDecimal();
                var used = data.GetProperty("total_usage").GetDecimal();
                var remain = total - used;

                var colorOk = remain > 1m;
                return new BalanceResult(true, $"${remain:N2}", $"总额 ${total:N2} · 已用 ${used:N2}" + (colorOk ? "" : "  ⚠ 余额不足"));
            }
            catch (Exception ex)
            {
                return new BalanceResult(false, "--", "OpenRouter 查询失败：" + Truncate(ex.Message, 120));
            }
        }

        private void SetBalanceUi(BalanceResult r)
        {
            TxtBalance.Text = r.Text;
            TxtBalance.Foreground = new System.Windows.Media.SolidColorBrush(
                r.Ok ? System.Windows.Media.Color.FromRgb(0x7C, 0xDB, 0x70)
                     : System.Windows.Media.Color.FromRgb(0xE8, 0x6B, 0x6B));
            TxtBalance.ToolTip = r.Detail;
            if (!r.Ok) TxtStatus.Text = Truncate(r.Detail, 46);
        }

        // ---------- 机场流量（猫猫云 V2Board 后端 API）----------
        // 登录: POST {base}/api/v1/passport/auth/login  body {email,password} -> data.auth.token
        // 流量: GET  {base}/api/v1/user/getSubscribe   Header Authorization: <token>
        //       返回 data.u(上传) / data.d(下载) / data.transfer_enable(总量) / data.expired_at(到期unix秒)
        private record TrafficResult(bool Ok, string Text, long? Used, long? Total, DateTimeOffset? Expire, string Detail);

        private async Task<TrafficResult> QueryAirportTrafficAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.AirportApiUrl))
                return new TrafficResult(false, "--", null, null, null, "未配置机场 API 地址，请点齿轮设置");

            if (string.IsNullOrWhiteSpace(_config.AirportUsername) || string.IsNullOrWhiteSpace(_config.AirportPassword))
                return new TrafficResult(false, "--", null, null, null, "未配置机场账号/密码，请点齿轮设置");

            try
            {
                var token = await AirportLoginAsync();
                if (token == null)
                    return new TrafficResult(false, "错误", null, null, null, "机场登录失败，请检查账号密码或网络");

                using var req = new HttpRequestMessage(HttpMethod.Get, $"{_config.AirportApiUrl.TrimEnd('/')}/api/v1/user/getSubscribe");
                req.Headers.TryAddWithoutValidation("Authorization", token);
                using var resp = await Http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return new TrafficResult(false, "错误", null, null, null, $"流量请求失败 HTTP {(int)resp.StatusCode}：{Truncate(body, 120)}");

                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                var status = root.TryGetProperty("status", out var st) ? st.GetString() : "";
                if (status != "success")
                    return new TrafficResult(false, "错误", null, null, null, "机场返回异常：" + Truncate(body, 120));

                var data = root.GetProperty("data");
                long upload = data.TryGetProperty("u", out var up) ? up.GetInt64() : 0;
                long download = data.TryGetProperty("d", out var dn) ? dn.GetInt64() : 0;
                long total = data.TryGetProperty("transfer_enable", out var te) ? te.GetInt64() : 0;
                long expire = data.TryGetProperty("expired_at", out var ex) ? ex.GetInt64() : 0;

                var used = upload + download;
                DateTimeOffset? expireDate = expire > 0 ? DateTimeOffset.FromUnixTimeSeconds(expire) : null;
                return new TrafficResult(true, "", used, total, expireDate, "");
            }
            catch (Exception ex)
            {
                return new TrafficResult(false, "--", null, null, null, "流量查询异常：" + Truncate(ex.Message, 120));
            }
        }

        // 返回 JWT（data.auth_data，接口认证用的 Authorization 头）
        private async Task<string?> AirportLoginAsync()
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{_config.AirportApiUrl.TrimEnd('/')}/api/v1/passport/auth/login");
            req.Content = new StringContent(
                JsonSerializer.Serialize(new { email = _config.AirportUsername.Trim(), password = _config.AirportPassword }),
                Encoding.UTF8, "application/json");
            using var resp = await Http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return null;

            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.TryGetProperty("data", out var data) && data.TryGetProperty("auth_data", out var jwt))
                    return jwt.GetString();
                return null;
            }
            catch { return null; }
        }

        private void SetTrafficUi(TrafficResult r)
        {
            if (!r.Ok)
            {
                TxtTraffic.Text = r.Text;
                BarTraffic.Value = 0;
                TxtExpire.Text = "";
                TxtTraffic.ToolTip = r.Detail;
                TxtStatus.Text = Truncate(r.Detail, 46);
                return;
            }

            long used = r.Used ?? 0, total = r.Total ?? 0;
            string text;
            string expireText = "";
            if (r.Expire is { } e)
                expireText = $"到期 {e.ToLocalTime():yyyy-MM-dd}";

            if (total <= 0)
            {
                text = $"已用 {Fmt(used)} / 不限量";
                BarTraffic.Value = 0;
            }
            else
            {
                var remain = Math.Max(0, total - used);
                var pct = total > 0 ? used * 100.0 / total : 0;
                text = $"剩余 {Fmt(remain)} / 共 {Fmt(total)}";
                BarTraffic.Value = Math.Min(100, pct);
                if (pct >= 90)
                    BarTraffic.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x6B, 0x6B));
                else if (pct >= 70)
                    BarTraffic.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xC0, 0x50));
                else
                    BarTraffic.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x6C, 0xC3, 0xFF));
            }

            TxtTraffic.Text = text;
            TxtTraffic.ToolTip = null;
            TxtExpire.Text = expireText;
        }

        // ---------- UI 事件 ----------
        private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2) return;
            DragMove();
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAllAsync();

        private void Traffic_Click(object sender, RoutedEventArgs e) => _ = RefreshTrafficAsync();

        private void Pin_Click(object sender, RoutedEventArgs e)
        {
            _topmost = !_topmost;
            Topmost = _topmost;
            BtnPin.Content = _topmost ? "\uE718" : "\uE77A";
            BtnPin.ToolTip = _topmost ? "当前置顶，点击取消" : "当前不置顶，点击置顶";
        }

        private void Minimize_Click(object sender, RoutedEventArgs e) => Hide();

        public WidgetConfig CurrentConfig => _config;

        public void ApplyConfig(WidgetConfig cfg)
        {
            _config = cfg;
            SaveConfig();
            _ = RefreshAllAsync();
        }

        // ---------- 开机自启 ----------
        private static string RunKeyPath => @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string RunKeyName = "DesktopWidget";

        private static bool IsAutoStartEnabled()
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunKeyName) != null;
        }

        private static void SetAutoStart(bool enable)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (enable)
                key.SetValue(RunKeyName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunKeyName, false);
        }

        // ---------- 伪桌面模式：窗口始终沉底，位于所有程序窗口之下、桌面之上 ----------
        private void EmbedInDesktop()
        {
            try
            {
                _progman = FindWindow("Progman", null);
                if (_progman == IntPtr.Zero)
                {
                    TxtStatus.Text = "未找到桌面窗口，保持悬浮模式";
                    return;
                }

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                // WS_EX_NOACTIVATE：点击面板时不抢焦点、不会跳到其他窗口前面
                SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE);

                _embedded = true;
                Topmost = false;
                Show();
                WindowState = WindowState.Normal;
                PositionWindow();
                PinAboveDesktop();

                _pinTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                _pinTimer.Tick += (_, _) => PinAboveDesktop();
                _pinTimer.Start();
                Deactivated += Window_Deactivated;
                UpdateEmbedButton();
            }
            catch
            {
                TxtStatus.Text = "嵌入桌面失败，保持悬浮模式";
            }
        }

        private void Window_Deactivated(object? sender, EventArgs e) => PinAboveDesktop();

        private void PinAboveDesktop()
        {
            try
            {
                if (!_embedded) return;
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (_progman == IntPtr.Zero)
                    _progman = FindWindow("Progman", null);
                if (_progman == IntPtr.Zero) return;

                // 找到 Z 序中紧贴桌面上方的那个窗口，把自己插到它下面（即紧贴桌面之上）
                var aboveDesktop = GetWindow(_progman, GW_HWNDPREV);
                if (aboveDesktop == hwnd) return; // 已在正确位置
                if (aboveDesktop == IntPtr.Zero) return;

                SetWindowPos(hwnd, aboveDesktop, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            catch { }
        }

        private void DetachFromDesktop()
        {
            _pinTimer?.Stop();
            _pinTimer = null;
            Deactivated -= Window_Deactivated;
            _embedded = false;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetWindowLong(hwnd, GWL_EXSTYLE, GetWindowLong(hwnd, GWL_EXSTYLE) & ~WS_EX_NOACTIVATE);
            Topmost = _topmost;
            PositionWindow();
            UpdateEmbedButton();
        }

        private void UpdateEmbedButton()
        {
            BtnEmbed.Content = _embedded ? "\uE8A9" : "\uE7F4";
            BtnEmbed.ToolTip = _embedded ? "退出桌面模式（恢复悬浮置顶）" : "嵌入桌面（不遮挡窗口）";
        }

        private void Embed_Click(object sender, RoutedEventArgs e)
        {
            _config.EmbedDesktop = !_embedded;
            SaveConfig();
            if (_embedded) DetachFromDesktop();
            else EmbedInDesktop();
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            var win = new SettingsWindow(_config, this) { Owner = this };
            win.ShowDialog();
        }

        protected override void OnClosed(EventArgs e)
        {
            _trayIcon?.Dispose();
            base.OnClosed(e);
        }

        // ---------- 工具 ----------
        private static string Fmt(long bytes) => bytes switch
        {
            >= 1L << 40 => $"{bytes / 1024.0 / 1024 / 1024 / 1024:F2} TB",
            >= 1L << 30 => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
            >= 1L << 20 => $"{bytes / 1024.0 / 1024:F1} MB",
            _ => $"{bytes} B"
        };

        private static string Clean(string s) => s.Replace("\r", " ").Replace("\n", " ");
        private static string Truncate(string s, int n) => s.Length <= n ? Clean(s) : Clean(s[..n]) + "…";
    }
}
