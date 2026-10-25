using HaTray.Ha;
using HaTray.Model;
using HaTray.Native;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;
using Windows.ApplicationModel.Activation;

namespace HaTray;

/// <summary>
/// 托盘常驻：没有主窗口，只有托盘图标、弹出面板和设置窗口。只允许一个实例。
/// 参数：--demo 用内置假数据，不连 HA；--debug 打开文字调试通道（见 FlyoutWindow.EnableDebug）；
/// --lang=zh / --lang=en 只在这次运行里固定界面语言，不改设置（tools\make-screenshots.ps1 截中英两套图用）。
/// </summary>
public partial class App : Application
{
    private const int MenuOpen = 1, MenuBrowser = 2, MenuSettings = 3, MenuStartup = 4, MenuReminders = 5, MenuAbout = 6, MenuExit = 9;

    private readonly bool _demo = Environment.GetCommandLineArgs().Contains("--demo");
    private readonly Home _home = new();
    private AppInstance? _instance;
    private DesktopNotifications? _notifications;
    private ReminderService? _reminders;
    private RemindersWindow? _remindersWindow;
    private Settings _settings = new();
    private TrayIcon? _tray;
    private FlyoutWindow? _flyout;
    private SettingsWindow? _settingsWindow;
    private AboutWindow? _aboutWindow;
    private IHomeBackend? _backend;

    public App()
    {
        InitializeComponent();

        // 没人处理的异常写进 %LOCALAPPDATA%\HaTray\crash.log（程序照样退出），排查崩溃用。
        UnhandledException += (_, e) =>
        {
            try
            {
                Directory.CreateDirectory(Settings.DataDir);
                File.AppendAllText(Path.Combine(Settings.DataDir, "crash.log"), $"{DateTime.Now:O}\n{e.Exception}\n{e.Message}\n\n");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        };
    }

    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        var ui = DispatcherQueue.GetForCurrentThread();
        _instance = AppInstance.FindOrRegisterForKey("HaTray");
        if (!_instance.IsCurrent)
        {
            await _instance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        _instance.Activated += (_, activation) => ui.TryEnqueue(() => HandleActivation(activation));
        _settings = Settings.Load();
        L.Init(Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--lang=", StringComparison.Ordinal))?[7..] ?? _settings.Language);

        _notifications = new DesktopNotifications(ui);
        _notifications.Invoked += OpenNotification;
        _notifications.Register();

        _flyout = new FlyoutWindow(_settings, _home);
        _flyout.SettingsRequested += OpenSettings;
        _flyout.RemindersRequested += OpenReminders;
        _flyout.RemindersPageRequested += OpenRemindersPage;
        _flyout.ReminderDebug = () => new { error = _reminders?.Error, triggered = _reminders?.Triggered, recent = _reminders?.Recent,
            notificationId = _notifications.LastId, window = _remindersWindow?.DebugReport };
        _flyout.TestReminder = () => _reminders?.Test() == true;
        _flyout.NotificationTarget = entity => _demo && (_notifications?.Show("HA Tray", entity, ReminderService.ServerKey("demo"), entity, "target") ?? false);
        _flyout.Prime();
        if (Environment.GetCommandLineArgs().Contains("--debug"))
        {
            _flyout.EnableDebug();
        }

        _tray = new TrayIcon("HA Tray");
        _tray.Invoked += () => _flyout.Toggle(_tray.GetRect());
        _tray.ContextMenuRequested += ShowTrayMenu;

        _ = ConnectAsync();
        HandleActivation(AppInstance.GetCurrent().GetActivatedEventArgs());
        if (!_demo && !_settings.IsConfigured)
        {
            OpenSettings();
        }
    }

    /// <summary>按当前设置换一个后端：演示数据，或连 HA。</summary>
    private async Task ConnectAsync()
    {
        _remindersWindow?.Close();
        _remindersWindow = null;
        _reminders?.Dispose();
        _reminders = null;
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
        _reminders = new ReminderService(_home, _settings, _notifications!, ui, _demo);
        _flyout.SetBackend(backend);
        backend?.Start();
    }

    private void HandleActivation(AppActivationArguments args)
    {
        if (args.Kind == ExtendedActivationKind.AppNotification && args.Data is AppNotificationActivatedEventArgs notification)
        {
            _notifications?.HandleActivation(notification);
        }
        else if (args.Data is ILaunchActivatedEventArgs launch)
        {
            var target = launch.Arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault(a => a.StartsWith("--open-entity=", StringComparison.Ordinal));
            if (target is not null)
            {
                OpenNotification(ReminderService.ServerKey(_demo ? "demo" : _settings.Url), target[14..]);
            }
        }
    }

    private void OpenNotification(string server, string entity)
    {
        if (server != ReminderService.ServerKey(_demo ? "demo" : _settings.Url))
        {
            _flyout?.ShowNotice(L.T("这条提醒来自其他 Home Assistant。", "This notification belongs to another Home Assistant."), _tray?.GetRect());
        }
        else if (entity.Length == 0)
        {
            OpenReminders(null);
        }
        else
        {
            _flyout?.ShowEntity(entity, _tray?.GetRect());
        }
    }

    private void OpenReminders(Entity? entity)
    {
        if (_reminders is null) return;
        if (_remindersWindow is null)
        {
            _remindersWindow = new RemindersWindow(_settings, _home, () => _backend, _reminders);
            _remindersWindow.Closed += (_, _) => _remindersWindow = null;
        }
        if (entity is not null) _remindersWindow.Select(entity);
        _remindersWindow.Activate();
    }

    private void OpenRemindersPage(string page)
    {
        OpenReminders(null);
        if (_remindersWindow is null) return;
        if (page == "source") _remindersWindow.ShowSourcePage();
        else _remindersWindow.ShowListPage();
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
            (MenuBrowser, L.T("打开 Home Assistant", "Open Home Assistant"), false),
            (0, null, false),
            (MenuReminders, L.T("提醒…", "Notifications…"), false),
            (MenuSettings, L.T("设置…", "Settings…"), false),
            (MenuStartup, L.T("开机时启动", "Start with Windows"), Settings.StartWithWindows),
            (MenuAbout, L.T("关于 HA Tray", "About HA Tray"), false),
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
            case MenuReminders:
                OpenReminders(null);
                break;
            case MenuSettings:
                OpenSettings();
                break;
            case MenuStartup:
                Settings.StartWithWindows = !Settings.StartWithWindows;
                break;
            case MenuAbout:
                ShowAbout();
                break;
            case MenuExit:
                Quit();
                break;
        }
    }

    private void ShowAbout()
    {
        if (_aboutWindow is null)
        {
            _aboutWindow = new AboutWindow();
            _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        }

        _aboutWindow.Activate();
    }

    private async void Quit()
    {
        _remindersWindow?.Close();
        _reminders?.Dispose();
        if (_backend is not null) await _backend.DisposeAsync();
        _notifications?.Dispose();
        _tray?.Dispose();
        _tray = null;
        _instance?.UnregisterKey();
        Exit();
    }
}
