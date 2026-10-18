namespace HaTray.Model;

/// <summary>主界面的分组，顺序沿用 HA 区域视图（areas-strategy-helper 的 AREA_STRATEGY_GROUPS）。</summary>
internal enum TileGroup
{
    Lights,
    Covers,
    Climate,
    Media,
    Security,
    Actions,
    Other,
}

internal sealed record DeviceGroup(Device? Device, string Name, IReadOnlyList<Entity> Normal, IReadOnlyList<Entity> Config, IReadOnlyList<Entity> Diagnostic);

/// <summary>
/// 一个房间（HA 的一个区域，或者「未分配区域」）：
/// - Chips：环境胶囊，区域设置里指定的温度、湿度传感器，加上打了标签的 sensor / binary_sensor；
/// - Groups：打了标签的其他实体，按 <see cref="TileGroup"/> 分组；
/// - Devices：这个房间的全部实体按设备分组（「更多设备」用）。
/// 在 HA 里隐藏或停用的实体都不算进来。
/// </summary>
internal sealed record Room(string Id, string Name, string? FloorId, IReadOnlyList<Entity> Chips, IReadOnlyList<(TileGroup Group, IReadOnlyList<Entity> Items)> Groups, IReadOnlyList<DeviceGroup> Devices)
{
    /// <summary>「未分配区域」的房间 id（HA 的区域 id 不会是空串）。</summary>
    public const string UnassignedId = "";

    public bool IsUnassigned => Id == UnassignedId;

    /// <summary>区域设置里指定的温度、湿度传感器（顺序：温度、湿度；没设是 null）。</summary>
    public string?[] AreaSensors { get; init; } = [];

    public int Count => Devices.Sum(d => d.Normal.Count + d.Config.Count + d.Diagnostic.Count);
}

internal static class RoomBuilder
{
    public static TileGroup GroupOf(string domain) => domain switch
    {
        "light" => TileGroup.Lights,
        "cover" => TileGroup.Covers,
        "climate" or "humidifier" or "water_heater" or "fan" => TileGroup.Climate,
        "media_player" => TileGroup.Media,
        "alarm_control_panel" or "lock" or "camera" => TileGroup.Security,
        "script" or "scene" or "automation" => TileGroup.Actions,
        _ => TileGroup.Other,
    };

    public static string GroupTitle(TileGroup g) => g switch
    {
        TileGroup.Lights => L.T("灯光", "Lights"),
        TileGroup.Covers => L.T("窗帘", "Covers"),
        TileGroup.Climate => L.T("气候", "Climate"),
        TileGroup.Media => L.T("媒体", "Media"),
        TileGroup.Security => L.T("安防", "Security"),
        TileGroup.Actions => L.T("场景和脚本", "Scenes & scripts"),
        _ => L.T("其他", "Other"),
    };

    /// <summary>按 HA 的区域顺序排房间（区域注册表返回的顺序就是用户在 HA 里排的顺序），最后是「未分配区域」。</summary>
    public static List<Room> Build(Home home, string labelName)
    {
        var labelId = home.Labels.GetValueOrDefault(labelName);
        var cmp = StringComparer.Create(L.Culture, ignoreCase: true);
        var visible = home.Entities.Values.Where(e => !e.Hidden && !e.Disabled).ToList();
        var areaIds = home.Areas.Select(a => a.Id).ToHashSet();
        var byArea = visible.GroupBy(e => e.AreaId is { } a && areaIds.Contains(a) ? a : Room.UnassignedId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var rooms = new List<Room>();
        foreach (var area in home.Areas)
        {
            rooms.Add(BuildRoom(home, area.Id, area.Name, area.FloorId, byArea.GetValueOrDefault(area.Id) ?? [], labelId,
                [area.TemperatureEntityId, area.HumidityEntityId], cmp));
        }

        rooms.Add(BuildRoom(home, Room.UnassignedId, L.T("未分配区域", "Unassigned"), null, byArea.GetValueOrDefault(Room.UnassignedId) ?? [], labelId, [], cmp));
        return rooms;
    }

    private static Room BuildRoom(Home home, string id, string name, string? floorId, List<Entity> entities, string? labelId, string?[] areaSensors, StringComparer cmp)
    {
        static bool IsSensor(Entity e) => e.Domain is "sensor" or "binary_sensor";
        var labeled = labelId is null ? [] : entities.Where(e => e.Labels.Contains(labelId)).ToList();

        // 胶囊：区域指定的温度、湿度传感器在前（不论在不在这个房间、有没有打标签），再是打了标签的传感器。
        var chips = new List<Entity>();
        foreach (var sid in areaSensors)
        {
            if (sid is not null && home.Entities.TryGetValue(sid, out var s) && !chips.Contains(s))
            {
                chips.Add(s);
            }
        }

        chips.AddRange(labeled.Where(IsSensor).Where(e => !chips.Contains(e))
            .OrderBy(e => ChipOrder(e.DeviceClass)).ThenBy(Presenter.FullName, cmp));

        // 用户在编辑模式里排过的（options.ha_tray.order）排在前面，按排好的顺序；没排过的跟在后面，按默认规则。
        chips = [.. chips.Select((e, i) => (e, i)).OrderBy(x => x.e.Tray.Order ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.e)];

        // 组内：排过的在前；组之间：按组里最小的序号，没排过的组按 HA 区域视图的顺序跟在后面。
        var groups = labeled.Where(e => !IsSensor(e))
            .GroupBy(e => GroupOf(e.Domain))
            .Select(g => (g.Key, Items: g.OrderBy(e => e.Tray.Order ?? int.MaxValue).ThenBy(Presenter.FullName, cmp).ToList()))
            .OrderBy(g => g.Items.Min(e => e.Tray.Order ?? int.MaxValue))
            .ThenBy(g => g.Key)
            .Select(g => (g.Key, (IReadOnlyList<Entity>)g.Items))
            .ToList();

        var devices = entities
            .GroupBy(e => e.Device)
            .Select(g =>
            {
                var items = g.OrderBy(Presenter.ShortName, cmp).ToList();
                return new DeviceGroup(g.Key, g.Key?.Name is { Length: > 0 } n ? n : L.T("其他", "Other"),
                    [.. items.Where(e => e.Category is null)],
                    [.. items.Where(e => e.Category == "config")],
                    [.. items.Where(e => e.Category == "diagnostic")]);
            })
            .OrderBy(d => d.Device is null ? 1 : 0)
            .ThenBy(d => d.Name, cmp)
            .ToList();

        return new Room(id, name, floorId, chips, groups, devices) { AreaSensors = areaSensors };
    }

    /// <summary>胶囊里温度、湿度排前面，别的按名字。</summary>
    private static int ChipOrder(string? deviceClass) => deviceClass switch
    {
        "temperature" => 0,
        "humidity" => 1,
        _ => 2,
    };
}
