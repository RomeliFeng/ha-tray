using System.Text.Json;
using System.Text.Json.Nodes;
using HaTray.Model;
using Microsoft.UI.Dispatching;

namespace HaTray.Ha;

/// <summary>
/// 演示模式（HaTray.exe --demo）：不连 HA，用一套假数据跑界面。每种实体类型、主要的功能位组合都有样例，
/// 数据格式和 HA 的接口一样，走的也是 <see cref="Home"/> 的同一套加载代码。不含任何真实家庭的数据。
/// 控制命令只做最简单的模拟：开关、设值，然后像 HA 一样推回一条状态变化。
/// </summary>
internal sealed class DemoBackend(Home home, DispatcherQueue ui, string labelName) : IHomeBackend
{
    private readonly JsonArray _areas = [];
    private readonly JsonArray _devices = [];
    private readonly JsonArray _entities = [];
    private readonly JsonObject _states = [];

    public ConnectionStatus Status => ConnectionStatus.Connected;

    public string? Error => null;

    public bool Ready { get; private set; }

    public bool IsAdmin => true;

    public event Action? StatusChanged;

    /// <summary>改假注册表里的标签，再像 HA 一样推一次注册表更新。</summary>
    public Task SetLabelAsync(string entityId, string labelName, bool on)
    {
        ui.TryEnqueue(() =>
        {
            if (_entities.OfType<JsonObject>().FirstOrDefault(r => (string?)r["entity_id"] == entityId) is { } row)
            {
                var labels = ((JsonArray)row["labels"]!).Select(l => (string)l!).Where(l => l != "demo_tray").ToList();
                if (on)
                {
                    labels.Add("demo_tray");
                }

                row["labels"] = new JsonArray([.. labels.Select(l => (JsonNode)l)]);
                home.SetEntityRegistry(Parse(_entities));
                home.NotifyStructureChanged();
            }
        });
        return Task.CompletedTask;
    }

    public Task SetTrayOptionsAsync(string entityId, TrayOptions options) => EditEntity(entityId, row =>
    {
        var all = row["options"] as JsonObject ?? [];
        all[TrayOptions.Domain] = options.ToJson();
        row["options"] = all;
    });

    public Task UpdateEntityAsync(string entityId, string field, string? value) => EditEntity(entityId, row => row[field] = value);

    public Task SetAreaSensorAsync(string areaId, string field, string? entityId)
    {
        ui.TryEnqueue(() =>
        {
            if (_areas.OfType<JsonObject>().FirstOrDefault(a => (string?)a["area_id"] == areaId) is { } area)
            {
                area[field] = entityId;
                home.SetAreas(Parse(_areas));
                home.NotifyStructureChanged();
            }
        });
        return Task.CompletedTask;
    }

    /// <summary>改一条假注册表记录，再像 HA 一样推一次注册表更新。</summary>
    private Task EditEntity(string entityId, Action<JsonObject> edit)
    {
        ui.TryEnqueue(() =>
        {
            if (_entities.OfType<JsonObject>().FirstOrDefault(r => (string?)r["entity_id"] == entityId) is { } row)
            {
                edit(row);
                home.SetEntityRegistry(Parse(_entities));
                home.NotifyStructureChanged();
            }
        });
        return Task.CompletedTask;
    }

    public void Start()
    {
        Build();
        ui.TryEnqueue(() =>
        {
            home.SetAreas(Parse(_areas));
            home.SetFloors(Parse(new JsonArray()));
            home.SetDevices(Parse(_devices));
            home.SetLabels(Parse(new JsonArray(new JsonObject { ["label_id"] = "demo_tray", ["name"] = labelName })));
            home.SetEntityRegistry(Parse(_entities));
            home.SetServices(Parse(Services()));
            home.Translations.Clear();
            home.Translations.Add(Parse(new JsonObject { ["resources"] = Translations() }));
            home.Icons.Clear();
            home.Icons.AddComponent(Parse(new JsonObject { ["resources"] = ComponentIcons() }));
            home.ClearStates();
            Ready = true;
            home.NotifyStructureChanged();
            home.ApplyEntitiesEvent(Parse(new JsonObject { ["a"] = _states.DeepClone() }));
            StatusChanged?.Invoke();
        });
    }

    public Task CallServiceAsync(string domain, string service, string entityId, JsonObject? data = null)
    {
        ui.TryEnqueue(() =>
        {
            if (!home.Entities.TryGetValue(entityId, out var e))
            {
                return;
            }

            var plus = new JsonObject();
            var attrs = new JsonObject();
            switch (service)
            {
                case "turn_on" or "open_cover" or "unlock" or "open_valve":
                    plus["s"] = domain switch { "cover" or "valve" => "open", "lock" => "unlocked", "climate" => "cool", _ => "on" };
                    break;
                case "turn_off" or "close_cover" or "lock" or "close_valve":
                    plus["s"] = domain switch { "cover" or "valve" => "closed", "lock" => "locked", _ => "off" };
                    break;
                case "toggle":
                    plus["s"] = e.State == "on" ? "off" : "on";
                    break;
                case "set_hvac_mode":
                    plus["s"] = data?["hvac_mode"]?.DeepClone();
                    break;
                case "select_option":
                    plus["s"] = data?["option"]?.DeepClone();
                    break;
                case "set_value":
                    plus["s"] = data?["value"]?.ToString();
                    break;
                case "press":
                    plus["s"] = DateTimeOffset.UtcNow.ToString("O");
                    break;
            }

            foreach (var (k, v) in data ?? [])
            {
                if (k == "brightness_pct")
                {
                    attrs["brightness"] = Math.Round(v!.GetValue<int>() * 255 / 100.0);
                }
                else if (k is not ("hvac_mode" or "option" or "value"))
                {
                    attrs[k] = v?.DeepClone();
                }
            }

            if (service == "media_play_pause")
            {
                plus["s"] = e.State == "playing" ? "paused" : "playing";
            }

            if (service == "set_percentage")
            {
                plus["s"] = data?["percentage"]?.GetValue<int>() > 0 ? "on" : "off";
            }

            if (attrs.Count > 0)
            {
                plus["a"] = attrs;
            }

            home.ApplyEntitiesEvent(Parse(new JsonObject { ["c"] = new JsonObject { [entityId] = new JsonObject { ["+"] = plus } } }));
        });
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static JsonElement Parse(JsonNode node) => JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();

    private void Area(string id, string zh, string en, string? temperature = null, string? humidity = null) =>
        _areas.Add(new JsonObject
        {
            ["area_id"] = id, ["name"] = L.T(zh, en), ["floor_id"] = null, ["icon"] = null,
            ["temperature_entity_id"] = temperature, ["humidity_entity_id"] = humidity,
        });

    private void Device(string id, string area, string zh, string en) =>
        _devices.Add(new JsonObject { ["id"] = id, ["area_id"] = area, ["name"] = L.T(zh, en), ["name_by_user"] = null });

    /// <summary>加一个实体。device 为 null 时直接放进 area；tray 表示打上主界面标签。</summary>
    private void Add(string entityId, string? device, string? area, string? zh, string? en, string state, JsonObject? attrs = null,
        bool tray = false, string? category = null, int features = 0)
    {
        _entities.Add(new JsonObject
        {
            ["entity_id"] = entityId, ["id"] = "demo_" + entityId, ["device_id"] = device, ["area_id"] = device is null ? area : null,
            ["name"] = null, ["original_name"] = zh is null ? null : L.T(zh, en!), ["icon"] = null, ["platform"] = "demo",
            ["translation_key"] = null, ["entity_category"] = category, ["hidden_by"] = null, ["disabled_by"] = null,
            ["labels"] = tray ? new JsonArray("demo_tray") : new JsonArray(), ["options"] = new JsonObject(),
            ["capabilities"] = new JsonObject { ["options"] = attrs?["options"]?.DeepClone(), ["event_types"] = attrs?["event_types"]?.DeepClone() },
        });
        var a = attrs ?? [];
        a["supported_features"] = features;
        _states[entityId] = new JsonObject { ["s"] = state, ["a"] = a, ["lc"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
    }

    private void Build()
    {
        Area("living", "客厅", "Living room", temperature: "sensor.living_temperature", humidity: "sensor.living_humidity");
        Area("bedroom", "卧室", "Bedroom");
        Area("kitchen", "厨房", "Kitchen");
        Area("study", "书房", "Study");
        Area("hall", "门厅", "Hallway");

        // ---- 客厅：灯、空调、风扇、窗帘、电视、扫地机、场景 ----
        Device("d_ceiling", "living", "吸顶灯", "Ceiling light");
        Add("light.living_ceiling", "d_ceiling", null, null, null, "on",
            new() { ["supported_color_modes"] = new JsonArray("color_temp"), ["color_mode"] = "color_temp", ["brightness"] = 180, ["color_temp_kelvin"] = 4000, ["min_color_temp_kelvin"] = 2700, ["max_color_temp_kelvin"] = 6500, ["effect_list"] = new JsonArray("日光", "月光") }, tray: true, features: 4);
        Device("d_strip", "living", "灯带", "LED strip");
        Add("light.living_strip", "d_strip", null, null, null, "off",
            new() { ["supported_color_modes"] = new JsonArray("hs", "color_temp"), ["min_color_temp_kelvin"] = 2000, ["max_color_temp_kelvin"] = 6500 }, tray: true, features: 36);
        Device("d_plug_lamp", "living", "落地灯插座", "Floor lamp plug");
        Add("light.living_floor", "d_plug_lamp", null, "灯", "Light", "off", new() { ["supported_color_modes"] = new JsonArray("onoff") }, tray: true);
        Add("light.living_plug_indicator", "d_plug_lamp", null, "指示灯", "Indicator", "on", new() { ["supported_color_modes"] = new JsonArray("onoff") }, category: "config");
        Device("d_ac", "living", "空调", "Air conditioner");
        Add("climate.living_ac", "d_ac", null, null, null, "cool", new()
        {
            ["hvac_modes"] = new JsonArray("off", "cool", "heat", "dry", "fan_only", "auto"), ["min_temp"] = 16, ["max_temp"] = 30, ["target_temp_step"] = 1,
            ["temperature"] = 24, ["current_temperature"] = null, ["hvac_action"] = "cooling",
            ["fan_modes"] = new JsonArray("auto", "low", "medium", "high"), ["fan_mode"] = "auto", ["swing_modes"] = new JsonArray("off", "vertical"), ["swing_mode"] = "off",
        }, tray: true, features: 1 | 8 | 32 | 128 | 256);
        Add("sensor.living_ac_power", "d_ac", null, "功率", "Power", "856.3", new() { ["unit_of_measurement"] = "W", ["device_class"] = "power", ["state_class"] = "measurement" }, tray: true);
        Device("d_fan", "living", "落地扇", "Pedestal fan");
        Add("fan.living_fan", "d_fan", null, null, null, "on", new() { ["percentage"] = 33, ["percentage_step"] = 33.333, ["preset_modes"] = new JsonArray("normal", "sleep", "nature"), ["preset_mode"] = "normal", ["oscillating"] = false }, tray: true, features: 1 | 2 | 8 | 16 | 32);
        Device("d_curtain", "living", "窗帘", "Curtain");
        Add("cover.living_curtain", "d_curtain", null, null, null, "open", new() { ["current_position"] = 70, ["device_class"] = "curtain" }, tray: true, features: 1 | 2 | 4 | 8);
        Device("d_tv", "living", "电视", "TV");
        Add("media_player.living_tv", "d_tv", null, null, null, "playing", new() { ["device_class"] = "tv", ["volume_level"] = 0.3, ["is_volume_muted"] = false, ["media_title"] = "Demo", ["source_list"] = new JsonArray("HDMI 1", "HDMI 2"), ["source"] = "HDMI 1" }, tray: true, features: 1 | 4 | 8 | 16 | 32 | 128 | 256 | 2048 | 16384);
        Device("d_vacuum", "living", "扫地机", "Robot vacuum");
        Add("vacuum.living_vacuum", "d_vacuum", null, null, null, "docked", new() { ["fan_speed_list"] = new JsonArray("quiet", "standard", "max"), ["fan_speed"] = "standard" }, tray: true, features: 4 | 8 | 16 | 32 | 512 | 4096 | 8192);
        Add("sensor.living_vacuum_battery", "d_vacuum", null, "电量", "Battery", "87", new() { ["unit_of_measurement"] = "%", ["device_class"] = "battery", ["state_class"] = "measurement" }, category: "diagnostic");
        Device("d_climate_sensor", "living", "温湿度计", "Thermometer");
        Add("sensor.living_temperature", "d_climate_sensor", null, "温度", "Temperature", "25.4", new() { ["unit_of_measurement"] = "°C", ["device_class"] = "temperature", ["state_class"] = "measurement" });
        Add("sensor.living_humidity", "d_climate_sensor", null, "湿度", "Humidity", "56", new() { ["unit_of_measurement"] = "%", ["device_class"] = "humidity", ["state_class"] = "measurement" });
        Add("sensor.living_pm25", "d_climate_sensor", null, "PM2.5", "PM2.5", "12", new() { ["unit_of_measurement"] = "µg/m³", ["device_class"] = "pm25", ["state_class"] = "measurement" }, tray: true);
        Add("sensor.living_co2", "d_climate_sensor", null, "二氧化碳", "CO₂", "620", new() { ["unit_of_measurement"] = "ppm", ["device_class"] = "carbon_dioxide", ["state_class"] = "measurement" }, tray: true);
        Add("scene.living_movie", null, "living", "观影", "Movie time", "2026-10-01T12:00:00+00:00", tray: true);
        Add("script.living_goodnight", null, "living", "晚安", "Good night", "off", tray: true);

        // ---- 卧室：灯、除湿机、电热水器、有人传感器、闹钟 ----
        Device("d_bed_lamp", "bedroom", "床头灯", "Bedside lamp");
        Add("light.bedroom_lamp", "d_bed_lamp", null, null, null, "unavailable", new() { ["supported_color_modes"] = new JsonArray("brightness") }, tray: true);
        Device("d_dehum", "bedroom", "除湿机", "Dehumidifier");
        Add("humidifier.bedroom_dehumidifier", "d_dehum", null, null, null, "on", new() { ["device_class"] = "dehumidifier", ["min_humidity"] = 40, ["max_humidity"] = 70, ["humidity"] = 50, ["current_humidity"] = 58, ["action"] = "drying", ["available_modes"] = new JsonArray("auto", "sleep", "dry_clothes"), ["mode"] = "auto" }, tray: true, features: 1);
        Add("select.bedroom_dehumidifier_fan", "d_dehum", null, "风机档位", "Fan level", "2", new() { ["options"] = new JsonArray("1", "2", "3") });
        Add("sensor.bedroom_dehumidifier_condition", "d_dehum", null, "设备状态", "Condition", "ready",
            new() { ["device_class"] = "enum", ["options"] = new JsonArray("ready", "reservoir_full", "service_due", "cooling") });
        Add("button.bedroom_dehumidifier_filter_reset", "d_dehum", null, "重置滤网", "Reset filter", "unknown");
        Add("switch.bedroom_dehumidifier_child_lock", "d_dehum", null, "童锁", "Child lock", "off", new() { ["device_class"] = "switch" }, category: "config");
        Add("number.bedroom_dehumidifier_timer", "d_dehum", null, "定时", "Timer", "0", new() { ["min"] = 0, ["max"] = 1440, ["step"] = 1, ["mode"] = "auto", ["unit_of_measurement"] = "min" });
        Device("d_occ", "bedroom", "人体传感器", "Presence sensor");
        Add("binary_sensor.bedroom_occupancy", "d_occ", null, "有人", "Occupancy", "on", new() { ["device_class"] = "occupancy" }, tray: true);
        Add("sensor.bedroom_illuminance", "d_occ", null, "照度", "Illuminance", "96.0", new() { ["unit_of_measurement"] = "lx", ["device_class"] = "illuminance", ["state_class"] = "measurement" });
        Add("input_boolean.bedroom_sleep_mode", null, "bedroom", "睡眠模式", "Sleep mode", "off", new() { ["icon"] = "mdi:weather-night" }, tray: true);
        Add("input_datetime.bedroom_alarm", null, "bedroom", "闹钟", "Alarm", "07:30:00", new() { ["has_date"] = false, ["has_time"] = true });

        // ---- 厨房：净水器、洗碗机、阀门 ----
        Device("d_purifier", "kitchen", "净水器", "Water purifier");
        Add("sensor.kitchen_purifier_status", "d_purifier", null, "工作状态", "Status", "standby", new() { ["device_class"] = "enum", ["options"] = new JsonArray("standby", "purifying") }, tray: true);
        Add("sensor.kitchen_purifier_tds_out", "d_purifier", null, "出水 TDS", "TDS out", "22", new() { ["unit_of_measurement"] = "ppm", ["state_class"] = "measurement" }, tray: true);
        Device("d_dishwasher", "kitchen", "洗碗机", "Dishwasher");
        Add("switch.kitchen_dishwasher", "d_dishwasher", null, null, null, "off", new() { ["device_class"] = "switch" }, tray: true);
        Add("select.kitchen_dishwasher_program", "d_dishwasher", null, "程序", "Program", "eco", new() { ["options"] = new JsonArray("eco", "quick", "intensive") }, tray: true);
        Add("button.kitchen_dishwasher_start", "d_dishwasher", null, "开始", "Start", "unknown", tray: true);
        Add("sensor.kitchen_dishwasher_remaining", "d_dishwasher", null, "剩余时间", "Time left", "45", new() { ["unit_of_measurement"] = "min", ["device_class"] = "duration" });
        Add("event.kitchen_dishwasher_done", "d_dishwasher", null, "洗涤结束", "Finished", "2026-10-17T20:15:00+00:00", new() { ["event_types"] = new JsonArray("finished"), ["event_type"] = "finished" });
        Device("d_valve", "kitchen", "进水阀", "Water valve");
        Add("valve.kitchen_water", "d_valve", null, null, null, "open", new() { ["device_class"] = "water" }, features: 1 | 2);

        // ---- 书房：空房间 ----

        // ---- 门厅：门锁、警报、门磁、开关、计数器、计时器 ----
        Device("d_lock", "hall", "门锁", "Door lock");
        Add("lock.hall_door", "d_lock", null, null, null, "locked", tray: true, features: 1);
        Add("sensor.hall_door_battery", "d_lock", null, "电量", "Battery", "50", new() { ["unit_of_measurement"] = "%", ["device_class"] = "battery", ["state_class"] = "measurement" });
        Add("event.hall_door_unlocked", "d_lock", null, "开锁", "Unlocked", "2026-10-18T05:27:34+00:00", new() { ["event_types"] = new JsonArray("unlocked"), ["event_type"] = "unlocked" });
        Device("d_contact", "hall", "门磁", "Door sensor");
        Add("binary_sensor.hall_door_contact", "d_contact", null, null, null, "off", new() { ["device_class"] = "door" }, tray: true);
        Add("event.hall_sensor_warning", "d_contact", null, "设备提醒", "Device warning", "unknown",
            new() { ["event_types"] = new JsonArray("low_power", "case_open"), ["event_type"] = null, ["code"] = 0 });
        Add("alarm_control_panel.home", null, "hall", "家庭警报", "Home alarm", "disarmed", new() { ["code_format"] = null }, tray: true, features: 1 | 2 | 4);
        Add("siren.hall", null, "hall", "警笛", "Siren", "off", features: 1 | 2);
        Add("counter.hall_visitors", null, "hall", "访客", "Visitors", "3", new() { ["minimum"] = 0, ["maximum"] = null, ["step"] = 1 });
        Add("timer.hall_delivery", null, "hall", "快递提醒", "Delivery", "idle", new() { ["duration"] = "0:10:00" });
        Add("switch.hall_plug", null, "hall", "插座", "Plug", "on", new() { ["device_class"] = "outlet" }, tray: true);
        Add("text.hall_message", null, "hall", "留言", "Message", "", new() { ["min"] = 0, ["max"] = 100, ["mode"] = "text", ["pattern"] = null });

        // ---- 没有区域的：天气、太阳、人、更新、热水器 ----
        Add("weather.home", null, null, "天气", "Weather", "rainy", new() { ["temperature"] = 21.5, ["humidity"] = 80, ["temperature_unit"] = "°C" }, features: 3);
        Add("sun.sun", null, null, "太阳", "Sun", "below_horizon");
        Add("person.demo", null, null, "演示用户", "Demo user", "home");
        Add("update.demo_firmware", null, null, "固件", "Firmware", "on", new() { ["installed_version"] = "1.0", ["latest_version"] = "1.1", ["device_class"] = "firmware" }, features: 1 | 4);
        Add("water_heater.boiler", null, null, "热水器", "Water heater", "eco", new() { ["min_temp"] = 35, ["max_temp"] = 75, ["temperature"] = 55, ["current_temperature"] = 48, ["operation_list"] = new JsonArray("eco", "performance", "off"), ["operation_mode"] = "eco" }, features: 1 | 2 | 8);
        Add("demo_unknown_domain.thing", null, null, "未知类型", "Unknown type", "on");
    }

    private static JsonObject Services()
    {
        var s = new JsonObject();
        foreach (var d in new[] { "light", "switch", "fan", "climate", "humidifier", "media_player", "input_boolean", "siren", "script", "automation", "water_heater", "remote", "vacuum" })
        {
            s[d] = new JsonObject { ["turn_on"] = new JsonObject(), ["turn_off"] = new JsonObject(), ["toggle"] = new JsonObject() };
        }

        return s;
    }

    /// <summary>HA 后端翻译的一小部分，够演示用。</summary>
    private static JsonObject Translations()
    {
        var t = new JsonObject();
        void S(string domain, string key, string state, string zh, string en) =>
            t[$"component.{domain}.entity_component.{key}.state.{state}"] = L.T(zh, en);
        S("light", "_", "on", "开", "On");
        S("light", "_", "off", "关", "Off");
        S("switch", "_", "on", "开", "On");
        S("switch", "_", "off", "关", "Off");
        S("input_boolean", "_", "on", "开", "On");
        S("input_boolean", "_", "off", "关", "Off");
        S("fan", "_", "on", "开", "On");
        S("humidifier", "_", "on", "开", "On");
        S("climate", "_", "cool", "制冷", "Cool");
        S("climate", "_", "off", "关", "Off");
        S("cover", "_", "open", "已打开", "Open");
        S("cover", "_", "closed", "已关闭", "Closed");
        S("valve", "_", "open", "已打开", "Open");
        S("lock", "_", "locked", "已上锁", "Locked");
        S("lock", "_", "unlocked", "已解锁", "Unlocked");
        S("media_player", "_", "playing", "播放中", "Playing");
        S("vacuum", "_", "docked", "已停靠", "Docked");
        S("binary_sensor", "occupancy", "on", "有人", "Detected");
        S("binary_sensor", "door", "off", "关闭", "Closed");
        S("alarm_control_panel", "_", "disarmed", "已撤防", "Disarmed");
        S("weather", "_", "rainy", "雨", "Rainy");
        S("sun", "_", "below_horizon", "地平线以下", "Below horizon");
        S("person", "_", "home", "在家", "Home");
        S("timer", "_", "idle", "空闲", "Idle");
        S("water_heater", "_", "eco", "节能", "Eco");
        S("update", "_", "on", "有更新", "Update available");
        S("sensor", "enum", "ready", "就绪", "Ready");
        S("sensor", "enum", "reservoir_full", "水箱满", "Reservoir full");
        S("sensor", "enum", "service_due", "需要维护", "Service due");
        S("sensor", "enum", "cooling", "冷却中", "Cooling");
        t["component.event.entity_component._.state_attributes.event_type.state.low_power"] = L.T("低电量", "Low power");
        t["component.event.entity_component._.state_attributes.event_type.state.case_open"] = L.T("外壳打开", "Case open");
        t["component.event.entity_component._.state_attributes.event_type.state.finished"] = L.T("已完成", "Finished");
        return t;
    }

    /// <summary>HA 图标规则的一小部分（格式和 frontend/get_icons 一样）。</summary>
    private static JsonObject ComponentIcons() => new()
    {
        ["light"] = new JsonObject { ["_"] = new JsonObject { ["default"] = "mdi:lightbulb", ["state"] = new JsonObject { ["off"] = "mdi:lightbulb-off" } } },
        ["switch"] = new JsonObject
        {
            ["_"] = new JsonObject { ["default"] = "mdi:toggle-switch-variant", ["state"] = new JsonObject { ["off"] = "mdi:toggle-switch-variant-off" } },
            ["outlet"] = new JsonObject { ["default"] = "mdi:power-plug", ["state"] = new JsonObject { ["off"] = "mdi:power-plug-off" } },
        },
        ["fan"] = new JsonObject { ["_"] = new JsonObject { ["default"] = "mdi:fan", ["state"] = new JsonObject { ["off"] = "mdi:fan-off" } } },
        ["cover"] = new JsonObject { ["curtain"] = new JsonObject { ["default"] = "mdi:curtains", ["state"] = new JsonObject { ["closed"] = "mdi:curtains-closed" } } },
        ["lock"] = new JsonObject { ["_"] = new JsonObject { ["default"] = "mdi:lock", ["state"] = new JsonObject { ["unlocked"] = "mdi:lock-open-variant" } } },
        ["sensor"] = new JsonObject
        {
            ["temperature"] = new JsonObject { ["default"] = "mdi:thermometer" },
            ["humidity"] = new JsonObject { ["default"] = "mdi:water-percent" },
            ["battery"] = new JsonObject { ["default"] = "mdi:battery", ["range"] = new JsonObject { ["0"] = "mdi:battery-outline", ["50"] = "mdi:battery-50", ["90"] = "mdi:battery-90" } },
        },
        ["binary_sensor"] = new JsonObject
        {
            ["occupancy"] = new JsonObject { ["default"] = "mdi:home-outline", ["state"] = new JsonObject { ["on"] = "mdi:home" } },
            ["door"] = new JsonObject { ["default"] = "mdi:door-closed", ["state"] = new JsonObject { ["on"] = "mdi:door-open" } },
        },
        ["humidifier"] = new JsonObject { ["dehumidifier"] = new JsonObject { ["default"] = "mdi:air-humidifier" } },
        ["media_player"] = new JsonObject { ["tv"] = new JsonObject { ["default"] = "mdi:television" } },
    };
}
