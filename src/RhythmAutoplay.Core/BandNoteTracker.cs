namespace RhythmAutoplay.Core;

/// <summary>二重発火として抑制された予約の診断情報。</summary>
public readonly record struct SuppressedDuplicate(NoteKind Kind, double THit);

/// <summary>レーンごとの追跡器の共通インターフェース（実プレイ経路が使う）。</summary>
public interface ILaneTracker
{
    List<ScheduledNote> Observe(double timeSeconds, IReadOnlyList<NoteDetection> laneDetections);
    List<ExpiredTrack> DrainExpired();
    List<SuppressedDuplicate> DrainSuppressed();
}

/// <summary>
/// 帯域トリガー方式の追跡器（v0.3.14、マスター発案）。
/// ノーツの同一性を全飛行にわたって管理する代わりに、判定ライン手前の
/// 帯域（既定 Y 0.76〜0.905）の中だけで検出を束ね、帯を抜ける瞬間に
/// 「その帯で測った速度のまま判定ラインへ到達する時刻」を予測して
/// 1 回だけ予約する。スクロール速度は曲によらず一定（BPM 105 と 194 で
/// 移動時間 1.855 秒 vs 1.862 秒と実測済み）なので、帯の中の短い観測でも
/// 到達時刻は決まる、という前提に立つ。
/// 従来方式（LaneNoteTracker）の弱点だった「軌跡の分断・対応付けの奪い
/// 合い・縫合の誤接続」は、帯の外で同一性を持たないことで構造的に消える。
/// 帯の中で分断した場合に備え、発火済み予約と到達時刻が近い同キー予約は
/// 抑制する（同じノーツの二重発火防止）。
/// v0.3.15: 初版は 60fps 前提の定数だったが、キャプチャループの実測は
/// 約 31fps で検出もちらつく。v0.3.14 の Fly Again 実測では候補 2334 件
/// （大半が 0.07 秒の無音分割で死ぬ 1〜2 サンプルの断片）に S=341、
/// CRINGE 259（従来方式 41）。無音許容と接続ギャップを 31fps の
/// 約 4 フレーム分へ広げて再測する。
/// </summary>
public sealed class BandNoteTracker : ILaneTracker
{
    private sealed class Candidate
    {
        public required NoteKind Kind;
        public List<(double Time, double Y)> Samples { get; } = new();
        public Dictionary<ArrowDirection, int> DirectionVotes { get; } = new();
        public double FirstY { get; set; }
        public double FirstTime { get; set; }
        public double LastY { get; set; }
        public double LastSeenTime { get; set; }
        public int TotalSamples { get; set; }

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

    private readonly List<Candidate> _open = new();
    private readonly List<ExpiredTrack> _expired = new();
    private readonly List<SuppressedDuplicate> _suppressed = new();
    private readonly List<(int Group, double THit)> _emitted = new();
    private readonly double _judgmentY;
    private readonly double _vanishingY;
    private readonly double _bandBottomY;
    private readonly double _bandTopY;
    private readonly double _maxResidual;
    private readonly double _dedupWindowSeconds;

    public BandNoteTracker(
        double judgmentY,
        double vanishingY,
        double bandBottomY = 0.76,
        double bandTopY = 0.905,
        double maxResidual = 0.012,
        double dedupWindowSeconds = 0.07)
    {
        _judgmentY = judgmentY;
        _vanishingY = vanishingY;
        _bandBottomY = bandBottomY;
        _bandTopY = bandTopY;
        _maxResidual = maxResidual;
        _dedupWindowSeconds = dedupWindowSeconds;
    }

    public List<ExpiredTrack> DrainExpired()
    {
        var drained = new List<ExpiredTrack>(_expired);
        _expired.Clear();
        return drained;
    }

    public List<SuppressedDuplicate> DrainSuppressed()
    {
        var drained = new List<SuppressedDuplicate>(_suppressed);
        _suppressed.Clear();
        return drained;
    }

    public List<ScheduledNote> Observe(
        double timeSeconds, IReadOnlyList<NoteDetection> laneDetections)
    {
        var ready = new List<ScheduledNote>();

        // 帯の中の検出だけを束ねる。帯の外は見ない（同一性を持たない）。
        foreach (NoteDetection det in laneDetections)
        {
            if (det.Y < _bandBottomY || det.Y > _bandTopY)
            {
                continue;
            }
            Candidate? best = null;
            double bestGap = double.PositiveInfinity;
            foreach (Candidate c in _open)
            {
                if (c.Kind != det.Kind || timeSeconds - c.LastSeenTime > 0.13)
                {
                    continue;
                }
                double expected = ExtrapolateY(c, timeSeconds);
                double gap = det.Y - expected;
                // 下限は広げない: 高密度の同レーン連続ノーツでは後続ノーツが
                // 先行候補のすぐ後ろ（約 -0.03）に入ってくるため、-0.04 まで
                // 広げると取り込んで 1 本に潰れる（単体テストで実証）。
                // 上限だけ +0.10 へ広げる（ちらつき後の再出現は前方にずれる）。
                if (gap < -0.02 || gap > 0.10)
                {
                    continue;
                }
                if (Math.Abs(gap) < bestGap)
                {
                    bestGap = Math.Abs(gap);
                    best = c;
                }
            }
            if (best is null)
            {
                best = new Candidate
                {
                    Kind = det.Kind,
                    FirstY = det.Y,
                    FirstTime = timeSeconds,
                    LastY = det.Y,
                    LastSeenTime = timeSeconds,
                };
                _open.Add(best);
            }
            best.Samples.Add((timeSeconds, det.Y));
            best.TotalSamples++;
            if (best.Samples.Count > 16)
            {
                best.Samples.RemoveAt(0);
            }
            best.LastY = det.Y;
            best.LastSeenTime = timeSeconds;
            if (det.Direction is { } voted)
            {
                best.DirectionVotes[voted] = best.DirectionVotes.GetValueOrDefault(voted) + 1;
            }
        }

        // 閉じ時: 帯の上端に達した、検出が 0.13 秒途切れた、のどちらか。
        // その瞬間に 1 回だけ予測して予約（または不成立として回収）する。
        for (int i = _open.Count - 1; i >= 0; i--)
        {
            Candidate c = _open[i];
            bool exitedTop = c.LastY >= _bandTopY - 0.007;
            bool wentSilent = timeSeconds - c.LastSeenTime > 0.13;
            if (!exitedTop && !wentSilent)
            {
                continue;
            }
            _open.RemoveAt(i);
            bool emitted = TryEmit(c, timeSeconds, ready, out double bestResidual);
            _expired.Add(new ExpiredTrack(
                c.Kind, emitted, c.TotalSamples,
                c.FirstY, c.LastY, c.FirstTime, c.LastSeenTime,
                double.IsPositiveInfinity(bestResidual) ? -1 : bestResidual));
        }

        return ready;
    }

    private bool TryEmit(
        Candidate c, double timeSeconds, List<ScheduledNote> ready, out double bestResidual)
    {
        bestResidual = double.PositiveInfinity;
        if (c.Samples.Count < 3)
        {
            return false;
        }
        if (!HitTimePredictor.TryPredictHitTimeProjective(
                c.Samples, _judgmentY, _vanishingY, out double hitTime, out double residual))
        {
            return false;
        }
        bestResidual = residual;
        if (residual > _maxResidual)
        {
            return false;
        }
        // 静止した UI（判定文字・コンボ数字）は帯の中にも現れるが、速度が
        // スクロールの法則から外れるため、実測レートの範囲で弾く。
        (double t0, double y0) = c.Samples[0];
        (double t1, double y1) = c.Samples[^1];
        if (t1 <= t0)
        {
            return false;
        }
        double rate = (1.0 / (y0 - _vanishingY) - 1.0 / (y1 - _vanishingY)) / (t1 - t0);
        if (rate < 0.9 || rate > 3.2)
        {
            return false;
        }
        if (hitTime < timeSeconds - 0.05 || hitTime > timeSeconds + 0.6)
        {
            return false;
        }
        ArrowDirection? modal = c.ModalDirection;
        if (c.Kind == NoteKind.Arrow && modal is null)
        {
            return false;
        }

        // 二重発火の抑制: 同じキーで到達時刻が近い予約が既に出ていれば、
        // 帯の中で分断した同じノーツの後半とみなして落とす。重複の判定を
        // 落下量のゲートより先に行うのは、分断した後半の断片が短くても
        // 「同じノーツの二重発火」として数えられるようにするため。
        // 実際に発火する候補だけを既発火リストへ登録する。
        int group = KeyGroupOf(c.Kind, modal);
        if (_dedupWindowSeconds > 0)
        {
            _emitted.RemoveAll(e => e.THit < timeSeconds - 0.5);
            foreach ((int g, double tHit) in _emitted)
            {
                if (g == group && Math.Abs(tHit - hitTime) <= _dedupWindowSeconds)
                {
                    _suppressed.Add(new SuppressedDuplicate(c.Kind, hitTime));
                    return false;
                }
            }
        }

        // 帯の中で十分に落下していない候補（短い断片）は発火しない。
        if (c.LastY - c.FirstY < 0.05)
        {
            return false;
        }
        if (_dedupWindowSeconds > 0)
        {
            _emitted.Add((group, hitTime));
        }

        ready.Add(new ScheduledNote(
            c.Kind, modal, hitTime, residual, c.TotalSamples, c.FirstY, c.FirstTime));
        return true;
    }

    /// <summary>予約が叩くキーのグループ。赤と黄は同じ下段キーを叩くため同一視する。</summary>
    private static int KeyGroupOf(NoteKind kind, ArrowDirection? direction) => kind switch
    {
        NoteKind.Red => 0,
        NoteKind.Yellow => 0,
        NoteKind.Blue => 1,
        NoteKind.Purple => 2,
        NoteKind.Arrow => 10 + (direction is { } d ? (int)d : 0),
        _ => 9,
    };

    private double ExtrapolateY(Candidate c, double t)
    {
        double expectedY = c.LastY;
        if (c.Samples.Count >= 2)
        {
            (double t1, double y1) = c.Samples[^2];
            (double t2, double y2) = c.Samples[^1];
            if (t2 > t1
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
        }
        return expectedY;
    }
}
