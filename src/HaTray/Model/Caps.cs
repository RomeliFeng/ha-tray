namespace HaTray.Model;

/// <summary>
/// 实体能做什么：照 HA 前端的判断（data/light.ts、data/fan.ts、data/number.ts、data/climate.ts 和各类型的 *EntityFeature）。
/// 功能位数值来自 HA 核心源码。
/// </summary>
internal static class Caps
{
    // ---- 功能位（HA 核心的 *EntityFeature）----
    public const int ClimateTargetTemp = 1, ClimateTargetRange = 2, ClimateTargetHumidity = 4, ClimateFanMode = 8, ClimatePreset = 16,
        ClimateSwing = 32, ClimateTurnOff = 128, ClimateTurnOn = 256, ClimateSwingHorizontal = 512;

    public const int FanSetSpeed = 1, FanOscillate = 2, FanDirection = 4, FanPreset = 8, FanTurnOff = 16, FanTurnOn = 32;

    public const int HumidifierModes = 1;

    public const int LightEffect = 4;

    public const int CoverOpen = 1, CoverClose = 2, CoverSetPosition = 4, CoverStop = 8;

    public const int LockOpen = 1;

    public const int MediaPause = 1, MediaVolumeSet = 4, MediaPrevious = 16, MediaNext = 32, MediaTurnOn = 128, MediaTurnOff = 256, MediaPlay = 16384;

    public const int VacuumPause = 4, VacuumStop = 8, VacuumReturnHome = 16, VacuumStart = 8192;

    public const int LawnMowerStart = 1, LawnMowerPause = 2, LawnMowerDock = 4;

    public const int WaterHeaterTargetTemp = 1, WaterHeaterOperation = 2, WaterHeaterOnOff = 8;

    public const int SirenTurnOn = 1, SirenTurnOff = 2;

    /// <summary>HA 前端 HVAC_MODES 的顺序，模式按它排。</summary>
    private static readonly string[] HvacOrder = ["auto", "heat_cool", "heat", "cool", "dry", "fan_only", "off"];

    private static readonly HashSet<string> BrightnessModes = ["hs", "xy", "rgb", "rgbw", "rgbww", "color_temp", "brightness", "white"];

    private static readonly HashSet<string> ColorModes = ["hs", "xy", "rgb", "rgbw", "rgbww"];

    // ---- 灯 ----
    public static bool LightBrightness(Entity e) => e.Strings("supported_color_modes").Any(BrightnessModes.Contains);

    public static bool LightColor(Entity e) => e.Strings("supported_color_modes").Any(ColorModes.Contains);

    public static bool LightColorTemp(Entity e) => e.Strings("supported_color_modes").Contains("color_temp");

    /// <summary>亮度百分比（属性 brightness 是 0–255）；关着时 HA 不报亮度，返回 null。</summary>
    public static int? BrightnessPct(Entity e) => e.Num("brightness") is { } b ? (int)Math.Round(b / 255 * 100) : null;

    // ---- 空调 ----
    public static IReadOnlyList<string> HvacModes(Entity e) =>
        [.. e.Strings("hvac_modes").OrderBy(m => Array.IndexOf(HvacOrder, m) is var i && i < 0 ? 99 : i)];

    /// <summary>温度步长：target_temp_step，没有就 °F 用 1、°C 用 0.5（HA 前端的规则）。</summary>
    public static double TempStep(Entity e, bool fahrenheit = false) => e.Num("target_temp_step") ?? (fahrenheit ? 1 : 0.5);

    // ---- 风扇 ----
    /// <summary>computeFanSpeedCount：档数（含「关」）= round(100 / percentage_step) + 1。</summary>
    public static int FanSpeedCount(Entity e) => (int)Math.Round(100 / (e.Num("percentage_step") ?? 1)) + 1;

    /// <summary>FAN_SPEEDS：不超过 4 档时用分段按钮。</summary>
    public static IReadOnlyList<string>? FanSpeeds(Entity e) => FanSpeedCount(e) switch
    {
        2 => ["off", "on"],
        3 => ["off", "low", "high"],
        4 => ["off", "low", "medium", "high"],
        _ => null,
    };

    public static string FanSpeedText(string speed) => speed switch
    {
        "off" => L.T("关", "Off"),
        "on" => L.T("开", "On"),
        "low" => L.T("低", "Low"),
        "medium" => L.T("中", "Medium"),
        _ => L.T("高", "High"),
    };

    /// <summary>fanPercentageToSpeed / fanSpeedToPercentage。</summary>
    public static string FanSpeedOf(Entity e)
    {
        var speeds = FanSpeeds(e);
        if (speeds is null || e.State != "on")
        {
            return "off";
        }

        var index = (int)Math.Round((e.Num("percentage") ?? 0) / (e.Num("percentage_step") ?? 1));
        return index >= 0 && index < speeds.Count ? speeds[index] : speeds[^1];
    }

    public static int FanPercentage(Entity e, string speed)
    {
        var speeds = FanSpeeds(e) ?? [];
        var i = speeds.IndexOf(speed);
        return i < 0 ? 0 : (int)Math.Floor(i * (e.Num("percentage_step") ?? 1));
    }

    // ---- 数值 ----
    /// <summary>showNumberSlider：mode 是 slider，或 mode 是 auto 且 (max − min) / step ≤ 256。</summary>
    public static bool NumberSlider(Entity e)
    {
        var mode = e.Str("mode");
        var min = e.Num("min") ?? 0;
        var max = e.Num("max") ?? 100;
        var step = e.Num("step") ?? 1;
        return mode == "slider" || (mode == "auto" && (max - min) / step <= 256);
    }

    private static int IndexOf(this IReadOnlyList<string> list, string value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value)
            {
                return i;
            }
        }

        return -1;
    }
}
