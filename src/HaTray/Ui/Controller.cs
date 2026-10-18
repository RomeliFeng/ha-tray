using System.Text.Json.Nodes;
using HaTray.Ha;
using HaTray.Model;
using Microsoft.UI.Dispatching;

namespace HaTray.Ui;

/// <summary>
/// 发控制命令，并记下每个实体的「等待中」和「出错」：
/// - 发出去以后不改本地状态，图标进入等待中，等 HA 推回这个实体的新状态才结束；5 秒没等到就恢复原样，状态行写「没有响应」。
/// - ± 这类连点的操作，数字先在本地变，停手 1 秒后才发一次（HA 前端调温度、湿度也是 1 秒）。
/// </summary>
internal sealed class Controller(DispatcherQueue ui, Func<IHomeBackend?> backend)
{
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    private readonly Dictionary<string, DispatcherQueueTimer> _waiting = [];
    private readonly Dictionary<string, string> _problems = [];
    private readonly Dictionary<(string Id, string Key), (double Value, DispatcherQueueTimer Timer)> _pendingValues = [];

    /// <summary>某个实体的等待中 / 出错状态变了，界面要重画它。</summary>
    public event Action<string>? Changed;

    public bool IsWaiting(string id) => _waiting.ContainsKey(id);

    public string? Problem(string id) => _problems.GetValueOrDefault(id);

    /// <summary>± 还没发出去的本地值。</summary>
    public double? PendingValue(string id, string key) => _pendingValues.TryGetValue((id, key), out var v) ? v.Value : null;

    /// <summary>HA 推回了这些实体的新状态。</summary>
    public void OnStatesChanged(IReadOnlyCollection<string> ids)
    {
        foreach (var id in ids)
        {
            if (_waiting.Remove(id, out var t))
            {
                t.Stop();
            }

            _problems.Remove(id);
        }
    }

    /// <summary>发一条服务调用。wait 为 true 时图标进入等待中。</summary>
    public void Call(Entity e, string domain, string service, JsonObject? data = null, bool wait = true)
    {
        if (backend() is not { } b)
        {
            return;
        }

        _problems.Remove(e.EntityId);
        if (wait)
        {
            StartWaiting(e.EntityId);
        }

        _ = SendAsync(b, e.EntityId, domain, service, data);
    }

    public void Call(Entity e, string service, JsonObject? data = null, bool wait = true) => Call(e, e.Domain, service, data, wait);

    /// <summary>连点的数值：current 是现在显示的值，delta 是这一下的变化。返回新值；停手 1 秒后调 send(新值)。</summary>
    public double Nudge(Entity e, string key, double current, double delta, double min, double max, Action<double> send)
    {
        var start = PendingValue(e.EntityId, key) ?? current;
        var value = Math.Clamp(Math.Round((start + delta) * 1000) / 1000, min, max);
        if (_pendingValues.TryGetValue((e.EntityId, key), out var old))
        {
            old.Timer.Stop();
        }

        var timer = ui.CreateTimer();
        timer.Interval = DebounceDelay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            _pendingValues.Remove((e.EntityId, key));
            send(value);
        };
        _pendingValues[(e.EntityId, key)] = (value, timer);
        timer.Start();
        Changed?.Invoke(e.EntityId);
        return value;
    }

    private void StartWaiting(string id)
    {
        if (_waiting.Remove(id, out var old))
        {
            old.Stop();
        }

        var timer = ui.CreateTimer();
        timer.Interval = WaitLimit;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            if (_waiting.Remove(id))
            {
                _problems[id] = L.T("没有响应", "No response");
                Changed?.Invoke(id);
            }
        };
        _waiting[id] = timer;
        timer.Start();
        Changed?.Invoke(id);
    }

    private async Task SendAsync(IHomeBackend b, string id, string domain, string service, JsonObject? data)
    {
        try
        {
            await b.CallServiceAsync(domain, service, id, data);
        }
        catch (Exception ex) when (ex is HaCommandException or IOException or TimeoutException or InvalidOperationException)
        {
            ui.TryEnqueue(() =>
            {
                if (_waiting.Remove(id, out var t))
                {
                    t.Stop();
                }

                _problems[id] = L.T("失败：", "Failed: ") + ex.Message;
                Changed?.Invoke(id);
            });
        }
    }
}

/// <summary>
/// 磁贴图标按钮的主动作，照 HA 前端：DOMAINS_TOGGLE 里的类型用 toggle，其余照 get_toggle_action.ts 的 SPECIAL_TOGGLE_ACTIONS，
/// 都没有时，这个类型同时有 turn_on、turn_off 服务且状态是 on / off 才给开关（canToggleDomain）。
/// </summary>
internal static class PrimaryAction
{
    private static readonly HashSet<string> ToggleDomains = ["light", "switch", "input_boolean", "fan", "humidifier", "automation", "group", "valve", "remote", "siren"];

    /// <summary>返回要调的服务；null 表示没有主动作。</summary>
    public static (string Service, JsonObject? Data)? For(Entity e, Home home)
    {
        if (e.IsUnavailable)
        {
            return null;
        }

        var off = !Presenter.IsActive(e);
        switch (e.Domain)
        {
            case "button" or "input_button":
                return ("press", null);
            case "scene":
                return ("turn_on", null);
            case "script":
                return (e.State == "on" ? "turn_off" : "turn_on", null);
            case "lock":
                return (e.State == "locked" ? "unlock" : "lock", null);
            case "cover" when e.Supports(Caps.CoverOpen | Caps.CoverClose):
                return (e.State == "closed" ? "open_cover" : "close_cover", null);
            case "valve" when e.Supports(1 | 2):
                return (e.State == "closed" ? "open_valve" : "close_valve", null);
            case "climate" when e.Supports(Caps.ClimateTurnOn | Caps.ClimateTurnOff):
                return (off ? "turn_on" : "turn_off", null);
            case "media_player" when e.Supports(Caps.MediaTurnOn | Caps.MediaTurnOff):
                return (off ? "turn_on" : "turn_off", null);
            case "water_heater" when e.Supports(Caps.WaterHeaterOnOff):
                return (e.State == "off" ? "turn_on" : "turn_off", null);
            case "vacuum":
                return e.State == "cleaning"
                    ? e.Supports(Caps.VacuumPause) ? ("pause", null) : null
                    : e.Supports(Caps.VacuumStart) ? ("start", null) : null;
            case "lawn_mower":
                return e.State == "mowing"
                    ? e.Supports(Caps.LawnMowerPause) ? ("pause", null) : null
                    : e.Supports(Caps.LawnMowerStart) ? ("start_mowing", null) : null;
            case "climate" or "cover" or "media_player":
                return null;
        }

        if (ToggleDomains.Contains(e.Domain) && e.State is "on" or "off")
        {
            return home.HasService(e.Domain, "toggle") ? ("toggle", null) : (e.State == "on" ? "turn_off" : "turn_on", null);
        }

        return e.State is "on" or "off" && home.HasService(e.Domain, "turn_on") && home.HasService(e.Domain, "turn_off")
            ? (e.State == "on" ? "turn_off" : "turn_on", null)
            : null;
    }
}
