using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HaTray.Model;
using HaTray.Native;
using Microsoft.UI.Dispatching;

namespace HaTray;

internal sealed class ReminderService : IDisposable
{
    private sealed record Archive(Dictionary<string, ReminderMemory> Memory, Dictionary<string, uint> Notifications);
    private readonly Home _home;
    private readonly Settings _settings;
    private readonly DesktopNotifications _notifications;
    private readonly bool _demo;
    private readonly ReminderEngine _engine;
    private readonly Dictionary<string, uint> _active;
    private readonly DispatcherQueueTimer _saveTimer;
    private readonly string _path;
    private bool _dirty;
    private string? _storageError;

    public ReminderService(Home home, Settings settings, DesktopNotifications notifications, DispatcherQueue ui, bool demo)
    {
        _home = home;
        _settings = settings;
        _notifications = notifications;
        _demo = demo;
        Server = ServerKey(demo ? "demo" : settings.Url);
        _path = Path.Combine(Settings.DataDir, $"notification-state-{Server}.json");
        Archive? archive = null;
        try
        {
            if (!demo && File.Exists(_path))
            {
                archive = JsonSerializer.Deserialize<Archive>(File.ReadAllText(_path));
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _storageError = L.T("读取提醒记录失败：", "Couldn't read notification history: ") + ex.Message;
        }

        _engine = new ReminderEngine(archive?.Memory);
        _active = archive?.Notifications ?? [];
        _saveTimer = ui.CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(500);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Save();
        home.StatesChanged += OnStates;
        home.StructureChanged += OnStructure;
        notifications.Changed += NotifyChanged;
    }

    public static string ServerKey(string url) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url.TrimEnd('/'))))[..16];
    public string Server { get; }
    public string? Error => _notifications.Error ?? _storageError;
    public int Triggered { get; private set; }
    public List<object> Recent { get; } = [];
    public event Action? Changed;

    private void NotifyChanged() => Changed?.Invoke();
    private void OnStructure() => OnStates(_home.Entities.Keys.ToArray());

    private void OnStates(IReadOnlyCollection<string> ids)
    {
        if (!_home.HasSnapshot)
        {
            return;
        }

        foreach (var id in ids)
        {
            if (!_home.Entities.TryGetValue(id, out var e))
            {
                continue;
            }

            var key = e.RegistryId ?? e.EntityId;
            _engine.Memory.TryGetValue(key, out var previous);
            var fire = _engine.Observe(e, _home.IsInitialSnapshot);
            if (!_engine.Changed)
            {
                continue;
            }

            _dirty = true;
            uint? previousNotification = _active.Remove(key, out var oldNotification) ? oldNotification : null;
            if ((!_engine.Memory.TryGetValue(key, out var current) || !current.Active) && previousNotification is { } removed)
            {
                _ = _notifications.RemoveAsync(removed);
                previousNotification = null;
            }

            if (!fire || !_settings.RemindersEnabled)
            {
                continue;
            }

            var presenter = new Presenter(_home);
            var value = e.Domain == "event"
                ? presenter.AttrText(e, "event_type", e.Str("event_type") ?? "")
                : presenter.StateText(e);
            var area = _home.Areas.FirstOrDefault(a => a.Id == e.AreaId)?.Name;
            var body = string.IsNullOrEmpty(area) ? value : area + " · " + value;
            if (e.Tray.Reminder?.Attribute is { Length: > 0 } attr && ReminderRule.Scalar(e.Attributes[attr]) is { } raw)
            {
                body += " · " + attr + ": " + presenter.AttrText(e, attr, raw);
            }

            if (!_demo && !_notifications.Show(Presenter.FullName(e), body, Server, id, Guid.NewGuid().ToString("N")[..16]))
            {
                // Windows 拒绝时不吞掉状态条件；下一次有效更新可以重试。
                if (previous is null) _engine.Memory.Remove(key);
                else _engine.Memory[key] = previous;
                if (previousNotification is { } kept)
                {
                    _active[key] = kept;
                }

                continue;
            }

            Triggered++;
            Recent.Add(new { entity = id, state = e.State, value, at = DateTimeOffset.UtcNow });
            if (Recent.Count > 20) Recent.RemoveAt(0);
            if (!_demo && e.Tray.Reminder?.Kind != "event")
            {
                _active[key] = _notifications.LastId;
                if (previousNotification is { } replaced)
                {
                    _ = _notifications.RemoveAsync(replaced);
                }
            }
        }

        // 持续变化时不再反复推迟保存。
        if (_dirty && !_saveTimer.IsRunning)
        {
            _saveTimer.Start();
        }

        Changed?.Invoke();
    }

    public bool Test() => _notifications.Show("HA Tray", L.T("Windows 提醒测试", "Windows notification test"), Server, "", "test");

    private void Save()
    {
        if (!_dirty || _demo) return;
        try
        {
            Directory.CreateDirectory(Settings.DataDir);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Archive(_engine.Memory, _active)));
            File.Move(temp, _path, true);
            _dirty = false;
            _storageError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _storageError = L.T("保存提醒记录失败：", "Couldn't save notification history: ") + ex.Message;
            Changed?.Invoke();
        }
    }

    public void Dispose()
    {
        _home.StatesChanged -= OnStates;
        _home.StructureChanged -= OnStructure;
        _notifications.Changed -= NotifyChanged;
        _saveTimer.Stop();
        Save();
    }
}
