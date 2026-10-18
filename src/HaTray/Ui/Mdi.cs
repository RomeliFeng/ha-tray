using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace HaTray.Ui;

/// <summary>
/// Material Design Icons：HA 的图标（mdi:xxx）都出自这套字体。字体和「名字 → 码位」对照表由 tools\fetch-mdi.ps1 生成，
/// 放在 Assets 里随程序走。不认识的名字（比如 HA 以后新加的图标）显示成问号框。
/// </summary>
internal static class Mdi
{
    /// <summary>不打包的程序里 ms-appx:/// 指向 exe 所在目录。</summary>
    public static readonly FontFamily Font = new("ms-appx:///Assets/mdi.ttf#Material Design Icons");

    private static readonly Lazy<Dictionary<string, string>> Glyphs = new(Load);

    public static int Count => Glyphs.Value.Count;

    /// <summary>"mdi:lightbulb" 或 "lightbulb" → 字符；不认识就是 null。</summary>
    public static string? Glyph(string? name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return null;
        }

        var key = name.StartsWith("mdi:", StringComparison.Ordinal) ? name[4..] : name;
        return Glyphs.Value.GetValueOrDefault(key);
    }

    public static FontIcon Icon(string? name, double size, Brush? foreground = null)
    {
        var icon = new FontIcon { FontFamily = Font, FontSize = size, Glyph = Glyph(name) ?? Glyph("help-box")! };
        if (foreground is not null)
        {
            icon.Foreground = foreground;
        }

        return icon;
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>();
        var file = Path.Combine(AppContext.BaseDirectory, "Assets", "mdi-codepoints.txt");
        foreach (var line in File.ReadLines(file))
        {
            var space = line.IndexOf(' ', StringComparison.Ordinal);
            if (space > 0 && int.TryParse(line.AsSpan(space + 1), System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                map[line[..space]] = char.ConvertFromUtf32(code);
            }
        }

        return map;
    }
}
