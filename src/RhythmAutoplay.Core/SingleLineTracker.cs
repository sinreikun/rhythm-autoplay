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
/// v0.3.81: 波救済を追加。実測（run 80 の PLR 分解）では、波に覆われた
/// 瞬間に単ラインはノーツを見失い、色を問わず落下の主因になっていた
/// （波の中: 赤 21% / 青 約18% / 黄 48〜65% 落下、波の外: 約0/0/39%）。
/// 波と一緒に降りるノーツは別の固定ラインでも波の後ろにいるため、
/// ライン増設では直らない。レーン全体を見る塊追跡なら帯の前後で
/// ノーツを追える（従来方式が波区間を拾えていた実績）ので、塊追跡の
/// 色予約を「そのレーンが波に覆われていた証跡がある場合に限り」
/// 救済として通す。ゲートは従来の穴埋めで実績のある標本 8 以上・
/// 残差 0.006 以下と、ライン発火への到達時刻 ±0.12 秒の重複排除。
/// 波の外では従来どおり機械式のみで発火する。
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

    // 波救済のゲート（v0.3.81）。標本・残差は従来の穴埋め（v0.3.50）の
    // 実績値、抑制窓はライン発火の残差 IQR（約 19ms）を十分に覆う幅。
    private const double RescueSuppressWindowSeconds = 0.12;
    private const int RescueMinSamples = 8;
    private const double RescueMaxResidual = 0.006;
    // 波の証跡とみなす範囲: 候補のライン横断時刻（到達 − リード）の
    // 前後この秒数以内に、同レーン同色の「帯の中の塊」が見えていたら
    // そのノーツは波に隠れていたと判定する。
    private const double BandAssocWindowSeconds = 0.45;
    private const double AcceptedLifetimeSeconds = 1.5;
    private const double SightingLifetimeSeconds = 2.5;

    private readonly double _judgmentW;
    private readonly double _vanishingY;
    private readonly LaneNoteTracker _arrowTracker;
    private readonly LaneNoteTracker _rescueTracker;
    private readonly bool[] _prevPresence = new bool[5];
    private readonly double[] _lastCrossAt = new double[5];
    private readonly List<(NoteKind Kind, double THit, double At)> _accepted = new();
    private readonly List<(NoteKind Kind, double At)> _bandSightings = new();
    private readonly List<SuppressedDuplicate> _suppressed = new();
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
        // 波救済の塊追跡も同一構成。色ノーツだけを入力する。
        _rescueTracker = new LaneNoteTracker(
            judgmentY,
            vanishingY: vanishingY,
            spawnGateY: spawnGateY,
            scheduleHorizonSeconds: 1.2,
            commitWindowSeconds: commitWindowSeconds);
        for (int k = 0; k < 5; k++)
        {
            _lastCrossAt[k] = double.NegativeInfinity;
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
            _rescueTracker.CollectSamples = value;
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
                double tHit = crossTime + LeadFrom(LineY);
                var fire = new ScheduledNote(
                    (NoteKind)k, null, tHit, 0.0, 1,
                    LineY, crossTime);
                scheduled.Add(fire);
                _accepted.Add((fire.Kind, fire.THit, timeSeconds));
            }
        }
        Array.Copy(presence, _prevPresence, presence.Length);
        _accepted.RemoveAll(r => timeSeconds - r.At > AcceptedLifetimeSeconds);

        // 波救済（v0.3.81）: 色ノーツの塊を追跡し、波に隠れていた証跡が
        // ある候補だけを通す。帯の中の塊の目撃はこのレーンの検出から
        // 集める（検出器が塊の行範囲と帯の重なりで InBand を立てる）。
        var rescueInput = new List<NoteDetection>();
        foreach (NoteDetection d in laneDetections)
        {
            if (d.IsLinePresence || d.Kind == NoteKind.Arrow || d.Kind == NoteKind.Purple)
            {
                continue;
            }
            rescueInput.Add(d);
            if (d.InBand)
            {
                _bandSightings.Add((d.Kind, d.TimeSeconds));
            }
        }
        _bandSightings.RemoveAll(s => timeSeconds - s.At > SightingLifetimeSeconds);
        foreach (ScheduledNote sn in _rescueTracker.Observe(timeSeconds, rescueInput))
        {
            bool covered = false;
            foreach ((NoteKind kind, double tHit, double _) in _accepted)
            {
                if (kind == sn.Kind
                    && Math.Abs(tHit - sn.THit) <= RescueSuppressWindowSeconds)
                {
                    covered = true;
                    break;
                }
            }
            bool confident = sn.SampleCount >= RescueMinSamples
                && sn.Residual <= RescueMaxResidual;
            double crossAt = sn.THit - LeadFrom(LineY);
            bool waveAssociated = false;
            foreach ((NoteKind kind, double at) in _bandSightings)
            {
                if (kind == sn.Kind
                    && Math.Abs(at - crossAt) <= BandAssocWindowSeconds)
                {
                    waveAssociated = true;
                    break;
                }
            }
            if (!covered && confident && waveAssociated)
            {
                scheduled.Add(sn);
                _accepted.Add((sn.Kind, sn.THit, timeSeconds));
            }
            else
            {
                _suppressed.Add(new SuppressedDuplicate(sn.Kind, sn.THit));
            }
        }
        return scheduled;
    }

    public List<ExpiredTrack> DrainExpired()
    {
        var drained = _arrowTracker.DrainExpired();
        drained.AddRange(_rescueTracker.DrainExpired());
        return drained;
    }

    public List<SuppressedDuplicate> DrainSuppressed()
    {
        var drained = _arrowTracker.DrainSuppressed();
        drained.AddRange(_rescueTracker.DrainSuppressed());
        drained.AddRange(_suppressed);
        _suppressed.Clear();
        return drained;
    }

    public List<RevisedNote> DrainRevisions()
    {
        var drained = _arrowTracker.DrainRevisions();
        drained.AddRange(_rescueTracker.DrainRevisions());
        return drained;
    }

    public List<(int Id, double Time, double Y)> DrainSampleLog()
    {
        var drained = _arrowTracker.DrainSampleLog();
        drained.AddRange(_rescueTracker.DrainSampleLog());
        return drained;
    }
}
