using System.Text.Json;
using RhythmAutoplay.Core;
using ArrowDirection = RhythmAutoplay.Core.ArrowDirection;

namespace RhythmAutoplay.App;

/// <summary>
/// 対象ゲームのプロファイル一覧。組み込み（HYPNOSYNC）に加えて、
/// profiles フォルダ（本体の隣と %LOCALAPPDATA%\RhythmAutoplay\profiles）の
/// JSON を読み込む。同じ gameId があれば JSON 側で置き換える（利用者の編集を優先）。
/// </summary>
public static class ProfileCatalog
{
    public static List<GameProfile> LoadAll(Action<string>? log = null)
    {
        var byId = new Dictionary<string, GameProfile>(StringComparer.OrdinalIgnoreCase)
        {
            [GameProfile.HypnosyncDefault().GameId] = GameProfile.HypnosyncDefault(),
        };

        foreach (string dir in ProfileDirectories())
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (string file in Directory.GetFiles(dir, "*.json"))
            {
                try
                {
                    GameProfile profile = ParseProfileFile(file);
                    byId[profile.GameId] = profile;
                }
                catch (Exception ex)
                {
                    log?.Invoke($"プロファイルを読み込めませんでした: {Path.GetFileName(file)} ({ex.Message})");
                }
            }
        }
        return byId.Values.OrderBy(p => p.GameId, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static IEnumerable<string> ProfileDirectories()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "profiles");
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RhythmAutoplay", "profiles");
    }

    private static GameProfile ParseProfileFile(string path)
    {
        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = doc.RootElement;

        string gameId = RequiredString(root, "gameId");
        string process = RequiredString(root, "windowProcessName");
        int laneCount = root.GetProperty("laneCount").GetInt32();
        double judgmentY = root.GetProperty("judgmentLineY").GetDouble();
        double tapHold = root.TryGetProperty("tapHoldMilliseconds", out JsonElement th) ? th.GetDouble() : 40;

        var lanes = new List<LaneBinding>();
        foreach (JsonElement laneEl in root.GetProperty("lanes").EnumerateArray())
        {
            lanes.Add(new LaneBinding(
                laneEl.GetProperty("lane").GetInt32(),
                laneEl.GetProperty("redKey").GetString()!,
                laneEl.GetProperty("blueKey").GetString()!));
        }
        lanes.Sort((a, b) => a.Lane.CompareTo(b.Lane));

        var arrows = new Dictionary<ArrowDirection, string>();
        if (root.TryGetProperty("arrowKeys", out JsonElement arrowEl))
        {
            foreach ((string name, ArrowDirection dir) in new[]
            {
                ("left", ArrowDirection.Left), ("right", ArrowDirection.Right),
                ("up", ArrowDirection.Up), ("down", ArrowDirection.Down),
            })
            {
                if (arrowEl.TryGetProperty(name, out JsonElement keyEl))
                {
                    arrows[dir] = keyEl.GetString()!;
                }
            }
        }

        LaneGeometry geometry = LaneGeometry.Default;
        if (root.TryGetProperty("geometry", out JsonElement geoEl))
        {
            geometry = new LaneGeometry(
                CenterX: geoEl.GetProperty("centerX").GetDouble(),
                TopY: geoEl.GetProperty("topY").GetDouble(),
                TopHalfWidth: geoEl.GetProperty("topHalfWidth").GetDouble(),
                BottomY: geoEl.GetProperty("bottomY").GetDouble(),
                BottomHalfWidth: geoEl.GetProperty("bottomHalfWidth").GetDouble(),
                VanishingY: geoEl.TryGetProperty("vanishingY", out JsonElement vy) ? vy.GetDouble() : 0.10);
        }

        return new GameProfile(gameId, process, laneCount, judgmentY, lanes, arrows, tapHold)
        {
            Geometry = geometry,
        };
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.GetProperty(name).GetString() ?? throw new InvalidDataException($"{name} が空です");
}
