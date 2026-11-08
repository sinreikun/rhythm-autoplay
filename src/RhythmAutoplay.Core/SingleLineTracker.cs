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
/// v0.3.81 で波救済（帯の証跡がある場合に限り塊追跡の色予約を通す）
/// を試したが、実走 2 回で棄却し v0.3.82 で撤去した。救済発火は
/// 約 130 件/回出たが、2 ラン間で再現したのは 31 件 (24%) だけで、
/// 残りは密な区間の塊の断片が自信ありげに追跡されたノイズだった。
/// そのノイズが押し間違い干渉で BASED を 973 / 795 へ落とした
/// （v0.3.80 の 1015〜1044 から悪化）。CRINGE は 14 / 20 と下がったが
/// 犠牲が大きい。密な譜面では塊の軌跡は証拠にならない、が結論。
/// 波に隠れたノーツの回収はこの方式では閉鎖する。
/// v0.3.83: サブフレーム補間を追加。発火ラインの走査 3 行（±2px）は
/// 約 15ms ずつずれて横断されるため、行別の立ち上がり時刻を各行の
/// リードで発火ライン横断へ換算して平均すると、横断時刻の量子化
/// 誤差（フレーム間隔の中点近似）が減る。PLR 生ログ（run 80/83/84、
/// 約 2,900 件）でのオフライン検証では残差 IQR 18.5 → 12.9ms、
/// ±20ms 内 89% → 96%。行別の読みは集約より約 14ms 遅れて立つ
/// （行内 2/3 標本が必要なため）ので、補間時の補正は -0.008 とする。
/// 発火の引き金は従来どおり集約在席の立ち上がりで、補間は時刻の
/// 精密化だけを行う（発火の有無・本数は変えない）。下行の立ち上がり
/// は次フレームで届くため、予約は 1 フレーム保留してから確定する
/// （到達まで約 1.07 秒あるので実害はない）。
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

    /// <summary>補間時の系統補正 [秒]（v0.3.83）。行別の立ち上がりは
    /// 集約在席より約 14ms 遅れる（PLR 実測）ため、集約用の +0.006 に
    /// 対して補間用は -0.008 とする。</summary>
    public static double InterpCorrectionSeconds { get; set; } = -0.008;

    /// <summary>走査 3 行の間隔（画面比率）。検出器の実間隔は 2 表示
    /// px で、内容高さ約 1,009〜1,048px に対して約 0.00195。</summary>
    public const double RowStepY = 0.00195;

    // 補間の対応付け窓: 行の立ち上がりを換算した推定が、集約の横断
    // 時刻からこの範囲内にあればその横断のものとして採用する。
    private const double AssocWindowSeconds = 0.06;

    // 横断観測の実効レート。在席の立ち上がりという観測量に固有の値で、
    // 従来ラインの実測で固定した 1.70 をそのまま使う（v0.3.44）。
    private const double CrossingRate = 1.70;
    private const double RefractorySeconds = 0.05;

    private readonly double _judgmentW;
    private readonly double _vanishingY;
    private readonly LaneNoteTracker _arrowTracker;
    private static readonly int KindSlots = Enum.GetValues<NoteKind>().Length;
    private readonly bool[] _prevPresence = new bool[KindSlots];
    private readonly double[] _lastCrossAt = new double[KindSlots];
    private readonly double[] _rowLead = new double[3];
    private readonly bool[,] _prevRowPresence = new bool[KindSlots, 3];
    private readonly List<(int Kind, int Row, double EdgeMid)> _rowEdges = new();
    private readonly List<(int Kind, double Cross0, double CreatedAt)> _pendings = new();
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
        for (int k = 0; k < KindSlots; k++)
        {
            _lastCrossAt[k] = double.NegativeInfinity;
        }
        for (int r = 0; r < 3; r++)
        {
            _rowLead[r] = (W(LineY + (r - 1) * RowStepY) - W(LineY)) / CrossingRate;
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

        // 発火ラインの在席。紫は「未解決の重なり」として発火させない
        // （種別としては存在しない: マスター確認 + 全セッションログ
        // 実測で紫の発火は 13 / 約 17,500 件、初期の 3 ランのみ）。
        // 参照ライン (UpperLineY) の検出はここでは使わない: 2 観測の
        // 傾きペアは v0.3.77 / v0.3.78 で 2 度実測し、上方の在席信号の
        // 点滅とエピソード融合のためペアの 83〜89% がゴミになった。
        // 傾き路線は閉鎖し、単ライン＋固定レートが line1 の完成形。
        // 集約在席 (PresenceRow なし) が発火の引き金、行別の在席
        // (PresenceRow -1/0/+1) は横断時刻の補間専用（v0.3.83）。
        var presence = new bool[KindSlots];
        var rowPresence = new bool[KindSlots, 3];
        foreach (NoteDetection d in laneDetections)
        {
            if (!d.IsLinePresence || d.Kind == NoteKind.Arrow || d.Kind == NoteKind.Purple)
            {
                continue;
            }
            if (Math.Abs(d.Y - LineY) >= 0.01)
            {
                continue;
            }
            if (d.PresenceRow is int pr && pr >= -1 && pr <= 1)
            {
                rowPresence[(int)d.Kind, pr + 1] = true;
            }
            else
            {
                presence[(int)d.Kind] = true;
            }
        }
        // 横断時刻は「不在→在席」の中点に置く（従来ラインと同じ約束）。
        double crossTime = timeSeconds - _frameDelta / 2;

        // 行の立ち上がりを記録する（補間の材料）。古いものは捨てる。
        for (int k = 0; k < KindSlots; k++)
        {
            if (k == (int)NoteKind.Arrow || k == (int)NoteKind.Purple)
            {
                continue;
            }
            for (int r = 0; r < 3; r++)
            {
                if (rowPresence[k, r] && !_prevRowPresence[k, r])
                {
                    _rowEdges.Add((k, r, crossTime));
                }
                _prevRowPresence[k, r] = rowPresence[k, r];
            }
        }
        _rowEdges.RemoveAll(e => timeSeconds - e.EdgeMid > 0.2);

        // 前フレームまでに生まれた保留を確定する。下行の立ち上がりは
        // 集約の横断より約 15ms 遅れて次フレームで届くため、1 フレーム
        // 待ってから行推定を平均する。行推定が 1 つも対応しなければ
        // 従来どおり集約の中点＋従来補正で確定する。
        for (int i = _pendings.Count - 1; i >= 0; i--)
        {
            (int kind, double cross0, double createdAt) = _pendings[i];
            if (timeSeconds <= createdAt)
            {
                continue;
            }
            _pendings.RemoveAt(i);
            double sum = 0;
            int n = 0;
            for (int r = 0; r < 3; r++)
            {
                foreach ((int ek, int er, double edgeMid) in _rowEdges)
                {
                    if (ek == kind && er == r
                        && Math.Abs(edgeMid + _rowLead[r] - cross0) <= AssocWindowSeconds)
                    {
                        sum += edgeMid + _rowLead[r];
                        n++;
                        break;
                    }
                }
            }
            double tHit;
            double crossEst;
            if (n > 0)
            {
                crossEst = sum / n;
                tHit = crossEst + (W(LineY) - _judgmentW) / CrossingRate
                    + InterpCorrectionSeconds;
            }
            else
            {
                crossEst = cross0;
                tHit = cross0 + LeadFrom(LineY);
            }
            scheduled.Add(new ScheduledNote(
                (NoteKind)kind, null, tHit, 0.0, 1,
                LineY, crossEst));
        }

        // 集約の立ち上がりが新しい横断。確定は次フレームの Observe で
        // 行うため、ここでは保留に積む。
        for (int k = 0; k < KindSlots; k++)
        {
            if (k == (int)NoteKind.Arrow || k == (int)NoteKind.Purple)
            {
                continue;
            }
            if (presence[k] && !_prevPresence[k]
                && timeSeconds - _lastCrossAt[k] > RefractorySeconds)
            {
                _lastCrossAt[k] = timeSeconds;
                _pendings.Add((k, crossTime, timeSeconds));
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
