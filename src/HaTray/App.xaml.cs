using HaTray.Ha;
using HaTray.Model;
using HaTray.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace HaTray;

/// <summary>
/// 托盘常驻：没有主窗口，只有托盘图标、弹出面板和设置窗口。只允许一个实例。
/// 参数：--demo 用内置假数据，不连 HA；--debug 打开文字调试通道（见 FlyoutWindow.EnableDebug）；
/// --lang=zh / --lang=en 只在这次运行里固定界面语言，不改设置（tools\make-screenshots.ps1 截中英两套图用）。
/// </summary>
public partial class App : Application
{
    private const int MenuOpen = 1, MenuBrowser = 2, MenuSettings = 3, MenuStartup = 4, MenuExit = 9;

    private readonly bool _demo = Environment.GetCommandLineArgs().Contains("--demo");
    private readonly Home _home = new();
    private Mutex? _singleInstance;
    private Settings _settings = new();
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private IHomeBackend? _backend;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _singleInstance = new Mutex(true, @"Local\HaTray.SingleInstance", out var created);
        if (!created)
        {
            Exit();
            return;
        }

        _settings = Settings.Load();
        L.Init(Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal))?[7..] ?? _settings.Language);

        _flyout = new FlyoutWindow(_settings, _home);
        _flyout.SettingsRequested += OpenSettings;
        _flyout.Prime();
        if (Environment.GetCommandLineArgs().Contains("--debug"))
        {
            _flyout.EnableDebug();
        }

        _tray = new TrayIcon("HA Tray");
        _tray.Invoked += () => _flyout.Toggle(_tray.GetRect());
        _tray.ContextMenuRequested += ShowTrayMenu;

        _ = ConnectAsync();
        if (!_demo && !_settings.IsConfigured)
        {
            OpenSettings();
        }
    }

    /// <summary>按当前设置换一个后端：演示数据，或连 HA。</summary>
    private async Task ConnectAsync()
    {
        var old = _backend;
        _backend = null;
        _flyout!.SetBackend(null);
        if (old is not null)
        {
            await old.DisposeAsync();
        }

        var ui = DispatcherQueue.GetForCurrentThread();
        IHomeBackend? backend = _demo
            ? new DemoBackend(_home, ui, _settings.LabelName)
            : _settings.IsConfigured && _settings.GetToken() is { Length: > 0 } token
                ? new HaSession(_home, ui, new Uri(_settings.Url), token)
                : null;
        _backend = backend;
        _flyout.SetBackend(backend);
        backend?.Start();
    }

    private void OpenSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Saved += reconnect =>
        {
            // 语言马上生效：先按设置定下来，重连后「自动」再按 HA 的语言定一次。
            L.Init(_settings.Language);
            _flyout!.ApplySettings();
            if (reconnect)
            {
                _ = ConnectAsync();
            }
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    private void ShowTrayMenu(int x, int y)
    {
        var cmd = _tray!.ShowMenu(x, y,
        [
            (MenuOpen, L.T("打开面板", "Open panel"), false),
            (MenuBrowser, L.T("在浏览器中打开 Home Assistant", "Open Home Assistant in browser"), false),
            (0, null, false),
            (MenuSettings, L.T("设置…", "Settings…"), false),
            (MenuStartup, L.T("开机时启动", "Start with Windows"), Settings.StartWithWindows),
            (0, null, false),
            (MenuExit, L.T("退出", "Exit"), false),
        ]);
        switch (cmd)
        {
            case MenuOpen:
                _flyout!.ShowFlyout(_tray.GetRect());
                break;
            case MenuBrowser when Uri.TryCreate(_settings.Url, UriKind.Absolute, out var uri):
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                break;
            case MenuSettings:
                OpenSettings();
                break;
            case MenuStartup:
                Settings.StartWithWindows = !Settings.StartWithWindows;
                break;
            case MenuExit:
                Quit();
                break;
        }
    }

    private void Quit()
    {
        _tray?.Dispose();
        _tray = null;
        _singleInstance?.ReleaseMutex();
        Exit();
    }
}
