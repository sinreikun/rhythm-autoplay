using RhythmAutoplay.Core;
using Xunit;

namespace RhythmAutoplay.Core.Tests;

public class ProfileColorTests
{
    [Fact]
    public void Parse_HexWithAndWithoutHash()
    {
        Assert.Equal(new ProfileColor(43, 207, 254), ProfileColor.Parse("#2BCFFE"));
        Assert.Equal(new ProfileColor(43, 207, 254), ProfileColor.Parse("2bcffe"));
        Assert.Equal("#2BCFFE", new ProfileColor(43, 207, 254).ToHex());
    }

    [Fact]
    public void Parse_Invalid_Throws()
    {
        Assert.Throws<FormatException>(() => ProfileColor.Parse("#12345"));
        Assert.Throws<FormatException>(() => ProfileColor.Parse("zzzzzz"));
    }

    [Fact]
    public void HueDistance_WrapsAround()
    {
        Assert.Equal(0.05, ColorClassifier.HueDistance(0.97, 0.02), precision: 10);
        Assert.Equal(0.5, ColorClassifier.HueDistance(0.10, 0.60), precision: 10);
        Assert.Equal(0.0, ColorClassifier.HueDistance(0.30, 0.30), precision: 10);
    }
}

public class PickedColorMatchTests
{
    private static readonly ProfileColor Blue = new(43, 207, 254);
    private static readonly ProfileColor Pink = new(255, 46, 138);

    [Fact]
    public void ExactColor_Matches()
    {
        Assert.True(ColorClassifier.MatchesPickedColor(43, 207, 254, Blue, 0.06));
        Assert.True(ColorClassifier.MatchesPickedColor(255, 46, 138, Pink, 0.06));
    }

    [Fact]
    public void OtherFamily_DoesNotMatch()
    {
        Assert.False(ColorClassifier.MatchesPickedColor(255, 46, 138, Blue, 0.06));
        Assert.False(ColorClassifier.MatchesPickedColor(43, 207, 254, Pink, 0.06));
    }

    [Fact]
    public void DimOrDesaturated_DoesNotMatch()
    {
        // 暗い画素（v が床未満）と無彩色寄り（s が床未満）は背景として捨てる。
        Assert.False(ColorClassifier.MatchesPickedColor(10, 50, 62, Blue, 0.06));
        Assert.False(ColorClassifier.MatchesPickedColor(200, 190, 190, Pink, 0.06));
    }

    [Fact]
    public void WashedOut_OnWave_MatchesLooseOnly()
    {
        // 白に薄まった青: 厳密読みでは落ち、緩い読みでは色相で拾える。
        Assert.False(ColorClassifier.MatchesPickedColor(180, 220, 240, Blue, 0.06));
        Assert.True(ColorClassifier.MatchesPickedColorLoose(180, 220, 240, Blue, 0.06));
    }

    [Fact]
    public void Tolerance_Boundary()
    {
        // 青 (h=0.537) に対して許容 0.06 なら h=0.59 は入り、h=0.65 は外れる。
        Assert.True(ColorClassifier.HueDistance(0.537, 0.59) <= 0.06);
        Assert.False(ColorClassifier.HueDistance(0.537, 0.65) <= 0.06);
    }
}

public class ColorBindingPlanTests
{
    private static GameProfile BindingProfile()
    {
        var lane = new LaneBinding(0, "D", "F")
        {
            Bindings = new[]
            {
                new NoteColorBinding("D", new[] { new ProfileColor(0, 220, 90) }),
                new NoteColorBinding("F", new[] { new ProfileColor(255, 46, 138) }),
            },
        };
        return new GameProfile(
            "test", "TEST", 1, 0.94,
            new[] { lane },
            new Dictionary<ArrowDirection, string>(),
            40);
    }

    [Fact]
    public void BindingKind_FiresBindingKey()
    {
        GameProfile profile = BindingProfile();
        var note0 = new NoteDetection(0, NoteKind.Binding0, null, 0.5, 0.7, 0.0);
        var note1 = new NoteDetection(0, NoteKind.Binding1, null, 0.5, 0.7, 0.0);

        var events0 = KeyPlanBuilder.BuildTap(profile, note0, 1.0);
        var events1 = KeyPlanBuilder.BuildTap(profile, note1, 1.0);

        Assert.Contains(events0, e => e.Key == "D" && e.IsDown);
        Assert.Contains(events1, e => e.Key == "F" && e.IsDown);
        Assert.DoesNotContain(events0, e => e.Key == "F");
    }

    [Fact]
    public void UsesColorBindings_Detected()
    {
        Assert.True(BindingProfile().UsesColorBindings);
        Assert.False(GameProfile.HypnosyncDefault().UsesColorBindings);
    }
}
