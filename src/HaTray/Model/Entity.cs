using System.Text.Json;
using System.Text.Json.Nodes;

namespace HaTray.Model;

internal sealed record Area(string Id, string Name, string? FloorId, string? Icon, string? TemperatureEntityId, string? HumidityEntityId);

internal sealed record Floor(string Id, string Name, int? Level);

internal sealed record Device(string Id, string Name, string? AreaId);

/// <summary>
/// 程序自己的实体选项，存在 HA 实体注册表的 options.ha_tray 里（config/entity_registry/update 的 options_domain），
/// 几台电脑共用：
/// - Order：在房间主界面里的位置（磁贴和胶囊各排各的）；
/// - Size：磁贴大小（half / wide，空着按类型）；
/// - Pinned：胶囊固定显示，收起时也看得到（房间里一个都没固定时，收起显示前两个）。
/// </summary>
internal sealed record TrayOptions(int? Order, string? Size, bool Pinned = false)
{
    public const string Domain = "ha_tray";

    public static readonly TrayOptions None = new(null, null);

    public System.Text.Json.Nodes.JsonObject? ToJson()
    {
        if (Order is null && Size is null && !Pinned)
        {
            return null;
        }

        var o = new System.Text.Json.Nodes.JsonObject();
        if (Order is { } order)
        {
            o["order"] = order;
        }

        if (Size is { } size)
        {
            o["size"] = size;
        }

        if (Pinned)
        {
            o["pinned"] = true;
        }

        return o;
    }
}

/// <summary>
/// 一个实体：注册表条目（可能没有，比如 YAML 里没写 unique_id 的）加当前状态。
/// 只在 UI 线程上读写。
/// </summary>
internal sealed class Entity(string entityId)
{
    public string EntityId { get; } = entityId;

    public string Domain { get; } = entityId[..Math.Max(0, entityId.IndexOf('.', StringComparison.Ordinal))];

    // ---- 注册表 ----
    public bool InRegistry { get; set; }

    public string? Name { get; set; }

    public string? OriginalName { get; set; }

    public string? DeviceId { get; set; }

    /// <summary>实体自己设的区域；没设时跟设备走（见 <see cref="AreaId"/>）。</summary>
    public string? OwnAreaId { get; set; }

    public string? Icon { get; set; }

    public string? Platform { get; set; }

    public string? TranslationKey { get; set; }

    /// <summary>config / diagnostic，普通实体为 null。</summary>
    public string? Category { get; set; }

    public bool Hidden { get; set; }

    public bool Disabled { get; set; }

    public IReadOnlyList<string> Labels { get; set; } = [];

    /// <summary>注册表里的显示精度（sensor 选项里用户设的，没有就用集成建议的）。</summary>
    public int? DisplayPrecision { get; set; }

    /// <summary>程序存在 HA 注册表里的选项（排序、磁贴大小）。</summary>
    public TrayOptions Tray { get; set; } = TrayOptions.None;

    // ---- 派生（注册表变了由 Home 重算）----
    public Device? Device { get; set; }

    public string? AreaId { get; set; }

    // ---- 状态 ----
    public bool HasState { get; set; }

    public string State { get; set; } = "unavailable";

    public JsonObject Attributes { get; set; } = [];

    public double LastChanged { get; set; }

    public string? DeviceClass => Str("device_class");

    public int Features => (int)(Num("supported_features") ?? 0);

    public bool Supports(int feature) => (Features & feature) == feature;

    public bool IsUnavailable => !HasState || State == "unavailable";

    public string? Str(string name) => Attributes[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public double? Num(string name) => Attributes[name] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    public IReadOnlyList<string> Strings(string name) =>
        Attributes[name] is JsonArray a ? [.. a.OfType<JsonValue>().Select(x => x.ToString())] : [];
}
