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
        public ArrowDirection? Direction;
        public List<(double Time, double Y)> Samples { get; } = new();
        public bool Scheduled { get; set; }
        public double LastSeenTime { get; set; }
        public double LastY { get; set; }
    }

    private readonly List<Track> _tracks = new();
    private readonly double _judgmentY;
    private readonly double _matchToleranceY;
    private readonly double _trackTimeoutSeconds;
    private readonly double _scheduleHorizonSeconds;
    private readonly double _maxResidual;

    public LaneNoteTracker(
        double judgmentY,
        double matchToleranceY = 0.06,
        double trackTimeoutSeconds = 0.25,
        double scheduleHorizonSeconds = 2.0,
        double maxResidual = 0.012)
    {
        _judgmentY = judgmentY;
        _matchToleranceY = matchToleranceY;
        _trackTimeoutSeconds = trackTimeoutSeconds;
        _scheduleHorizonSeconds = scheduleHorizonSeconds;
        _maxResidual = maxResidual;
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

        foreach (NoteDetection det in laneDetections)
        {
            Track? best = null;
            double bestGap = double.MaxValue;
            foreach (Track track in _tracks)
            {
                if (matched.Contains(track) || track.Kind != det.Kind)
                {
                    continue;
                }
                // ノーツは基本的に下方向（Y 増）へだけ動く。若干の上振れは測定誤差として許す。
                double gap = det.Y - track.LastY;
                if (gap < -0.02 || gap > _matchToleranceY)
                {
                    continue;
                }
                if (Math.Abs(gap) < bestGap)
                {
                    bestGap = Math.Abs(gap);
                    best = track;
                }
            }

            if (best is null)
            {
                best = new Track
                {
                    Kind = det.Kind,
                    Direction = det.Direction,
                    LastSeenTime = timeSeconds,
                    LastY = det.Y,
                };
                _tracks.Add(best);
            }

            best.Samples.Add((timeSeconds, det.Y));
            best.LastY = det.Y;
            best.LastSeenTime = timeSeconds;
            if (det.Direction is not null)
            {
                best.Direction = det.Direction;
            }
            matched.Add(best);

            if (!best.Scheduled && best.Samples.Count >= 3)
            {
                if (HitTimePredictor.TryPredictHitTime(best.Samples, _judgmentY, out double hitTime, out double residual)
                    && residual <= _maxResidual
                    && hitTime >= timeSeconds - 0.02
                    && hitTime <= timeSeconds + _scheduleHorizonSeconds)
                {
                    best.Scheduled = true;
                    ready.Add((best.Kind, best.Direction, hitTime));
                }
            }
        }

        _tracks.RemoveAll(t =>
            timeSeconds - t.LastSeenTime > _trackTimeoutSeconds
            || t.LastY > _judgmentY + 0.08);

        return ready;
    }
}
