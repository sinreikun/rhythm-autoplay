namespace RhythmAutoplay.Core;

/// <summary>
/// 単ライン機械式の追跡器（v0.3.73、マスター発案）。
/// 判定文字（2 段で y≈0.71 まで）と判定ライン際のエフェクトが届かない
/// 高さに走査ラインを 1 本だけ置き、在席の ON/OFF と色だけを観測する。
/// 横断したら、その瞬間に到着予測（固定レートの等速外挿）で即発火する。
/// 鎖・適合・裏付け・穴埋めは一切持たない「機械式」に徹する（マスター
/// 指示）: 見方に万能さを求めて重なりや演出へ適応するほど誤検知が
/// 減らなかったため、解釈を排し、失敗は数えられる形でだけ起きる
/// ようにする。事前のオフライン試算（GOODBOUNCE の上部ライン在席
/// ログ、コンセンサス譜面対比）では、黄のゴミ発火が従来方式の
/// 168 件 → 3 件、残差 IQR は [−5.5, +10.5] ms と従来と僅差だった。
/// 矢印は v0.3.76 から従来の塊追跡 (LaneNoteTracker) へ委譲する。
/// 自前の波追跡は 2 方式とも実走で敗れた: 横断方式 (v0.3.73) は
/// 方向なし失効 102 件 + 方向の上偏り、方向ゾーン方式 (v0.3.75、
/// マスター提案の「方向だけ別の場所で読む」) は失効 208 件に悪化
/// した（帯の中での方向の読みが実際にはほぼ成立しない）。塊追跡は
/// 降下中の方向読みの最頻値で確定する実績方式で、「方向はタイミング
/// と別の場所・別の仕組みで読む」という分離自体はこの形で生きる。
/// </summary>
public sealed class SingleLineTracker : ILaneTracker
{
    /// <summary>走査ラインの Y（画面比率）。検出器の単ライン走査と共有する。</summary>
    public static double LineY { get; set; } = 0.665;

    /// <summary>傾き参照ラインの Y（v0.3.77）。発火はせず、発火ラインとの
    /// 横断間隔からそのノーツ自身の実効レートを測るためだけに使う。</summary>
    public static double UpperLineY { get; set; } = 0.60;

    /// <summary>到着推定への系統補正 [秒]。v0.3.73 の発火を合意集合と
    /// 照合すると予測が中央値で約 9ms 早かったため、-0.003 から
    /// +0.006 へ移した (v0.3.75)。</summary>
    public static double CorrectionSeconds { get; set; } = 0.006;

    // 横断観測の実効レート。在席の立ち上がりという観測量に固有の値で、
    // 従来ラインの実測で固定した 1.70 をそのまま使う（v0.3.44）。
    private const double CrossingRate = 1.70;
    private const double RefractorySeconds = 0.05;
    // 傾きペアの受理条件: 2 ラインの横断間隔が想定範囲にあり、
    // 割り出したレートが実測の分布内にあるときだけ傾き発火にする。
    // 範囲外は固定レートの単独発火へ落とす（機械式の縮退形）。
    // v0.3.78 で窓を実測の移動時間クラスタ（0.60〜0.85 秒）に合わせて
    // 狭めた（v0.3.77 の [0.30, 1.05] は広すぎた）。
    private const double MinPairDtSeconds = 0.55;
    private const double MaxPairDtSeconds = 0.80;
    private const double MinSlopeRate = 1.50;
    private const double MaxSlopeRate = 1.90;
    // 参照ラインのエッジ衛生（v0.3.78）: 上方の薄いノーツは厳格な彩度
    // 下限の前後で点滅し、1 ノーツに複数の立ち上がりが出る（v0.3.77
    // 実測: PL2 エッジの多くが本物の移動時間より手前に湧いた）。対策は
    // (a) 在席が 2 フレーム続いたエピソードだけを数える、(b) エピソード
    // の最初の横断だけを記録し、エピソード内の後の点滅では上書きしない
    // （エピソード間隔 0.35 秒で区切る）。
    private const double UpperEpisodeGapSeconds = 0.35;

    private readonly double _judgmentW;
    private readonly double _vanishingY;
    private readonly LaneNoteTracker _arrowTracker;
    // 在席は [ライン (0 = 参照, 1 = 発火), 色] で持つ。
    private readonly bool[,] _prevPresence = new bool[2, 5];
    private readonly double[,] _lastCrossAt = new double[2, 5];
    // 色ごとの直近の参照ライン横断時刻（傾きペア用、未観測は NaN）。
    private readonly double[] _upperCrossAt = new double[5];
    // 参照ラインのエピソード管理（v0.3.78）: 連続在席フレーム数、
    // エピソード開始フレームの横断時刻、最後にエッジを記録した時刻。
    private readonly int[] _upperRun = new int[5];
    private readonly double[] _upperRunStart = new double[5];
    private readonly double[] _upperEdgeAt = new double[5];
    private double _lastFrameTime = double.NaN;
    private double _frameDelta = 1.0 / 31;

    public SingleLineTracker(
        double judgmentY,
        double vanishingY,
        double spawnGateY,
        double commitWindowSeconds)
    {
        _judgmentW = 1.0 / (judgmentY - vanishingY);
        _vanishingY = vanishingY;
        // 矢印の委譲先は従来ライン方式と同一の構成にする。
        _arrowTracker = new LaneNoteTracker(
            judgmentY,
            vanishingY: vanishingY,
            spawnGateY: spawnGateY,
            scheduleHorizonSeconds: 1.2,
            commitWindowSeconds: commitWindowSeconds);
        for (int li = 0; li < 2; li++)
        {
            for (int k = 0; k < 5; k++)
            {
                _lastCrossAt[li, k] = double.NegativeInfinity;
            }
        }
        for (int k = 0; k < 5; k++)
        {
            _upperCrossAt[k] = double.NaN;
            _upperRunStart[k] = double.NaN;
            _upperEdgeAt[k] = double.NegativeInfinity;
        }
    }

    private bool _collectSamples;

    public bool CollectSamples
    {
        get => _collectSamples;
        set
        {
            _collectSamples = value;
            _arrowTracker.CollectSamples = value;
        }
    }

    private double W(double y) => 1.0 / (y - _vanishingY);

    /// <summary>その高さの横断から判定ライン到達までのリード [秒]（補正込み）。</summary>
    private double LeadFrom(double y) => (W(y) - _judgmentW) / CrossingRate + CorrectionSeconds;

    public List<ScheduledNote> Observe(double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var scheduled = new List<ScheduledNote>();
        if (!double.IsNaN(_lastFrameTime) && timeSeconds > _lastFrameTime)
        {
            _frameDelta = Math.Clamp(timeSeconds - _lastFrameTime, 0.005, 0.1);
        }
        _lastFrameTime = timeSeconds;

        // 矢印は従来方式へ（従来ライン方式と同じ委譲）。
        var arrows = new List<NoteDetection>();
        foreach (NoteDetection d in laneDetections)
        {
            if (d.Kind == NoteKind.Arrow)
            {
                arrows.Add(d);
            }
        }
        scheduled.AddRange(_arrowTracker.Observe(
            timeSeconds,
            arrows.Count > 0 ? arrows : Array.Empty<NoteDetection>()));

        // 両ラインの在席。紫は「未解決の重なり」として発火させない
        // （種別としては存在しない: マスター確認 + 全セッションログ
        // 実測で紫の発火は 13 / 約 17,500 件、初期の 3 ランのみ）。
        var presence = new bool[2, 5];
        foreach (NoteDetection d in laneDetections)
        {
            if (!d.IsLinePresence || d.Kind == NoteKind.Arrow || d.Kind == NoteKind.Purple)
            {
                continue;
            }
            if (Math.Abs(d.Y - UpperLineY) < 0.01)
            {
                presence[0, (int)d.Kind] = true;
            }
            else if (Math.Abs(d.Y - LineY) < 0.01)
            {
                presence[1, (int)d.Kind] = true;
            }
        }
        // 横断時刻は「不在→在席」の中点に置く（従来ラインと同じ約束）。
        double crossTime = timeSeconds - _frameDelta / 2;
        // 参照ラインの横断は発火せず、傾きペアの片端として記録する。
        // エッジ衛生（v0.3.78）: 在席が 2 フレーム続いた時点で、その
        // エピソードの開始横断を 1 回だけ記録する。エピソード内の
        // 点滅による後の立ち上がりでは上書きしない。
        for (int k = 0; k < 5; k++)
        {
            if (k == (int)NoteKind.Arrow || k == (int)NoteKind.Purple)
            {
                continue;
            }
            if (presence[0, k])
            {
                if (_upperRun[k] == 0)
                {
                    _upperRunStart[k] = crossTime;
                }
                _upperRun[k]++;
                if (_upperRun[k] == 2
                    && timeSeconds - _upperEdgeAt[k] > UpperEpisodeGapSeconds)
                {
                    _upperEdgeAt[k] = timeSeconds;
                    _upperCrossAt[k] = _upperRunStart[k];
                }
            }
            else
            {
                _upperRun[k] = 0;
            }
        }
        // 発火ラインの横断で発火する。参照ラインとペアが組めれば、その
        // ノーツ自身の実効レートで外挿する（2 観測・1 除算・1 クランプ
        // だけの機械式）。組めなければ固定レートで発火する。
        for (int k = 0; k < 5; k++)
        {
            if (k == (int)NoteKind.Arrow || k == (int)NoteKind.Purple)
            {
                continue;
            }
            if (presence[1, k] && !_prevPresence[1, k]
                && timeSeconds - _lastCrossAt[1, k] > RefractorySeconds)
            {
                _lastCrossAt[1, k] = timeSeconds;
                double tHit;
                int samples = 1;
                double tA = _upperCrossAt[k];
                double dt = crossTime - tA;
                if (!double.IsNaN(tA) && dt >= MinPairDtSeconds && dt <= MaxPairDtSeconds)
                {
                    double rate = (W(UpperLineY) - W(LineY)) / dt;
                    if (rate >= MinSlopeRate && rate <= MaxSlopeRate)
                    {
                        tHit = crossTime + (W(LineY) - _judgmentW) / rate
                            + CorrectionSeconds;
                        samples = 2;
                    }
                    else
                    {
                        tHit = crossTime + LeadFrom(LineY);
                    }
                }
                else
                {
                    tHit = crossTime + LeadFrom(LineY);
                }
                _upperCrossAt[k] = double.NaN;
                scheduled.Add(new ScheduledNote(
                    (NoteKind)k, null, tHit, 0.0, samples,
                    LineY, crossTime));
            }
        }
        Array.Copy(presence, _prevPresence, presence.Length);
        return scheduled;
    }

    public List<ExpiredTrack> DrainExpired() => _arrowTracker.DrainExpired();

    public List<SuppressedDuplicate> DrainSuppressed() => _arrowTracker.DrainSuppressed();

    public List<RevisedNote> DrainRevisions() => _arrowTracker.DrainRevisions();

    public List<(int Id, double Time, double Y)> DrainSampleLog() => _arrowTracker.DrainSampleLog();
}
