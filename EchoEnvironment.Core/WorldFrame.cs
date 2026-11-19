namespace EchoEnvironment.Core;

/// <summary>
/// The single documented source of truth for the world coordinate convention.
///
/// WORLD FRAME: X = east, Y = up, -Z = north.
///
/// Everything in this codebase that names an axis must derive from these constants. Before this
/// type existed, the convention lived only in a sentence in OrientationFilter's doc comment and in
/// the literals of its private fields, while MainWindow.AddAxes labelled Y at -Z (blue) and Z at
/// +Y (green) and CreateWorldAxes used Y up (green) and +Z (blue) — two different conventions in
/// one file (defect D9). Resolving that disagreement is Phase 5d's job; this file is what makes it
/// resolvable at all.
///
/// Note that North is -Z, not +Z. That is why Mat3.CreateOrientation negates its north column.
/// </summary>
public static class WorldFrame
{
    /// <summary>World +X.</summary>
    public static readonly Vec3 East = new(1, 0, 0);

    /// <summary>World +Y, i.e. away from the centre of the earth.</summary>
    public static readonly Vec3 Up = new(0, 1, 0);

    /// <summary>World north. This is -Z, not +Z.</summary>
    public static readonly Vec3 North = new(0, 0, -1);

    /// <summary>World south, i.e. world +Z.</summary>
    public static readonly Vec3 South = new(0, 0, 1);

    /// <summary>
    /// The direction the VL53L5CX looks, in its own sensor frame: -Z. In world coordinates it is
    /// whatever Mat3.CreateOrientation maps -Z to.
    /// </summary>
    public static readonly Vec3 SensorLookAxis = new(0, 0, -1);
}
