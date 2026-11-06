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
    /// キー配置はゲーム側のデフォルトそのまま（2026-11-06 にマスター提供の
    /// EDIT KEYBINDS 画面で実測）: 上段ボタン T1–T4（青）= R T Y U、
    /// 下段ボタン B1–B4（ピンク）= F G H J、レバー = W（上）A（左）S（下）D（右）。
    /// 利用者はゲーム内設定を変えなくてよい。
    /// 色と段の対応（青ノーツ→T段、赤/ピンクノーツ→B段）はボタンの色からの
    /// 推定で、v0.2 の実機検証で確定させる。
    /// </summary>
    public static GameProfile HypnosyncDefault() => new(
        GameId: "hypnosync",
        WindowProcessName: "HYPNOSYNC",
        LaneCount: 4,
        JudgmentLineY: 0.941,
        Lanes: new[]
        {
            new LaneBinding(0, "F", "R"),
            new LaneBinding(1, "G", "T"),
            new LaneBinding(2, "H", "Y"),
            new LaneBinding(3, "J", "U"),
        },
        ArrowKeys: new Dictionary<ArrowDirection, string>
        {
            [ArrowDirection.Left] = "A",
            [ArrowDirection.Right] = "D",
            [ArrowDirection.Down] = "S",
            [ArrowDirection.Up] = "W",
        },
        TapHoldMilliseconds: 40)
    {
        Geometry = LaneGeometry.HypnosyncEstimate,
    };

    /// <summary>レーン形状（透視）。検出器がサンプリング位置を決めるのに使う。</summary>
    public LaneGeometry Geometry { get; init; } = LaneGeometry.Default;
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
