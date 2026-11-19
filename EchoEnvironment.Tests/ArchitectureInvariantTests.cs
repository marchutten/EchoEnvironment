using System.Reflection;
using EchoEnvironment.Core;
using Xunit;

namespace EchoEnvironment.Tests;

/// <summary>
/// Guards the architectural invariant the whole migration depends on: the domain logic lives in an
/// assembly that cannot see WPF.
///
/// PHASE 2 STATUS: the invariant is now enforceable, so the guard that Phase 1 left skipped is
/// active. MIGRATION_PLAN 8 said "Phase 2 must un-skip it. Do not read '1 skipped' as a green
/// gate." There is no skipped test in this file any more.
///
/// MIGRATION_PLAN 7.4 explains why this test, and not a TargetFramework setting, is what actually
/// protects the invariant: a Core project that accidentally compiled as net10.0-windows would
/// still reference no WPF type, still build green, and still pass every test. The TFM is a proxy.
/// This assertion is the fact.
/// </summary>
public class ArchitectureInvariantTests
{
    private const string CoreAssemblyName = "EchoEnvironment.Core";

    private static readonly string[] ForbiddenWpfAssemblies =
    [
        "PresentationCore",
        "PresentationFramework",
        "WindowsBase",
        "System.Windows.Media",
    ];

    [Fact]
    public void CoreContainsTheDomainTypes_AndTheAppDoesNot()
    {
        // The move itself. If either type drifts back into the App assembly, the invariant is gone
        // and nothing else in this file means anything.
        Assembly coreAssembly = typeof(OrientationFilter).Assembly;
        Assembly appAssembly = typeof(MainWindow).Assembly;

        Assert.NotEqual(coreAssembly, appAssembly);

        Assert.Equal(CoreAssemblyName, typeof(OrientationFilter).Assembly.GetName().Name);
        Assert.Equal(CoreAssemblyName, typeof(SensorFrameParser).Assembly.GetName().Name);
        Assert.Equal(CoreAssemblyName, typeof(Vec3).Assembly.GetName().Name);
        Assert.Equal(CoreAssemblyName, typeof(Mat3).Assembly.GetName().Name);
        Assert.Equal(CoreAssemblyName, typeof(WorldFrame).Assembly.GetName().Name);

        // The adapter is allowed to see WPF, so it must NOT be in Core.
        Assert.Equal(appAssembly, typeof(WpfMath).Assembly);
    }

    [Fact]
    public void CoreAssemblyReferencesNoWpfAssembly()
    {
        // The load-bearing assertion in the whole plan.
        string[] referenced = typeof(OrientationFilter).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        foreach (string forbidden in ForbiddenWpfAssemblies)
        {
            Assert.DoesNotContain(forbidden, referenced);
        }

        // Belt and braces: nothing whose name even looks like WPF slipped in under another alias.
        Assert.All(referenced, name =>
        {
            Assert.False(name.StartsWith("System.Windows", StringComparison.Ordinal),
                $"Core references {name}.");
            Assert.False(name.StartsWith("Presentation", StringComparison.Ordinal),
                $"Core references {name}.");
        });
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
        string? targetFramework = typeof(OrientationFilter).Assembly
            .GetCustomAttributes()
            .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
            .FirstOrDefault()?
            .FrameworkName;

        Assert.NotNull(targetFramework);

        Assert.True(targetFramework!.StartsWith(".NETCoreApp,Version=v10.0", StringComparison.Ordinal),
            $"Core must target .NET 10; got {targetFramework}.");

        Assert.True(!targetFramework.Contains("Platform=", StringComparison.Ordinal),
            $"Core must target plain net10.0, not a platform-specific TFM; got {targetFramework}.");
    }

    [Fact]
    public void CoreDomainTypesExposeNoWpfTypes()
    {
        // Public surface AND private fields: a private Vector3D field is still a WPF dependency and
        // still blocks the assembly from being WPF-free.
        foreach (Type type in new[] { typeof(OrientationFilter), typeof(SensorFrameParser), typeof(Vec3), typeof(Mat3) })
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotWpf(method.ReturnType, $"{type.Name}.{method.Name} return type");
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    AssertNotWpf(parameter.ParameterType, $"{type.Name}.{method.Name} parameter '{parameter.Name}'");
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotWpf(field.FieldType, $"{type.Name} field '{field.Name}'");
            }

            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                AssertNotWpf(property.PropertyType, $"{type.Name}.{property.Name} property type");
            }
        }
    }

    [Fact]
    public void OrientationFilterNoLongerExposesMatrix3D()
    {
        // The specific thing Phase 2a existed to achieve. Phase 1 asserted the opposite
        // (Current and Update() returned System.Windows.Media.Media3D.Matrix3D); that assertion is
        // now inverted, and the inversion is the proof the keystone landed.
        PropertyInfo current = typeof(OrientationFilter).GetProperty("Current")
            ?? throw new InvalidOperationException("OrientationFilter.Current not found.");
        MethodInfo update = typeof(OrientationFilter).GetMethod("Update")
            ?? throw new InvalidOperationException("OrientationFilter.Update not found.");

        Assert.Equal("EchoEnvironment.Core.Mat3", current.PropertyType.FullName);
        Assert.Equal("EchoEnvironment.Core.Mat3", update.ReturnType.FullName);

        Assert.False(IsWpf(current.PropertyType), "Current must not be a WPF type.");
        Assert.False(IsWpf(update.ReturnType), "Update() must not return a WPF type.");
    }

    [Fact]
    public void SensorFrameParserDependsOnNothingButTheBcl()
    {
        // True before the move and must stay true forever. The parser is pure string -> int[]
        // logic; it is the easiest thing in the codebase to move, and this test is what proves it.
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
    public void TheAppIsStillAllowedToSeeWpf()
    {
        // The invariant is one-directional. Core must be WPF-free; the App is a WPF application and
        // being WPF-free there would be a mistake. If this ever fails, something over-corrected.
        string[] referenced = typeof(MainWindow).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToArray();

        Assert.Contains("PresentationCore", referenced);
        Assert.Contains("PresentationFramework", referenced);
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

        // The adapter must exist and must be the one doing the converting.
        Assert.Contains(typeof(WpfMath), appTypes);
        Assert.True(HasWpfSignature(typeof(WpfMath)), "WpfMath is expected to expose WPF types.");
    }

    private static bool HasWpfSignature(Type type)
    {
        foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (IsWpf(method.ReturnType))
            {
                return true;
            }

            if (method.GetParameters().Any(p => IsWpf(p.ParameterType)))
            {
                return true;
            }
        }

        return false;
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
