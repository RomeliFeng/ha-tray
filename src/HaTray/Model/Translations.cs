using System.Text.Json;

namespace HaTray.Model;

/// <summary>
/// HA 后端的翻译（frontend/get_translations 返回的扁平键值），比如
/// component.climate.entity_component._.state.cool → 制冷。
/// </summary>
internal sealed class Translations
{
    private readonly Dictionary<string, string> _map = [];

    public void Clear() => _map.Clear();

    public void Add(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Object && result.TryGetProperty("resources", out var res) && res.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in res.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String)
                {
                    _map[p.Name] = p.Value.GetString()!;
                }
            }
        }
    }

    public string? Get(string key) => _map.TryGetValue(key, out var v) && v.Length > 0 ? v : null;

    public int Count => _map.Count;
}

/// <summary>HA 的一条图标规则：默认图标，按状态的图标，按数值区间的图标（取不超过数值的最大门槛）。</summary>
internal sealed record IconRule(string? Default, IReadOnlyDictionary<string, string> State, IReadOnlyList<(double Threshold, string Icon)> Range)
{
    public static IconRule? Parse(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var state = new Dictionary<string, string>();
        if (e.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in s.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                state[p.Name] = p.Value.GetString()!;
            }
        }

        var range = new List<(double, string)>();
        if (e.TryGetProperty("range", out var r) && r.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in r.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                if (double.TryParse(p.Name, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t))
                {
                    range.Add((t, p.Value.GetString()!));
                }
            }

            range.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        }

        return new IconRule(J.Str(e, "default"), state, range);
    }

    /// <summary>照 HA 前端 getIconFromTranslations：先按状态精确匹配，再按数值区间，最后默认。</summary>
    public string? Pick(string? state)
    {
        if (state is { Length: > 0 } && State.TryGetValue(state, out var byState))
        {
            return byState;
        }

        if (state is not null && Range.Count > 0
            && double.TryParse(state, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)
            && v >= Range[0].Threshold)
        {
            return Range.Last(x => v >= x.Threshold).Icon;
        }

        return Default;
    }
}

/// <summary>
/// frontend/get_icons 的两类规则：entity_component（按类型、device_class）和 entity（按集成、类型、translation_key）。
/// </summary>
internal sealed class IconRules
{
    private readonly Dictionary<(string Domain, string DeviceClass), IconRule> _component = [];
    private readonly Dictionary<(string Platform, string Domain, string Key), IconRule> _entity = [];

    public void Clear()
    {
        _component.Clear();
        _entity.Clear();
    }

    /// <summary>resources: { domain: { device_class 或 "_": 规则 } }</summary>
    public void AddComponent(JsonElement result)
    {
        foreach (var domain in Resources(result))
        {
            foreach (var dc in domain.Value.EnumerateObject())
            {
                if (IconRule.Parse(dc.Value) is { } rule)
                {
                    _component[(domain.Name, dc.Name)] = rule;
                }
            }
        }
    }

    /// <summary>resources: { platform: { domain: { translation_key: 规则 } } }</summary>
    public void AddEntity(JsonElement result)
    {
        foreach (var platform in Resources(result))
        {
            foreach (var domain in platform.Value.EnumerateObject().Where(d => d.Value.ValueKind == JsonValueKind.Object))
            {
                foreach (var key in domain.Value.EnumerateObject())
                {
                    if (IconRule.Parse(key.Value) is { } rule)
                    {
                        _entity[(platform.Name, domain.Name, key.Name)] = rule;
                    }
                }
            }
        }
    }

    public IconRule? Component(string domain, string? deviceClass) =>
        (deviceClass is not null ? _component.GetValueOrDefault((domain, deviceClass)) : null) ?? _component.GetValueOrDefault((domain, "_"));

    public IconRule? Entity(string platform, string domain, string key) => _entity.GetValueOrDefault((platform, domain, key));

    public int Count => _component.Count + _entity.Count;

    private static IEnumerable<JsonProperty> Resources(JsonElement result) =>
        result.ValueKind == JsonValueKind.Object && result.TryGetProperty("resources", out var res) && res.ValueKind == JsonValueKind.Object
            ? res.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Object)
            : [];
}
