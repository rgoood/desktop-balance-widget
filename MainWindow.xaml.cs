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

        [JsonPropertyName("airport_subscription_url")]
        public string AirportSubscriptionUrl { get; set; } = "";

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

        // ---------- Win32：嵌入桌面 ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string cls, string? title);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

        private const uint WM_SPAWN_WORKERW = 0x052C;

        public MainWindow()
        {
            InitializeComponent();
            _configPath = Path.Combine(_configDir, "config.json");

            LoadConfig();
            InitTrayIcon();

            PositionWindow();
            StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };

            _clock.Tick += async (_, _) => await OnTickAsync();
            _clock.Start();

            Loaded += (_, _) =>
            {
                if (_config.EmbedDesktop) EmbedInDesktop();
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
            Activate();
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
            var t0 = Task.Run(() => QueryOpenRouterBalanceAsync());
            var t1 = Task.Run(() => QueryAirportTrafficAsync());
            await Task.WhenAll(t0, t1);

            SetBalanceUi(t0.Result);
            SetTrafficUi(t1.Result);
            if (t0.Result.Ok && t1.Result.Ok)
                TxtStatus.Text = $"更新于 {DateTime.Now:HH:mm:ss}";

            var interval = TimeSpan.FromHours(Math.Max(0.1, _config.RefreshIntervalHours));
            if (interval.TotalMinutes < 1) interval = TimeSpan.FromMinutes(1);
            _nextRefresh = DateTime.Now.Add(interval);
            TxtNext.Text = $"下次更新 {_nextRefresh:HH:mm}";
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

        // ---------- 机场流量（订阅 subscription-userinfo 头）----------
        private record TrafficResult(bool Ok, string Text, long? Used, long? Total, DateTimeOffset? Expire, string Detail);

        private async Task<TrafficResult> QueryAirportTrafficAsync()
        {
            if (string.IsNullOrWhiteSpace(_config.AirportSubscriptionUrl))
                return new TrafficResult(false, "--", null, null, null, "未配置订阅链接，请点齿轮设置");

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, _config.AirportSubscriptionUrl.Trim());
                req.Headers.UserAgent.ParseAdd("clash-verge/v1.6.0");
                using var resp = await Http.SendAsync(req);
                var body = await resp.Content.ReadAsStringAsync();

                if (!resp.IsSuccessStatusCode)
                    return new TrafficResult(false, "错误", null, null, null, $"订阅请求失败 HTTP {(int)resp.StatusCode}：{Truncate(body, 120)}");

                if (!resp.Headers.TryGetValues("subscription-userinfo", out var values))
                    return new TrafficResult(false, "--", null, null, null,
                        "响应中没有 subscription-userinfo 头。若机场提示“订阅开关已关闭”，请先到官网临时打开开关再刷新。" +
                        Truncate(body, 80));

                var header = string.Join(";", values);
                long upload = 0, download = 0, total = 0, expire = 0;
                foreach (var part in header.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var kv = part.Split('=', 2);
                    if (kv.Length != 2) continue;
                    if (!long.TryParse(kv[1].Trim(), out var v)) continue;
                    switch (kv[0].Trim().ToLowerInvariant())
                    {
                        case "upload": upload = v; break;
                        case "download": download = v; break;
                        case "total": total = v; break;
                        case "expire": expire = v; break;
                    }
                }

                var used = upload + download;
                DateTimeOffset? expireDate = expire > 0 ? DateTimeOffset.FromUnixTimeSeconds(expire) : null;
                return new TrafficResult(true, "", used, total, expireDate, "");
            }
            catch (Exception ex)
            {
                return new TrafficResult(false, "--", null, null, null, "订阅请求异常：" + Truncate(ex.Message, 120));
            }
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

        // ---------- 嵌入桌面 ----------
        private void EmbedInDesktop()
        {
            try
            {
                var progman = FindWindow("Progman", null);
                SendMessageTimeout(progman, WM_SPAWN_WORKERW, IntPtr.Zero, IntPtr.Zero, 0x0002, 1000, out _);

                IntPtr defViewHost = IntPtr.Zero, wallpaper = IntPtr.Zero;
                EnumWindows((h, _) =>
                {
                    var sb = new StringBuilder(64);
                    GetClassName(h, sb, 64);
                    if (sb.ToString() == "WorkerW")
                    {
                        if (FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero)
                            defViewHost = h;
                        else if (defViewHost != IntPtr.Zero && wallpaper == IntPtr.Zero)
                            wallpaper = h;
                    }
                    return wallpaper == IntPtr.Zero;
                }, IntPtr.Zero);

                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                if (wallpaper == IntPtr.Zero || SetParent(hwnd, wallpaper) == IntPtr.Zero)
                {
                    TxtStatus.Text = "嵌入桌面失败，保持悬浮模式";
                    return;
                }

                _embedded = true;
                Topmost = false;
                PositionWindow();
                UpdateEmbedButton();
            }
            catch
            {
                TxtStatus.Text = "嵌入桌面失败，保持悬浮模式";
            }
        }

        private void DetachFromDesktop()
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetParent(hwnd, IntPtr.Zero);
            _embedded = false;
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
