namespace HaTray.Model;

internal sealed record ReminderMemory(string Rule, string? State, string? Event, bool Active);

/// <summary>只判断一条主动订阅。事件按新时间戳，状态按进入条件；未知保留上次有效结果。</summary>
internal sealed class ReminderEngine(Dictionary<string, ReminderMemory>? memory = null)
{
    public Dictionary<string, ReminderMemory> Memory { get; } = memory ?? [];

    public bool Changed { get; private set; }

    public bool Observe(Entity e, bool snapshot)
    {
        Changed = false;
        var key = e.RegistryId ?? e.EntityId;
        var rule = e.Tray.Reminder;
        if (rule is not { Enabled: true } || e.Disabled || e.Hidden)
        {
            Changed = Memory.Remove(key);
            return false;
        }

        var fingerprint = rule.ToJson().ToJsonString();
        Memory.TryGetValue(key, out var previous);
        if (previous?.Rule != fingerprint)
        {
            previous = null;
        }

        if (!e.HasState || e.State is "unknown" or "unavailable")
        {
            // 尚未发生过事件也要建立订阅基线，否则第一次真实事件会被漏掉。
            if (e.HasState && e.State == "unknown" && rule.Kind == "event" && previous is null)
            {
                Memory[key] = new(fingerprint, null, null, false);
                Changed = true;
            }
            return false;
        }

        var attributeMatches = string.IsNullOrEmpty(rule.Attribute) ||
            ReminderRule.Scalar(e.Attributes[rule.Attribute]) == rule.AttributeValue;
        bool fire;
        ReminderMemory next;
        if (rule.Kind == "event")
        {
            if (e.Domain != "event" || !DateTimeOffset.TryParse(e.State, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out _))
            {
                return false;
            }

            fire = !snapshot && previous is not null && previous.Event != e.State &&
                rule.Values.Contains(e.Str("event_type"), StringComparer.Ordinal) && attributeMatches;
            next = new(fingerprint, null, e.State, false);
        }
        else
        {
            bool active;
            if (rule.Kind is "below" or "above")
            {
                if (rule.Threshold is not { } threshold || !ReminderRule.TryNumber(e.State, out var value))
                {
                    return false;
                }

                active = rule.Kind == "below" ? value < threshold : value > threshold;
                if (previous?.Active == true && rule.ResetThreshold is { } reset)
                {
                    active = rule.Kind == "below" ? value < reset : value > reset;
                }
            }
            else
            {
                active = rule.Kind == "state" && rule.Values.Contains(e.State, StringComparer.Ordinal);
            }

            active &= attributeMatches;
            // 多选枚举里从一个已订阅的状态变成另一个，也是一条新提醒。
            fire = active && (previous?.Active != true || (rule.Kind == "state" && previous.State != e.State));
            next = new(fingerprint, e.State, null, active);
        }

        if (previous != next || !Memory.ContainsKey(key))
        {
            Memory[key] = next;
            Changed = true;
        }

        return fire;
    }
}
