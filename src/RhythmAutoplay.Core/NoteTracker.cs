namespace RhythmAutoplay.Core;

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

    public LaneNoteTracker(
        double judgmentY,
        double matchToleranceY = 0.06,
        double trackTimeoutSeconds = 0.25,
        double scheduleHorizonSeconds = 2.0,
        double maxResidual = 0.012,
        double vanishingY = double.NaN,
        double spawnGateY = double.NaN)
    {
        _judgmentY = judgmentY;
        _matchToleranceY = matchToleranceY;
        _trackTimeoutSeconds = trackTimeoutSeconds;
        _scheduleHorizonSeconds = scheduleHorizonSeconds;
        _maxResidual = maxResidual;
        _vanishingY = vanishingY;
        _spawnGateY = spawnGateY;
    }

    public int ActiveTrackCount => _tracks.Count;

    /// <summary>
    /// このレーンの検出列を時刻 timeSeconds のフレームとして取り込む。
    /// 戻り値は「今回新たにスケジュールすべきヒット」（種別・方向・到達時刻）。
    /// </summary>
    public List<(NoteKind Kind, ArrowDirection? Direction, double THit)> Observe(
        double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var ready = new List<(NoteKind, ArrowDirection?, double)>();
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
                double expectedY = track.LastY;
                if (track.Samples.Count >= 2)
                {
                    (double t1, double y1) = track.Samples[^2];
                    (double t2, double y2) = track.Samples[^1];
                    if (t2 > t1)
                    {
                        expectedY = y2 + (y2 - y1) / (t2 - t1) * (timeSeconds - t2);
                    }
                }
                double gap = det.Y - expectedY;
                if (gap < -_matchToleranceY / 2 || gap > _matchToleranceY * 0.75)
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
                };
                _tracks.Add(best);
            }

            best.Samples.Add((timeSeconds, det.Y));
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
                // 透視レーンでは消失点つきの射影予測を使う（等速接近が直線になる座標系）。
                // vanishingY 未指定（NaN）のプロファイルでは通常の直線予測に倒す。
                double hitTime, residual;
                bool predicted = double.IsNaN(_vanishingY)
                    ? HitTimePredictor.TryPredictHitTime(best.Samples, _judgmentY, out hitTime, out residual)
                    : HitTimePredictor.TryPredictHitTimeProjective(best.Samples, _judgmentY, _vanishingY, out hitTime, out residual);
                // 出現ゲート: 本物のノーツはレーン上端から現れて落下する。
                // 画面下部にいきなり現れる塊（判定文字・コンボ数字などの UI）は
                // 最初の検出 Y がゲートより下なので、そもそも発火候補にしない。
                bool bornAboveGate = double.IsNaN(_spawnGateY) || best.FirstY <= _spawnGateY;
                // 矢印は方向が決まるまで発火しない。出現直後は模様が小さく
                // 推定が付かないことが多く、方向なしで予約すると入力できないまま
                // 終わってしまうため、票が入るまで予約を保留する。
                ArrowDirection? modal = best.ModalDirection;
                bool directionReady = best.Kind != NoteKind.Arrow || modal is not null;
                if (predicted
                    && bornAboveGate
                    && directionReady
                    && residual <= _maxResidual
                    && hitTime >= timeSeconds - 0.02
                    && hitTime <= timeSeconds + _scheduleHorizonSeconds)
                {
                    best.Scheduled = true;
                    ready.Add((best.Kind, modal, hitTime));
                }
            }
        }

        _tracks.RemoveAll(t =>
            timeSeconds - t.LastSeenTime > _trackTimeoutSeconds
            || t.LastY > _judgmentY + 0.08);

        return ready;
    }
}
