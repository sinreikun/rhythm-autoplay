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
        Assert.Equal("D", plan[0].Key);
        Assert.True(plan[0].IsDown);
        Assert.False(plan[1].IsDown);
    }

    [Fact]
    public void Purple_PressesRedAndBlueTogether()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Purple, lane: 2), 1.0);
        var downs = plan.Where(p => p.IsDown).Select(p => p.Key).ToList();
        Assert.Equal(new[] { "J", "U" }, downs);
        Assert.All(plan.Where(p => p.IsDown), p => Assert.Equal(1.0, p.TimeSeconds));
    }

    [Fact]
    public void Arrow_UsesDirectionKey()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Arrow, dir: ArrowDirection.Left), 1.0);
        Assert.Equal("S", plan[0].Key);
    }

    [Fact]
    public void InvalidLane_ReturnsEmpty()
    {
        var plan = KeyPlanBuilder.BuildTap(Profile, Note(NoteKind.Red, lane: 9), 1.0);
        Assert.Empty(plan);
    }
}
