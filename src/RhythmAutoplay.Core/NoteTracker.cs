namespace RhythmAutoplay.Core;

/// <summary>予約が確定したノーツと、その時点の診断情報（残差・標本数・出現位置/時刻）。</summary>
public readonly record struct ScheduledNote(
    NoteKind Kind,
    ArrowDirection? Direction,
    double THit,
    double Residual,
    int SampleCount,
    double FirstY,
    double FirstTime);

/// <summary>消滅した軌跡の診断情報。予約されずに死んだ軌跡が「取りこぼし」の内訳になる。</summary>
public readonly record struct ExpiredTrack(
    NoteKind Kind,
    bool Scheduled,
    int TotalSamples,
    double FirstY,
    double LastY,
    double FirstTime,
    double LastSeenTime,
    double BestResidual);

/// <summary>
/// 1レーン分のノーツ追跡。フレームごとの検出（中心 Y の列）を同一ノーツの
/// 軌跡に結びつけ、標本が溜まったら到達時刻を予測して一度だけ発火候補を返す。
/// 予測は設計の核（docs/design.md §3）: 「判定ラインに来てから押す」のではなく
/// 「到達時刻を推定して未来に入力を予約する」。
/// </summary>
public sealed class LaneNoteTracker
{
    private sealed class Track
    {
        public required NoteKind Kind;
        public List<(double Time, double Y)> Samples { get; } = new();
        public bool Scheduled { get; set; }
        public double LastSeenTime { get; set; }
        public double LastY { get; set; }
        public double FirstY { get; set; }
        public double FirstTime { get; set; }
        public int TotalSamples { get; set; }
        public double BestResidual { get; set; } = double.PositiveInfinity;

        /// <summary>矢印の方向はフレームごとに推定が揺れる（模様が流れるアニメーション）ため、票を貯めて多数決で決める。</summary>
        public Dictionary<ArrowDirection, int> DirectionVotes { get; } = new();

        public ArrowDirection? ModalDirection
        {
            get
            {
                ArrowDirection? best = null;
                int bestCount = 0;
                foreach ((ArrowDirection dir, int count) in DirectionVotes)
                {
                    if (count > bestCount)
                    {
                        bestCount = count;
                        best = dir;
                    }
                }
                return best;
            }
        }
    }

    private readonly List<Track> _tracks = new();
    private readonly List<(Track Track, double DeathTime)> _corpses = new();
    private readonly double _judgmentY;
    private readonly double _matchToleranceY;
    private readonly double _trackTimeoutSeconds;
    private readonly double _scheduleHorizonSeconds;
    private readonly double _maxResidual;
    private readonly double _vanishingY;
    private readonly double _spawnGateY;
    private readonly double _commitWindowSeconds;
    private readonly double _stitchWindowSeconds;
    private readonly double _stitchToleranceY;

    public LaneNoteTracker(
        double judgmentY,
        double matchToleranceY = 0.06,
        double trackTimeoutSeconds = 0.25,
        double scheduleHorizonSeconds = 2.0,
        double maxResidual = 0.012,
        double vanishingY = double.NaN,
        double spawnGateY = double.NaN,
        double commitWindowSeconds = 0,
        double stitchWindowSeconds = 0.45,
        double stitchToleranceY = 0.09)
    {
        _judgmentY = judgmentY;
        _matchToleranceY = matchToleranceY;
        _trackTimeoutSeconds = trackTimeoutSeconds;
        _scheduleHorizonSeconds = scheduleHorizonSeconds;
        _maxResidual = maxResidual;
        _vanishingY = vanishingY;
        _spawnGateY = spawnGateY;
        _commitWindowSeconds = commitWindowSeconds;
        _stitchWindowSeconds = stitchWindowSeconds;
        _stitchToleranceY = stitchToleranceY;
    }

    public int ActiveTrackCount => _tracks.Count;

    private readonly List<ExpiredTrack> _expired = new();

    /// <summary>前回呼び出し以降に消滅した軌跡の診断情報を取り出す（取りこぼし解析用）。</summary>
    public List<ExpiredTrack> DrainExpired()
    {
        var drained = new List<ExpiredTrack>(_expired);
        _expired.Clear();
        return drained;
    }

    /// <summary>
    /// このレーンの検出列を時刻 timeSeconds のフレームとして取り込む。
    /// 戻り値は「今回新たにスケジュールすべきヒット」（種別・方向・到達時刻＋診断情報）。
    /// </summary>
    public List<ScheduledNote> Observe(
        double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var ready = new List<ScheduledNote>();
        var matched = new HashSet<Track>();

        // 縫合の猶予を過ぎた死骸は捨てる（古い軌跡への誤った繋ぎ直しを防ぐ）。
        _corpses.RemoveAll(c => timeSeconds - c.DeathTime > _stitchWindowSeconds);

        // 対応付けは「前回位置との近さ」ではなく、各軌跡の速度から予測した
        // 今回位置との近さで行い、候補ペア全体を近い順に貪欲に割り当てる。
        // 同色ノーツが接近して並ぶと、前回位置基準では後続ノーツの検出が
        // 先行軌跡に奪われて両方壊れる（実機で見落としの主因と判断）。
        var detList = laneDetections.ToList();
        var pairs = new List<(double AbsGap, NoteDetection Det, Track Track)>();
        foreach (NoteDetection det in detList)
        {
            foreach (Track track in _tracks)
            {
                if (track.Kind != det.Kind)
                {
                    continue;
                }
                double expectedY = ExtrapolateY(track, timeSeconds);
                double gap = det.Y - expectedY;
                if (gap < -_matchToleranceY * 0.66 || gap > _matchToleranceY)
                {
                    continue;
                }
                pairs.Add((Math.Abs(gap), det, track));
            }
        }
        pairs.Sort((a, b) => a.AbsGap.CompareTo(b.AbsGap));
        var assignment = new Dictionary<NoteDetection, Track>();
        var usedDets = new HashSet<NoteDetection>();
        foreach ((double _, NoteDetection det, Track track) in pairs)
        {
            if (usedDets.Contains(det) || matched.Contains(track))
            {
                continue;
            }
            assignment[det] = track;
            usedDets.Add(det);
            matched.Add(track);
        }
        var assignedTracks = new HashSet<Track>(assignment.Values);

        foreach (NoteDetection det in detList)
        {
            Track? best = assignment.GetValueOrDefault(det);

            if (best is null)
            {
                // 軌跡の縫合（v0.3.13）: 対応付けから漏れた検出をいきなり新規の
                // 軌跡にせず、まず「同じノーツの続き」がないか探す。同時ノーツの
                // 重なりやレーン発光で検出が途切れると軌跡が分断し、後半の断片は
                // 標本不足・残差過大・方向未確定で予約できないまま死ぬ（v0.3.11 の
                // 実機ログでは、未予約で死んだ軌跡の約半数に、同レーン・同種の
                // 後継が 0.2 秒以内に生まれていた）。位置の連続性が合う場合だけ
                // 旧軌跡に繋ぎ直し、標本と方向票を引き継ぐ。
                best = TryReattach(det, timeSeconds, matched, assignedTracks);
                if (best is null)
                {
                    best = new Track
                    {
                        Kind = det.Kind,
                        LastSeenTime = timeSeconds,
                        LastY = det.Y,
                        FirstY = det.Y,
                        FirstTime = timeSeconds,
                    };
                    _tracks.Add(best);
                }
            }

            best.Samples.Add((timeSeconds, det.Y));
            best.TotalSamples++;
            if (best.Samples.Count > 12)
            {
                best.Samples.RemoveAt(0); // 直近の標本だけを使う（出現直後の不安定な検出に引っ張られない）
            }
            best.LastY = det.Y;
            best.LastSeenTime = timeSeconds;
            if (det.Direction is { } voted)
            {
                best.DirectionVotes[voted] = best.DirectionVotes.GetValueOrDefault(voted) + 1;
            }
            matched.Add(best);

            if (!best.Scheduled && best.Samples.Count >= 3)
            {
                if (TryBuildSchedule(best, timeSeconds, bypassCommit: false) is { } note)
                {
                    best.Scheduled = true;
                    ready.Add(note);
                }
            }
        }

        foreach (Track t in _tracks)
        {
            if (timeSeconds - t.LastSeenTime > _trackTimeoutSeconds
                || t.LastY > _judgmentY + 0.08)
            {
                // 死に際の救済（v0.3.11）: 検出が途中で止まった軌跡は、遅延確定の
                // 窓が開く前にタイムアウトで消えてしまう（矢印は判定帯に近づくと
                // 検出が止まりやすく、v0.3.10 で予約が 51 件減った）。消滅の瞬間に
                // 限り、確定窓を無視して最後の予測で予約する。
                // （v0.3.12 で入れた「毎フレーム試行」と「過去方向の猶予 0.12 秒」は
                // 実機で小幅な悪化だったため v0.3.13 で取り下げ、v0.3.11 の挙動に
                // 戻した。悪化の内訳は Fly Again CRINGE 41→54、IGNITED 55→84。）
                if (!t.Scheduled && t.Samples.Count >= 3
                    && TryBuildSchedule(t, timeSeconds, bypassCommit: true) is { } lastChance)
                {
                    t.Scheduled = true;
                    ready.Add(lastChance);
                }
                _expired.Add(new ExpiredTrack(
                    t.Kind, t.Scheduled, t.TotalSamples,
                    t.FirstY, t.LastY, t.FirstTime, t.LastSeenTime, t.BestResidual));
                // 縫合用の死骸として残す（v0.3.13）: タイムアウトで死んだ未予約の
                // 軌跡のうち、本物のノーツらしいもの（標本 3 件以上・実際に落下・
                // 出現ゲート内・判定ラインをまだ越えていない）だけを、短時間
                // 保持する。判定ラインを越えて消えた軌跡は対象外（後続の検出は
                // 別の何かであり、繋ぐと二重発火の原因になる）。
                if (!t.Scheduled
                    && timeSeconds - t.LastSeenTime > _trackTimeoutSeconds
                    && t.LastY <= _judgmentY + 0.08
                    && t.TotalSamples >= 3
                    && t.LastY - t.FirstY >= 0.03
                    && (double.IsNaN(_spawnGateY) || t.FirstY <= _spawnGateY))
                {
                    _corpses.Add((t, timeSeconds));
                    if (_corpses.Count > 8)
                    {
                        _corpses.RemoveAt(0);
                    }
                }
            }
        }
        _tracks.RemoveAll(t =>
            timeSeconds - t.LastSeenTime > _trackTimeoutSeconds
            || t.LastY > _judgmentY + 0.08);

        return ready;
    }

    /// <summary>
    /// 軌跡の直近 2 標本から時刻 t における Y を外挿する。消失点が分かれば
    /// 射影の運動法則（w = 1/(y − vanishingY) が時間に比例）で外挿し、
    /// 直線外挿の系統的な見積もり不足（加速するノーツを低く見積もる）を避ける。
    /// </summary>
    private double ExtrapolateY(Track track, double t)
    {
        double expectedY = track.LastY;
        if (track.Samples.Count >= 2)
        {
            (double t1, double y1) = track.Samples[^2];
            (double t2, double y2) = track.Samples[^1];
            if (t2 > t1)
            {
                if (!double.IsNaN(_vanishingY)
                    && y1 - _vanishingY > 1e-6
                    && y2 - _vanishingY > 1e-6)
                {
                    double w1 = 1.0 / (y1 - _vanishingY);
                    double w2 = 1.0 / (y2 - _vanishingY);
                    double wExpected = w2 + (w2 - w1) / (t2 - t1) * (t - t2);
                    expectedY = wExpected > 1e-6
                        ? _vanishingY + 1.0 / wExpected
                        : y2 + (y2 - y1) / (t2 - t1) * (t - t2);
                }
                else
                {
                    expectedY = y2 + (y2 - y1) / (t2 - t1) * (t - t2);
                }
            }
        }
        return expectedY;
    }

    /// <summary>
    /// 縫合（v0.3.13）: 対応付けから漏れた検出について、同種で今フレーム
    /// 未対応の生存軌跡、または保持中の死骸の中から、射影外挿の位置が
    /// 連続するものを探して繋ぎ直す。通常の対応付けより窓を広げるが、
    /// 「外挿位置からのずれが小さい」「最後の検出位置より上に戻らない」
    /// 「判定ラインを越えていない」の 3 条件で、別ノーツへの誤接続を抑える。
    /// 繋ぎ直した軌跡（死骸の場合は生存側へ戻したもの）を返す。なければ null。
    /// </summary>
    private Track? TryReattach(
        NoteDetection det, double timeSeconds, HashSet<Track> matched, HashSet<Track> assignedTracks)
    {
        if (_stitchWindowSeconds <= 0 || det.Y > _judgmentY)
        {
            return null;
        }
        Track? best = null;
        double bestAbsGap = double.PositiveInfinity;
        bool bestIsCorpse = false;

        void Consider(Track candidate, bool isCorpse)
        {
            if (candidate.Kind != det.Kind || candidate.Scheduled)
            {
                return;
            }
            double expectedY = ExtrapolateY(candidate, timeSeconds);
            double gap = det.Y - expectedY;
            if (gap < -0.06 || gap > _stitchToleranceY)
            {
                return;
            }
            if (det.Y < candidate.LastY - 0.02)
            {
                return;
            }
            if (Math.Abs(gap) < bestAbsGap)
            {
                bestAbsGap = Math.Abs(gap);
                best = candidate;
                bestIsCorpse = isCorpse;
            }
        }

        foreach (Track t in _tracks)
        {
            // 今フレームで既に対応づいた軌跡と、別の検出に割り当て済みの軌跡は
            // 対象外（割り当て済みを奪うと、1 本の軌跡に同時刻の標本が 2 つ
            // 入って予測が壊れる）。
            if (!matched.Contains(t) && !assignedTracks.Contains(t))
            {
                Consider(t, isCorpse: false);
            }
        }
        foreach ((Track corpse, double _) in _corpses)
        {
            Consider(corpse, isCorpse: true);
        }

        if (best is not null && bestIsCorpse)
        {
            _corpses.RemoveAll(c => ReferenceEquals(c.Track, best));
            _tracks.Add(best);
        }
        return best;
    }

    /// <summary>
    /// 軌跡の現在の標本から予約を組み立てる。全ゲート（出現位置・動き・方向・
    /// 残差・到達範囲）を満たす場合のみ返す。bypassCommit=false のときは
    /// 遅延確定の窓（到達まで commitWindowSeconds を切る）も要求する。
    /// </summary>
    private ScheduledNote? TryBuildSchedule(Track track, double timeSeconds, bool bypassCommit, double pastGraceSeconds = 0.02)
    {
        // 透視レーンでは消失点つきの射影予測を使う（等速接近が直線になる座標系）。
        // vanishingY 未指定（NaN）のプロファイルでは通常の直線予測に倒す。
        double hitTime, residual;
        bool predicted = double.IsNaN(_vanishingY)
            ? HitTimePredictor.TryPredictHitTime(track.Samples, _judgmentY, out hitTime, out residual)
            : HitTimePredictor.TryPredictHitTimeProjective(track.Samples, _judgmentY, _vanishingY, out hitTime, out residual);
        if (predicted && residual < track.BestResidual)
        {
            track.BestResidual = residual;
        }
        // 出現ゲート: 本物のノーツはレーン上端から現れて落下する。
        // 画面下部にいきなり現れる塊（判定文字・コンボ数字などの UI）は
        // 最初の検出 Y がゲートより下なので、そもそも発火候補にしない。
        bool bornAboveGate = double.IsNaN(_spawnGateY) || track.FirstY <= _spawnGateY;
        // 動きの確認: 実際に画面を下ってきた軌跡だけを発火する。
        // 判定文字やコンボ数字は静止（またはフェード）するため、
        // 出現位置のゲートを緩めてもここで弾ける。高速譜面では
        // 本物のノーツの初検出がゲート近くまで下がるため、位置では
        // なく動きで見分けるのが本筋（v0.3.7、TRANCE 譜面の実測から）。
        bool moved = track.LastY - track.FirstY >= 0.03;
        // 矢印は方向が決まるまで発火しない。出現直後は模様が小さく
        // 推定が付かないことが多く、方向なしで予約すると入力できないまま
        // 終わってしまうため、票が入るまで予約を保留する。
        ArrowDirection? modal = track.ModalDirection;
        bool directionReady = track.Kind != NoteKind.Arrow || modal is not null;
        // 遅延確定（v0.3.10）: 予測が立っても即座には確定せず、到達まで
        // commitWindow を切るまで標本を溜め続けて、最後の予測で確定する。
        // 早期の確定は外挿区間が長く、ノーツが小さい間の検出端バイアス
        // （アンチエイリアスで端が内側に寄る等）の変化をそのまま時刻誤差に
        // 変える。v0.3.9 の実測では GREAT+OK? が 77 件あり、残差（標本内）
        // では説明できない時刻のばらつきがあった。下限 0.05 秒は、遅れて
        // 生まれた軌跡を取りこぼさないための締切。
        double commitThreshold = _commitWindowSeconds <= 0
            ? double.PositiveInfinity
            : Math.Max(_commitWindowSeconds, 0.05);
        bool commitReady = bypassCommit || hitTime - timeSeconds <= commitThreshold;
        if (predicted
            && bornAboveGate
            && moved
            && directionReady
            && commitReady
            && residual <= _maxResidual
            && hitTime >= timeSeconds - pastGraceSeconds
            && hitTime <= timeSeconds + _scheduleHorizonSeconds)
        {
            return new ScheduledNote(
                track.Kind, modal, hitTime, residual,
                track.TotalSamples, track.FirstY, track.FirstTime);
        }
        return null;
    }
}
