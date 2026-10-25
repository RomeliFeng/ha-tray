using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace HaTray;

/// <summary>
/// 本机设置，存在 %LOCALAPPDATA%\HaTray\settings.json。令牌按「这台机器的这个用户」用 DPAPI 加密，拷到别处解不开。
/// </summary>
internal sealed class Settings
{
    public static readonly string DataDir = Environment.GetCommandLineArgs().Contains("--demo")
        ? Path.Combine(AppContext.BaseDirectory, "DemoData")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HaTray");
    private static readonly string FilePath = Path.Combine(DataDir, "settings.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "HaTray";

    /// <summary>HA 的地址，如 http://homeassistant.local:8123，末尾不带斜杠。</summary>
    public string Url { get; set; } = "";

    /// <summary>DPAPI 加密后的长期访问令牌，Base64。</summary>
    public string ProtectedToken { get; set; } = "";

    /// <summary>默认房间（HA 区域 id；空串是「未分配区域」；null 表示没设，用第一个房间）。每次弹出面板先显示它。</summary>
    public string? DefaultRoom { get; set; }

    /// <summary>打了这个标签的实体上主界面。</summary>
    public string LabelName { get; set; } = L.T("托盘", "Tray");

    /// <summary>界面语言："auto" / "zh" / "en"。</summary>
    public string Language { get; set; } = "auto";

    /// <summary>面板尺寸，单位是 DIP（100% 缩放下的像素）。</summary>
    public int Width { get; set; } = 400;

    public int Height { get; set; } = 640;

    /// <summary>仅控制本机接收；实体订阅保存在 HA。</summary>
    public bool RemindersEnabled { get; set; } = true;

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsConfigured => Uri.TryCreate(Url, UriKind.Absolute, out _) && ProtectedToken.Length > 0;

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public string GetToken()
    {
        if (ProtectedToken.Length == 0)
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }

    public void SetToken(string token)
    {
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        ProtectedToken = Convert.ToBase64String(bytes);
    }

    public static bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }

        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }
        }
    }
}
