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
    [InlineData(170, 60, 230, PixelColor.Purple)]
    [InlineData(250, 210, 40, PixelColor.Yellow)]
    [InlineData(20, 20, 30, PixelColor.None)] // 暗い背景
    [InlineData(200, 200, 200, PixelColor.None)] // 無彩色
    public void Classify_KnownColors(byte r, byte g, byte b, PixelColor expected)
    {
        Assert.Equal(expected, ColorClassifier.Classify(r, g, b));
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
            foreach (var (kind, _, tHit) in tracker.Observe(t, new[] { det }))
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
