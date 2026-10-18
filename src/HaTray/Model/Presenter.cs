using System.Globalization;

namespace HaTray.Model;

/// <summary>
/// 实体怎么显示：名字、状态文字、图标、算不算「开着」。规则照抄 HA 前端（20260826.7）：
/// compute_entity_name.ts、compute_state_display.ts、data/icons.ts、state_active.ts。
/// </summary>
internal sealed class Presenter(Home home)
{
    /// <summary>按时间戳记状态的类型（state 是最近一次触发的时间），HA 前端的 TIMESTAMP_STATE_DOMAINS。</summary>
    private static readonly HashSet<string> TimestampDomains =
        ["ai_task", "button", "conversation", "event", "image", "infrared", "input_button", "notify", "radio_frequency", "scene", "stt", "tag", "tts", "wake_word", "datetime"];

    private static readonly HashSet<string> NumberDomains = ["counter", "input_number", "number"];

    private static readonly HashSet<string> DateTimeDomains = ["date", "input_datetime", "time"];

    /// <summary>HA 前端 FALLBACK_DOMAIN_ICONS：类型没有图标规则时用。</summary>
    private static readonly Dictionary<string, string> FallbackIcons = new()
    {
        ["ai_task"] = "mdi:star-four-points", ["air_quality"] = "mdi:air-filter", ["alert"] = "mdi:alert", ["automation"] = "mdi:robot",
        ["calendar"] = "mdi:calendar", ["climate"] = "mdi:thermostat", ["configurator"] = "mdi:cog", ["conversation"] = "mdi:forum-outline",
        ["counter"] = "mdi:counter", ["date"] = "mdi:calendar", ["datetime"] = "mdi:calendar-clock", ["device_tracker"] = "mdi:account",
        ["group"] = "mdi:google-circles-communities", ["homeassistant"] = "mdi:home-assistant", ["image_processing"] = "mdi:image-filter-frames",
        ["image"] = "mdi:image", ["infrared"] = "mdi:led-on", ["input_boolean"] = "mdi:toggle-switch", ["input_button"] = "mdi:button-pointer",
        ["input_datetime"] = "mdi:calendar-clock", ["input_number"] = "mdi:ray-vertex", ["input_select"] = "mdi:format-list-bulleted",
        ["input_text"] = "mdi:form-textbox", ["lawn_mower"] = "mdi:robot-mower", ["light"] = "mdi:lightbulb", ["notify"] = "mdi:comment-alert",
        ["number"] = "mdi:ray-vertex", ["persistent_notification"] = "mdi:bell", ["person"] = "mdi:account", ["plant"] = "mdi:flower",
        ["proximity"] = "mdi:apple-safari", ["radio_frequency"] = "mdi:radio-tower", ["remote"] = "mdi:remote", ["scene"] = "mdi:palette",
        ["schedule"] = "mdi:calendar-clock", ["script"] = "mdi:script-text", ["select"] = "mdi:format-list-bulleted", ["sensor"] = "mdi:eye",
        ["simple_alarm"] = "mdi:bell", ["siren"] = "mdi:bullhorn", ["stt"] = "mdi:microphone-message", ["sun"] = "mdi:white-balance-sunny",
        ["text"] = "mdi:form-textbox", ["time"] = "mdi:clock", ["timer"] = "mdi:timer-outline", ["template"] = "mdi:code-braces",
        ["todo"] = "mdi:clipboard-list", ["tts"] = "mdi:speaker-message", ["vacuum"] = "mdi:robot-vacuum", ["wake_word"] = "mdi:chat-sleep",
        ["weather"] = "mdi:weather-partly-cloudy", ["zone"] = "mdi:map-marker-radius",
    };

    private const string DefaultIcon = "mdi:bookmark";

    // ---------------- 名字 ----------------

    /// <summary>设备名（去掉首尾空白）；不属于设备时为 null。</summary>
    public static string? DeviceName(Entity e) => e.Device is { Name.Length: > 0 } d ? d.Name : null;

    /// <summary>
    /// 实体自己的那半截名字（computeEntityEntryName）。null 表示这个实体就用设备名。
    /// 程序另外去掉首尾空白：有的集成给的原始名带前导空格。
    /// </summary>
    public static string? EntityName(Entity e)
    {
        var name = (e.Name ?? e.OriginalName)?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            name = null;
        }

        if (!e.InRegistry)
        {
            return e.Str("friendly_name")?.Trim() ?? ObjectIdName(e);
        }

        if (DeviceName(e) is not { } device)
        {
            return name ?? e.Str("friendly_name")?.Trim() ?? ObjectIdName(e);
        }

        if (name is null || name == device)
        {
            return null;
        }

        return StripPrefix(name, device) ?? name;
    }

    /// <summary>完整的名字：「设备 实体」（HA 前端默认的显示方式），实体用设备名时就是设备名。</summary>
    public static string FullName(Entity e)
    {
        var device = DeviceName(e);
        var entity = EntityName(e);
        return entity is null ? device ?? ObjectIdName(e) : device is null ? entity : device + " " + entity;
    }

    /// <summary>设备分组里用的短名：只要实体那半截，用设备名的就显示设备名。</summary>
    public static string ShortName(Entity e) => EntityName(e) ?? DeviceName(e) ?? ObjectIdName(e);

    private static string ObjectIdName(Entity e) => e.EntityId[(e.EntityId.IndexOf('.', StringComparison.Ordinal) + 1)..].Replace('_', ' ');

    /// <summary>stripPrefixFromEntityName：实体名以「设备名 + 空格 / ': ' / ' - '」开头时去掉这段，首字母按 HA 的规则大写。</summary>
    private static string? StripPrefix(string name, string prefix)
    {
        foreach (var suffix in new[] { " ", ": ", " - " })
        {
            var p = prefix + suffix;
            if (name.StartsWith(p, StringComparison.OrdinalIgnoreCase) && name.Length > p.Length)
            {
                var rest = name[p.Length..];
                var space = rest.IndexOf(' ', StringComparison.Ordinal);
                var firstWord = space < 0 ? "" : rest[..space];
                return firstWord.ToLowerInvariant() != firstWord ? rest : char.ToUpper(rest[0], CultureInfo.InvariantCulture) + rest[1..];
            }
        }

        return null;
    }

    // ---------------- 状态文字 ----------------

    public string StateText(Entity e) => StateText(e, e.State);

    /// <summary>computeStateDisplay：数值按显示精度带单位；日期时间、时间戳按本地时间；其余查 HA 的翻译，查不到显示原文。</summary>
    public string StateText(Entity e, string state)
    {
        if (!e.HasState || state == "unavailable")
        {
            return L.T("不可用", "Unavailable");
        }

        if (state == "unknown")
        {
            return L.T("未知", "Unknown");
        }

        var dc = e.DeviceClass;
        var unitAttr = e.Str("unit_of_measurement");
        var numeric = unitAttr is not null || e.Attributes.ContainsKey("state_class") || NumberDomains.Contains(e.Domain)
            || (e.Domain == "sensor" && dc is not null && dc is not ("enum" or "timestamp" or "date" or "uptime"));
        if (numeric && double.TryParse(state, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            var text = FormatNumber(e, state, number);
            var unit = (e.TranslationKey is { } tk && e.Platform is { } pf
                ? home.Translations.Get($"component.{pf}.entity.{e.Domain}.{tk}.unit_of_measurement")
                : null) ?? unitAttr;
            return unit is null ? text : text + (unit is "°" or "%" ? "" : " ") + unit;
        }

        if (DateTimeDomains.Contains(e.Domain))
        {
            return FormatLocalDateTime(state);
        }

        if (TimestampDomains.Contains(e.Domain) || (e.Domain == "sensor" && dc is "timestamp" or "uptime"))
        {
            return DateTimeOffset.TryParse(state, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)
                ? t.ToLocalTime().ToString("g", L.Culture)
                : state;
        }

        return (e.TranslationKey is { } key && e.Platform is { } platform
                ? home.Translations.Get($"component.{platform}.entity.{e.Domain}.{key}.state.{state}")
                : null)
            ?? (dc is not null ? home.Translations.Get($"component.{e.Domain}.entity_component.{dc}.state.{state}") : null)
            ?? home.Translations.Get($"component.{e.Domain}.entity_component._.state.{state}")
            ?? state;
    }

    /// <summary>
    /// 属性值的文字（computeAttributeValueDisplay）：风速、摆风、模式这类。查集成的翻译、类型的翻译，查不到显示原文。
    /// </summary>
    public string AttrText(Entity e, string attr, string value) =>
        (e.TranslationKey is { } key && e.Platform is { } platform
            ? home.Translations.Get($"component.{platform}.entity.{e.Domain}.{key}.state_attributes.{attr}.state.{value}")
            : null)
        ?? (e.DeviceClass is { } dc ? home.Translations.Get($"component.{e.Domain}.entity_component.{dc}.state_attributes.{attr}.state.{value}") : null)
        ?? home.Translations.Get($"component.{e.Domain}.entity_component._.state_attributes.{attr}.state.{value}")
        ?? value;

    /// <summary>数值按步长定小数位，带单位（± 调节器、滑条右端的数值用）。</summary>
    public static string NumberText(double value, string? unit, double step)
    {
        var digits = step >= 1 || step <= 0 ? 0 : Math.Min(3, (int)Math.Ceiling(-Math.Log10(step) - 1e-9));
        var text = value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), L.Culture);
        return unit is null ? text : text + (unit is "°" or "%" ? "" : " ") + unit;
    }

    /// <summary>
    /// getNumberFormatOptions：注册表有显示精度就按它；步长和数值都是整数时不带小数；
    /// 否则保留原文里的小数位数（HA 的 getDefaultFormatOptions）。
    /// </summary>
    private static string FormatNumber(Entity e, string raw, double value)
    {
        int digits;
        if (e.DisplayPrecision is { } p)
        {
            digits = p;
        }
        else if (e.Num("step") is { } step && step == Math.Floor(step) && value == Math.Floor(value))
        {
            digits = 0;
        }
        else
        {
            var dot = raw.IndexOf('.', StringComparison.Ordinal);
            digits = dot < 0 ? 0 : raw.Length - dot - 1;
            if (raw.Contains('e', StringComparison.OrdinalIgnoreCase))
            {
                digits = 2;
            }
        }

        return value.ToString("N" + Math.Clamp(digits, 0, 15).ToString(CultureInfo.InvariantCulture), L.Culture);
    }

    /// <summary>date / time / input_datetime 的状态不带时区，按本地时间显示。</summary>
    private static string FormatLocalDateTime(string state)
    {
        var parts = state.Split(' ');
        if (parts.Length == 2 && DateTime.TryParse(state, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
        {
            return dt.ToString("g", L.Culture);
        }

        if (parts.Length == 1 && state.Contains('-', StringComparison.Ordinal) && DateOnly.TryParse(state, CultureInfo.InvariantCulture, out var d))
        {
            return d.ToString("d", L.Culture);
        }

        if (parts.Length == 1 && state.Contains(':', StringComparison.Ordinal) && TimeOnly.TryParse(state, CultureInfo.InvariantCulture, out var t))
        {
            return t.ToString("t", L.Culture);
        }

        return state;
    }

    // ---------------- 图标 ----------------

    /// <summary>
    /// 图标（mdi 名字）：注册表里设的 → 状态属性 icon → 集成的图标规则（translation_key）→ 个别类型的特例
    /// → 类型的图标规则（device_class）→ 类型默认图标。和 HA 前端 ha-state-icon / entityIcon 的顺序一样。
    /// </summary>
    public string Icon(Entity e) => Icon(e, e.State);

    public string Icon(Entity e, string state)
    {
        if (e.Icon is { Length: > 0 } reg)
        {
            return reg;
        }

        if (e.Str("icon") is { Length: > 0 } attr)
        {
            return attr;
        }

        if (e.TranslationKey is { } key && e.Platform is { } platform && home.Icons.Entity(platform, e.Domain, key)?.Pick(state) is { } byKey)
        {
            return byKey;
        }

        if (SpecialIcon(e, state) is { } special)
        {
            return special;
        }

        if (home.Icons.Component(e.Domain, e.DeviceClass)?.Pick(state) is { } byClass)
        {
            return byClass;
        }

        return FallbackIcons.GetValueOrDefault(e.Domain, DefaultIcon);
    }

    /// <summary>HA 前端 stateIcon 里的特例（update 的安装中、sun、只有日期或时间的 input_datetime）。</summary>
    private static string? SpecialIcon(Entity e, string state) => e.Domain switch
    {
        "sun" => state == "above_horizon" ? "mdi:white-balance-sunny" : "mdi:weather-night",
        "input_datetime" when e.Attributes["has_date"]?.GetValueKind() == System.Text.Json.JsonValueKind.False => "mdi:clock",
        "input_datetime" when e.Attributes["has_time"]?.GetValueKind() == System.Text.Json.JsonValueKind.False => "mdi:calendar",
        "update" when e.Attributes["in_progress"]?.GetValueKind() is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.Number => "mdi:package-down",
        "update" => state == "on" ? "mdi:package-up" : "mdi:package",
        _ => null,
    };

    // ---------------- 开着 / 关着 ----------------

    /// <summary>stateActive：卡片要不要高亮。</summary>
    public static bool IsActive(Entity e) => IsActive(e, e.State);

    public static bool IsActive(Entity e, string state)
    {
        if (TimestampDomains.Contains(e.Domain))
        {
            return e.HasState && state != "unavailable";
        }

        if (!e.HasState || state is "unavailable" or "unknown")
        {
            return false;
        }

        if (state == "off" && e.Domain != "alert")
        {
            return false;
        }

        return e.Domain switch
        {
            "alarm_control_panel" => state != "disarmed",
            "alert" => state != "idle",
            "cover" or "valve" => state != "closed",
            "device_tracker" or "person" => state != "not_home",
            "lawn_mower" => state is not ("docked" or "paused"),
            "lock" => state != "locked",
            "media_player" => state != "standby",
            "vacuum" => state is not ("idle" or "docked" or "paused"),
            "plant" => state == "problem",
            "group" => state is "on" or "home" or "open" or "locked" or "problem",
            "timer" => state == "active",
            "camera" => state is "streaming" or "recording",
            _ => true,
        };
    }
}
