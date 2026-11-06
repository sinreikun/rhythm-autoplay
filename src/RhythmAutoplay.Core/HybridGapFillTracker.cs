namespace RhythmAutoplay.Core;

/// <summary>
/// ハイブリッド追跡器（v0.3.49）: 固定ライン横断を主軸に、塊追跡で穴埋めする。
/// ライン方式は時刻精度で勝る（実測: 中央誤差 4〜6ms）が、過密と矢印の波で
/// 横断が 1 つしか取れないノーツは鎖が組めずに落ちる（Floating の
/// CRINGE 約 40 の主因）。塊追跡は精度で劣る（同 9ms）が、下部帯域では
/// ノーツを見続けられる場面がある。そこで:
/// - ライン方式には全検出を渡し（矢印はその内部委譲、通常塊は無視される）、
///   その予約を正とする。
/// - 塊追跡には通常ノーツの塊だけを渡し、その予約は「同種で到達時刻が
///   60ms 以内にライン予約がある」場合に重複として捨てる。残った予約が
///   穴埋めになる。
/// 60ms の根拠: 同一レーン同キーの実在する最短間隔は 16 分音符
/// （BPM 195 で 77ms）で、それより十分に狭く、両方式の予測誤差
/// （各 10ms 級）の和より十分に広い。
/// 逆順（塊が先に予約し、ラインが後から来る）は塊の確定窓がヒット
/// 100ms 前、ラインの予約が同 110ms 以上前と、通常はラインが先行する
/// ため稀。残った二重はセッション側の 35ms 抑制と「誤キーは無害」の
/// 判定モデルが受ける。
/// </summary>
public sealed class HybridGapFillTracker : ILaneTracker
{
    private const double MergeWindowSeconds = 0.06;
    private const double LineRecordLifetimeSeconds = 1.5;

    private readonly LineCrossingTracker _line;
    private readonly LaneNoteTracker _track;
    private readonly List<(NoteKind Kind, double THit, double At)> _lineAccepted = new();
    private readonly List<SuppressedDuplicate> _suppressed = new();

    public HybridGapFillTracker(
        double judgmentY,
        double vanishingY,
        double spawnGateY,
        double commitWindowSeconds)
    {
        _line = new LineCrossingTracker(judgmentY, vanishingY, spawnGateY, commitWindowSeconds);
        _track = new LaneNoteTracker(
            judgmentY,
            vanishingY: vanishingY,
            spawnGateY: spawnGateY,
            scheduleHorizonSeconds: 1.2,
            commitWindowSeconds: commitWindowSeconds);
    }

    public bool CollectSamples
    {
        get => _line.CollectSamples;
        set
        {
            _line.CollectSamples = value;
            _track.CollectSamples = value;
        }
    }

    public List<ScheduledNote> Observe(double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var result = new List<ScheduledNote>();
        foreach (ScheduledNote sn in _line.Observe(timeSeconds, laneDetections))
        {
            _lineAccepted.Add((sn.Kind, sn.THit, timeSeconds));
            result.Add(sn);
        }
        _lineAccepted.RemoveAll(r => timeSeconds - r.At > LineRecordLifetimeSeconds);

        var trackInput = new List<NoteDetection>();
        foreach (NoteDetection d in laneDetections)
        {
            if (!d.IsLinePresence && d.Kind != NoteKind.Arrow)
            {
                trackInput.Add(d);
            }
        }
        foreach (ScheduledNote sn in _track.Observe(timeSeconds, trackInput))
        {
            bool covered = false;
            foreach ((NoteKind kind, double tHit, double _) in _lineAccepted)
            {
                if (kind == sn.Kind && Math.Abs(tHit - sn.THit) <= MergeWindowSeconds)
                {
                    covered = true;
                    break;
                }
            }
            if (covered)
            {
                _suppressed.Add(new SuppressedDuplicate(sn.Kind, sn.THit));
            }
            else
            {
                result.Add(sn);
            }
        }
        return result;
    }

    public List<ExpiredTrack> DrainExpired()
    {
        var drained = _line.DrainExpired();
        drained.AddRange(_track.DrainExpired());
        return drained;
    }

    public List<SuppressedDuplicate> DrainSuppressed()
    {
        var drained = _line.DrainSuppressed();
        drained.AddRange(_track.DrainSuppressed());
        drained.AddRange(_suppressed);
        _suppressed.Clear();
        return drained;
    }

    public List<RevisedNote> DrainRevisions()
    {
        var drained = _line.DrainRevisions();
        drained.AddRange(_track.DrainRevisions());
        return drained;
    }

    public List<(int Id, double Time, double Y)> DrainSampleLog()
    {
        var drained = _line.DrainSampleLog();
        drained.AddRange(_track.DrainSampleLog());
        return drained;
    }
}
