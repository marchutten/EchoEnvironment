using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Phase 2 architecture invariants. These were skipped in Phase 1 on purpose: asserting them
/// before extraction would have been vacuous, and asserting them after extraction is the whole
/// point of the extraction.
///
/// They are deliberately written against the compiled assemblies rather than against csproj text,
/// so they survive refactoring of the project files and fail when the actual dependency graph
/// breaks.
/// </summary>
public class ArchitectureInvariantTests
{
    /// <summary>
    /// The assemblies that count as WPF. Not just WindowsBase: Matrix3D and Vector3D live in
    /// PresentationCore, Window and Application live in PresentationFramework. A guard that only
    /// looked for WindowsBase reported that WpfMath exposes no WPF types at all, which is how the
    /// first run of WpfConversionIsConfinedToOneType failed.
    /// </summary>
    private static readonly string[] WpfAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Forms",
        "Direct3D",
        "SharpDX",
    ];

    [Fact]
    public void CoreAssemblyReferencesNoWpfAssembly()
    {
        string[] referenced = typeof(OrientationFilter).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.All(WpfAssemblies, assembly =>
            Assert.DoesNotContain(assembly, referenced));
    }

    [Fact]
    public void CoreIsNotATargetFrameworkWindowsProject()
    {
        // Core must compile as plain net10.0. TargetFrameworkAttribute is emitted into the assembly,
        // so this is checkable from the compiled artifact rather than from the csproj.
        //
        // Note the trap: a net10.0-windows assembly reports FrameworkName
        // ".NETCoreApp,Version=v10.0; Platform=Windows, Version=...". It does NOT contain the
        // substring "-windows", so searching for that would let the exact drift this test exists to
        // catch slip straight through. The marker to look for is "Platform=".
        string? declared = typeof(OrientationFilter).Assembly
            .GetCustomAttributes()
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .FirstOrDefault()?
            .FrameworkName;

        Assert.NotNull(declared);

        // Bound to a non-nullable local so every later dereference is warning-free: this project
        // inherits TreatWarningsAsErrors, so a single CS8602 here would fail the build.
        string targetFramework = declared!;

        Assert.True(targetFramework.StartsWith(".NETCoreApp,Version=v10.0", StringComparison.Ordinal),
            $"Core must target .NET 10; got {targetFramework}.");

        Assert.True(!targetFramework.Contains("Platform=", StringComparison.Ordinal),
            $"Core must target plain net10.0, not a platform-specific TFM; got {targetFramework}.");
    }

    [Fact]
    public void CoreContainsNoPublicTypeOutsideItsOwnNamespace()
    {
        // Guards against Core silently re-acquiring UI types, or against the App's UI types drifting
        // into Core's namespace and being mistaken for domain code.
        //
        // IsPublic is not decoration. Every assembly carries a compiler-generated
        // <PrivateImplementationDetails> that has no namespace at all, and GetTypes() returns it.
        // The first run of this test failed on exactly that - in Core - which incidentally proves
        // Core itself is clean.
        Type[] offenders = typeof(OrientationFilter).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace != "EchoEnvironment.Core")
            .ToArray();

        Assert.Empty(offenders);
    }

    [Fact]
    public void OrientationFilterNoLongerExposesMatrix3D()
    {
        // Phase 1 asserted the opposite of this. The inversion is the proof that the keystone landed.
        Assert.All(typeof(OrientationFilter).GetMethods(BindingFlags.Public | BindingFlags.Instance),
            method =>
            {
                Assert.NotEqual(typeof(Matrix3D), method.ReturnType);
                Assert.DoesNotContain(method.GetParameters(), p => p.ParameterType == typeof(Matrix3D));
            });

        MethodInfo update = typeof(OrientationFilter).GetMethod("Update")!;
        Assert.Equal(typeof(Mat3), update.ReturnType);
    }

    [Fact]
    public void CurrentOrientationIsCoreMat3()
    {
        PropertyInfo current = typeof(OrientationFilter).GetProperty("Current")!;
        Assert.Equal(typeof(Mat3), current.PropertyType);
    }

    [Fact]
    public void WpfConversionIsConfinedToOneType()
    {
        // The adapter must stay a single, findable boundary. If a second conversion point appears,
        // "Core has no WPF" stops being a structural fact and becomes something to remember.
        //
        // Allowed to cross the boundary: WpfMath (the adapter) and anything that IS a WPF type
        // already, i.e. the UI itself. Everything else is a new hole.
        Type[] appTypes = typeof(MainWindow).Assembly.GetTypes()
            .Where(t => t.IsPublic && t.Namespace == "EchoEnvironment")
            .ToArray();

        Assert.True(appTypes.Length > 0, "no public types found in the App assembly.");

        Assert.Contains(typeof(WpfMath), appTypes);
        Assert.True(HasWpfSignature(typeof(WpfMath)), "WpfMath is expected to expose WPF types.");

        foreach (Type type in appTypes)
        {
            if (!HasWpfSignature(type))
            {
                continue;
            }

            if (type == typeof(WpfMath))
            {
                continue;
            }

            // A Window or an Application is allowed to speak WPF: it is the UI.
            if (typeof(System.Windows.Window).IsAssignableFrom(type)
                || typeof(System.Windows.Application).IsAssignableFrom(type))
            {
                continue;
            }

            Assert.Fail($"{type.FullName} crosses the Core/WPF boundary but is neither WpfMath nor a UI type.");
        }
    }

    [Fact]
    public void AppAssemblyStillReferencesWpf()
    {
        // Sanity check on the sanity check: if this ever passes, the App assembly has somehow lost
        // its WPF reference and the other guards are measuring nothing.
        string[] referenced = typeof(MainWindow).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("PresentationFramework", referenced);
        Assert.Contains("EchoEnvironment.Core", referenced);
    }

    private static bool HasWpfSignature(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        bool returnType = type.GetMethods(flags)
            .Any(m => IsWpfAssembly(m.ReturnType));

        bool parameterType = type.GetMethods(flags)
            .Any(m => m.GetParameters().Any(p => IsWpfAssembly(p.ParameterType)));

        bool propertyType = type.GetProperties(flags)
            .Any(p => IsWpfAssembly(p.PropertyType));

        return returnType || parameterType || propertyType;
    }

    private static bool IsWpfAssembly(Type type) =>
        WpfAssemblies.Contains(type.Assembly.GetName().Name);

    // ------------------------------------------------------------------
    // Phase 3 guards. Same shape as the Phase 2 guards: read the compiled
    // assembly's own ReferencedAssemblies, so the rule is enforced by the build
    // output rather than by a comment in a csproj.
    // ------------------------------------------------------------------

    /// <summary>
    /// Hardware may depend on Core and nothing else from this solution. Written as "no reference to
    /// any other EchoEnvironment assembly" rather than "references exactly Core", because the failure
    /// this exists to catch is Hardware quietly acquiring the App or (at Phase 5) Rendering — the
    /// edge the plan's dependency graph forbids and that nothing in the graph itself prevents.
    ///
    /// It also asserts the positive half, which is what makes the guard falsifiable in both
    /// directions: Hardware must reference System.IO.Ports, because it is the layer that owns the
    /// serial transport. If the transport ever creeps back into the App, this fails.
    /// </summary>
    [Fact]
    public void HardwareAssemblyReferencesNothingButCore()
    {
        Assembly hardware = typeof(EchoEnvironment.Hardware.ISensorStream).Assembly;

        string[] referenced = hardware
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("EchoEnvironment.Core", referenced);

        Assert.All(referenced, name =>
        {
            if (name.StartsWith("EchoEnvironment", StringComparison.Ordinal))
            {
                Assert.NotEqual("EchoEnvironment", name);
                Assert.NotEqual("EchoEnvironment.Tests", name);
                Assert.NotEqual("EchoEnvironment.Rendering", name);
            }
        });

        Assert.Contains("System.IO.Ports", referenced);

        // Hardware must stay testable without a UI: no WPF, and no UseWPF in its TFM.
        Assert.All(WpfAssemblies, assembly => Assert.DoesNotContain(assembly, referenced));
    }

    /// <summary>
    /// The App must no longer be able to name a serial type. This is the whole point of Phase 3, and
    /// it is the guard that makes the claim in EchoEnvironment.csproj ("the App no longer talks to
    /// SerialPort") a fact rather than a comment.
    /// </summary>
    [Fact]
    public void AppAssemblyDoesNotReferenceSystemIoPorts()
    {
        string[] referenced = typeof(MainWindow).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("System.IO.Ports", referenced);

        // And the seam it replaced must actually be there.
        Assert.Contains("EchoEnvironment.Hardware", referenced);
    }
}
