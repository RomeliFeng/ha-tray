using System.Text.Json;
using System.Text.Json.Nodes;
using HaTray.Model;

var failures = 0;
Check("历史事件不重放", !Observe(Event("2026-01-01T00:00:00+00:00", "low_power"), "event", ["low_power"], snapshot: true));

var baseline = new ReminderEngine();
var waiting = Event("unknown", null);
waiting.Tray = Rule("event", ["low_power"]);
Check("首次事件建立基线但不触发", !baseline.Observe(waiting, false) && baseline.Memory.Count == 1);
waiting.State = "2026-01-01T00:00:01+00:00";
waiting.Attributes["event_type"] = "low_power";
Check("首次真实事件触发", baseline.Observe(waiting, false));
waiting.State = "2026-01-01T00:00:02+00:00";
Check("同类型新时间戳再次触发", baseline.Observe(waiting, false));
waiting.Attributes["code"] = 1;
Check("仅属性变化不触发", !baseline.Observe(waiting, false));

var filtered = new ReminderEngine();
var coded = Event("unknown", null);
coded.Tray = Rule("event", ["low_power"]) with { Reminder = new ReminderRule("event", ["low_power"], Attribute: "code", AttributeValue: "7") };
filtered.Observe(coded, false);
coded.State = "2026-01-01T00:00:03+00:00";
coded.Attributes["event_type"] = "low_power";
coded.Attributes["code"] = 8;
Check("属性不匹配不触发", !filtered.Observe(coded, false));
coded.State = "2026-01-01T00:00:04+00:00";
coded.Attributes["code"] = 7;
Check("属性匹配触发", filtered.Observe(coded, false));

var states = new ReminderEngine();
var sensor = Sensor("ready");
sensor.Tray = Rule("state", ["reservoir_full", "service_due"]);
Check("当前已有状态只建立基线", !states.Observe(sensor, false) && !states.Memory.Single().Value.Active);
sensor.State = "reservoir_full";
Check("进入订阅状态触发", states.Observe(sensor, false));
sensor.State = "service_due";
Check("订阅状态之间切换触发", states.Observe(sensor, false));
sensor.State = "service_due";
Check("重复状态不触发", !states.Observe(sensor, false));
sensor.State = "unknown";
Check("未知状态不解除", !states.Observe(sensor, false) && states.Memory.Single().Value.Active);
sensor.State = "ready";
Check("恢复后解除", !states.Observe(sensor, false) && !states.Memory.Single().Value.Active);

var levels = new ReminderEngine();
var battery = Sensor("87");
battery.Tray = TrayOptions.None with { Reminder = new ReminderRule("below", [], 20, 25) };
Check("高于阈值不触发", !levels.Observe(battery, false));
battery.State = "19";
Check("低于阈值触发", levels.Observe(battery, false));
battery.State = "24";
Check("未到恢复阈值仍保持", !levels.Observe(battery, false) && levels.Memory.Single().Value.Active);
battery.State = "25";
Check("达到恢复阈值解除", !levels.Observe(battery, false) && !levels.Memory.Single().Value.Active);
battery.State = "19";
Check("再次低于阈值触发", levels.Observe(battery, false));
battery.State = "unavailable";
Check("不可用既不触发也不恢复", !levels.Observe(battery, false) && levels.Memory.Single().Value.Active);

var home = new Home();
using (var registry = JsonDocument.Parse("""
[{"entity_id":"sensor.demo","id":"registry-demo","platform":"demo","options":{"ha_tray":{"order":4,"future":"kept","reminder":{"kind":"state","values":["a"],"enabled":true}},"other":{}},"capabilities":{"event_types":["low_power"]}}]
"""))
{
    home.SetEntityRegistry(registry.RootElement);
}

var registered = home.Entities["sensor.demo"];
registered.Tray = registered.Tray with { Order = 9, Size = "wide", Pinned = true };
var saved = registered.Tray.ToJson()!;
Check("保存订阅保留未知字段和布局", saved["future"]!.GetValue<string>() == "kept" && saved["order"]!.GetValue<int>() == 9 && saved["reminder"] is JsonObject);
using (var reordered = JsonDocument.Parse(saved.ToJsonString()))
{
    var loaded = TrayOptions.FromJson(reordered.RootElement);
    Check("重新加载不丢失订阅", loaded.Reminder?.Values is ["a"] && loaded.Order == 9 && loaded.Extra["future"]!.GetValue<string>() == "kept");
}

home.ApplyEntitiesEvent(EventDocument("""{"a":{"sensor.demo":{"s":"a","a":{}}}}"""));
home.ApplyEntitiesEvent(EventDocument("""{"c":{"sensor.demo":{"+":{"s":"b"}}}}"""));
Check("初始快照后的变化不再标记为初始", !home.IsInitialSnapshot && registered.State == "b");
home.ClearStates();
Check("断线后等待新的初始快照", !home.HasSnapshot && !registered.HasState);

return failures == 0 ? 0 : 1;

static Entity Event(string state, string? type)
{
    var e = new Entity("event.demo") { HasState = true, State = state, InRegistry = true, RegistryId = "event-demo" };
    e.Attributes["event_types"] = new JsonArray("low_power", "case_open");
    if (type is not null) e.Attributes["event_type"] = type;
    return e;
}

static Entity Sensor(string state) => new("sensor.demo") { HasState = true, State = state, InRegistry = true, RegistryId = "sensor-demo" };

static TrayOptions Rule(string kind, string[] values) => TrayOptions.None with { Reminder = new ReminderRule(kind, values) };

static bool Observe(Entity e, string kind, string[] values, bool snapshot)
{
    e.Tray = Rule(kind, values);
    return new ReminderEngine().Observe(e, snapshot);
}

static JsonElement EventDocument(string json)
{
    using var document = JsonDocument.Parse(json);
    return document.RootElement.Clone();
}

void Check(string name, bool condition)
{
    if (condition)
    {
        Console.WriteLine("通过  " + name);
        return;
    }

    Console.Error.WriteLine("失败  " + name);
    failures++;
}
