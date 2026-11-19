using System.Reflection;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Guards the architectural invariant the whole migration depends on: the domain logic must end up
/// in an assembly that cannot see WPF.
///
/// Today OrientationFilter and SensorFrameParser live in the WPF App assembly, so the invariant
/// cannot be enforced yet. What this file does is (a) record the current state precisely, so Phase
/// 2's change is provable, and (b) hold the one half of the invariant that is already true.
/// </summary>
public class ArchitectureInvariantTests
{
    private static readonly string[] ForbiddenWpfAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Media",
    ];

    [Fact]
    public void SensorFrameParserDependsOnNothingButTheBcl()
    {
        // True today and must stay true forever. The parser is pure string -> int[] logic; it is the
        // easiest thing in the codebase to move to Core, and this test is what proves it.
        var referenced = typeof(SensorFrameParser).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name)
            .ToHashSet();

        // Sanity: the App assembly does reference WPF today, so a broken reflection call would show
        // up as this assertion passing when it should not.
        Assert.Contains("PresentationCore", referenced);

        // The parser's own signature must be WPF-free.
        foreach (MethodInfo method in typeof(SensorFrameParser).GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            AssertNotWpf(method.ReturnType, $"{method.Name} return type");

            foreach (ParameterInfo parameter in method.GetParameters())
            {
                AssertNotWpf(parameter.ParameterType, $"{method.Name} parameter '{parameter.Name}'");
            }
        }
    }

    [Fact]
    public void DomainTypesCurrentlyLiveInTheWpfAppAssembly_Phase2MovesThem()
    {
        // Records the starting point. After Phase 2 this test is updated to assert the opposite:
        // that these types live in EchoEnvironment.Core and that Core references no WPF assembly.
        Assembly appAssembly = typeof(MainWindow).Assembly;

        Assert.Equal(appAssembly, typeof(SensorFrameParser).Assembly);
        Assert.Equal(appAssembly, typeof(OrientationFilter).Assembly);
    }

    private const string Matrix3DFullName = "System.Windows.Media.Media3D.Matrix3D";

    [Fact]
    public void OrientationFilterPublicSurfaceIsCurrentlyAllWpfTypes()
    {
        // FINDING, not approval: every type in OrientationFilter's public signature is a WPF type,
        // which is exactly why Phase 2a (Vec3/Mat3) has to exist before the class can move.
        PropertyInfo current = typeof(OrientationFilter).GetProperty("Current")
            ?? throw new InvalidOperationException("OrientationFilter.Current not found.");
        MethodInfo update = typeof(OrientationFilter).GetMethod("Update")
            ?? throw new InvalidOperationException("OrientationFilter.Update not found.");

        Assert.True(IsWpf(current.PropertyType),
            $"expected Current to be a WPF type today, got {current.PropertyType.FullName}");
        Assert.True(IsWpf(update.ReturnType),
            $"expected Update() to return a WPF type today, got {update.ReturnType.FullName}");

        // The exact Phase 2a target: these are the two signatures Vec3/Mat3 must replace.
        Assert.Equal(Matrix3DFullName, current.PropertyType.FullName);
        Assert.Equal(Matrix3DFullName, update.ReturnType.FullName);
    }

    /// <summary>
    /// The real guard for the plan's core invariant. It is skipped today because it would fail —
    /// OrientationFilter has not moved yet. Phase 2 must un-skip it. A skipped test is honest; a
    /// passing test that asserts nothing would be worse.
    /// </summary>
    [Fact(Skip = "Phase 2: activate once OrientationFilter moves to EchoEnvironment.Core with Vec3/Mat3.")]
    public void CoreDomainTypesExposeNoWpfTypes()
    {
        foreach (Type type in new[] { typeof(OrientationFilter), typeof(SensorFrameParser) })
        {
            Assembly assembly = type.Assembly;

            // Exact match, not a substring: "EchoEnvironment.Core" CONTAINS "EchoEnvironment", so a
            // containment check would pass for the wrong reason.
            Assert.Equal("EchoEnvironment.Core", assembly.GetName().Name);

            foreach (string forbidden in ForbiddenWpfAssemblies)
            {
                Assert.DoesNotContain(forbidden, assembly.GetReferencedAssemblies().Select(a => a.Name));
            }

            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotWpf(method.ReturnType, $"{type.Name}.{method.Name} return type");
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AssertNotWpf(parameter.ParameterType, $"{type.Name}.{method.Name} parameter '{parameter.Name}'");
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                // Private fields count: a private Vector3D field is still a WPF dependency and
                // still blocks the move.
                AssertNotWpf(field.FieldType, $"{type.Name} field '{field.Name}'");
            }
        }
    }

    [Fact]
    public void TestProjectCanReachInternalDomainTypes()
    {
        // The Phase 1 seam itself. If InternalsVisibleTo is ever removed, every test in this
        // project stops compiling — this test documents why the attribute exists.
        Assert.NotNull(typeof(SensorFrameParser));
        Assert.NotNull(typeof(OrientationFilter));
    }

    private static void AssertNotWpf(Type type, string what)
    {
        Type? current = type;
        while (current is not null)
        {
            if (IsWpf(current))
            {
                Assert.Fail($"{what} is a WPF type: {current.FullName}");
            }

            current = current.DeclaringType;
        }

        if (type.IsGenericType)
        {
            foreach (Type argument in type.GetGenericArguments())
            {
                AssertNotWpf(argument, $"{what} generic argument");
            }
        }

        if (type.IsArray && type.GetElementType() is Type element)
        {
            AssertNotWpf(element, $"{what} array element");
        }
    }

    private static bool IsWpf(Type type)
    {
        string? assemblyName = type.Assembly.GetName().Name;
        if (assemblyName is null)
        {
            return false;
        }

        return ForbiddenWpfAssemblies.Contains(assemblyName)
            || assemblyName.StartsWith("System.Windows", StringComparison.Ordinal)
            || assemblyName.StartsWith("Presentation", StringComparison.Ordinal);
    }
}
