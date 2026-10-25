using System.Globalization;
using System.Text.Json.Nodes;

namespace HaTray.Model;

/// <summary>用户主动选择的条件；比较原始值，显示文字由 Presenter 翻译。</summary>
internal sealed record ReminderRule(string Kind, string[] Values, double? Threshold = null,
    double? ResetThreshold = null, string? Attribute = null, string? AttributeValue = null, bool Enabled = true)
{
    public JsonObject ToJson() => new()
    {
        ["kind"] = Kind,
        ["values"] = new JsonArray([.. Values.Select(v => (JsonNode)v)]),
        ["threshold"] = Threshold,
        ["reset_threshold"] = ResetThreshold,
        ["attribute"] = Attribute,
        ["attribute_value"] = AttributeValue,
        ["enabled"] = Enabled,
    };

    public static ReminderRule? FromJson(JsonNode? node)
    {
        if (node is not JsonObject o)
        {
            return null;
        }

        var kind = Text(o["kind"]);
        if (kind is not ("state" or "event" or "below" or "above"))
        {
            return null;
        }

        var values = o["values"] is JsonArray a ? a.Select(Text).OfType<string>().Distinct().ToArray() : [];
        var threshold = Number(o["threshold"]);
        var reset = Number(o["reset_threshold"]);
        if ((kind is "below" or "above") && (threshold is null ||
            (reset is { } r && (kind == "below" ? r < threshold : r > threshold))))
        {
            return null;
        }

        return new ReminderRule(kind, values, threshold, reset, Text(o["attribute"]), Text(o["attribute_value"]),
            o["enabled"] is not JsonValue v || !v.TryGetValue<bool>(out var enabled) || enabled);
    }

    public static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static string? Scalar(JsonNode? node) => node is JsonValue ? Text(node) ?? node.ToJsonString() : null;

    public static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue<double>(out var n) && double.IsFinite(n) ? n : null;

    public static bool TryNumber(string value, out double number) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && double.IsFinite(number);
}
