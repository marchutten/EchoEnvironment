namespace EchoEnvironment.Core;

/// <summary>
/// An RGB triple with no WPF in it.
///
/// Exists because Core compiles as plain net10.0 and must never reference PresentationCore, so the
/// domain cannot express a colour as System.Windows.Media.Color. This is the shape the colour ramp
/// produces; WpfMath.ToColor is the only permitted way to turn it into a brush colour.
/// </summary>
public readonly record struct Rgb(byte R, byte G, byte B);

/// <summary>
/// Distance to colour, as pure arithmetic.
///
/// Moved out of MainWindow at Phase 5 (MIGRATION_PLAN 5 names Core/ColorScale.cs for this). The
/// formulas are unchanged element-for-element; only the return type changed, because the plan's own
/// parenthetical - "pure math, belongs in Core so it's testable" - is only achievable if the WPF
/// Color construction is left behind. That construction now lives in WpfMath.ToColor.
///
/// maxDistance is a parameter rather than a constant so the ramp can be tested at any scale. The
/// value MainWindow passes is its MaxDistanceMm, which is what the ramp was always calibrated to.
/// </summary>
public static class ColorScale
{
    /// <summary>
    /// Red at zero distance, blue at max, green peaking at the midpoint. Distances outside
    /// [0, maxDistance] clamp rather than wrapping: the byte casts below would otherwise produce
    /// a valid-looking but wrong colour.
    /// </summary>
    public static Rgb For(double mm, double maxDistance)
    {
        double t = Math.Clamp(mm / maxDistance, 0, 1);
        byte r = (byte)(255 * (1 - t));
        byte b = (byte)(255 * t);
        return new Rgb(r, (byte)(200 * (1 - Math.Abs(2 * t - 1))), b);
    }
}
