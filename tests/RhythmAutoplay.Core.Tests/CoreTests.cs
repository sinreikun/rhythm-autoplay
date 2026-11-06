using RhythmAutoplay.Core;
using Xunit;

namespace RhythmAutoplay.Core.Tests;

public class HitTimePredictorTests
{
    [Fact]
    public void ConstantVelocity_PredictsHitTime()
    {
        var samples = new List<(double, double)>();
        for (int i = 0; i < 6; i++)
        {
            double t = i * 0.05;
            samples.Add((t, 0.20 + 0.90 * t));
        }

        bool ok = HitTimePredictor.TryPredictHitTime(samples, 0.94, out double hitTime, out double residual);

        Assert.True(ok);
        // 0.20 + 0.90 t = 0.94 -> t = 0.8222...
        Assert.Equal(0.8222, hitTime, precision: 3);
        Assert.True(residual < 1e-9);
    }

    [Fact]
    public void TooFewSamples_ReturnsFalse()
    {
        var samples = new List<(double, double)> { (0.0, 0.1), (0.1, 0.2) };
        Assert.False(HitTimePredictor.TryPredictHitTime(samples, 0.9, out _, out _));
    }

    [Fact]
    public void AlreadyPassed_ReturnsFalse()
    {
        var samples = new List<(double, double)> { (0.0, 0.5), (0.1, 0.6), (0.2, 0.7) };
        Assert.False(HitTimePredictor.TryPredictHitTime(samples, 0.4, out _, out _));
    }
}

public class InputSchedulerTests
{
    [Fact]
    public void CollectDue_ReturnsInTimeOrder()
    {
        var scheduler = new InputScheduler();
        scheduler.Enqueue(new ScheduledInput(1.0, "D", true));
        scheduler.Enqueue(new ScheduledInput(0.5, "F", true));
        scheduler.Enqueue(new ScheduledInput(2.0, "J", true));

        var due = scheduler.CollectDue(1.0);

        Assert.Equal(2, due.Count);
        Assert.Equal("F", due[0].Key);
        Assert.Equal("D", due[1].Key);
        Assert.Equal(1, scheduler.Count);
        Assert.Equal(2.0, scheduler.NextTimeSeconds);
    }

    [Fact]
    public void ShiftPending_MovesOnlyTaggedEvents()
    {
        // 予約の事後修正（v0.3.37）: タグ付きの down/up だけが一緒に動き、
        // 他の予約には触れない。down が発火済みなら修正はできない。
        var scheduler = new InputScheduler();
        scheduler.Enqueue(new ScheduledInput(1.00, "F", true, Tag: 7));
        scheduler.Enqueue(new ScheduledInput(1.04, "F", false, Tag: 7));
        scheduler.Enqueue(new ScheduledInput(1.02, "G", true, Tag: 8));

        Assert.True(scheduler.ShiftPending(7, 0.02));
        Assert.False(scheduler.ShiftPending(99, 0.02));

        var due = scheduler.CollectDue(1.03);
        Assert.Equal(2, due.Count); // G(1.02) と、ずれた F down(1.02)
        Assert.Contains(due, e => e.Key == "F" && e.IsDown && Math.Abs(e.TimeSeconds - 1.02) < 1e-9);
        Assert.False(scheduler.ShiftPending(7, 0.01)); // down はもう無い
        var rest = scheduler.CollectDue(2.0);
        Assert.Single(rest);
        Assert.Equal(1.06, rest[0].TimeSeconds, precision: 9); // F up も一緒にずれている
    }
}

public class KeyPlanBuilderTests
{
    private static readonly GameProfile Profile = GameProfile.HypnosyncDefault();

    private static NoteDetection Note(NoteKind kind, int lane = 0, ArrowDirection? dir = null)
        => new(lane, kind, dir, 0.5, 0.5, 0.0);

    [Fact]
    public void Red_UsesRedKey()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Red), 1.0);
        Assert.Equal(2, plan.Count);
        Assert.Equal("F", plan[0].Key);
        Assert.True(plan[0].IsDown);
        Assert.False(plan[1].IsDown);
    }

    [Fact]
    public void Purple_PressesRedAndBlueTogether()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Purple, lane: 2), 1.0);
        var downs = plan.Where(p => p.IsDown).Select(p => p.Key).ToList();
        Assert.Equal(new[] { "H", "Y" }, downs);
        Assert.All(plan.Where(p => p.IsDown), p => Assert.Equal(1.0, p.TimeSeconds));
    }

    [Fact]
    public void Arrow_UsesDirectionKey()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Arrow, dir: ArrowDirection.Left), 1.0);
        Assert.Equal("A", plan[0].Key);
    }

    [Fact]
    public void InvalidLane_ReturnsEmpty()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Red, lane: 9), 1.0);
        Assert.Empty(plan);
    }
}

public class LaneGeometryTests
{
    [Fact]
    public void CenterLane_BottomCenterMatchesProfile()
    {
        var geo = LaneGeometry.Default;
        // 4レーンの中央境界は CenterX。レーン1中心は CenterX - 半幅 + レーン幅/2。
        double lane0 = geo.LaneCenterX(0, 4, geo.BottomY);
        double lane3 = geo.LaneCenterX(3, 4, geo.BottomY);
        Assert.Equal(geo.CenterX - geo.BottomHalfWidth + geo.LaneWidthAt(4, geo.BottomY) / 2, lane0, precision: 10);
        Assert.Equal(geo.CenterX + geo.BottomHalfWidth - geo.LaneWidthAt(4, geo.BottomY) / 2, lane3, precision: 10);
    }

    [Fact]
    public void HalfWidth_ShrinksTowardTop()
    {
        var geo = LaneGeometry.Default;
        Assert.True(geo.HalfWidthAt(geo.TopY) < geo.HalfWidthAt(geo.BottomY));
        Assert.Equal(geo.TopHalfWidth, geo.HalfWidthAt(geo.TopY), precision: 10);
        Assert.Equal(geo.BottomHalfWidth, geo.HalfWidthAt(geo.BottomY), precision: 10);
    }
}

public class ColorClassifierTests
{
    [Theory]
    [InlineData(255, 40, 40, PixelColor.Red)]
    [InlineData(255, 80, 160, PixelColor.Red)] // ピンク寄りも赤系に含める
    [InlineData(60, 120, 255, PixelColor.Blue)]
    [InlineData(43, 207, 254, PixelColor.Blue)] // HYPNOSYNC 青ノーツ実測色 (h=0.537)
    [InlineData(169, 32, 104, PixelColor.Red)] // HYPNOSYNC ピンクノーツ実測色 (h=0.912)
    [InlineData(65, 43, 54, PixelColor.None)] // コンボ数字の半透明ゴースト実測色 (v≈0.25, s≈0.35)
    [InlineData(170, 60, 230, PixelColor.Purple)]
    [InlineData(250, 210, 40, PixelColor.Yellow)]
    [InlineData(20, 20, 30, PixelColor.None)] // 暗い背景
    [InlineData(200, 200, 200, PixelColor.None)] // 無彩色
    public void Classify_KnownColors(byte r, byte g, byte b, PixelColor expected)
    {
        Assert.Equal(expected, ColorClassifier.Classify(r, g, b));
    }

    [Fact]
    public void ClassifyLoose_ReadsTranslucentNoteOverWhiteBand()
    {
        // ピンク (169,32,104) を白の上に 50% で重ねた色: 通常分類では
        // 彩度が落ちて None になるが、緩い分類では色相から赤と読める。
        Assert.Equal(PixelColor.None, ColorClassifier.Classify(212, 143, 179));
        Assert.Equal(PixelColor.Red, ColorClassifier.ClassifyLoose(212, 143, 179));
        // 白そのものや暗いゴーストは緩い分類でも拾わない。
        Assert.Equal(PixelColor.None, ColorClassifier.ClassifyLoose(255, 255, 255));
        Assert.Equal(PixelColor.None, ColorClassifier.ClassifyLoose(65, 43, 54));
    }
}

public class LaneNoteTrackerTests
{
    [Fact]
    public void SteadyNote_SchedulesOnceWithPredictedHitTime()
    {
        var tracker = new LaneNoteTracker(judgmentY: 0.94);
        int scheduledCount = 0;
        double scheduledHit = 0;
        // 等速 0.9/s で落下する赤ノーツを 60fps で観測する。
        for (int i = 0; i <= 30; i++)
        {
            double t = i / 60.0;
            double y = 0.30 + 0.9 * t;
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            foreach (var (kind, _, tHit, _, _, _, _) in tracker.Observe(t, new[] { det }))
            {
                scheduledCount++;
                scheduledHit = tHit;
                Assert.Equal(NoteKind.Red, kind);
            }
        }
        Assert.Equal(1, scheduledCount);
        // 0.30 + 0.9 t = 0.94 -> t = 0.7111
        Assert.Equal(0.7111, scheduledHit, precision: 2);
    }

    [Fact]
    public void StarvedTrack_SchedulesFromFrozenFit()
    {
        // 判定帯の近くで検出が途切れた軌跡は、検出と結びつかない
        // フレームでも、凍結した標本の予測が確定窓に入れば予約される。
        // (v0.3.24: 従来はタイムアウト時の死に際救済しか残らず、
        // 予測到達時刻が過去猶予を過ぎて捨てられていた。)
        var tracker = new LaneNoteTracker(judgmentY: 0.94, commitWindowSeconds: 0.15);
        int scheduledCount = 0;
        double scheduledHit = 0;
        double scheduledAt = 0;
        // 等速 0.9/s: 0.30 + 0.9 t = 0.94 -> 到達 t = 0.7111。
        // t=0.52 (到達まで 0.19 秒、確定窓 0.15 の外) で検出が止まる。
        for (int i = 0; i <= 200; i++)
        {
            double t = i / 60.0;
            var dets = t <= 0.52
                ? new[] { new NoteDetection(0, NoteKind.Red, null, 0.4, 0.30 + 0.9 * t, t) }
                : Array.Empty<NoteDetection>();
            foreach (var note in tracker.Observe(t, dets))
            {
                scheduledCount++;
                scheduledHit = note.THit;
                scheduledAt = t;
            }
        }
        Assert.Equal(1, scheduledCount);
        Assert.Equal(0.7111, scheduledHit, precision: 2);
        // 到達前に予約が確定していること（死に際の遅すぎる発火ではない）。
        Assert.True(scheduledAt < scheduledHit);
    }

    [Fact]
    public void Tracker_Revision_RefinesCommittedHit()
    {
        // 予約の事後修正（v0.3.37）: 確定後に速度がわずかに変わると、
        // 確定時の予測は古い法則のまま外れる。ライン際の標本で締め直した
        // 予測が 8ms 以上ずれるなら、修正イベントが出て発火時刻が
        // 本当の到達時刻に近づくこと。
        var tracker = new LaneNoteTracker(judgmentY: 0.94, commitWindowSeconds: 0.10);
        double committedHit = double.NaN;
        var revisions = new List<RevisedNote>();
        // t=0.55 (y=0.795) までは 0.9/s、それ以降は 1.05/s。
        // 本当の到達は 0.55 + 0.145/1.05 = 0.6881。旧法則の予測は 0.7111。
        static double Y(double t) => t <= 0.55 ? 0.30 + 0.9 * t : 0.795 + 1.05 * (t - 0.55);
        for (int i = 0; i <= 45; i++)
        {
            double t = i / 60.0;
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, Y(t), t);
            foreach (var note in tracker.Observe(t, new[] { det }))
            {
                committedHit = note.THit;
            }
            revisions.AddRange(tracker.DrainRevisions());
        }
        Assert.False(double.IsNaN(committedHit));
        Assert.NotEmpty(revisions);
        double finalHit = revisions[^1].NewHit;
        Assert.True(
            Math.Abs(finalHit - 0.6881) < Math.Abs(committedHit - 0.6881) - 0.004,
            $"committed={committedHit:F4} final={finalHit:F4} true=0.6881");
    }

    [Fact]
    public void BestLineFallback_PollutedLateSamples_StillSchedules()
    {
        // 判定帯近くで検出位置がフレームごとに荒れると（判定エフェクトの
        // 重なりで blob の下端が揺れる実測パターン）、直近 12 標本の窓は
        // 残差ゲートを超えて現在の適合では予約できない。過去の最良直線
        // （清浄な期間の適合）が保存されていれば、その直線の予測で
        // 予約できる（v0.3.28、v0.3.27 の実測診断: ゴーストの 64% が
        // resid 死・21% が nofit 死だったことへの対策）。
        // 荒れオフセットは固定列（乱数シード 1000 の一様 ±0.03 から生成し、
        // フォールバック無効化時は予約が消えることを差分確認済み）。
        var tracker = new LaneNoteTracker(
            judgmentY: 0.941, vanishingY: 0.39, commitWindowSeconds: 0.10);
        int scheduledCount = 0;
        double scheduledHit = 0;
        double scheduledAt = 0;
        var ragged = new Dictionary<int, double>
        {
            { 14, 0.0166 }, { 15, 0.0102 }, { 16, -0.0241 }, { 17, -0.0088 },
            { 18, -0.0019 }, { 19, 0.0021 }, { 20, 0.0287 },
        };
        // y(t) = 0.39 + 1/(3.2 - 1.6 t): 判定 w=1/(0.941-0.39)=1.8149 より
        // 到達 t = (3.2 - 1.8149) / 1.6 = 0.8657。
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 30.0;
            NoteDetection[] dets;
            if (t <= 0.6833)
            {
                double y = 0.39 + 1.0 / (3.2 - 1.6 * t) + ragged.GetValueOrDefault(i);
                dets = new[] { new NoteDetection(0, NoteKind.Red, null, 0.4, y, t) };
            }
            else
            {
                dets = Array.Empty<NoteDetection>();
            }
            foreach (var note in tracker.Observe(t, dets))
            {
                scheduledCount++;
                scheduledHit = note.THit;
                scheduledAt = t;
            }
        }
        Assert.Equal(1, scheduledCount);
        Assert.Equal(0.8657, scheduledHit, precision: 2);
        Assert.True(scheduledAt < scheduledHit);
    }

    [Fact]
    public void BestLineFallback_NeverCleanTrack_DoesNotSchedule()
    {
        // 最初から最後まで標本が荒れている軌跡は、最良直線の残差自体が
        // ゲートを超えているため、フォールバックでも予約されない。
        // （残差ゲートを 0.0025 に締め、±0.006 の交代ジッタを与える:
        // 最良直線の残差は約 0.006 で、ゲート超えのまま保存される。）
        var tracker = new LaneNoteTracker(
            judgmentY: 0.941, vanishingY: 0.39, commitWindowSeconds: 0.10,
            maxResidual: 0.0025);
        int scheduledCount = 0;
        for (int i = 0; i <= 80; i++)
        {
            double t = i / 60.0;
            var dets = Array.Empty<NoteDetection>();
            if (t <= 0.6833)
            {
                double jitter = (i % 2 == 0) ? 0.006 : -0.006;
                double y = 0.39 + 1.0 / (3.2 - 1.6 * t) + jitter;
                dets = new[] { new NoteDetection(0, NoteKind.Red, null, 0.4, y, t) };
            }
            foreach (var _ in tracker.Observe(t, dets))
            {
                scheduledCount++;
            }
        }
        Assert.Equal(0, scheduledCount);
    }

    [Fact]
    public void SpawnGate_NoteBornBelowGate_NeverSchedules()
    {
        // 判定文字やコンボ数字のような UI は画面下部にいきなり現れる。
        // ゲートより下で生まれた軌跡は、落下して見えても発火しない。
        var tracker = new LaneNoteTracker(judgmentY: 0.94, spawnGateY: 0.80);
        int scheduledCount = 0;
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 60.0;
            double y = 0.84 + 0.9 * t;
            var det = new NoteDetection(0, NoteKind.Yellow, null, 0.4, y, t);
            scheduledCount += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(0, scheduledCount);
    }

    [Fact]
    public void SpawnGate_NoteBornAboveGate_Schedules()
    {
        var tracker = new LaneNoteTracker(judgmentY: 0.94, spawnGateY: 0.80);
        int scheduledCount = 0;
        for (int i = 0; i <= 30; i++)
        {
            double t = i / 60.0;
            double y = 0.30 + 0.9 * t;
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            scheduledCount += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(1, scheduledCount);
    }

    [Fact]
    public void FastNoteBornLow_StillSchedules()
    {
        // 高速譜面では本物のノーツの初検出が画面下部（Y≈0.80）になる。
        // 緩いゲート (0.88) の内側で生まれ、実際に下っていれば発火する。
        var tracker = new LaneNoteTracker(
            judgmentY: 0.94, spawnGateY: 0.88, scheduleHorizonSeconds: 1.2);
        int scheduledCount = 0;
        for (int i = 0; i <= 10; i++)
        {
            double t = i / 60.0;
            double y = 0.80 + 1.2 * t;
            var det = new NoteDetection(0, NoteKind.Blue, null, 0.4, y, t);
            scheduledCount += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(1, scheduledCount);
    }

    [Fact]
    public void StaticBlob_NeverSchedules()
    {
        // 判定文字のような静止した塊は、ゲートの内側にあっても発火しない。
        var tracker = new LaneNoteTracker(
            judgmentY: 0.94, spawnGateY: 0.88, scheduleHorizonSeconds: 1.2);
        int scheduledCount = 0;
        for (int i = 0; i <= 30; i++)
        {
            double t = i / 60.0;
            var det = new NoteDetection(1, NoteKind.Yellow, null, 0.5, 0.77, t);
            scheduledCount += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(0, scheduledCount);
    }

    [Fact]
    public void ArrowWaitsForDirection_ThenSchedulesWithModalDirection()
    {
        var tracker = new LaneNoteTracker(judgmentY: 0.94);
        int scheduledCount = 0;
        ArrowDirection? scheduledDirection = null;
        int scheduledAtFrame = -1;
        for (int i = 0; i <= 45; i++)
        {
            double t = i / 60.0;
            double y = 0.30 + 0.9 * t;
            ArrowDirection? dir = i switch
            {
                < 5 => null,
                5 => ArrowDirection.Left,
                6 => ArrowDirection.Right,
                _ => ArrowDirection.Left,
            };
            var det = new NoteDetection(0, NoteKind.Arrow, dir, 0.4, y, t);
            foreach (var (_, direction, _, _, _, _, _) in tracker.Observe(t, new[] { det }))
            {
                scheduledCount++;
                scheduledDirection = direction;
                scheduledAtFrame = i;
            }
        }
        Assert.Equal(1, scheduledCount);
        Assert.True(scheduledAtFrame >= 5, "方向が付く前に予約してはいけない");
        Assert.Equal(ArrowDirection.Left, scheduledDirection);
    }

    [Fact]
    public void ArrowWithoutDirection_NeverSchedules()
    {
        var tracker = new LaneNoteTracker(judgmentY: 0.94);
        int scheduledCount = 0;
        for (int i = 0; i <= 45; i++)
        {
            double t = i / 60.0;
            var det = new NoteDetection(0, NoteKind.Arrow, null, 0.4, 0.30 + 0.9 * t, t);
            scheduledCount += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(0, scheduledCount);
    }

    [Fact]
    public void DenseSameLanePair_BothSchedule()
    {
        // 同色・同レーンの接近した2連（間隔 0.055、高速）。予測位置ベースの
        // 対応付けで両方とも予約できることを確認する。
        var tracker = new LaneNoteTracker(judgmentY: 0.94);
        int scheduledCount = 0;
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 60.0;
            double yFront = 0.20 + 1.4 * t;
            var dets = new List<NoteDetection>
            {
                new(0, NoteKind.Blue, null, 0.4, yFront - 0.055, t),
                new(0, NoteKind.Blue, null, 0.4, yFront, t),
            };
            scheduledCount += tracker.Observe(t, dets).Count;
        }
        Assert.Equal(2, scheduledCount);
    }

    [Fact]
    public void TwoNotesSameLane_ScheduleSeparately()
    {
        var tracker = new LaneNoteTracker(judgmentY: 0.94);
        int scheduledCount = 0;
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 60.0;
            var dets = new List<NoteDetection>
            {
                new(0, NoteKind.Blue, null, 0.4, 0.30 + 0.9 * t, t),
                new(0, NoteKind.Blue, null, 0.4, 0.10 + 0.9 * t, t),
            };
            scheduledCount += tracker.Observe(t, dets).Count;
        }
        Assert.Equal(2, scheduledCount);
    }
}

public class ProjectivePredictorTests
{
    // 等速で近づくノーツは画面上では加速して見える。
    // y(t) = vy + 1 / (w0 - k t) という射影運動で合成する。
    private static List<(double, double)> ProjectiveSamples(double vy, double w0, double k, double tEnd)
    {
        var samples = new List<(double, double)>();
        for (double t = 0; t <= tEnd; t += 0.05)
        {
            samples.Add((t, vy + 1.0 / (w0 - k * t)));
        }
        return samples;
    }

    [Fact]
    public void Projective_PredictsTrueHitTime()
    {
        const double vy = 0.12, w0 = 8.0, k = 6.0, target = 0.94;
        var samples = ProjectiveSamples(vy, w0, k, 0.5);
        // 目標到達: w = 1/(0.94-0.12) = 1.2195 -> t = (8 - 1.2195)/6 = 1.1301
        bool ok = HitTimePredictor.TryPredictHitTimeProjective(samples, target, vy, out double hitTime, out double residual);
        Assert.True(ok);
        Assert.Equal(1.1301, hitTime, precision: 2);
        Assert.True(residual < 1e-6);
    }

    [Fact]
    public void Linear_OnProjectiveMotion_PredictsTooLate()
    {
        // v0.2.0 の不具合の再現: 直線フィットは到達を遅く見積もりすぎる。
        const double vy = 0.12, w0 = 8.0, k = 6.0, target = 0.94;
        var samples = ProjectiveSamples(vy, w0, k, 0.5);
        Assert.True(HitTimePredictor.TryPredictHitTime(samples, target, out double linearHit, out _));
        Assert.True(linearHit > 1.1301 + 0.15, $"linear={linearHit:F3} は真値より大幅に遅いはず");
    }

    [Fact]
    public void Tracker_WithVanishingY_SchedulesProjectiveNote()
    {
        var tracker = new LaneNoteTracker(judgmentY: 0.94, vanishingY: 0.12);
        int scheduled = 0;
        double hit = 0;
        for (int i = 0; i <= 40; i++)
        {
            double t = i / 60.0;
            double y = 0.12 + 1.0 / (8.0 - 6.0 * t);
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            foreach (var (_, _, tHit, _, _, _, _) in tracker.Observe(t, new[] { det }))
            {
                scheduled++;
                hit = tHit;
            }
        }
        Assert.Equal(1, scheduled);
        Assert.Equal(1.1301, hit, precision: 2);
    }

    [Fact]
    public void Tracker_ProjectiveMatching_SurvivesFrameHole()
    {
        // 高速域でフレームが抜けても、射影外挿なら対応付けが外れず軌跡が
        // 分断しない（直線外挿では予測が低く出て別軌跡になり、二重予約や
        // 予約遅れの原因になっていた。v0.3.9 の実機診断に基づく対策）。
        var tracker = new LaneNoteTracker(judgmentY: 0.94, vanishingY: 0.12);
        int scheduled = 0;
        double hit = 0;
        for (int i = 0; i <= 68; i++)
        {
            if (i >= 63 && i <= 67)
            {
                continue; // 判定直前の高速域で 5 フレーム抜け
            }
            double t = i / 60.0;
            double y = 0.12 + 1.0 / (8.0 - 6.0 * t);
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            foreach (var sn in tracker.Observe(t, new[] { det }))
            {
                scheduled++;
                hit = sn.THit;
            }
        }
        Assert.Equal(1, scheduled);
        Assert.Equal(1.1301, hit, precision: 2);
        // ノーツ通過後に軌跡が診断つきで回収される
        for (int i = 69; i <= 100; i++)
        {
            tracker.Observe(i / 60.0, Array.Empty<NoteDetection>());
        }
        var expired = tracker.DrainExpired();
        Assert.Single(expired);
        Assert.True(expired[0].Scheduled);
    }

    [Fact]
    public void Tracker_LateCommit_WaitsForWindowThenSchedulesOnce()
    {
        // 遅延確定（v0.3.10）: 予測が立っても到達まで commitWindow を切るまでは
        // 確定せず、窓に入ってから一度だけ、正確な到達時刻で確定する。
        var tracker = new LaneNoteTracker(
            judgmentY: 0.94, vanishingY: 0.12, commitWindowSeconds: 0.15);
        int scheduled = 0;
        double hit = 0;
        double scheduledAt = -1;
        for (int i = 0; i <= 68; i++)
        {
            double t = i / 60.0;
            double y = 0.12 + 1.0 / (8.0 - 6.0 * t);
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            foreach (var sn in tracker.Observe(t, new[] { det }))
            {
                scheduled++;
                hit = sn.THit;
                scheduledAt = t;
            }
        }
        Assert.Equal(1, scheduled);
        Assert.Equal(1.1301, hit, precision: 2);
        // 真の到達 1.1301 に対して、確定は窓（0.15 秒）に入ってから
        Assert.True(scheduledAt >= 1.1301 - 0.15 - 1e-9, $"scheduledAt={scheduledAt:F3} は早すぎる");
    }

    [Fact]
    public void Tracker_DeathbedRescue_SchedulesWhenDetectionsStopEarly()
    {
        // 死に際の救済（v0.3.11）: 検出が途中で止まった軌跡は、確定窓が開く前に
        // タイムアウトで消える。消滅の瞬間に最後の予測で一度だけ予約される。
        var tracker = new LaneNoteTracker(
            judgmentY: 0.94, vanishingY: 0.12, commitWindowSeconds: 0.15);
        int scheduled = 0;
        double hit = 0;
        // t=0..0.5 まで標本を与え、その後検出が止まる（到達 1.1301 まで遠い）
        for (int i = 0; i <= 30; i++)
        {
            double t = i / 60.0;
            double y = 0.12 + 1.0 / (8.0 - 6.0 * t);
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            scheduled += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(0, scheduled); // 窓（0.15 秒）にはまだ入っていない
        for (int i = 31; i <= 60; i++)
        {
            double t = i / 60.0;
            foreach (var sn in tracker.Observe(t, Array.Empty<NoteDetection>()))
            {
                scheduled++;
                hit = sn.THit;
            }
        }
        Assert.Equal(1, scheduled);
        Assert.Equal(1.1301, hit, precision: 2);
    }

    [Fact]
    public void Tracker_SilentTrackNearWindow_CommitsOnSilentFrames()
    {
        // 履歴: v0.3.11 の挙動では、到達直前で検出が止まった軌跡は確定窓が
        // 開いても予約されず、死に際救済でも到達時刻が猶予 (0.02 秒) を
        // 過ぎていて救えなかった。v0.3.12 の毎フレーム試行はこの欠落を
        // 埋めたが、過去猶予 0.12 秒との抱き合わせ・重複抑制なしの条件で
        // 実機小幅悪化となり v0.3.13 で取り下げた。
        // v0.3.24 で再導入する根拠: セッションログの X 行フォレンジクスで、
        // 適合の良い (残差中央値 0.001 台) 未予約軌跡が Floating 1 ランに
        // 67〜146 件死んでおり、その数が確定窓を狭めるほど増えて CRINGE と
        // 連動していた。今回は未マッチ軌跡に限り、ゲート不変・猶予不変・
        // 重複抑制ありの条件で、凍結標本の予測が確定窓に入った時点で予約
        // する。成否は実機 A/B で判断する。
        var tracker = new LaneNoteTracker(
            judgmentY: 0.94, vanishingY: 0.12, commitWindowSeconds: 0.15);
        int scheduled = 0;
        double hit = 0;
        // t=0..0.9333 まで標本（到達 1.1301 まで 0.197 秒 = 窓の外）
        for (int i = 0; i <= 56; i++)
        {
            double t = i / 60.0;
            double y = 0.12 + 1.0 / (8.0 - 6.0 * t);
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, y, t);
            scheduled += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(0, scheduled);
        // 以後は検出なし。確定窓が開く t=0.9801 以降の無検出フレームで、
        // 凍結した標本の予測のまま 1 件だけ予約される。
        for (int i = 57; i <= 75; i++)
        {
            double t = i / 60.0;
            foreach (var sn in tracker.Observe(t, Array.Empty<NoteDetection>()))
            {
                scheduled++;
                hit = sn.THit;
            }
        }
        Assert.Equal(1, scheduled);
        Assert.Equal(1.1301, hit, precision: 2);
        var expired = tracker.DrainExpired();
        Assert.Single(expired);
        Assert.True(expired[0].Scheduled);
    }

    // 帯域トリガー（v0.3.14）用の合成運動: 実機の幾何（判定 0.941、消失点
    // 0.39）と実測レート（w の減少速度 1.86/秒）で組み立てる。
    // 出現 Y=0.563、判定到達は t=2.1318。帯（0.76〜0.905）には
    // t=1.6545 で入り、t=2.0636 で出る。
    private static double BandLawY(double t) => 0.39 + 1.0 / (5.78 - 1.86 * t);

    [Fact]
    public void Band_SteadyNote_SchedulesOnceAtBandExit()
    {
        var tracker = new BandNoteTracker(judgmentY: 0.941, vanishingY: 0.39);
        int scheduled = 0;
        double hit = 0;
        for (int i = 96; i <= 138; i++)
        {
            double t = i / 60.0;
            var det = new NoteDetection(0, NoteKind.Red, null, 0.4, BandLawY(t), t);
            foreach (var sn in tracker.Observe(t, new[] { det }))
            {
                scheduled++;
                hit = sn.THit;
            }
        }
        Assert.Equal(1, scheduled);
        Assert.Equal(2.1318, hit, precision: 2);
    }

    [Fact]
    public void Band_StaticBlob_NeverSchedules()
    {
        // 帯の中で静止する塊（判定文字など）は、速度ゲートで弾かれる。
        var tracker = new BandNoteTracker(judgmentY: 0.941, vanishingY: 0.39);
        int scheduled = 0;
        for (int i = 0; i <= 78; i++)
        {
            double t = i / 60.0;
            var dets = t <= 1.0
                ? new[] { new NoteDetection(0, NoteKind.Yellow, null, 0.4, 0.83, t) }
                : Array.Empty<NoteDetection>();
            scheduled += tracker.Observe(t, dets).Count;
        }
        Assert.Equal(0, scheduled);
    }

    [Fact]
    public void Band_FragmentedNote_SecondFragmentSuppressed()
    {
        // 帯の中で検出が途切れて 2 つの候補に割れても、二重発火はしない:
        // 前半が予約した到達時刻とほぼ同じ後半の予約は抑制される。
        var tracker = new BandNoteTracker(judgmentY: 0.941, vanishingY: 0.39);
        int scheduled = 0;
        for (int i = 100; i <= 130; i++)
        {
            double t = i / 60.0;
            bool firstFragment = t <= 1.85;
            bool secondFragment = t >= 2.0;
            var dets = (firstFragment || secondFragment)
                ? new[] { new NoteDetection(0, NoteKind.Red, null, 0.4, BandLawY(t), t) }
                : Array.Empty<NoteDetection>();
            scheduled += tracker.Observe(t, dets).Count;
        }
        Assert.Equal(1, scheduled);
        var suppressed = tracker.DrainSuppressed();
        Assert.Single(suppressed);
        Assert.Equal(NoteKind.Red, suppressed[0].Kind);
    }

    [Fact]
    public void Band_DensePair_BothSchedule()
    {
        // 0.12 秒差の同レーン 2 連は、両方とも独立に予約される
        // （抑制の窓 0.07 秒より離れているため）。
        var tracker = new BandNoteTracker(judgmentY: 0.941, vanishingY: 0.39);
        int scheduled = 0;
        for (int i = 96; i <= 150; i++)
        {
            double t = i / 60.0;
            var dets = new List<NoteDetection>
            {
                new(0, NoteKind.Red, null, 0.4, BandLawY(t - 0.12), t),
                new(0, NoteKind.Red, null, 0.4, BandLawY(t), t),
            };
            scheduled += tracker.Observe(t, dets).Count;
        }
        Assert.Equal(2, scheduled);
        Assert.Empty(tracker.DrainSuppressed());
    }

    [Fact]
    public void Band_ArrowWithoutDirection_NeverSchedules()
    {
        var tracker = new BandNoteTracker(judgmentY: 0.941, vanishingY: 0.39);
        int scheduled = 0;
        for (int i = 96; i <= 138; i++)
        {
            double t = i / 60.0;
            var det = new NoteDetection(0, NoteKind.Arrow, null, 0.4, BandLawY(t), t);
            scheduled += tracker.Observe(t, new[] { det }).Count;
        }
        Assert.Equal(0, scheduled);
    }

}
