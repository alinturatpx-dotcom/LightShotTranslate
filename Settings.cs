using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace LST;

public sealed class AppSettings
{
    public string HotkeyCapture { get; set; } = "Alt+A";
    public string HotkeyOcr { get; set; } = "Alt+S";
    public string HotkeyTranslate { get; set; } = "Alt+T";
    public string HotkeyPin { get; set; } = "Alt+P";

    public string ApiBaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public string TargetLanguage { get; set; } = "简体中文";

    public string SaveDirectory { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "LightShotTranslate");

    /// <summary>OCR 模型驻留：Light（空闲 30 秒释放）/ Balanced（5 分钟，默认）/ Resident（常驻，退出时释放）。</summary>
    public string OcrKeepMode { get; set; } = "Balanced";

    /// <summary>OCR 识别时的内存档位：Memory（省内存，大图稍慢）/ Balanced（默认）/ Accuracy（占用更高、大图更快）。不影响识别精度。</summary>
    public string OcrQuality { get; set; } = "Memory";

    /// <summary>配置版本，用于升级时迁移默认值。</summary>
    public int Version { get; set; } = 0;

    /// <summary>段落合并：Off / Strict / Standard / Loose。翻译窗口内按 M 可临时切换。</summary>
    public string MergeMode { get; set; } = "Standard";

    public bool AutoStart { get; set; } = false;

    /// <summary>API Key 经 Windows DPAPI 加密（仅当前 Windows 用户可解密），不会明文落盘。</summary>
    public string EncryptedApiKey { get; set; } = "";

    [JsonIgnore]
    public string ApiKey
    {
        get
        {
            if (string.IsNullOrEmpty(EncryptedApiKey)) return "";
            try
            {
                var raw = ProtectedData.Unprotect(Convert.FromBase64String(EncryptedApiKey), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(raw);
            }
            catch { return ""; }
        }
        set
        {
            EncryptedApiKey = string.IsNullOrEmpty(value)
                ? ""
                : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }

    // ---------- 持久化 ----------
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static string ConfigDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LightShotTranslate");

    private static string ConfigPath => Path.Combine(ConfigDir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(ConfigPath)) ?? new AppSettings();
                if (loaded.Version < 2) loaded.Version = 2;
                return loaded;
            }
        }
        catch { /* 配置损坏时回退默认值 */ }
        return new AppSettings { Version = 2 };
    }

    public void Save()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        Startup.Apply(AutoStart);
    }

    public AppSettings Clone() =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this))!;
}

internal static class Startup
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LightShotTranslate";

    public static void Apply(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;
            if (enable && Environment.ProcessPath is { } exe)
                key.SetValue(ValueName, $"\"{exe}\"");
            else
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { }
    }
}
