using System;
using System.IO;
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
        const BindingFlags constructorFlags = BindingFlags.Public | BindingFlags.Instance;

        bool returnType = type.GetMethods(flags)
            .Any(m => IsWpfAssembly(m.ReturnType));

        bool parameterType = type.GetMethods(flags)
            .Any(m => m.GetParameters().Any(p => IsWpfAssembly(p.ParameterType)));

        bool propertyType = type.GetProperties(flags)
            .Any(p => IsWpfAssembly(p.PropertyType));

        // Constructors are public surface, and until Phase 5 commit 2 this guard could not see them.
        // The gap was found by accident: ZoneRenderer's public methods name neither Core nor WPF,
        // because the WPF groups it fills arrive through its constructor. A guard that inspects only
        // methods would have reported that a type taking three WPF types to be constructed exposes no
        // WPF types at all.
        bool constructorType = type.GetConstructors(constructorFlags)
            .Any(c => c.GetParameters().Any(p => IsWpfAssembly(p.ParameterType)));

        return returnType || parameterType || propertyType || constructorType;
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
        const BindingFlags constructorFlags = BindingFlags.Public | BindingFlags.Instance;

        bool returnType = type.GetMethods(flags)
            .Any(m => IsCoreAssembly(m.ReturnType));

        bool parameterType = type.GetMethods(flags)
            .Any(m => m.GetParameters().Any(p => IsCoreAssembly(p.ParameterType)));

        bool propertyType = type.GetProperties(flags)
            .Any(p => IsCoreAssembly(p.PropertyType));

        // Same reason as in HasWpfSignature: a constructor is part of what a type exposes.
        bool constructorType = type.GetConstructors(constructorFlags)
            .Any(c => c.GetParameters().Any(p => IsCoreAssembly(p.ParameterType)));

        return returnType || parameterType || propertyType || constructorType;
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

    // ------------------------------------------------------------------
    // Phase 5 commit 2 guards. The stateful members moved here, and the design
    // decision worth protecting is the one that was made to satisfy a rule rather
    // than to work around it: ZoneRenderer owns a Core WorldMap but does not name
    // one in its public surface.
    // ------------------------------------------------------------------

    /// <summary>
    /// ZoneRenderer holds a WorldMap (Core) in a private field. If it ever exposed one publicly - a
    /// Map property, an AddReading returning a bucket - its public surface would name a Core type and a
    /// WPF type in the same breath, which is exactly what WpfConversionIsConfinedToOneType calls a
    /// conversion point, and that guard exempts only WpfMath and UI types.
    ///
    /// The alternative was to widen the exemption to cover ZoneRenderer. This guard is the record that
    /// the exemption was not taken: the invariant survived by changing the design (the map stays
    /// private; MainWindow calls ClearMap() and never sees a WorldMap) instead of by relaxing the rule.
    /// Without this test, the next person to need the map would add a public property and the confined
    /// guard would fail with a message that reads as if the rule were the problem.
    /// </summary>
    [Fact]
    public void ZoneRendererDoesNotExposeCoreTypes()
    {
        Assert.Equal("EchoEnvironment.Rendering", typeof(ZoneRenderer).Assembly.GetName().Name);

        // The positive half: it does speak WPF, so the negative half below is measuring something
        // rather than passing on an empty surface.
        Assert.True(HasWpfSignature(typeof(ZoneRenderer)),
            "ZoneRenderer is expected to expose WPF types; it builds the zone visuals.");

        Assert.False(HasCoreSignature(typeof(ZoneRenderer)),
            "ZoneRenderer names an EchoEnvironment.Core type in its public surface. Keep the WorldMap "
            + "private and expose intent (ClearMap) instead: a public Core type here would be a second "
            + "Core-to-WPF conversion point, and WpfConversionIsConfinedToOneType exempts only WpfMath "
            + "and UI types.");
    }

    /// <summary>
    /// WorldMap is Core, so its public surface must be WPF-free or the map arithmetic stops being
    /// testable headlessly - which is the only reason it was extracted to Core rather than left in the
    /// window. Same shape as ColorScaleRampIsInCore_AndIsWpfFree.
    /// </summary>
    [Fact]
    public void WorldMapIsInCore_AndIsWpfFree()
    {
        Assert.Equal("EchoEnvironment.Core", typeof(WorldMap).Assembly.GetName().Name);

        Assert.All(typeof(WorldMap).GetMethods(BindingFlags.Public | BindingFlags.Instance), method =>
        {
            Assert.False(IsWpfAssembly(method.ReturnType),
                $"WorldMap.{method.Name} returns {method.ReturnType.FullName}; the map must stay testable without a UI.");
            Assert.All(method.GetParameters(), parameter =>
                Assert.False(IsWpfAssembly(parameter.ParameterType),
                    $"WorldMap.{method.Name} parameter '{parameter.Name}' is {parameter.ParameterType.FullName}; the map must stay testable without a UI."));
        });
    }

    /// <summary>
    /// The state that moved out of the window must stay out. MIGRATION_PLAN 5 names the camera state as
    /// `_yaw/_pitch/_zoom`, but the fields MainWindow actually held were `_camYaw`, `_camPitch`,
    /// `_camDistance`, `_viewRotation`, `_lastMouse` and `_dragging` - six, not three - and the zone/map
    /// state was five more. All eleven are asserted absent by name.
    ///
    /// Written as a name check rather than a count, because a count would pass while a field crept back
    /// under a new name and the behaviour moved with it. Names are what a reader looks for when deciding
    /// where a behaviour lives.
    /// </summary>
    [Fact]
    public void MainWindowNoLongerOwnsCameraOrZoneState()
    {
        string[] movedOut =
        [
            "_camYaw", "_camPitch", "_camDistance", "_viewRotation", "_lastMouse", "_dragging",
            "_map", "_mapGroup", "_mapModels", "_lastMapRender", "_zoneModels",
        ];

        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        foreach (string name in movedOut)
        {
            FieldInfo? field = typeof(MainWindow).GetField(name, flags);
            Assert.True(field is null,
                $"MainWindow.{name} is back. It moved to OrbitCameraController or ZoneRenderer at Phase 5 commit 2; "
                + "restoring it re-couples view state to the window.");
        }

        // And the replacements must actually be there, so the test above cannot pass by deleting both
        // the state and the thing that holds it.
        Assert.NotNull(typeof(MainWindow).GetField("_orbit", flags));
        Assert.NotNull(typeof(MainWindow).GetField("_zones", flags));
    }

    /// <summary>
    /// Phase 5 commit 4 (defect D8, MIGRATION_PLAN 5c). The window used to own a flat Model3DGroup whose
    /// meaning depended on the order things were appended to it, and ShowGrid_Click positioned the grid
    /// with Children.Insert(root.Children.Count - 1, _gridModel) on that same collection. Named layers
    /// replaced that, and this guard is what stops the flat group coming back.
    ///
    /// Written as a name check, in the shape of MainWindowNoLongerOwnsCameraOrZoneState, because a count
    /// would pass while a flat group crept back under a new name and the positional arithmetic moved with
    /// it. Names are what a reader looks for when deciding where a behaviour lives.
    /// </summary>
    [Fact]
    public void MainWindowNoLongerOwnsTheFlatSceneGraph()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        // "_points" was the flat group; "_mapGroup" was the local that used to be a field before it.
        foreach (string name in new[] { "_points", "_mapGroup", "_root" })
        {
            FieldInfo? field = typeof(MainWindow).GetField(name, flags);
            Assert.True(field is null,
                $"MainWindow.{name} is back. The sensor viewport's layers moved to SceneLayers at Phase 5 commit 4; "
                + "restoring a flat group re-opens defect D8, because layer order becomes an insertion sequence again.");
        }

        Assert.NotNull(typeof(MainWindow).GetField("_layers", flags));

        // And the positional rule itself must be gone from the window's source, not merely discouraged by
        // a comment: an Insert whose index is computed from a Children.Count is the signature of D8.
        //
        // THE GUARD'S OWN BUG, AND WHY IT WAS NOT PAPERED OVER. The first version asserted
        // DoesNotContain over the whole file and failed - because ShowGrid_Click now contains the old code
        // quoted verbatim in a comment explaining what was replaced. That is a real hazard in any
        // source-text guard: a comment that documents a removed defect trips the guard that exists to keep
        // it removed. Two ways out. Weakening the guard to ignore comments would also ignore the code the
        // comment describes, which is the case the guard exists for. So the assertion is scoped to the one
        // method it is about, and the method body is extracted by brace matching - the same technique
        // FrameCoalescerTests uses for the same reason. The comment stays, and the code is measured.
        string appDirectory = Path.Combine(FindRepositoryRoot(), "EchoEnvironment");
        string source = StripLineComments(File.ReadAllText(Path.Combine(appDirectory, "MainWindow.xaml.cs")));

        string showGridHandler = ExtractMethodBody(source, "private void ShowGrid_Click");
        Assert.DoesNotContain("Children.Insert(", showGridHandler, StringComparison.Ordinal);
        Assert.DoesNotContain("root.Children.Count", showGridHandler, StringComparison.Ordinal);

        // Reaching into the viewport's content is the other half of the defect: the window used to cast
        // SensorVisual.Content back to a Model3DGroup in order to reach the flat group at all.
        Assert.DoesNotContain("(Model3DGroup)SensorVisual.Content", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the body of a method by brace matching, so an assertion can be made about one method instead
    /// of about a whole file. Copied from FrameCoalescerTests for the same reason FindRepositoryRoot is:
    /// a guard should not depend on another test class's private helper.
    /// </summary>
    private static string ExtractMethodBody(string source, string signatureStart)
    {
        int start = source.IndexOf(signatureStart, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method '{signatureStart}' not found in the App source.");

        int open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{signatureStart}'.");

        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{')
            {
                depth++;
            }
            else if (source[i] == '}' && --depth == 0)
            {
                return source.Substring(start, i - start + 1);
            }
        }

        Assert.Fail($"unbalanced braces after '{signatureStart}'.");
        return string.Empty;
    }

    /// <summary>
    /// Removes // line comments from source text, so a source-text guard measures code and not prose.
    ///
    /// WHY THIS IS NEEDED AT ALL. The first version of MainWindowNoLongerOwnsTheFlatSceneGraph asserted
    /// that the positional insertion was gone from MainWindow.xaml.cs, and it FAILED - because
    /// ShowGrid_Click now quotes the code it replaced, verbatim, in a comment explaining what was fixed.
    /// That is a general hazard in any guard that greps its own source: the comment that documents a
    /// removed defect trips the guard that exists to keep it removed.
    ///
    /// Two ways out, and only one is honest. Deleting the comment would lose the explanation, which is the
    /// most valuable part of a defect fix. Making the guard ignore the whole file would also ignore the
    /// code the comment describes - the exact case the guard exists to catch. So the guard strips prose and
    /// measures code: the comment stays, and the assertion is about what the compiler sees.
    ///
    /// THE LIMITATION, stated rather than hidden: this strips from "//" to end of line without parsing, so
    /// a "//" inside a string literal would be mistaken for the start of a comment. MainWindow.xaml.cs
    /// contains no such literal. If one is ever added, this helper over-strips and the guard weakens
    /// silently - which is why it is a helper in a test file and not a general-purpose utility.
    /// </summary>
    private static string StripLineComments(string source)
    {
        var builder = new System.Text.StringBuilder();
        foreach (string line in source.Split('\n'))
        {
            int comment = line.IndexOf("//", StringComparison.Ordinal);
            builder.AppendLine(comment >= 0 ? line[..comment] : line);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The one place the order of the sensor viewport's layers is decided. SceneLayers' constructor appends
    /// exactly four layers to its root; a fifth _root.Children.Add is a new layer whose position somebody
    /// chose, which is defect D8 returning in a new guise.
    /// </summary>
    [Fact]
    public void SceneLayersIsTheOnlyPlaceThatOrdersTheLayers()
    {
        string renderingDirectory = Path.Combine(FindRepositoryRoot(), "EchoEnvironment.Rendering");
        string layers = File.ReadAllText(Path.Combine(renderingDirectory, "SceneLayers.cs"));

        Assert.Equal(4, CountOccurrences(layers, "_root.Children.Add("));

        // And the viewport is handed the layers rather than a group the window assembled itself.
        string appSource = File.ReadAllText(Path.Combine(
            Path.Combine(FindRepositoryRoot(), "EchoEnvironment"), "MainWindow.xaml.cs"));
        Assert.Contains("SensorVisual.Content = _layers.Root;", appSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// THE BOARD VIEWPORT IS A SECOND SCENE TREE, and this guard records that rather than pretending it
    /// does not exist. SceneLayers orders the sensor viewport. The board viewport still has its own
    /// Model3DGroup built in MainWindow, with CreateWorldAxes in it and the board model plus the labelled
    /// gizmo inside _boardGroup.
    ///
    /// That is deliberate and it is a correction to the plan: MIGRATION_PLAN 5c lists "grid, world axes"
    /// as back-layer content, which conflates the two viewports. The world axes are not in the sensor
    /// viewport at all, so putting them in SceneLayers' back layer would move them into a tree they have
    /// never been in, and defect D9 - the two viewports disagreeing about which axis is which - is about
    /// the difference between those trees. 5d settles D9; flattening the board viewport into SceneLayers
    /// first would destroy the thing 5d has to compare.
    ///
    /// Asserted as a fact so that the next reader does not "tidy" the board viewport into the layers and
    /// silently pre-empt 5d.
    /// </summary>
    [Fact]
    public void BoardViewportStillHasItsOwnSceneTree_AndKeepsTheWorldAxesOutOfTheSensorLayers()
    {
        string appSource = File.ReadAllText(Path.Combine(
            Path.Combine(FindRepositoryRoot(), "EchoEnvironment"), "MainWindow.xaml.cs"));

        Assert.Contains("var boardRoot = new Model3DGroup();", appSource, StringComparison.Ordinal);
        Assert.Contains("BoardVisual.Content = boardRoot;", appSource, StringComparison.Ordinal);
        Assert.Contains("boardRoot.Children.Add(ScenePrimitives.CreateWorldAxes());", appSource, StringComparison.Ordinal);

        // The world axes must not have crept into the sensor viewport's back layer.
        string renderingSource = File.ReadAllText(Path.Combine(
            Path.Combine(FindRepositoryRoot(), "EchoEnvironment.Rendering"), "SceneLayers.cs"));
        Assert.DoesNotContain("CreateWorldAxes", renderingSource, StringComparison.Ordinal);
        Assert.DoesNotContain("AddAxes", renderingSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Defect D9 (MIGRATION_PLAN 5d): the axis letters and their colours must be decided in exactly one
    /// file, and that file must derive the directions from Core's WorldFrame rather than restate them.
    ///
    /// D9 was not only "the two gizmos disagree". It was that each gizmo spelled its own arms and colours by
    /// hand, so nothing in the codebase stated what blue means. Changing a colour or moving an arm in one
    /// gizmo would not touch the other, and no compile error would notice. This guard removes the ability to
    /// do that: ScenePrimitives may not name a colour at all, and both gizmos must loop over the same list.
    ///
    /// WHY THE COUNT IS 2 AND NOT "AT LEAST 1". One gizmo looping over AxisConvention and one hand-writing
    /// its arms is exactly how the two drifted apart. Asserting both loops exist is what stops "one
    /// convention" from degrading back into two conventions that merely happen to agree today.
    ///
    /// WHY ScenePrimitives MAY NOT NAME A COLOUR. The colours are not decoration here: they are the mapping a
    /// reader uses to tell which arm is which. A colour literal in ScenePrimitives is a second copy of that
    /// mapping, and a second copy is what D9 was. AxisConvention may name colours; ScenePrimitives may only
    /// ask which colour belongs to which letter.
    ///
    /// WHY THE APP MAY NOT CALL CreateAxisLabel. An axis letter placed by the window is an axis letter
    /// outside the convention - the same class of defect, moved one layer up. The window asks for a gizmo; it
    /// does not assemble one.
    ///
    /// THE COMMENT TRAP, AGAIN (Finding 20). ScenePrimitives' own doc comment quotes the pre-5d literals
    /// verbatim - Colors.Red, Colors.DeepSkyBlue, Color.FromRgb(120, 40, 40) - because a fix that does not
    /// explain the defect it fixes is worth reading twice. A guard grepping the raw file would trip over that
    /// explanation. StripLineComments strips from "//" to end of line, and a "///" doc-comment line begins
    /// with "//", so doc comments are stripped too and the assertion measures code. Same stated limitation as
    /// everywhere else this helper is used: a "//" inside a string literal would be over-stripped.
    /// </summary>
    [Fact]
    public void AxisConventionIsTheOnlyPlaceThatNamesAnAxisColour()
    {
        string renderingDirectory = Path.Combine(FindRepositoryRoot(), "EchoEnvironment.Rendering");

        string primitives = StripLineComments(File.ReadAllText(
            Path.Combine(renderingDirectory, "ScenePrimitives.cs")));

        foreach (string literal in new[] { "Colors.Red", "Colors.DeepSkyBlue", "Colors.LimeGreen", "Color.FromRgb" })
        {
            Assert.DoesNotContain(literal, primitives, StringComparison.Ordinal);
        }

        Assert.Equal(2, CountOccurrences(primitives, "foreach (AxisLabel axis in AxisConvention.WorldAxes)"));

        // AxisConvention must read Core's WorldFrame rather than copy its numbers: one use per axis, and no
        // hand-built vector that could quietly become a second source of the directions.
        string convention = StripLineComments(File.ReadAllText(
            Path.Combine(renderingDirectory, "AxisConvention.cs")));
        Assert.Equal(3, CountOccurrences(convention, "WorldFrame."));
        Assert.DoesNotContain("new Vector3D(", convention, StringComparison.Ordinal);

        // And the window asks for a gizmo instead of building one.
        string appSource = StripLineComments(File.ReadAllText(Path.Combine(
            Path.Combine(FindRepositoryRoot(), "EchoEnvironment"), "MainWindow.xaml.cs")));
        Assert.Contains("ScenePrimitives.AddAxes(_boardGroup, 0.5, 0.01);", appSource, StringComparison.Ordinal);
        Assert.Contains("boardRoot.Children.Add(ScenePrimitives.CreateWorldAxes());", appSource, StringComparison.Ordinal);
        Assert.DoesNotContain("CreateAxisLabel(", appSource, StringComparison.Ordinal);
    }

    /// <summary>
    /// Walks up from this test assembly to the repository root, the same anchor FrameCoalescerTests uses.
    ///
    /// Duplicated rather than shared, and deliberately so. FrameCoalescerTests is a test class, not a
    /// utility, and making an architecture guard depend on another test class's private helper couples two
    /// unrelated guards to one file's internals. The duplication is three lines and a comment; the coupling
    /// would be a rename away from a failure that reads as if the architecture broke.
    /// </summary>
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(Path.GetDirectoryName(typeof(ArchitectureInvariantTests).Assembly.Location)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EchoEnvironment.Tests.csproj")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        string? repoRoot = directory!.Parent?.FullName;
        Assert.NotNull(repoRoot);

        return repoRoot!;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
