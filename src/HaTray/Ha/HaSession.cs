using System.Text.Json;
using System.Text.Json.Nodes;
using HaTray.Model;
using Microsoft.UI.Dispatching;

namespace HaTray.Ha;

/// <summary>面板用的后端：真的 HA（<see cref="HaSession"/>）或演示数据（<see cref="DemoBackend"/>）。事件都在 UI 线程上触发。</summary>
internal interface IHomeBackend : IAsyncDisposable
{
    ConnectionStatus Status { get; }

    /// <summary>断开时的原因，给人看的。</summary>
    string? Error { get; }

    /// <summary>注册表、翻译这些都取到了，房间可以显示了。</summary>
    bool Ready { get; }

    event Action? StatusChanged;

    /// <summary>令牌对应的 HA 用户是不是管理员（改标签要管理员）。</summary>
    bool IsAdmin { get; }

    void Start();

    Task CallServiceAsync(string domain, string service, string entityId, JsonObject? data = null);

    /// <summary>给实体加上或去掉某个标签（按名字；标签不存在时先建）。改完 HA 会推注册表更新，界面跟着变。</summary>
    Task SetLabelAsync(string entityId, string labelName, bool on);

    /// <summary>写实体注册表的 options.ha_tray（排序、磁贴大小）。全部为空时删掉这一项。</summary>
    Task SetTrayOptionsAsync(string entityId, TrayOptions options);

    /// <summary>改实体注册表里的名字或图标（field 是 name / icon；null 表示恢复默认）。和在 HA 里改一样。</summary>
    Task UpdateEntityAsync(string entityId, string field, string? value);

    /// <summary>改区域设置里的温度、湿度传感器（field 是 temperature_entity_id / humidity_entity_id；null 表示清空）。</summary>
    Task SetAreaSensorAsync(string areaId, string field, string? entityId);
}

/// <summary>
/// 连着 HA 的后端：每次连上（包括断线重连）都把全部数据重取一遍，再订阅实体状态和注册表变化。
/// 用到的接口见 docs/design.md 第 2 节。
/// </summary>
internal sealed class HaSession : IHomeBackend
{
    private static readonly string[] RegistryEvents =
        ["area_registry_updated", "floor_registry_updated", "device_registry_updated", "entity_registry_updated", "label_registry_updated"];

    private readonly Home _home;
    private readonly DispatcherQueue _ui;
    private readonly HaConnection _conn;
    private volatile int _entitiesSub = -1;
    private Dictionary<int, string> _registrySubs = [];
    private readonly HashSet<string> _refetchPending = [];

    public HaSession(Home home, DispatcherQueue ui, Uri url, string token)
    {
        _home = home;
        _ui = ui;
        _conn = new HaConnection(url, token);
        _conn.Connected += OnConnectedAsync;
        _conn.EventReceived += OnEvent;
        _conn.StatusChanged += (status, error) => _ui.TryEnqueue(() =>
        {
            Status = status;
            Error = error;
            StatusChanged?.Invoke();
        });
    }

    public ConnectionStatus Status { get; private set; } = ConnectionStatus.Connecting;

    public string? Error { get; private set; }

    public bool Ready { get; private set; }

    public bool IsAdmin { get; private set; }

    public event Action? StatusChanged;

    public void Start() => _conn.Start();

    public Task CallServiceAsync(string domain, string service, string entityId, JsonObject? data = null) =>
        _conn.CallServiceAsync(domain, service, entityId, data);

    /// <summary>config/label_registry/create（没有这个标签时）+ config/entity_registry/update（标签列表整个替换，保留实体原有的其他标签）。</summary>
    public async Task SetLabelAsync(string entityId, string labelName, bool on)
    {
        var labelId = _home.Labels.GetValueOrDefault(labelName);
        if (labelId is null)
        {
            if (!on)
            {
                return;
            }

            var created = await Cmd("config/label_registry/create", new JsonObject { ["name"] = labelName });
            labelId = J.Str(created, "label_id")!;
        }

        var current = _home.Entities.TryGetValue(entityId, out var e) ? e.Labels : [];
        var labels = on ? current.Append(labelId).Distinct() : current.Where(l => l != labelId);
        await Cmd("config/entity_registry/update", new JsonObject
        {
            ["entity_id"] = entityId,
            ["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)l)]),
        });
    }

    public Task SetTrayOptionsAsync(string entityId, TrayOptions options) =>
        Cmd("config/entity_registry/update", new JsonObject
        {
            ["entity_id"] = entityId,
            ["options_domain"] = TrayOptions.Domain,
            ["options"] = options.ToJson(),
        });

    public Task UpdateEntityAsync(string entityId, string field, string? value) =>
        Cmd("config/entity_registry/update", new JsonObject { ["entity_id"] = entityId, [field] = value });

    public Task SetAreaSensorAsync(string areaId, string field, string? entityId) =>
        Cmd("config/area_registry/update", new JsonObject { ["area_id"] = areaId, [field] = entityId });

    public ValueTask DisposeAsync() => _conn.DisposeAsync();

    private Task<JsonElement> Cmd(string type, JsonObject? extra = null)
    {
        var o = new JsonObject { ["type"] = type };
        foreach (var (k, v) in extra ?? [])
        {
            o[k] = v?.DeepClone();
        }

        return _conn.SendCommandAsync(o);
    }

    /// <summary>后台线程。全部取完再一次性交给 UI 线程，界面不会看到一半的数据。</summary>
    private async Task OnConnectedAsync(HaConnection c)
    {
        _entitiesSub = -1;

        // 先拿 HA 的语言：界面语言选「自动」时跟着它走，翻译也按它要（见 L）。
        var config = await Cmd("get_config").ConfigureAwait(false);
        var haLanguage = J.Str(config, "language");
        var translationLanguage = L.ChineseFor(haLanguage) ? "zh-Hans" : "en";

        var areas = Cmd("config/area_registry/list");
        var floors = Cmd("config/floor_registry/list");
        var devices = Cmd("config/device_registry/list");
        var entities = Cmd("config/entity_registry/list");
        var labels = Cmd("config/label_registry/list");
        var services = Cmd("get_services");
        var trComponent = Cmd("frontend/get_translations", new JsonObject { ["language"] = translationLanguage, ["category"] = "entity_component" });
        var trEntity = Cmd("frontend/get_translations", new JsonObject { ["language"] = translationLanguage, ["category"] = "entity" });
        var iconsComponent = Cmd("frontend/get_icons", new JsonObject { ["category"] = "entity_component" });
        var user = Cmd("auth/current_user");
        await Task.WhenAll(areas, floors, devices, entities, labels, services, trComponent, trEntity, iconsComponent, user).ConfigureAwait(false);
        var isAdmin = user.Result.TryGetProperty("is_admin", out var admin) && admin.ValueKind == JsonValueKind.True;

        // 集成的图标规则按集成要：注册表里出现过的集成都要一份。
        var platforms = new JsonArray([.. entities.Result.EnumerateArray()
            .Select(e => J.Str(e, "platform")).Where(p => p is not null).Distinct().Select(p => (JsonNode)p!)]);
        var iconsEntity = await Cmd("frontend/get_icons", new JsonObject { ["category"] = "entity", ["integration"] = platforms }).ConfigureAwait(false);

        var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _ui.TryEnqueue(() =>
        {
            L.UseHaLanguage(haLanguage);
            _home.SetAreas(areas.Result);
            _home.SetFloors(floors.Result);
            _home.SetDevices(devices.Result);
            _home.SetLabels(labels.Result);
            _home.SetEntityRegistry(entities.Result);
            _home.SetServices(services.Result);
            _home.Translations.Clear();
            _home.Translations.Add(trComponent.Result);
            _home.Translations.Add(trEntity.Result);
            _home.Icons.Clear();
            _home.Icons.AddComponent(iconsComponent.Result);
            _home.Icons.AddEntity(iconsEntity);
            _home.ClearStates();
            IsAdmin = isAdmin;
            Ready = true;
            _home.NotifyStructureChanged();
            loaded.SetResult();
        });
        await loaded.Task.ConfigureAwait(false);

        // 订阅放在数据交给 UI 之后：状态事件排在注册表后面处理。
        // 先领好 id 再发，回包后紧跟着来的第一条事件才认得出是谁的。
        var subs = RegistryEvents.ToDictionary(_ => c.NextId());
        _registrySubs = subs;
        foreach (var (id, type) in subs)
        {
            await c.SendCommandAsync(new JsonObject { ["id"] = id, ["type"] = "subscribe_events", ["event_type"] = type }).ConfigureAwait(false);
        }

        var entitiesId = c.NextId();
        _entitiesSub = entitiesId;
        await c.SendCommandAsync(new JsonObject { ["id"] = entitiesId, ["type"] = "subscribe_entities" }).ConfigureAwait(false);
    }

    /// <summary>后台线程。</summary>
    private void OnEvent(int id, JsonElement ev)
    {
        if (id == _entitiesSub)
        {
            _ui.TryEnqueue(() => _home.ApplyEntitiesEvent(ev));
        }
        else if (_registrySubs.TryGetValue(id, out var type))
        {
            _ = RefetchAsync(type);
        }
    }

    /// <summary>
    /// 某个注册表变了：只重取那一份。同一类事件 300 毫秒内连着来（比如拖动排序一次改好几个实体）只重取一次。
    /// </summary>
    private async Task RefetchAsync(string eventType)
    {
        lock (_refetchPending)
        {
            if (!_refetchPending.Add(eventType))
            {
                return;
            }
        }

        try
        {
            await Task.Delay(300).ConfigureAwait(false);
            lock (_refetchPending)
            {
                _refetchPending.Remove(eventType);
            }

            var command = eventType switch
            {
                "area_registry_updated" => "config/area_registry/list",
                "floor_registry_updated" => "config/floor_registry/list",
                "device_registry_updated" => "config/device_registry/list",
                "entity_registry_updated" => "config/entity_registry/list",
                _ => "config/label_registry/list",
            };
            var result = await Cmd(command).ConfigureAwait(false);
            _ui.TryEnqueue(() =>
            {
                switch (eventType)
                {
                    case "area_registry_updated":
                        _home.SetAreas(result);
                        break;
                    case "floor_registry_updated":
                        _home.SetFloors(result);
                        break;
                    case "device_registry_updated":
                        _home.SetDevices(result);
                        break;
                    case "entity_registry_updated":
                        _home.SetEntityRegistry(result);
                        break;
                    default:
                        _home.SetLabels(result);
                        break;
                }

                _home.NotifyStructureChanged();
            });
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or HaCommandException or InvalidOperationException)
        {
            // 断线了：重连时会全部重取。
        }
    }
}
