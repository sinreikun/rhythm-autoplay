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
    /// <remarks>
    /// 4 本目 0.915 の救済ラインは v0.3.47 で実験し、悪化したため戻した
    /// （CRINGE 40→70、BASED 824→779）。0.915 は判定文字とレーン発光の
    /// 帯域で、そこの在席は「ノーツ」ではなく「演出」を拾う。文字由来の
    /// 横断が次のノーツの鎖に紛れ、早すぎる発射が先行ノーツの判定を
    /// 奪ったとみられる。ラインは効果帯の手前までに限る。
    /// </remarks>
    public static double[] LineYs { get; set; } = { 0.80, 0.845, 0.89 };

    /// <summary>
    /// 検出範囲と発火ラインの分離（v0.3.66、マスター発案）用の上部ライン。
    /// 判定文字の帯（実測 y≈0.78〜0.86、矢印と重なると 2 段で ≈0.71 まで）と
    /// 判定ライン際の星＋円エフェクトを避け、横断観測だけを文字帯より上で
    /// 完結させる。発火は従来どおり本物の判定ラインへの到達推定で行う。
    /// </summary>
    public static readonly double[] UpperLineYs = { 0.63, 0.665, 0.70 };

    // 横断観測の実効レート（w/秒、v=0.39 空間）。塊の下端が従う 2.13 では
    // なく、在席の立ち上がりという観測量に固有の値で、実測の鎖内ばらつき
    // が最小になる 1.70 を使う（v0.3.44 のログで検証: レート固定・切片のみ
    // 推定で 3 横断の中央誤差 4.2ms / ±10ms 内 87%、自由フィットの
    // 8.0ms / 61% を大きく上回った）。スクロール速度は曲によらず一定
    // （実測済み）なので、レートを全ノーツ共通の定数として固定できる。
    private const double CrossingRate = 1.70;
    /// <summary>到達推定への系統補正 [秒]。上部ラインでは実測の偏りが変わるため可変にする。</summary>
    public static double HitCorrectionSeconds { get; set; } = -0.003;
    /// <summary>
    /// 鎖ごとの傾きフィット（v0.3.67、上部ライン専用）: 横断間隔が広い
    /// 配置では、鎖自身の横断から実効レートを最小二乗で求めて外挿する。
    /// 従来の狭い間隔では固定レートが自由フィットに勝った（v0.3.44 実測）が、
    /// 上部ラインは外挿距離が約 8 倍あり、ノーツごとのレートずれがそのまま
    /// 増幅されるため、条件が逆転する。傾きが健全域を外れたら固定レートへ。
    /// </summary>
    public static bool UseSlopeFit { get; set; }
    private const double AttachToleranceSeconds = 0.035;
    /// <summary>推定ばらつきの許容 [秒]。上部ラインでは横断間隔が広がり誤差が増えるため可変にする。</summary>
    public static double MaxEstimateDeviationSeconds { get; set; } = 0.018;
    private const double EmitHorizonSeconds = 0.35;
    private const double RefractorySeconds = 0.05;
    /// <summary>鎖の失効までの時間 [秒]。最長の横断間隔より長く取る必要があるため可変にする。</summary>
    public static double ChainTimeoutSeconds { get; set; } = 0.30;

    // 最終ライン救済（v0.3.53）: 上流ラインで見えたのに鎖が予約に至らな
    // かったノーツが、最終ライン (0.89) を横切る瞬間にだけ拾う終端
    // トリガ。v0.3.47 の「4 本目の鎖ライン」とは別物で、鎖には一切
    // 入れない。発火条件は (a) 未予約の鎖の到達推定と 60ms 以内で
    // 一致する（裏付け）、(b) 同種の予約が推定到達の ±55ms 以内に
    // 無い、(c) 同種の救済から 40ms 以上空いている、の 3 つ全て。
    // 判定文字の漂流は上流の裏付けを持たないため、ここでは発火しない。
    // v0.3.57: ゲート (b)(c) を緩めた。在席ログ (v0.3.56) の実測では
    // 取りこぼしの最大区分が「断片も最終ラインも見えたのに無予約」で、
    // 16 分音符の間隔 (BPM195 で 77ms) より長い不応期 90ms と ±100ms の
    // 抑制窓が、密な区間で救済同士・正規予約との近接を構造的に塞いで
    // いた。二重発射の最終防衛はセッション側の重複抑制 (35ms) が担う。
    private const double RescueAgreeSeconds = 0.06;
    private const double RescueSuppressSeconds = 0.055;
    private const double RescueRefractorySeconds = 0.04;
    private const double RescueResidual = 0.030;
    // 救済推定の系統偏り補正は REFUTED（v0.3.54 で実験し v0.3.55 で撤回）:
    // 救済の到達推定は実測で中央値 -7ms (Floating) / -10ms (GOODBOUNCE)
    // と前にずれるが、+8ms を足すと推定の中央値は中心へ寄る一方で
    // CRINGE は両曲とも悪化した (Floating 28→33、GOODBOUNCE 96→110)。
    // 裏付けゲートが補正後の推定で別の断片を通し、ハイブリッド側の
    // 抑制関係も変わるため、単純な平行移動は利得にならない。
    private const double RescueCorrectionSeconds = 0.0;

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
    private readonly List<(NoteKind Kind, double THit, double At)> _emitted = new();
    private readonly double[] _lastRescueAt = new double[5];
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
        for (int k = 0; k < 5; k++)
        {
            _lastRescueAt[k] = double.NegativeInfinity;
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
        _emitted.RemoveAll(e => timeSeconds - e.At > 1.5);

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
        else
        {
            TryRescue(t, kind, scheduled);
        }
    }

    /// <summary>
    /// 最終ライン救済（v0.3.53）。最終ラインの横断がどの鎖にも接続
    /// できなかった場合に限り、未予約の鎖の到達推定が裏付けとして
    /// 一致すれば、その横断 1 点から到達時刻を推定して予約する。
    /// 裏付けに使った鎖は予約済みにして二重発射を防ぐ。
    /// </summary>
    private void TryRescue(double t, NoteKind kind, List<ScheduledNote> scheduled)
    {
        if (t - _lastRescueAt[(int)kind] < RescueRefractorySeconds)
        {
            return;
        }
        double rescueEst = t + (W(LineYs[^1]) - _judgmentW) / CrossingRate
            + HitCorrectionSeconds + RescueCorrectionSeconds;
        foreach ((NoteKind k, double tHit, double _) in _emitted)
        {
            if (k == kind && Math.Abs(tHit - rescueEst) <= RescueSuppressSeconds)
            {
                return;
            }
        }
        Chain? corroborator = null;
        double bestAgree = double.PositiveInfinity;
        foreach (Chain ch in _chains)
        {
            if (ch.Kind != kind || ch.Scheduled || ch.Crossings.Count == 0)
            {
                continue;
            }
            double agree = Math.Abs(EstimateHit(ch, out _) - rescueEst);
            if (agree < bestAgree)
            {
                bestAgree = agree;
                corroborator = ch;
            }
        }
        if (corroborator == null || bestAgree > RescueAgreeSeconds)
        {
            return;
        }
        // 裏付けに使った鎖はその場で消費する（v0.3.57）。予約済みのまま
        // 生かしておくと、接続許容が広い (70ms) 予約済み鎖として次の
        // ノーツの最終ライン横断を吸収してしまい、後続の救済が成立
        // しなくなる。在席ログの実測で見つけた構造的な競合。
        _chains.Remove(corroborator);
        double cFirstY = _vanishingY + 1.0 / corroborator.Crossings[0].W;
        double cLastY = _vanishingY + 1.0 / corroborator.Crossings[^1].W;
        _expired.Add(new ExpiredTrack(
            corroborator.Kind, true, corroborator.Crossings.Count,
            cFirstY, cLastY,
            corroborator.FirstTime, corroborator.Crossings[^1].T, 0, ""));
        _lastRescueAt[(int)kind] = t;
        _emitted.Add((kind, rescueEst, t));
        scheduled.Add(new ScheduledNote(
            kind, null, rescueEst, RescueResidual, 1,
            LineYs[^1], t));
    }

    /// <summary>
    /// レート固定の到達推定。各横断は「その時刻にそのラインの w を通過した」
    /// という観測で、到達時刻の推定値 t + (w − w判定) / レート を 1 つ返す。
    /// 鎖の推定はそれらの平均。ばらつき（最大偏差）が小さいほど観測が
    /// 一貫している。自由フィットと違い、短い基線でも誤差が増幅されない。
    /// </summary>
    /// <summary>この鎖の実効レート。傾きフィット有効時は横断の最小二乗傾き（健全域内のみ）、それ以外は固定レート。</summary>
    private double EffectiveRate(Chain ch)
    {
        if (!UseSlopeFit || ch.Crossings.Count < 2)
        {
            return CrossingRate;
        }
        double tMean = 0, wMean = 0;
        foreach ((double t, double w) in ch.Crossings)
        {
            tMean += t;
            wMean += w;
        }
        tMean /= ch.Crossings.Count;
        wMean /= ch.Crossings.Count;
        double num = 0, den = 0;
        foreach ((double t, double w) in ch.Crossings)
        {
            num += (t - tMean) * (w - wMean);
            den += (t - tMean) * (t - tMean);
        }
        if (den < 1e-9)
        {
            return CrossingRate;
        }
        double rate = -num / den;
        return rate is >= 1.45 and <= 1.95 ? rate : CrossingRate;
    }

    private double EstimateHit(Chain ch, out double maxDeviation)
    {
        double rate = EffectiveRate(ch);
        double sum = 0;
        var estimates = new List<double>(ch.Crossings.Count);
        foreach ((double t, double w) in ch.Crossings)
        {
            double est = t + (w - _judgmentW) / rate;
            estimates.Add(est);
            sum += est;
        }
        double mean = sum / estimates.Count;
        maxDeviation = 0;
        foreach (double est in estimates)
        {
            maxDeviation = Math.Max(maxDeviation, Math.Abs(est - mean));
        }
        return mean;
    }

    private double PredictCrossing(Chain ch, double w)
    {
        double tHit = EstimateHit(ch, out _);
        return tHit - (w - _judgmentW) / EffectiveRate(ch);
    }

    private void TrySchedule(Chain ch, double now, List<ScheduledNote> scheduled, bool isRevision)
    {
        if (ch.Crossings.Count < 2)
        {
            return;
        }
        double mean = EstimateHit(ch, out double maxDev);
        if (maxDev > MaxEstimateDeviationSeconds)
        {
            ch.FailReason = "resid";
            return;
        }
        double tHit = mean + HitCorrectionSeconds;
        if (!ch.Scheduled)
        {
            if (tHit - now < -0.02 || tHit - now > EmitHorizonSeconds)
            {
                ch.FailReason = "horizon";
                return;
            }
            ch.Scheduled = true;
            ch.ScheduledHit = tHit;
            _emitted.Add((ch.Kind, tHit, now));
            scheduled.Add(new ScheduledNote(
                ch.Kind, null, tHit, maxDev, ch.Crossings.Count,
                LineYs[0], ch.FirstTime));
        }
        else if (isRevision && Math.Abs(tHit - ch.ScheduledHit) >= 0.006)
        {
            _revisions.Add(new RevisedNote(
                ch.Kind, null, ch.ScheduledHit, tHit, maxDev, ch.Crossings.Count));
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
