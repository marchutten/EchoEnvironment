using System;
using System.Linq;
using System.Reflection;
using System.Windows.Media.Media3D;
using EchoEnvironment.Core;
using Xunit;
using EchoEnvironment.Rendering;

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

    /// <summary>
    /// The adapter must stay a single, findable boundary. If a second conversion point appears,
    /// "Core has no WPF" stops being a structural fact and becomes something to remember.
    ///
    /// RETARGETED at Phase 5 — and the plan's reason for retargeting it is wrong. MIGRATION_PLAN 5e
    /// predicts that leaving this guard alone makes it "keep inspecting the App assembly, find no
    /// WPF-typed public types there, and pass for the wrong reason." It does not. The previous body
    /// asserted Contains(typeof(WpfMath), appTypes), and WpfMath left the App assembly in this commit,
    /// so the untouched guard fails loudly rather than silently. The retarget is still required, for a
    /// different reason: the rule the guard was meant to state was never stated by it.
    ///
    /// The old rule — "only WpfMath and UI types may expose WPF types" — cannot survive Phase 5,
    /// because Rendering's entire job is exposing WPF types. MeshFactory.CreateCubeMesh returns
    /// MeshGeometry3D; that is the design working, not a hole. What has to stay confined is the
    /// conversion itself: a public signature that names Core and WPF in the same breath. That is the
    /// narrower rule, it is still falsifiable, and it is the one this guard now enforces.
    /// </summary>
    [Fact]
    public void WpfConversionIsConfinedToOneType()
    {
        // The adapter now lives in EchoEnvironment.Rendering, so the guard inspects that assembly.
        Assert.Equal("EchoEnvironment.Rendering", typeof(WpfMath).Assembly.GetName().Name);
        Assert.True(HasWpfSignature(typeof(WpfMath)), "WpfMath is expected to expose WPF types.");
        Assert.True(HasCoreSignature(typeof(WpfMath)), "WpfMath is expected to expose Core types; it is the conversion point.");

        // App and Rendering together, because Phase 5 split the UI across both. Core is deliberately
        // not scanned: Core speaking Core is not a conversion.
        Type[] boundaryTypes = typeof(MainWindow).Assembly.GetTypes()
            .Concat(typeof(WpfMath).Assembly.GetTypes())
            .Where(t => t.IsPublic && t.Namespace is not null
                && t.Namespace.StartsWith("EchoEnvironment", StringComparison.Ordinal))
            .ToArray();

        Assert.True(boundaryTypes.Length > 0, "no public types found in the App or Rendering assemblies.");

        foreach (Type type in boundaryTypes)
        {
            // A type that speaks only WPF is a rendering type or the UI itself. A type that speaks
            // only Core is domain code. Neither one converts anything.
            if (!HasWpfSignature(type) || !HasCoreSignature(type))
            {
                continue;
            }

            // The adapter is the permitted conversion point, so it is exempt by name. Without this
            // clause the guard fails on the very type it exists to protect: WpfMath speaks Core and
            // WPF in the same signature, which is its job.
            if (type == typeof(WpfMath))
            {
                continue;
            }

            // A Window or an Application is allowed to speak both: it is the UI, and the UI is where
            // the two finally meet.
            if (typeof(System.Windows.Window).IsAssignableFrom(type)
                || typeof(System.Windows.Application).IsAssignableFrom(type))
            {
                continue;
            }

            Assert.Fail($"{type.FullName} converts Core to WPF but is neither WpfMath nor a UI type.");
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

    /// <summary>
    /// True when a type's public surface names a type from EchoEnvironment.Core. Read together with
    /// HasWpfSignature it states the Phase 5 rule: a type may speak WPF (Rendering does, everywhere),
    /// and a type may speak Core (Core does), but only WpfMath may speak both in the same signature.
    /// </summary>
    private static bool HasCoreSignature(Type type)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        bool returnType = type.GetMethods(flags)
            .Any(m => IsCoreAssembly(m.ReturnType));

        bool parameterType = type.GetMethods(flags)
            .Any(m => m.GetParameters().Any(p => IsCoreAssembly(p.ParameterType)));

        bool propertyType = type.GetProperties(flags)
            .Any(p => IsCoreAssembly(p.PropertyType));

        return returnType || parameterType || propertyType;
    }

    private static bool IsCoreAssembly(Type type) =>
        type.Assembly.GetName().Name == "EchoEnvironment.Core";

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

    // ------------------------------------------------------------------
    // Phase 5 guards. EchoEnvironment.Rendering now exists, which adds an edge
    // the plan's dependency graph forbids and nothing in the graph itself
    // prevents: Rendering reaching back into the App.
    // HardwareAssemblyReferencesNothingButCore already names Rendering in its ban
    // list — it was written in Phase 3 for exactly this moment — so these are the
    // guards that exist because the assembly is now real rather than predicted.
    // ------------------------------------------------------------------

    /// <summary>
    /// Rendering may see Core and WPF, and no other assembly in this solution.
    ///
    /// The positive half is what makes this falsifiable rather than a shape check: Rendering must
    /// reference PresentationCore, because producing WPF visuals is the entire reason it exists. If
    /// someone re-declares it as plain net10.0, every factory in it stops compiling and this guard is
    /// the message that explains why.
    /// </summary>
    [Fact]
    public void RenderingAssemblyReferencesCoreAndNoOtherSolutionAssembly()
    {
        Assembly rendering = typeof(WpfMath).Assembly;
        Assert.Equal("EchoEnvironment.Rendering", rendering.GetName().Name);

        string[] referenced = rendering
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("EchoEnvironment.Core", referenced);
        Assert.Contains("PresentationCore", referenced);

        Assert.All(referenced, name =>
        {
            if (name.StartsWith("EchoEnvironment", StringComparison.Ordinal))
            {
                Assert.NotEqual("EchoEnvironment", name);
                Assert.NotEqual("EchoEnvironment.Tests", name);
                Assert.NotEqual("EchoEnvironment.Hardware", name);
            }
        });
    }

    /// <summary>
    /// Core must not be able to name Rendering. The dependency is one-directional by design:
    /// Rendering turns Core values into WPF visuals, so a Core type reaching into Rendering would
    /// drag PresentationCore into the assembly whose whole purpose is staying free of it.
    /// CoreAssemblyReferencesNoWpfAssembly catches that consequence; this catches the cause.
    /// </summary>
    [Fact]
    public void CoreAssemblyDoesNotReferenceRendering()
    {
        string[] referenced = typeof(OrientationFilter).Assembly
            .GetReferencedAssemblies()
            .Select(name => name.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain("EchoEnvironment.Rendering", referenced);
    }

    /// <summary>
    /// The colour ramp moved to Core at Phase 5 so it could be tested without a UI. That only holds
    /// while the ramp's own signature is WPF-free: the moment ColorScale.For returns
    /// System.Windows.Media.Color the ramp is untestable headlessly again and the reason it was moved
    /// is gone. MIGRATION_PLAN 5 asks for this member in Core without noticing that its old return
    /// type is a PresentationCore type, so the split — arithmetic in Core, Color construction in
    /// WpfMath — is what this guard pins.
    /// </summary>
    [Fact]
    public void ColorScaleRampIsInCore_AndIsWpfFree()
    {
        Assert.Equal("EchoEnvironment.Core", typeof(ColorScale).Assembly.GetName().Name);

        MethodInfo ramp = typeof(ColorScale).GetMethod("For")!;
        Assert.Equal("EchoEnvironment.Core", ramp.ReturnType.Assembly.GetName().Name);

        Assert.All(ramp.GetParameters(), parameter =>
            Assert.False(IsWpfAssembly(parameter.ParameterType),
                $"ColorScale.For parameter '{parameter.Name}' is {parameter.ParameterType.FullName}; the ramp must stay testable without a UI."));
    }
}
