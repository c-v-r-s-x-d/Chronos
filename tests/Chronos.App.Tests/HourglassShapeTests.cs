using Chronos.App.Controls;

namespace Chronos.App.Tests;

public sealed class HourglassShapeTests
{
    private const double Tolerance = 1e-6;

    [Fact]
    public void AtTheStartAllTheSandIsOnTop()
    {
        Assert.Equal(1, HourglassShape.TopSand(0), Tolerance);
        Assert.Equal(0, HourglassShape.BottomSand(0), Tolerance);
    }

    [Fact]
    public void AtTheEndAllTheSandIsBelow()
    {
        Assert.Equal(0, HourglassShape.TopSand(1), Tolerance);
        Assert.Equal(1, HourglassShape.BottomSand(1), Tolerance);
    }

    [Theory]
    [InlineData(0.1)]
    [InlineData(0.5)]
    [InlineData(0.9)]
    public void TheSandIsSharedByAreaAndNotByHeight(double progress)
    {
        var full = HourglassShape.Area(1);
        var top = HourglassShape.Area(HourglassShape.TopSand(progress));
        var bottom = full - HourglassShape.Area(1 - HourglassShape.BottomSand(progress));

        Assert.Equal((1 - progress) * full, top, 1e-4);
        Assert.Equal(progress * full, bottom, 1e-4);
    }

    [Fact]
    public void HalfwayIsNotHalfTheHeight() =>
        // The bulb narrows towards the neck, so half the sand sits lower than half the height.
        Assert.True(HourglassShape.TopSand(0.5) > 0.5);

    [Fact]
    public void TheLevelsMoveOneWayAsTimePasses()
    {
        double top = 1, bottom = 0;
        for (var p = 0.01; p <= 1; p += 0.01)
        {
            Assert.True(HourglassShape.TopSand(p) <= top + Tolerance);
            Assert.True(HourglassShape.BottomSand(p) >= bottom - Tolerance);
            (top, bottom) = (HourglassShape.TopSand(p), HourglassShape.BottomSand(p));
        }
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(1.5)]
    [InlineData(double.NaN)]
    public void AShareOutsideTheRangeIsClamped(double progress)
    {
        var top = HourglassShape.TopSand(progress);

        Assert.InRange(top, 0, 1);
        Assert.InRange(HourglassShape.BottomSand(progress), 0, 1);
    }

    [Fact]
    public void TheNeckIsNarrowAndTheRimIsWidest()
    {
        Assert.True(HourglassShape.HalfWidth(0) < 0.2);
        Assert.Equal(1, HourglassShape.HalfWidth(1), Tolerance);
    }

    [Theory]
    [InlineData(0.25)]
    [InlineData(0.5)]
    [InlineData(0.75)]
    [InlineData(1)]
    public void TheAreaIsTheIntegralOfTheHalfWidth(double d)
    {
        const int steps = 1000;
        var h = d / steps;
        var sum = HourglassShape.HalfWidth(0) + HourglassShape.HalfWidth(d);
        for (var i = 1; i < steps; i++)
        {
            sum += (i % 2 == 0 ? 2 : 4) * HourglassShape.HalfWidth(i * h);
        }

        Assert.Equal(sum * h / 3, HourglassShape.Area(d), 1e-6);
    }

    [Fact]
    public void TheHalfWidthFollowsASineFromTheNeck()
    {
        var expected = HourglassShape.Neck + (1 - HourglassShape.Neck) * Math.Sin(Math.PI / 4);

        Assert.Equal(expected, HourglassShape.HalfWidth(0.5), Tolerance);
        Assert.Equal(HourglassShape.Neck, HourglassShape.HalfWidth(0), Tolerance);
    }
}
