using System.Globalization;
using System.Runtime.InteropServices;

namespace HaTray;

/// <summary>
/// 界面文案，两种说法写在调用处，不拆资源文件。语言可以固定成中文或英文，也可以选「自动」：
/// 自动时跟随 Home Assistant 服务器的语言（get_config 的 language，以 zh 开头就用中文，否则英文）；
/// 还没连上 HA（或演示模式）时先跟随 Windows 显示语言。
/// 跟 HA 而不跟 Windows，是因为实体名字、集成给的选项（比如小米空调的「自动 / 低 / 中 / 高」）都是 HA 那边的语言，
/// 界面用另一种语言就会中英混杂（2026-10-18 用户发现）。
/// </summary>
internal static partial class L
{
    public static bool Chinese { get; private set; } = SystemIsChinese;

    /// <summary>"auto" / "zh" / "en"。</summary>
    public static string Mode { get; private set; } = "auto";

    /// <summary>自动模式：语言跟着 HA 走。</summary>
    public static bool FollowsHa => Mode is not ("zh" or "en");

    /// <summary>排序、数字和时间格式用的区域。</summary>
    public static CultureInfo Culture => Chinese ? CultureInfo.GetCultureInfo("zh-CN") : CultureInfo.CurrentCulture;

    /// <summary>向 HA 要翻译时用的语言代码，和界面语言一致。</summary>
    public static string HaLanguage => Chinese ? "zh-Hans" : "en";

    private static bool SystemIsChinese => (GetUserDefaultUILanguage() & 0x3FF) == 0x04;

    public static void Init(string mode)
    {
        Mode = mode;
        Chinese = mode switch
        {
            "zh" => true,
            "en" => false,
            _ => SystemIsChinese,
        };
    }

    /// <summary>自动模式下，HA 的语言（如 zh-Hans、en）对应的界面语言是不是中文；固定模式不变。</summary>
    public static bool ChineseFor(string? haLanguage) =>
        FollowsHa && haLanguage is { Length: > 0 } ? haLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase) : Chinese;

    /// <summary>连上 HA 后（UI 线程上）按 HA 的语言定下界面语言。</summary>
    public static void UseHaLanguage(string? haLanguage) => Chinese = ChineseFor(haLanguage);

    public static string T(string zh, string en) => Chinese ? zh : en;

    [LibraryImport("kernel32.dll")]
    private static partial ushort GetUserDefaultUILanguage();
}
