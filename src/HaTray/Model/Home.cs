using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaTray.Model;

/// <summary>
/// HA 那边的全部数据：区域、楼层、设备、实体注册表、标签、服务、翻译、图标规则和实体状态。
/// 数据都是 HA 原样给的，这里只做拼接，不做筛选；显示规则在 <see cref="Presenter"/> 和 <see cref="RoomBuilder"/>。
/// 只在 UI 线程上用。
/// </summary>
internal sealed class Home
{
    private readonly Dictionary<string, Entity> _entities = [];
    private Dictionary<string, Device> _devices = [];
    private HashSet<string> _services = [];

    public IReadOnlyList<Area> Areas { get; private set; } = [];

    public IReadOnlyDictionary<string, Floor> Floors { get; private set; } = new Dictionary<string, Floor>();

    public IReadOnlyDictionary<string, Device> Devices => _devices;

    public IReadOnlyDictionary<string, Entity> Entities => _entities;

    /// <summary>标签名 → 标签 id。</summary>
    public IReadOnlyDictionary<string, string> Labels { get; private set; } = new Dictionary<string, string>();

    public Translations Translations { get; } = new();

    public IconRules Icons { get; } = new();

    /// <summary>有没有某个服务，比如 ("light", "turn_on")。</summary>
    public bool HasService(string domain, string service) => _services.Contains(domain + "." + service);

    /// <summary>区域、设备、实体注册表、标签这类结构变了，房间要重排。</summary>
    public event Action? StructureChanged;

    /// <summary>这些实体的状态变了。</summary>
    public event Action<IReadOnlyCollection<string>>? StatesChanged;

    public bool HasSnapshot { get; private set; }

    /// <summary>仅在首批 subscribe_entities 回调期间为 true；旧事件不能重放。</summary>
    public bool IsInitialSnapshot { get; private set; }

    public event Action? StatesReset;

    public void SetAreas(JsonElement areas)
    {
        Areas = [.. areas.EnumerateArray().Select(a => new Area(
            J.Str(a, "area_id")!, J.Str(a, "name")?.Trim() ?? "", J.Str(a, "floor_id"), J.Str(a, "icon"),
            J.Str(a, "temperature_entity_id"), J.Str(a, "humidity_entity_id")))];
    }

    /// <summary>本地先改一个区域（写回 HA 之后，HA 推回的区域列表会再覆盖一次，值相同）。</summary>
    public void PatchArea(string id, Func<Area, Area> patch) =>
        Areas = [.. Areas.Select(a => a.Id == id ? patch(a) : a)];

    public void SetFloors(JsonElement floors)
    {
        Floors = floors.EnumerateArray()
            .Select(f => new Floor(J.Str(f, "floor_id")!, J.Str(f, "name")?.Trim() ?? "", f.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : null))
            .ToDictionary(f => f.Id);
    }

    public void SetDevices(JsonElement devices)
    {
        _devices = devices.EnumerateArray()
            .Select(d => new Device(J.Str(d, "id")!, (J.Str(d, "name_by_user") ?? J.Str(d, "name") ?? "").Trim(), J.Str(d, "area_id")))
            .ToDictionary(d => d.Id);
        Relink();
    }

    public void SetLabels(JsonElement labels)
    {
        var map = new Dictionary<string, string>();
        foreach (var l in labels.EnumerateArray())
        {
            if (J.Str(l, "name") is { } name && J.Str(l, "label_id") is { } id)
            {
                map.TryAdd(name, id);
            }
        }

        Labels = map;
    }

    /// <summary>config/entity_registry/list。注册表里有、还没有状态的实体也建出来。</summary>
    public void SetEntityRegistry(JsonElement entries)
    {
        var seen = new HashSet<string>();
        foreach (var r in entries.EnumerateArray())
        {
            var id = J.Str(r, "entity_id")!;
            seen.Add(id);
            var e = GetOrAdd(id);
            e.InRegistry = true;
            e.RegistryId = J.Str(r, "id");
            e.Capabilities = r.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Object
                ? JsonNode.Parse(caps.GetRawText())!.AsObject() : [];
            e.Name = J.Str(r, "name");
            e.OriginalName = J.Str(r, "original_name");
            e.DeviceId = J.Str(r, "device_id");
            e.OwnAreaId = J.Str(r, "area_id");
            e.Icon = J.Str(r, "icon");
            e.Platform = J.Str(r, "platform");
            e.TranslationKey = J.Str(r, "translation_key");
            e.Category = J.Str(r, "entity_category");
            e.Hidden = J.Str(r, "hidden_by") is not null;
            e.Disabled = J.Str(r, "disabled_by") is not null;
            e.Labels = r.TryGetProperty("labels", out var ls) && ls.ValueKind == JsonValueKind.Array
                ? [.. ls.EnumerateArray().Select(x => x.GetString()!)]
                : [];
            e.DisplayPrecision = DisplayPrecisionOf(r);
            e.Tray = TrayOptionsOf(r);
        }

        // 从注册表里删掉的：没有状态就整个去掉，有状态的留着当作「不在注册表里」的实体。
        foreach (var e in _entities.Values.Where(e => e.InRegistry && !seen.Contains(e.EntityId)).ToList())
        {
            if (e.HasState)
            {
                e.InRegistry = false;
                e.RegistryId = null;
                e.Capabilities = [];
                e.Name = e.OriginalName = e.DeviceId = e.OwnAreaId = e.Icon = e.Platform = e.TranslationKey = e.Category = null;
                e.Hidden = e.Disabled = false;
                e.Labels = [];
                e.DisplayPrecision = null;
                e.Tray = TrayOptions.None;
            }
            else
            {
                _entities.Remove(e.EntityId);
            }
        }

        Relink();
    }

    public void SetServices(JsonElement services)
    {
        var set = new HashSet<string>();
        foreach (var domain in services.EnumerateObject())
        {
            foreach (var service in domain.Value.EnumerateObject())
            {
                set.Add(domain.Name + "." + service.Name);
            }
        }

        _services = set;
    }

    /// <summary>注册表都更新完了，通知界面重排房间。</summary>
    public void NotifyStructureChanged() => StructureChanged?.Invoke();

    /// <summary>
    /// subscribe_entities 推来的事件：a 是新增（第一条带全部实体），c 是变化（+ 新增或改了的字段，- 去掉的属性），r 是删除。
    /// 字段名是压缩过的：s 状态、a 属性、lc 最近变化时间、lu 最近更新时间（HA 的 websocket_api/messages.py）。
    /// </summary>
    public void ApplyEntitiesEvent(JsonElement ev)
    {
        IsInitialSnapshot = !HasSnapshot;
        HasSnapshot = true;
        var changed = new HashSet<string>();
        var structural = false;
        if (ev.TryGetProperty("a", out var added))
        {
            foreach (var p in added.EnumerateObject())
            {
                structural |= !_entities.ContainsKey(p.Name);
                var e = GetOrAdd(p.Name);
                e.HasState = true;
                e.State = J.Str(p.Value, "s") ?? "unknown";
                e.Attributes = p.Value.TryGetProperty("a", out var a) ? JsonNode.Parse(a.GetRawText())!.AsObject() : [];
                e.LastChanged = p.Value.TryGetProperty("lc", out var lc) && lc.ValueKind == JsonValueKind.Number ? lc.GetDouble() : 0;
                changed.Add(p.Name);
            }
        }

        if (ev.TryGetProperty("c", out var diffs))
        {
            foreach (var p in diffs.EnumerateObject())
            {
                if (!_entities.TryGetValue(p.Name, out var e))
                {
                    continue;
                }

                if (p.Value.TryGetProperty("+", out var plus))
                {
                    if (J.Str(plus, "s") is { } s)
                    {
                        e.State = s;
                    }

                    if (plus.TryGetProperty("lc", out var lc) && lc.ValueKind == JsonValueKind.Number)
                    {
                        e.LastChanged = lc.GetDouble();
                    }

                    if (plus.TryGetProperty("a", out var attrs))
                    {
                        foreach (var attr in attrs.EnumerateObject())
                        {
                            e.Attributes[attr.Name] = JsonNode.Parse(attr.Value.GetRawText());
                        }
                    }
                }

                if (p.Value.TryGetProperty("-", out var minus) && minus.TryGetProperty("a", out var removed))
                {
                    foreach (var name in removed.EnumerateArray())
                    {
                        e.Attributes.Remove(name.GetString()!);
                    }
                }

                changed.Add(p.Name);
            }
        }

        if (ev.TryGetProperty("r", out var gone))
        {
            foreach (var id in gone.EnumerateArray().Select(x => x.GetString()!))
            {
                if (_entities.TryGetValue(id, out var e))
                {
                    if (e.InRegistry)
                    {
                        e.HasState = false;
                        e.State = "unavailable";
                        e.Attributes = [];
                    }
                    else
                    {
                        _entities.Remove(id);
                        structural = true;
                    }

                    changed.Add(id);
                }
            }
        }

        if (structural)
        {
            Relink();
            StructureChanged?.Invoke();
        }

        if (changed.Count > 0)
        {
            StatesChanged?.Invoke(changed);
        }

        IsInitialSnapshot = false;
    }

    /// <summary>断线重连前清掉状态，重连后由 subscribe_entities 的第一条事件重新填。</summary>
    public void ClearStates()
    {
        HasSnapshot = false;
        StatesReset?.Invoke();
        foreach (var e in _entities.Values.Where(e => !e.InRegistry).ToList())
        {
            _entities.Remove(e.EntityId);
        }

        foreach (var e in _entities.Values)
        {
            e.HasState = false;
            e.State = "unavailable";
            e.Attributes = [];
        }
    }

    private Entity GetOrAdd(string id)
    {
        if (!_entities.TryGetValue(id, out var e))
        {
            e = new Entity(id);
            _entities[id] = e;
            Link(e);
        }

        return e;
    }

    private void Relink()
    {
        foreach (var e in _entities.Values)
        {
            Link(e);
        }
    }

    /// <summary>实体的区域：自己设了就用自己的，否则跟设备走（和 HA 前端的 getEntityAreaId 一样）。</summary>
    private void Link(Entity e)
    {
        e.Device = e.DeviceId is { } did ? _devices.GetValueOrDefault(did) : null;
        e.AreaId = e.OwnAreaId ?? e.Device?.AreaId;
    }

    /// <summary>注册表 options.ha_tray：程序自己存在 HA 里的排序和磁贴大小（见 docs/design.md 第 5 节）。</summary>
    private static TrayOptions TrayOptionsOf(JsonElement r)
    {
        if (r.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty(TrayOptions.Domain, out var t) && t.ValueKind == JsonValueKind.Object)
        {
            return TrayOptions.FromJson(t);
        }

        return TrayOptions.None;
    }

    /// <summary>注册表 options.sensor 里用户设的 display_precision，没有就用集成建议的 suggested_display_precision。</summary>
    private static int? DisplayPrecisionOf(JsonElement r)
    {
        if (r.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Object
            && o.TryGetProperty("sensor", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "display_precision", "suggested_display_precision" })
            {
                if (s.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number)
                {
                    return v.GetInt32();
                }
            }
        }

        return null;
    }
}

/// <summary>读 JSON 的小工具。</summary>
internal static class J
{
    public static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
