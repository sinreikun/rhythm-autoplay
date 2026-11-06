namespace RhythmAutoplay.Core;

/// <summary>
/// 固定ライン横断方式の追跡器（v0.3.42、マスター発案）。
/// 塊（ブロブ）の成立位置が状況で変動する従来方式と違い、判定ライン手前の
/// 固定した横ライン（既定 Y=0.80 / 0.845 / 0.89）をノーツが横切る時刻だけを
/// 観測する。射影の法則では横断時刻は w=1/(y−消失点) に対して直線になる
/// ため、横断が 2 つ取れれば軌道は一意に決まり、3 つ目で締め直せる。
/// 「オルゴールの櫛」のように順番が固定される分、対応付けの奪い合いや
/// 軌跡分断が構造的に起きない、という前提に立つ。
/// 実測の前提: 上部帯のノーツは描画が薄く（彩度・明度が下部の半分以下）、
/// 塊として成立するのは点灯境界 y≈0.80 以降。そのためラインは点灯済みの
/// 帯域に置く。矢印ノーツは従来の軌跡追跡に委譲する（帯の下端追跡が実証済み）。
/// </summary>
public sealed class LineCrossingTracker : ILaneTracker
{
    /// <summary>横断ラインの Y（画面比率）。検出器のライン走査と共有する。</summary>
    public static readonly double[] LineYs = { 0.80, 0.845, 0.89 };

    private const double NominalRate = 2.13; // w/秒（移動時間 1.86 秒の実測から）
    private const double MinRate = 1.2;
    private const double MaxRate = 3.4;
    private const double AttachToleranceSeconds = 0.035;
    private const double MaxFitResidualSeconds = 0.012;
    private const double EmitHorizonSeconds = 0.35;
    private const double RefractorySeconds = 0.05;
    private const double ChainTimeoutSeconds = 0.30;

    private sealed class Chain
    {
        public int Id;
        public NoteKind Kind;
        public List<(double T, double W)> Crossings { get; } = new();
        public int LastLine = -1;
        public bool Scheduled;
        public double ScheduledHit;
        public double FirstTime;
        public string FailReason = "";
    }

    private readonly double _judgmentY;
    private readonly double _vanishingY;
    private readonly double _judgmentW;
    private readonly LaneNoteTracker _arrowTracker;
    private readonly List<Chain> _chains = new();
    private readonly List<ExpiredTrack> _expired = new();
    private readonly List<RevisedNote> _revisions = new();
    private readonly List<(int Id, double Time, double Y)> _sampleLog = new();
    private readonly bool[,] _prevPresence = new bool[LineYs.Length, 5];
    private readonly double[,] _lastCrossAt = new double[LineYs.Length, 5];
    private int _nextId = 1;
    private double _lastFrameTime = double.NaN;
    private double _frameDelta = 1.0 / 31;

    public LineCrossingTracker(
        double judgmentY,
        double vanishingY,
        double spawnGateY,
        double commitWindowSeconds)
    {
        _judgmentY = judgmentY;
        _vanishingY = vanishingY;
        _judgmentW = 1.0 / (judgmentY - vanishingY);
        _arrowTracker = new LaneNoteTracker(
            judgmentY,
            vanishingY: vanishingY,
            spawnGateY: spawnGateY,
            scheduleHorizonSeconds: 1.2,
            commitWindowSeconds: commitWindowSeconds);
        for (int i = 0; i < LineYs.Length; i++)
        {
            for (int k = 0; k < 5; k++)
            {
                _lastCrossAt[i, k] = double.NegativeInfinity;
            }
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

    private static int LineIndexOf(double y)
    {
        for (int i = 0; i < LineYs.Length; i++)
        {
            if (Math.Abs(y - LineYs[i]) < 0.01)
            {
                return i;
            }
        }
        return -1;
    }

    public List<ScheduledNote> Observe(double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var scheduled = new List<ScheduledNote>();
        if (!double.IsNaN(_lastFrameTime) && timeSeconds > _lastFrameTime)
        {
            _frameDelta = Math.Clamp(timeSeconds - _lastFrameTime, 0.005, 0.1);
        }
        _lastFrameTime = timeSeconds;

        // 矢印は従来方式へ。
        var arrows = new List<NoteDetection>();
        foreach (NoteDetection d in laneDetections)
        {
            if (d.Kind == NoteKind.Arrow)
            {
                arrows.Add(d);
            }
        }
        if (arrows.Count > 0)
        {
            scheduled.AddRange(_arrowTracker.Observe(timeSeconds, arrows));
        }
        else
        {
            // 矢印トラッカーの時刻も進める（期限切れ処理のため）。
            scheduled.AddRange(_arrowTracker.Observe(timeSeconds, Array.Empty<NoteDetection>()));
        }

        // 今フレームのライン在席。
        var presence = new bool[LineYs.Length, 5];
        foreach (NoteDetection d in laneDetections)
        {
            if (!d.IsLinePresence || d.Kind == NoteKind.Arrow)
            {
                continue;
            }
            int li = LineIndexOf(d.Y);
            if (li >= 0)
            {
                presence[li, (int)d.Kind] = true;
            }
        }

        // 立ち上がり = 横断イベント。横断時刻は「不在→在席」の中点に置く
        // （在席初回フレームの時刻そのままだと半フレーム遅れが全ラインに乗り、
        // 切片が系統的に遅れるため）。
        var crossings = new List<(int Line, NoteKind Kind)>();
        for (int li = 0; li < LineYs.Length; li++)
        {
            for (int k = 0; k < 5; k++)
            {
                if (k == (int)NoteKind.Arrow)
                {
                    continue;
                }
                if (presence[li, k] && !_prevPresence[li, k]
                    && timeSeconds - _lastCrossAt[li, k] > RefractorySeconds)
                {
                    _lastCrossAt[li, k] = timeSeconds;
                    crossings.Add((li, (NoteKind)k));
                }
            }
        }
        Array.Copy(presence, _prevPresence, presence.Length);

        // レーン発光の棄却（v0.3.44）: ヒット時にレーン全体が光ると、全ライン
        // 同時に同色の在席が立ち上がる。本物のノーツは 1 フレームに 1 ライン
        // しか横切らないため、同フレームに同種で 2 ライン以上の横断が立つ
        // 場合は発光（と、それに付随する判定文字）とみなして全部捨てる。
        // v0.3.43 の実測では鎖の 76% が横断 1 回で死んでおり、その大半が
        // この発光由来のゴミ横断だった。
        var kindsInFrame = new HashSet<NoteKind>();
        var dupKinds = new HashSet<NoteKind>();
        foreach ((int _, NoteKind kind) in crossings)
        {
            if (!kindsInFrame.Add(kind))
            {
                dupKinds.Add(kind);
            }
        }
        if (dupKinds.Count > 0)
        {
            crossings.RemoveAll(c => dupKinds.Contains(c.Kind));
        }

        double crossTime = timeSeconds - _frameDelta / 2;
        foreach ((int li, NoteKind kind) in crossings.OrderBy(c => c.Line))
        {
            HandleCrossing(crossTime, li, kind, scheduled);
        }

        // 3 本目の横断が来ない鎖の落下傘予約は、横断のないフレームでも
        // 毎フレーム再評価する（v0.3.43、飢餓予約と同じ考え方）。
        foreach (Chain ch in _chains)
        {
            if (!ch.Scheduled && ch.Crossings.Count >= 2)
            {
                TrySchedule(ch, timeSeconds, scheduled, isRevision: false);
            }
        }

        // 期限切れの鎖を診断へ。
        for (int i = _chains.Count - 1; i >= 0; i--)
        {
            Chain ch = _chains[i];
            double lastT = ch.Crossings.Count > 0 ? ch.Crossings[^1].T : ch.FirstTime;
            bool done = ch.Scheduled && ch.LastLine == LineYs.Length - 1;
            if (done || timeSeconds - lastT > ChainTimeoutSeconds)
            {
                double firstY = ch.Crossings.Count > 0
                    ? _vanishingY + 1.0 / ch.Crossings[0].W
                    : LineYs[0];
                double lastY = ch.Crossings.Count > 0
                    ? _vanishingY + 1.0 / ch.Crossings[^1].W
                    : LineYs[0];
                _expired.Add(new ExpiredTrack(
                    ch.Kind, ch.Scheduled, ch.Crossings.Count,
                    firstY, lastY,
                    ch.FirstTime, lastT, 0,
                    ch.Scheduled ? "" : (ch.FailReason == "" ? "fewcross" : ch.FailReason)));
                _chains.RemoveAt(i);
            }
        }
        return scheduled;
    }

    private void HandleCrossing(double t, int line, NoteKind kind, List<ScheduledNote> scheduled)
    {
        double w = W(LineYs[line]);
        // 接続先の鎖を探す: 同種で、最終ラインが手前で、予測横断時刻が近いもの。
        Chain? best = null;
        double bestErr = double.PositiveInfinity;
        foreach (Chain ch in _chains)
        {
            if (ch.Kind != kind || ch.LastLine >= line)
            {
                continue;
            }
            double pred = PredictCrossing(ch, w);
            double err = Math.Abs(pred - t);
            if (err < bestErr)
            {
                bestErr = err;
                best = ch;
            }
        }
        // 予約済みの鎖への接続は許容を広げる（v0.3.43）: 2 点フィットは
        // 誤差が大きく、その予測で 3 本目を弾くと締め直しの機会自体を
        // 失う（v0.3.42 の実測: 予約 876 件中 874 件が 2 横断のまま確定）。
        double tolerance = best != null && best.Scheduled
            ? AttachToleranceSeconds * 2
            : AttachToleranceSeconds;
        if (best != null && bestErr <= tolerance)
        {
            best.Crossings.Add((t, w));
            best.LastLine = line;
            if (_collectSamples)
            {
                _sampleLog.Add((best.Id, t, LineYs[line]));
            }
            TrySchedule(best, t, scheduled, isRevision: true);
            return;
        }
        if (line <= 1)
        {
            var ch = new Chain { Id = _nextId++, Kind = kind, FirstTime = t, LastLine = line };
            ch.Crossings.Add((t, w));
            _chains.Add(ch);
            if (_collectSamples)
            {
                _sampleLog.Add((ch.Id, t, LineYs[line]));
            }
        }
    }

    private double PredictCrossing(Chain ch, double w)
    {
        if (ch.Crossings.Count >= 2 && Fit(ch, out double a, out double b, out _))
        {
            return (w - a) / b;
        }
        (double lt, double lw) = ch.Crossings[^1];
        return lt + (lw - w) / NominalRate;
    }

    /// <summary>w = a + b*t の最小二乗。b は負（w は時間とともに減る）。</summary>
    private static bool Fit(Chain ch, out double a, out double b, out double maxResidualT)
    {
        a = 0;
        b = 0;
        maxResidualT = double.PositiveInfinity;
        int n = ch.Crossings.Count;
        if (n < 2)
        {
            return false;
        }
        double mt = 0, mw = 0;
        foreach ((double t, double w) in ch.Crossings)
        {
            mt += t;
            mw += w;
        }
        mt /= n;
        mw /= n;
        double sxx = 0, sxy = 0;
        foreach ((double t, double w) in ch.Crossings)
        {
            sxx += (t - mt) * (t - mt);
            sxy += (t - mt) * (w - mw);
        }
        if (sxx <= 0)
        {
            return false;
        }
        b = sxy / sxx;
        a = mw - b * mt;
        if (b >= -1e-9)
        {
            return false;
        }
        maxResidualT = 0;
        foreach ((double t, double w) in ch.Crossings)
        {
            double predT = (w - a) / b;
            maxResidualT = Math.Max(maxResidualT, Math.Abs(predT - t));
        }
        return true;
    }

    private void TrySchedule(Chain ch, double now, List<ScheduledNote> scheduled, bool isRevision)
    {
        if (ch.Crossings.Count < 2)
        {
            return;
        }
        if (!Fit(ch, out double a, out double b, out double maxRes))
        {
            ch.FailReason = "nofit";
            return;
        }
        double rate = -b;
        if (rate < MinRate || rate > MaxRate)
        {
            ch.FailReason = "rate";
            return;
        }
        if (maxRes > MaxFitResidualSeconds)
        {
            ch.FailReason = "resid";
            return;
        }
        // 在席の立ち上がりはノーツ下端がラインに触れる数 px 前から起きる
        // ため、横断時刻は系統的に少し早く読める（v0.3.43 実測で中央値
        // -4.2ms）。到達予測に +4ms の補正を入れる。
        double tHit = (_judgmentW - a) / b + 0.004;
        if (!ch.Scheduled)
        {
            // 予約は原則 3 横断そろってから（v0.3.43）。2 点フィットは残差が
            // 恒等的に 0 で品質ゲートが効かず、短い基線の誤差が外挿で増幅
            // された（v0.3.42 実測: 中央誤差 14.2ms、±10ms 内 38%）。
            // 3 本目のライン到達予測を 40ms 過ぎても横断が来ない場合のみ、
            // 2 点フィットで落下傘予約する（ライン2横断はヒット 87ms 前、
            // 予測+40ms でもヒットまで約 47ms 残る）。
            if (ch.Crossings.Count < 3)
            {
                double wLast = W(LineYs[LineYs.Length - 1]);
                double predLast = (wLast - a) / b;
                if (now <= predLast + 0.040)
                {
                    ch.FailReason = "wait3";
                    return;
                }
            }
            if (tHit - now < -0.02 || tHit - now > EmitHorizonSeconds)
            {
                ch.FailReason = "horizon";
                return;
            }
            ch.Scheduled = true;
            ch.ScheduledHit = tHit;
            scheduled.Add(new ScheduledNote(
                ch.Kind, null, tHit, maxRes, ch.Crossings.Count,
                LineYs[0], ch.FirstTime));
        }
        else if (isRevision && Math.Abs(tHit - ch.ScheduledHit) >= 0.006)
        {
            _revisions.Add(new RevisedNote(
                ch.Kind, null, ch.ScheduledHit, tHit, maxRes, ch.Crossings.Count));
            ch.ScheduledHit = tHit;
        }
    }

    public List<ExpiredTrack> DrainExpired()
    {
        var result = new List<ExpiredTrack>(_expired);
        _expired.Clear();
        result.AddRange(_arrowTracker.DrainExpired());
        return result;
    }

    public List<SuppressedDuplicate> DrainSuppressed() => _arrowTracker.DrainSuppressed();

    public List<RevisedNote> DrainRevisions()
    {
        var result = new List<RevisedNote>(_revisions);
        _revisions.Clear();
        result.AddRange(_arrowTracker.DrainRevisions());
        return result;
    }

    public List<(int Id, double Time, double Y)> DrainSampleLog()
    {
        var result = new List<(int Id, double Time, double Y)>(_sampleLog);
        _sampleLog.Clear();
        result.AddRange(_arrowTracker.DrainSampleLog());
        return result;
    }
}
