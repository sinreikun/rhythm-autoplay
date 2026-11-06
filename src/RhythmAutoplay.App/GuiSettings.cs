using System.Text.Json;
using RhythmAutoplay.Core;

namespace RhythmAutoplay.App;

/// <summary>プロファイルごとの形状上書き（GUI のドラッグ調整の保存先）。</summary>
public sealed class GeometryOverride
{
    public double CenterX { get; set; }
    public double TopY { get; set; }
    public double TopHalfWidth { get; set; }
    public double BottomY { get; set; }
    public double BottomHalfWidth { get; set; }
    public double VanishingY { get; set; }
    public double JudgmentLineY { get; set; }

    public static GeometryOverride FromProfile(GameProfile profile) => new()
    {
        CenterX = profile.Geometry.CenterX,
        TopY = profile.Geometry.TopY,
        TopHalfWidth = profile.Geometry.TopHalfWidth,
        BottomY = profile.Geometry.BottomY,
        BottomHalfWidth = profile.Geometry.BottomHalfWidth,
        VanishingY = profile.Geometry.VanishingY,
        JudgmentLineY = profile.JudgmentLineY,
    };

    public GameProfile ApplyTo(GameProfile profile) => profile with
    {
        JudgmentLineY = JudgmentLineY,
        Geometry = new LaneGeometry(CenterX, TopY, TopHalfWidth, BottomY, BottomHalfWidth, VanishingY),
    };
}

/// <summary>
/// GUI の設定。ウィンドウ位置・タイミングオフセット・選択中プロファイル・
/// プロファイルごとの形状上書きを %LOCALAPPDATA%\RhythmAutoplay\gui-settings.json に保存する。
/// </summary>
public sealed class GuiSettings
{
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public double OffsetMilliseconds { get; set; }
    public string ProfileId { get; set; } = "hypnosync";
    public bool AlwaysOnTop { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public Dictionary<string, GeometryOverride> GeometryOverrides { get; set; } = new();

    private static string SettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RhythmAutoplay", "gui-settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static GuiSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                return JsonSerializer.Deserialize<GuiSettings>(File.ReadAllText(SettingsPath)) ?? new GuiSettings();
            }
        }
        catch
        {
            // 壊れた設定ファイルは既定値で起動する。
        }
        return new GuiSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 保存失敗は起動・終了を妨げない。
        }
    }
}
