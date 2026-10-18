using System.Runtime.InteropServices;
using HaTray.Ha;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using static HaTray.Native.Win32;

namespace HaTray;

/// <summary>
/// HA 地址、令牌、主界面标签、界面语言、面板尺寸、开机启动。
/// 地址或令牌改了，保存前先真连一次，连不上或令牌不对就留在窗口里说清楚。
/// </summary>
internal sealed partial class SettingsWindow : Window
{
    private static readonly string[] Languages = ["auto", "zh", "en"];
    private readonly Settings _settings;

    public SettingsWindow(Settings settings)
    {
        InitializeComponent();
        _settings = settings;
        SystemBackdrop = new MicaBackdrop();
        Title = L.T("HA Tray 设置", "HA Tray settings");
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "HaTray.ico"));
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsResizable = false;
        }

        TitleText.Text = L.T("HA Tray 设置", "HA Tray settings");
        UrlBox.Header = L.T("Home Assistant 地址", "Home Assistant address");
        UrlBox.Text = settings.Url;
        TokenBox.Header = L.T("长期访问令牌", "Long-lived access token");
        TokenBox.PlaceholderText = settings.ProtectedToken.Length > 0 ? L.T("已保存，留空不改", "Saved. Leave empty to keep it") : "";
        TokenHelp.Text = L.T(
            "在 Home Assistant 的个人资料 →「安全」→「长期访问令牌」里创建",
            "Create one in Home Assistant under Profile → Security → Long-lived access tokens");
        LabelBox.Header = L.T("主界面标签", "Panel label");
        LabelBox.Text = settings.LabelName;
        LanguageBox.Header = L.T("界面语言", "Language");
        LanguageBox.Items.Add(L.T("自动（跟随 Home Assistant）", "Auto (same as Home Assistant)"));
        LanguageBox.Items.Add("中文");
        LanguageBox.Items.Add("English");
        LanguageBox.SelectedIndex = Math.Max(0, Array.IndexOf(Languages, settings.Language));
        WidthBox.Header = L.T("面板宽度", "Panel width");
        WidthBox.Value = settings.Width;
        HeightBox.Header = L.T("面板高度", "Panel height");
        HeightBox.Value = settings.Height;
        StartupSwitch.Header = L.T("开机时启动", "Start with Windows");
        StartupSwitch.IsOn = Settings.StartWithWindows;
        SaveButton.Content = L.T("保存", "Save");
        CancelButton.Content = L.T("取消", "Cancel");

        // 按屏幕缩放定尺寸，放在鼠标所在屏幕的中间。
        GetCursorPos(out var pt);
        var monitor = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
        GetDpiForMonitor(monitor, 0, out var dpi, out _);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        GetMonitorInfo(monitor, ref info);
        var scale = dpi / 96.0;
        var size = new SizeInt32((int)(500 * scale), (int)(680 * scale));
        var work = info.rcWork;
        AppWindow.MoveAndResize(new RectInt32(
            work.Left + ((work.Right - work.Left - size.Width) / 2),
            work.Top + ((work.Bottom - work.Top - size.Height) / 2),
            size.Width,
            size.Height));
    }

    /// <summary>保存成功。参数表示地址或令牌变了，要重连。</summary>
    public event Action<bool>? Saved;

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        ErrorText.Text = "";
        var url = UrlBox.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            ErrorText.Text = L.T("地址要以 http:// 或 https:// 开头，比如 http://homeassistant.local:8123", "Enter an address starting with http:// or https://, like http://homeassistant.local:8123");
            return;
        }

        var newToken = TokenBox.Password.Trim();
        var token = newToken.Length > 0 ? newToken : _settings.GetToken();
        if (token.Length == 0)
        {
            ErrorText.Text = L.T("填上长期访问令牌", "Enter a long-lived access token");
            return;
        }

        var label = LabelBox.Text.Trim();
        if (label.Length == 0)
        {
            ErrorText.Text = L.T("填上主界面标签的名字", "Enter the label name");
            return;
        }

        // 语言改了也重连：翻译要按新语言重取。
        var language = Languages[Math.Max(0, LanguageBox.SelectedIndex)];
        var reconnect = url != _settings.Url || newToken.Length > 0 || language != _settings.Language;
        if (reconnect)
        {
            SaveButton.IsEnabled = false;
            Busy.IsActive = true;
            var error = await HaConnection.TestAsync(uri, token);
            Busy.IsActive = false;
            SaveButton.IsEnabled = true;
            if (error is not null)
            {
                ErrorText.Text = error;
                return;
            }
        }

        _settings.Url = url;
        if (newToken.Length > 0)
        {
            _settings.SetToken(newToken);
        }

        _settings.LabelName = label;
        _settings.Language = language;
        _settings.Width = double.IsNaN(WidthBox.Value) ? 400 : (int)WidthBox.Value;
        _settings.Height = double.IsNaN(HeightBox.Value) ? 640 : (int)HeightBox.Value;
        _settings.Save();
        try
        {
            Settings.StartWithWindows = StartupSwitch.IsOn;
        }
        catch (UnauthorizedAccessException)
        {
        }

        Saved?.Invoke(reconnect);
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
}
