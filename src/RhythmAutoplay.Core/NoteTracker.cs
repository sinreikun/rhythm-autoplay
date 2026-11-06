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
    double BestResidual,
    string FailReason = "",
    string DeathbedReason = "");

/// <summary>
/// 1レーン分のノーツ追跡。フレームごとの検出（中心 Y の列）を同一ノーツの
/// 軌跡に結びつけ、標本が溜まったら到達時刻を予測して一度だけ発火候補を返す。
/// 予測は設計の核（docs/design.md §3）: 「判定ラインに来てから押す」のではなく
/// 「到達時刻を推定して未来に入力を予約する」。
/// </summary>
public sealed class LaneNoteTracker : ILaneTracker
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

        /// <summary>最後の通常試行（確定窓あり）が落ちたゲート名。診断用（v0.3.27）。</summary>
        public string LastFailNormal { get; set; } = "none";

        /// <summary>最後の死に際試行（確定窓バイパス）が落ちたゲート名。診断用（v0.3.27）。</summary>
        public string LastFailBypass { get; set; } = "none";

        /// <summary>
        /// 最も残差の小さかった射影フィット直線（w 空間）の保存（v0.3.28）。
        /// 判定帯近くでは標本が荒れて現在の窓の適合が残差ゲートを超えるが、
        /// 過去の最良直線は壊れていないことが多い（v0.3.27 の実測診断:
        /// ゴーストの 64% が resid、21% が nofit で死亡）。
        /// </summary>
        public (double Velocity, double Intercept)? BestLine { get; set; }
        public double BestLineResidual { get; set; } = double.PositiveInfinity;

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
    private readonly double _judgmentY;
    private readonly double _matchToleranceY;
    private readonly double _trackTimeoutSeconds;
    private readonly double _scheduleHorizonSeconds;
    private readonly double _maxResidual;
    private readonly double _vanishingY;
    private readonly double _spawnGateY;
    private readonly double _commitWindowSeconds;

    public LaneNoteTracker(
        double judgmentY,
        double matchToleranceY = 0.06,
        double trackTimeoutSeconds = 0.25,
        double scheduleHorizonSeconds = 2.0,
        double maxResidual = 0.012,
        double vanishingY = double.NaN,
        double spawnGateY = double.NaN,
        double commitWindowSeconds = 0)
    {
        _judgmentY = judgmentY;
        _matchToleranceY = matchToleranceY;
        _trackTimeoutSeconds = trackTimeoutSeconds;
        _scheduleHorizonSeconds = scheduleHorizonSeconds;
        _maxResidual = maxResidual;
        _vanishingY = vanishingY;
        _spawnGateY = spawnGateY;
        _commitWindowSeconds = commitWindowSeconds;
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

    /// <summary>このエンジンは二重予約の抑制を行わない（v0.3.11 の挙動そのまま）。</summary>
    public List<SuppressedDuplicate> DrainSuppressed() => new();

    /// <summary>
    /// このレーンの検出列を時刻 timeSeconds のフレームとして取り込む。
    /// 戻り値は「今回新たにスケジュールすべきヒット」（種別・方向・到達時刻＋診断情報）。
    /// </summary>
    public List<ScheduledNote> Observe(
        double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var ready = new List<ScheduledNote>();
        var matched = new HashSet<Track>();

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
        foreach (NoteDetection det in detList)
        {
            Track? best = assignment.GetValueOrDefault(det);

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

        // 検出が止まった軌跡の救済（v0.3.24）: 判定帯の近くではノーツが
        // 判定バーやエフェクトに埋もれて検出が途切れる。予約の試行が
        // 「検出と結びついたフレーム」でしか起きないと、適合の良い軌跡でも
        // 確定窓に入る前に最後の検出から 0.25 秒のタイムアウトを迎え、
        // 死に際の救済では予測到達時刻が過去猶予 (0.02 秒) を過ぎていて
        // 捨てられてしまう（Floating の未予約ゴースト実測: 確定窓
        // 150ms→67 件 / 100ms→97 件 / 70ms→146 件と、窓を狭めるほど増えた）。
        // 未マッチの軌跡についても毎フレーム予約を試み、凍結した標本の
        // 予測が確定窓に入った時点で予約する。ゲートは通常経路と同一。
        // ただし検出の途絶が 60ms 未満の軌跡は対象外とする（v0.3.25）。
        // 実測では無条件の救済で予約が +199 件まで膨らんだ: 1〜2 フレーム
        // だけ結びつかなかった断片は次のマッチで正常に確定するのが本筋で、
        // 凍結標本で先に確定してしまうと、適合が改善する機会を奪ったまま
        // 二重の発火になる。救済は「本当に見失った」軌跡に限る。
        foreach (Track t in _tracks)
        {
            if (!t.Scheduled && !matched.Contains(t) && t.Samples.Count >= 3
                && timeSeconds - t.LastSeenTime >= 0.06
                && TryBuildSchedule(t, timeSeconds, bypassCommit: false) is { } starved)
            {
                t.Scheduled = true;
                ready.Add(starved);
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
                // 実機で小幅な悪化だったため取り下げ、v0.3.11 の挙動に戻した。
                // 悪化の内訳は Fly Again CRINGE 41→54、IGNITED 55→84。
                // v0.3.13 の軌跡縫合も A/B で不採用（Floating 136→149、
                // IGNITED 55→74）となり、v0.3.14 で取り下げた。）
                if (!t.Scheduled && t.Samples.Count >= 3
                    && TryBuildSchedule(t, timeSeconds, bypassCommit: true) is { } lastChance)
                {
                    t.Scheduled = true;
                    ready.Add(lastChance);
                }
                _expired.Add(new ExpiredTrack(
                    t.Kind, t.Scheduled, t.TotalSamples,
                    t.FirstY, t.LastY, t.FirstTime, t.LastSeenTime, t.BestResidual,
                    t.LastFailNormal, t.LastFailBypass));
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
        // 最良フィットの保存（v0.3.28）: 標本が十分溜まった時点の直線だけを
        // 残す（出現直後の小標本フィットは残差が偶然小さくても端バイアスを
        // 含むため、窓が 8 標本以上あるときに限る）。
        if (predicted && !double.IsNaN(_vanishingY) && track.Samples.Count >= 8
            && HitTimePredictor.TryFitProjectiveLine(track.Samples, _vanishingY,
                out double lv, out double li, out double lres)
            && lres < track.BestLineResidual)
        {
            track.BestLine = (lv, li);
            track.BestLineResidual = lres;
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
        // 診断（v0.3.27）: 最初に落ちたゲートを記録する。ゴースト解析で
        // 「適合の良い軌跡がなぜ死ぬのか」を推測でなく実測で切り分けるため。
        string fail =
            !predicted ? "nofit" :
            !bornAboveGate ? "born" :
            !moved ? "moved" :
            !directionReady ? "dir" :
            !commitReady ? "commit" :
            residual > _maxResidual ? "resid" :
            hitTime < timeSeconds - pastGraceSeconds ? "past" :
            hitTime > timeSeconds + _scheduleHorizonSeconds ? "horizon" :
            "ok";
        // 最良直線へのフォールバック（v0.3.28）: 現在の窓の適合だけが理由で
        // 落ちる場合に限り、保存した最良直線で到達時刻を組み直し、その時刻で
        // 確定窓・過去猶予・ホライズンを判定し直す。他のゲート落ち（出現位置・
        // 動き・方向）は直線の問題ではないので救済しない。
        if ((fail == "resid" || fail == "nofit")
            && track.BestLine is { } bl
            && track.BestLineResidual <= _maxResidual
            && bornAboveGate && moved && directionReady
            && bl.Velocity < -1e-9
            && !double.IsNaN(_vanishingY)
            && _judgmentY - _vanishingY > 1e-6)
        {
            double fbHit = (1.0 / (_judgmentY - _vanishingY) - bl.Intercept) / bl.Velocity;
            bool fbCommit = bypassCommit || fbHit - timeSeconds <= commitThreshold;
            if (fbCommit
                && fbHit >= timeSeconds - pastGraceSeconds
                && fbHit <= timeSeconds + _scheduleHorizonSeconds)
            {
                hitTime = fbHit;
                residual = track.BestLineResidual;
                fail = "ok";
            }
        }
        if (bypassCommit)
        {
            track.LastFailBypass = fail;
        }
        else
        {
            track.LastFailNormal = fail;
        }
        if (fail == "ok")
        {
            return new ScheduledNote(
                track.Kind, modal, hitTime, residual,
                track.TotalSamples, track.FirstY, track.FirstTime);
        }
        return null;
    }
}
