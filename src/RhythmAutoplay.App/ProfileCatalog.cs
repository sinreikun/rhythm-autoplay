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
        yield return UserProfilesDirectory;
    }

    /// <summary>利用者が GUI から保存するプロファイルの置き場（読み込み時も優先される）。</summary>
    public static string UserProfilesDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RhythmAutoplay", "profiles");

    public static string UserProfilePath(string gameId) =>
        Path.Combine(UserProfilesDirectory, gameId + ".json");

    /// <summary>その gameId の利用者ファイルが存在するか（組み込みのみなら false）。</summary>
    public static bool HasUserProfile(string gameId) => File.Exists(UserProfilePath(gameId));

    /// <summary>ファイル名に使える gameId か（半角英数・ハイフン・アンダースコアのみ）。</summary>
    public static bool IsValidGameId(string gameId) =>
        gameId.Length > 0 && gameId.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>
    /// プロファイルを利用者フォルダへ JSON で保存する。ParseProfileFile が読む形式と
    /// 対称に書くので、保存したものはそのまま再読み込みできる。
    /// </summary>
    public static void SaveProfile(GameProfile profile)
    {
        if (!IsValidGameId(profile.GameId))
        {
            throw new ArgumentException($"プロファイル名に使えない文字が含まれています: {profile.GameId}");
        }
        var arrowKeys = new Dictionary<string, string>();
        foreach ((string name, ArrowDirection dir) in new[]
        {
            ("left", ArrowDirection.Left), ("right", ArrowDirection.Right),
            ("up", ArrowDirection.Up), ("down", ArrowDirection.Down),
        })
        {
            if (profile.ArrowKeys.TryGetValue(dir, out string? key) && !string.IsNullOrWhiteSpace(key))
            {
                arrowKeys[name] = key;
            }
        }
        var dto = new
        {
            gameId = profile.GameId,
            windowProcessName = profile.WindowProcessName,
            laneCount = profile.LaneCount,
            judgmentLineY = profile.JudgmentLineY,
            tapHoldMilliseconds = profile.TapHoldMilliseconds,
            lanes = profile.Lanes.OrderBy(l => l.Lane)
                .Select(l => new
                {
                    lane = l.Lane,
                    redKey = l.RedKey,
                    blueKey = l.BlueKey,
                    bindings = l.Bindings is { Count: > 0 }
                        ? l.Bindings.Select(b => new
                        {
                            key = b.Key,
                            colors = b.Colors.Select(c => c.ToHex()).ToArray(),
                            hueTolerance = b.HueTolerance,
                        }).ToArray()
                        : null,
                }).ToArray(),
            arrowKeys = arrowKeys.Count > 0 ? arrowKeys : null,
            geometry = new
            {
                centerX = profile.Geometry.CenterX,
                topY = profile.Geometry.TopY,
                topHalfWidth = profile.Geometry.TopHalfWidth,
                bottomY = profile.Geometry.BottomY,
                bottomHalfWidth = profile.Geometry.BottomHalfWidth,
                vanishingY = profile.Geometry.VanishingY,
            },
        };
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        Directory.CreateDirectory(UserProfilesDirectory);
        File.WriteAllText(UserProfilePath(profile.GameId), JsonSerializer.Serialize(dto, options));
    }

    /// <summary>利用者ファイルを消す（組み込みプロファイル自体は消えない）。消せたら true。</summary>
    public static bool DeleteUserProfile(string gameId)
    {
        string path = UserProfilePath(gameId);
        if (!File.Exists(path))
        {
            return false;
        }
        File.Delete(path);
        return true;
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
            int laneIndex = laneEl.GetProperty("lane").GetInt32();
            List<NoteColorBinding>? bindings = null;
            if (laneEl.TryGetProperty("bindings", out JsonElement bindingsEl))
            {
                bindings = new List<NoteColorBinding>();
                foreach (JsonElement bindingEl in bindingsEl.EnumerateArray())
                {
                    var colors = new List<ProfileColor>();
                    foreach (JsonElement colorEl in bindingEl.GetProperty("colors").EnumerateArray())
                    {
                        colors.Add(ProfileColor.Parse(colorEl.GetString()!));
                    }
                    double tolerance = bindingEl.TryGetProperty("hueTolerance", out JsonElement tolEl)
                        ? Math.Clamp(tolEl.GetDouble(), 0.01, 0.30)
                        : 0.06;
                    bindings.Add(new NoteColorBinding(
                        bindingEl.GetProperty("key").GetString()!, colors, tolerance));
                }
                if (bindings.Count > GameProfile.MaxColorBindingsPerLane)
                {
                    throw new InvalidDataException(
                        $"レーン {laneIndex + 1} の色割り当てが上限 {GameProfile.MaxColorBindingsPerLane} 件を超えています");
                }
            }
            string? redKey = laneEl.TryGetProperty("redKey", out JsonElement redEl) ? redEl.GetString() : null;
            string? blueKey = laneEl.TryGetProperty("blueKey", out JsonElement blueEl) ? blueEl.GetString() : null;
            if (bindings is not { Count: > 0 })
            {
                // 従来形式は赤/青キーが必須（欠けていれば読み込みエラーにする）。
                redKey ??= laneEl.GetProperty("redKey").GetString();
                blueKey ??= laneEl.GetProperty("blueKey").GetString();
            }
            // 色指定だけのレーンでは赤/青キーを先頭の割り当てで補う（フォールバック用）。
            redKey ??= bindings![0].Key;
            blueKey ??= bindings!.Count > 1 ? bindings[1].Key : redKey;
            lanes.Add(new LaneBinding(laneIndex, redKey, blueKey) { Bindings = bindings });
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
