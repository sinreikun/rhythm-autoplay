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
/// 矢印は帯の下端がラインを横切る瞬間に、検出器が白の形状から読んだ
/// 方向つきで同じ機械式に載せる（方向だけは色でなく形状で決める）。
/// </summary>
public sealed class SingleLineTracker : ILaneTracker
{
    /// <summary>走査ラインの Y（画面比率）。検出器の単ライン走査と共有する。</summary>
    public static double LineY { get; set; } = 0.665;

    /// <summary>到着推定への系統補正 [秒]。まずは従来ラインと同じ -0.003 から。</summary>
    public static double CorrectionSeconds { get; set; } = -0.003;

    // 横断観測の実効レート。在席の立ち上がりという観測量に固有の値で、
    // 従来ラインの実測で固定した 1.70 をそのまま使う（v0.3.44）。
    private const double CrossingRate = 1.70;
    private const double RefractorySeconds = 0.05;
    // 矢印の対応付け: 同じ波とみなす X のずれと、観測が途切れてよい時間。
    private const double ArrowMatchXTolerance = 0.06;
    private const double ArrowStaleSeconds = 0.25;
    // 方向が未確定のまま横断した場合に、方向の確定を待つ猶予。
    // 発火は約 0.8 秒先なので、この待ちは発火時刻に影響しない。
    private const double ArrowDirectionWaitSeconds = 0.20;

    private sealed class ArrowObs
    {
        public ArrowDirection? Direction;
        public double X;
        public double LastY;
        public double LastT;
        public double FirstY;
        public double FirstT;
        public bool Fired;
        public bool Pending;
        public double PendingTHit;
        public double PendingDeadline;
    }

    private readonly double _judgmentW;
    private readonly double _vanishingY;
    private readonly List<ArrowObs> _arrows = new();
    private readonly List<ExpiredTrack> _expired = new();
    private readonly bool[] _prevPresence = new bool[5];
    private readonly double[] _lastCrossAt = new double[5];
    private double _lastFrameTime = double.NaN;
    private double _frameDelta = 1.0 / 31;

    public SingleLineTracker(double judgmentY, double vanishingY)
    {
        _judgmentW = 1.0 / (judgmentY - vanishingY);
        _vanishingY = vanishingY;
        for (int k = 0; k < 5; k++)
        {
            _lastCrossAt[k] = double.NegativeInfinity;
        }
    }

    public bool CollectSamples { get; set; }

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

        ObserveArrows(timeSeconds, laneDetections, scheduled);

        // このラインの在席。紫は「未解決の重なり」として発火させない
        // （種別としては存在しない: マスター確認 + 全セッションログ
        // 実測で紫の発火は 13 / 約 17,500 件、初期の 3 ランのみ）。
        var presence = new bool[5];
        foreach (NoteDetection d in laneDetections)
        {
            if (!d.IsLinePresence || d.Kind == NoteKind.Arrow || d.Kind == NoteKind.Purple)
            {
                continue;
            }
            if (Math.Abs(d.Y - LineY) < 0.01)
            {
                presence[(int)d.Kind] = true;
            }
        }
        // 横断時刻は「不在→在席」の中点に置く（従来ラインと同じ約束）。
        double crossTime = timeSeconds - _frameDelta / 2;
        for (int k = 0; k < 5; k++)
        {
            if (k == (int)NoteKind.Arrow || k == (int)NoteKind.Purple)
            {
                continue;
            }
            if (presence[k] && !_prevPresence[k]
                && timeSeconds - _lastCrossAt[k] > RefractorySeconds)
            {
                _lastCrossAt[k] = timeSeconds;
                scheduled.Add(new ScheduledNote(
                    (NoteKind)k, null, crossTime + LeadFrom(LineY), 0.0, 1,
                    LineY, crossTime));
            }
        }
        Array.Copy(presence, _prevPresence, presence.Length);
        return scheduled;
    }

    private void ObserveArrows(double t, IReadOnlyList<NoteDetection> dets, List<ScheduledNote> scheduled)
    {
        foreach (NoteDetection d in dets)
        {
            if (d.Kind != NoteKind.Arrow || d.IsLinePresence)
            {
                continue;
            }
            // 同じ波とみなす観測: X が近く、途切れが短く、下端 Y が最も近いもの。
            ArrowObs? match = null;
            double bestDy = double.PositiveInfinity;
            foreach (ArrowObs o in _arrows)
            {
                if (Math.Abs(d.X - o.X) > ArrowMatchXTolerance || t - o.LastT > ArrowStaleSeconds)
                {
                    continue;
                }
                double dy = Math.Abs(d.Y - o.LastY);
                if (dy < bestDy)
                {
                    bestDy = dy;
                    match = o;
                }
            }
            if (match == null)
            {
                var o = new ArrowObs
                {
                    Direction = d.Direction,
                    X = d.X,
                    LastY = d.Y,
                    LastT = t,
                    FirstY = d.Y,
                    FirstT = t,
                };
                _arrows.Add(o);
                if (d.Y >= LineY)
                {
                    // 初見ですでにラインより下: 横断は観測できないため、
                    // 初見位置からの到着予測で即発火する（機械式の縮退形）。
                    FireArrow(o, t + LeadFrom(d.Y), t, scheduled);
                }
                continue;
            }
            double prevY = match.LastY;
            double prevT = match.LastT;
            if (d.Direction != null)
            {
                match.Direction = d.Direction;
            }
            match.X = d.X;
            match.LastY = d.Y;
            match.LastT = t;
            if (!match.Fired && !match.Pending
                && prevY < LineY && d.Y >= LineY && d.Y > prevY)
            {
                // 下端がラインを横切る時刻を前後フレームで補間する。
                double frac = (LineY - prevY) / (d.Y - prevY);
                double tCross = prevT + frac * (t - prevT);
                FireArrow(match, tCross + LeadFrom(LineY), t, scheduled);
            }
        }

        // 方向待ちの確定・失効と、古い観測の掃除。
        for (int i = _arrows.Count - 1; i >= 0; i--)
        {
            ArrowObs o = _arrows[i];
            if (o.Pending && o.Direction != null)
            {
                o.Pending = false;
                o.Fired = true;
                scheduled.Add(new ScheduledNote(
                    NoteKind.Arrow, o.Direction, o.PendingTHit, 0.0, 1,
                    LineY, o.FirstT));
            }
            else if (o.Pending && t > o.PendingDeadline)
            {
                // 方向が最後まで読めなかった波は発火しない（方向なしの
                // 矢印はキーに変換できない）。失効として記録する。
                o.Pending = false;
                o.Fired = true;
                _expired.Add(new ExpiredTrack(
                    NoteKind.Arrow, false, 1, o.FirstY, o.LastY,
                    o.FirstT, o.LastT, 0, "nodir"));
            }
            if (t - o.LastT > ArrowStaleSeconds)
            {
                if (!o.Fired && !o.Pending)
                {
                    _expired.Add(new ExpiredTrack(
                        NoteKind.Arrow, false, 1, o.FirstY, o.LastY,
                        o.FirstT, o.LastT, 0, "stale"));
                }
                _arrows.RemoveAt(i);
            }
        }
    }

    private void FireArrow(ArrowObs o, double tHit, double now, List<ScheduledNote> scheduled)
    {
        if (o.Direction != null)
        {
            o.Fired = true;
            scheduled.Add(new ScheduledNote(
                NoteKind.Arrow, o.Direction, tHit, 0.0, 1, LineY, o.FirstT));
        }
        else
        {
            o.Pending = true;
            o.PendingTHit = tHit;
            o.PendingDeadline = now + ArrowDirectionWaitSeconds;
        }
    }

    public List<ExpiredTrack> DrainExpired()
    {
        var result = new List<ExpiredTrack>(_expired);
        _expired.Clear();
        return result;
    }

    public List<SuppressedDuplicate> DrainSuppressed() => new();

    public List<RevisedNote> DrainRevisions() => new();

    public List<(int Id, double Time, double Y)> DrainSampleLog() => new();
}
