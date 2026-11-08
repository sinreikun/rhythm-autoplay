namespace RhythmAutoplay.Core;

/// <summary>プロファイルで指定する色（スポイトで拾った RGB）。</summary>
public sealed record ProfileColor(byte R, byte G, byte B)
{
    /// <summary>HSV 表現（H は 0..1 の色相環）。</summary>
    public (double H, double S, double V) Hsv => ColorClassifier.RgbToHsv(R, G, B);

    /// <summary>"#RRGGBB" 形式。</summary>
    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>"#RRGGBB" / "RRGGBB" を読む。読めなければ FormatException。</summary>
    public static ProfileColor Parse(string text)
    {
        string t = text.Trim();
        if (t.StartsWith('#'))
        {
            t = t[1..];
        }
        if (t.Length != 6)
        {
            throw new FormatException($"色は #RRGGBB 形式で指定してください: {text}");
        }
        byte r = Convert.ToByte(t[..2], 16);
        byte g = Convert.ToByte(t[2..4], 16);
        byte b = Convert.ToByte(t[4..6], 16);
        return new ProfileColor(r, g, b);
    }
}

/// <summary>
/// 色指定のキー割り当て（汎用プロファイル用）。そのレーンで、指定した色の
/// どれかに近いノーツが来たら Key を押す。色は複数指定でき、グラデーション
/// や発光でばらつく同種ノーツをまとめて拾う。HueTolerance は色相環
/// （0..1）での許容差。
/// </summary>
public sealed record NoteColorBinding(
    string Key,
    IReadOnlyList<ProfileColor> Colors,
    double HueTolerance = 0.06);

/// <summary>1レーン分のキー割り当て。HYPNOSYNC はレーンごとに赤/青の2キーを持つ。</summary>
public sealed record LaneBinding(int Lane, string RedKey, string BlueKey)
{
    /// <summary>
    /// 色指定の割り当て（任意）。非 null で色が 1 件以上あれば、そのレーンは
    /// 色ファミリー（赤/青）ではなく指定色で判定する。対応する検出器は
    /// line1 のみで、他のエンジンでは PlaySession が line1 に固定する。
    /// </summary>
    public IReadOnlyList<NoteColorBinding>? Bindings { get; init; }
}

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

    /// <summary>1 レーンあたりの色割り当ての上限（NoteKind の Binding0..3 に対応）。</summary>
    public const int MaxColorBindingsPerLane = 4;

    /// <summary>色指定の割り当てを持つレーンが 1 つでもあるか。</summary>
    public bool UsesColorBindings => Lanes.Any(l => l.Bindings is { Count: > 0 });
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
            case NoteKind.Binding0:
            case NoteKind.Binding1:
            case NoteKind.Binding2:
            case NoteKind.Binding3:
                int bindingIndex = (int)note.Kind - (int)NoteKind.Binding0;
                if (lane.Bindings is { } bindings && bindingIndex < bindings.Count)
                {
                    AddKey(bindings[bindingIndex].Key);
                }
                break;
        }

        return events;
    }
}
