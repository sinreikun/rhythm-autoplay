namespace RhythmAutoplay.Core;

/// <summary>1レーン分のキー割り当て。HYPNOSYNC はレーンごとに赤/青の2キーを持つ。</summary>
public sealed record LaneBinding(int Lane, string RedKey, string BlueKey);

/// <summary>
/// ゲーム別プロファイル。コードではなくデータとして持つことが汎用化の要（docs/design.md §4）。
/// 座標はゲームウィンドウ内の正規化座標 (0..1)。
/// </summary>
public sealed record GameProfile(
    string GameId,
    string WindowProcessName,
    int LaneCount,
    double JudgmentLineY,
    IReadOnlyList<LaneBinding> Lanes,
    IReadOnlyDictionary<ArrowDirection, string> ArrowKeys,
    double TapHoldMilliseconds)
{
    /// <summary>
    /// HYPNOSYNC の既定プロファイル。
    /// キー配置はコミュニティで実績のある配置（赤=D F J K / 青=E R U I / 方向=S L V N）。
    /// ゲーム内設定（Settings → View Keybinds → Edit Keybinds）をこの配置に合わせる前提。
    /// </summary>
    public static GameProfile HypnosyncDefault() => new(
        GameId: "hypnosync",
        WindowProcessName: "HYPNOSYNC",
        LaneCount: 4,
        JudgmentLineY: 0.94,
        Lanes: new[]
        {
            new LaneBinding(0, "D", "E"),
            new LaneBinding(1, "F", "R"),
            new LaneBinding(2, "J", "U"),
            new LaneBinding(3, "K", "I"),
        },
        ArrowKeys: new Dictionary<ArrowDirection, string>
        {
            [ArrowDirection.Left] = "S",
            [ArrowDirection.Right] = "L",
            [ArrowDirection.Down] = "V",
            [ArrowDirection.Up] = "N",
        },
        TapHoldMilliseconds: 40);
}

/// <summary>ノーツ1件を、押すべき入力イベント列に変換する。</summary>
public static class KeyPlanBuilder
{
    public static IReadOnlyList<ScheduledInput> BuildTap(GameProfile profile, NoteDetection note, double hitTimeSeconds)
    {
        if (note.Lane < 0 || note.Lane >= profile.Lanes.Count)
        {
            return Array.Empty<ScheduledInput>();
        }

        LaneBinding lane = profile.Lanes[note.Lane];
        double upTime = hitTimeSeconds + profile.TapHoldMilliseconds / 1000.0;
        var events = new List<ScheduledInput>();

        void AddKey(string key)
        {
            events.Add(new ScheduledInput(hitTimeSeconds, key, IsDown: true));
            events.Add(new ScheduledInput(upTime, key, IsDown: false));
        }

        switch (note.Kind)
        {
            case NoteKind.Red:
                AddKey(lane.RedKey);
                break;
            case NoteKind.Blue:
                AddKey(lane.BlueKey);
                break;
            case NoteKind.Purple:
                AddKey(lane.RedKey);
                AddKey(lane.BlueKey);
                break;
            case NoteKind.Yellow:
                // 黄はどちらでも可。赤キーに統一する（プロファイルで一貫させる）。
                AddKey(lane.RedKey);
                break;
            case NoteKind.Arrow:
                if (note.Direction is ArrowDirection dir && profile.ArrowKeys.TryGetValue(dir, out string? arrowKey))
                {
                    AddKey(arrowKey);
                }
                break;
        }

        return events;
    }
}
