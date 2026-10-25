using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace HaTray.Native;

/// <summary>Windows 本地通知；不需要云推送或应用商店。所有导航回到 UI 线程。</summary>
internal sealed class DesktopNotifications(DispatcherQueue ui) : IDisposable
{
    private bool _registered;
    public string? Error { get; private set; }
    public uint LastId { get; private set; }
    public event Action<string, string>? Invoked;
    public event Action? Changed;

    public void Register()
    {
        try
        {
            if (!AppNotificationManager.IsSupported())
            {
                Error = L.T("此系统不支持应用通知。", "App notifications are not supported on this system.");
                return;
            }

            // 必须先订阅回调再注册，否则点击会启动另一个进程（Windows App SDK Register 文档）。
            AppNotificationManager.Default.NotificationInvoked += OnInvoked;
            AppNotificationManager.Default.Register("HA Tray", new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "HaTray.ico")));
            _registered = true;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or NotSupportedException or UnauthorizedAccessException)
        {
            Error = L.T("Windows 通知不可用：", "Windows notifications unavailable: ") + ex.Message;
        }
    }

    public void HandleActivation(AppNotificationActivatedEventArgs args)
    {
        if (args.Arguments.TryGetValue("server", out var server) && args.Arguments.TryGetValue("entity", out var entity))
        {
            ui.TryEnqueue(() => Invoked?.Invoke(server, entity));
        }
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) => HandleActivation(args);

    public bool Show(string title, string text, string server, string entity, string tag)
    {
        try
        {
            if (!_registered)
            {
                Error ??= L.T("Windows 通知未就绪。", "Windows notifications are not ready.");
                return false;
            }

            if (AppNotificationManager.Default.Setting != AppNotificationSetting.Enabled)
            {
                Error = L.T("Windows 已关闭此应用的通知。", "Windows notifications are disabled for this app.");
                return false;
            }

            // Builder 转义 XML 和激活参数；参数只用于导航，不用于执行服务。
            var notification = new AppNotificationBuilder()
                .AddArgument("server", server).AddArgument("entity", entity)
                .AddText(Limit(title, 160)).AddText(Limit(text, 400))
                .AddButton(new AppNotificationButton(L.T("打开", "Open")).AddArgument("server", server).AddArgument("entity", entity))
                .SetGroup(server).SetTag(tag).BuildNotification();
            notification.Expiration = DateTimeOffset.Now.AddDays(1);
            AppNotificationManager.Default.Show(notification);
            LastId = notification.Id;
            Error = notification.Id == 0 ? L.T("Windows 未接受通知。", "Windows did not accept the notification.") : null;
            return notification.Id != 0;
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException or UnauthorizedAccessException)
        {
            Error = L.T("发送通知失败：", "Couldn't send notification: ") + ex.Message;
            return false;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    public async Task RemoveAsync(uint id)
    {
        try
        {
            if (_registered && id != 0)
            {
                await AppNotificationManager.Default.RemoveByIdAsync(id);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            Error = L.T("移除通知失败：", "Couldn't remove notification: ") + ex.Message;
            Changed?.Invoke();
        }
    }

    private static string Limit(string value, int length) => value.Length > length ? value[..length] + "…" : value;

    public void Dispose()
    {
        if (_registered)
        {
            AppNotificationManager.Default.NotificationInvoked -= OnInvoked;
            AppNotificationManager.Default.Unregister();
            _registered = false;
        }
    }
}
