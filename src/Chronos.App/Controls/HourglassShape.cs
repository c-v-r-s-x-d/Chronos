namespace Chronos.App.Controls;

/// <summary>One bulb in unit terms; d is the distance from the neck, levels are solved by area.</summary>
public static class HourglassShape
{
    /// <summary>Half-width at the neck, as a share of the widest.</summary>
    public const double Neck = 0.08;

    public static double HalfWidth(double d) => Neck + (1 - Neck) * Math.Sin(Math.PI / 2 * Unit(d));

    /// <summary>Area between the neck and d, per unit of half-width.</summary>
    public static double Area(double d)
    {
        d = Unit(d);

        return Neck * d + (1 - Neck) * (2 / Math.PI) * (1 - Math.Cos(Math.PI / 2 * d));
    }

    /// <summary>Height of the sand resting on the neck of the upper bulb.</summary>
    public static double TopSand(double progress) =>
        Solve(Area, (1 - Unit(progress)) * Area(1));

    /// <summary>Height of the pile at the bottom of the lower bulb.</summary>
    public static double BottomSand(double progress) =>
        Solve(h => Area(1) - Area(1 - h), Unit(progress) * Area(1));

    private static double Unit(double value) => double.IsNaN(value) ? 0 : Math.Clamp(value, 0, 1);

    // Both areas grow with the height, so bisection is enough.
    private static double Solve(Func<double, double> area, double target)
    {
        double low = 0, high = 1;
        for (var i = 0; i < 50; i++)
        {
            var mid = (low + high) / 2;
            if (area(mid) < target) low = mid; else high = mid;
        }

        return (low + high) / 2;
    }
}
