# EchoEnvironment — Migration Plan

**Baseline:** `main` @ `bc4413a` (Phase 0 complete), working tree clean, solution builds 0 errors / 0 warnings.
**Phase status:** 0 ✅ done · 1 ✅ written (⚠ not yet executed — see §8) · 2 ← **next** · 3–7 pending.
**Goal:** split the 758-line `MainWindow` into a testable core + hardware abstraction + rendering layer, **without** a rewrite and **without** a big-bang refactor.

**Governing rule:** *seam first, then move.* Every phase must leave the app runnable and the build green. No phase may mix a behavior change with a structural move.

---

## 0. Current state (verified)

```
EchoEnvironment.slnx                 <Project Path="EchoEnvironment/EchoEnvironment.csproj" />
EchoEnvironment/
├── App.xaml            StartupUri="MainWindow.xaml"
├── App.xaml.cs         namespace EchoEnvironment  (empty body)
├── AssemblyInfo.cs     [assembly: ThemeInfo(...)]   ← WPF-only assembly attribute
├── EchoEnvironment.csproj   net10.0-windows, WinExe, UseWPF, Nullable, ImplicitUsings
│                            PackageReference: System.IO.Ports 11.0.0
├── MainWindow.xaml / MainWindow.xaml.cs   758 lines, 5 responsibilities
├── OrientationFilter.cs        internal sealed  → depends on System.Windows.Media.Media3D
└── SensorFrameParser.cs        internal static  → clean, no WPF dependency
```

Repo hygiene gaps: no tests, no CI, no README, no `.editorconfig`, no `global.json`, three **empty** `apply_grid*.ps1` at root, `.gitignore` is C++/CMake-flavored (`*.o`, `CMakeFiles/`, `vcpkg_installed/`, `Testing/`).

**The blocking coupling:** `OrientationFilter` returns `Matrix3D` and uses `Vector3D` (`PresentationCore`). That single dependency is what currently makes the domain logic untestable headlessly. Removing it is the keystone of this plan.

---

## 1. Target layout

Chosen for **minimal churn**: the existing project directory is *not* moved, so git history, `.csproj.user`, and VS window state survive untouched. New projects are added as siblings.

```
EchoEnvironment.slnx
EchoEnvironment/                 → becomes the App (WPF, thin)      net10.0-windows
EchoEnvironment.Core/            NEW                                 net10.0        ← no WPF, no System.IO.Ports
EchoEnvironment.Hardware/        NEW                                 net10.0-windows
EchoEnvironment.Rendering/       NEW                                 net10.0-windows
EchoEnvironment.Tests/           NEW                                 net10.0-windows
Directory.Build.props            NEW  (shared props)
global.json                      NEW  (pin SDK — net10.0-windows is a brand-new TFM)
```

> **Optional later step (Phase 7):** relocate everything under `src/` + `tests/`. Deliberately deferred: doing it now costs a repo-wide rename diff that would obscure every real change in this migration.

### Dependency graph (acyclic, and this is the contract)

```
Tests ──→ Core, Hardware, Rendering
App   ──→ Core, Hardware, Rendering
Rendering ──→ Core
Hardware  ──→ Core
Core      ──→ (nothing)
```

Enforced rule: **`Core` must never reference a `WindowsBase`/`PresentationCore`/`PresentationFramework` type.** If a type needs `Point3D`, it belongs in `Rendering`.

### Namespace strategy

| Assembly | Namespace | Rationale |
|---|---|---|
| `EchoEnvironment.Core` | `EchoEnvironment.Core` | explicit; makes the boundary visible in code |
| `EchoEnvironment.Hardware` | `EchoEnvironment.Hardware` | |
| `EchoEnvironment.Rendering` | `EchoEnvironment.Rendering` | |
| `EchoEnvironment` (App) | `EchoEnvironment` | **unchanged** — `MainWindow.xaml`'s `x:Class="EchoEnvironment.MainWindow"` must not be edited |

Use file-scoped namespaces everywhere (`namespace EchoEnvironment.Core;`) — the existing codebase is already mixed (file-scoped in the two domain files, block-scoped in `MainWindow`/`App`). Normalize on file-scoped when a file is touched, not before.

### Gotchas that will bite if ignored

1. **`AssemblyInfo.cs` / `ThemeInfo`** is a WPF attribute. It exists at `EchoEnvironment/AssemblyInfo.cs` — project **root**, not `Properties/` (verified at `bc4413a`). Do not copy it into Core/Hardware/Rendering. Note the SDK also generates its own `EchoEnvironment.AssemblyInfo.cs` under `obj/`, which contains **no** `ThemeInfo`; the hand-written one is load-bearing.
2. **SDK globbing.** These are SDK-style projects with no `<Compile Include>` entries, so *moving a file on disk is enough* — no `.csproj` edits per file. Only `EchoEnvironment.slnx` needs new `<Project Path="…"/>` entries.
3. **TFM direction.** `net10.0-windows` may reference `net10.0`, never the reverse. This is what forces `Vec3`/`Mat3` into Core.
4. ⚠️ **`TargetFramework` now lives in `Directory.Build.props` as `net10.0-windows` — inherited by every new project.** Phase 2 needs `EchoEnvironment.Core` to be plain **`net10.0`**. The per-project csproj is evaluated *after* `Directory.Build.props`, so an explicit `<TargetFramework>net10.0</TargetFramework>` in `Core.csproj` overrides it correctly — but the override is now a **silent requirement**, and a project that forgets it will quietly compile as `net10.0-windows` while still referencing no WPF types, defeating the "Core has no WPF" invariant without any build error. See §7.4 for the recommended follow-up.

---

## 2. Phase sequence

Each phase = one commit with a real message. Tag `pre-refactor` before Phase 1.

---

### Phase 0 — Hygiene ✅ DONE (`c69881d`)

Do this first so every later diff is *only* code.

- Delete `apply_grid.ps1`, `apply_grid2.ps1`, `apply_grid3.ps1` (all empty).
- Replace `.gitignore` with a .NET-only one (`bin/`, `obj/`, `.vs/`, `*.user`, `*.suo`, `TestResults/`).
- Add `.editorconfig`. Evidence it's needed: `CreateGridModel` uses **tabs** while the rest of `MainWindow.xaml.cs` uses spaces.
- Add `global.json` pinning the SDK in use.
- Add `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
```

> `TreatWarningsAsErrors` is a deliberate choice: the build is currently warning-free, and locking that in is cheaper than recovering it later. If it fights a generated WPF file, drop it to `TreatWarningsAsErrors=false` + `<WarningLevel>`.

**Gate:** build green, app runs, serial connect works.

#### Phase 0 verification record — ✅ PASSED (`c69881d`, verified at `bc4413a`)

| Item | Status | Evidence |
|---|---|---|
| Delete `apply_grid*.ps1` | ✅ | `c69881d` diffstat shows all three at `0` lines; no `*.ps1` remains in the tree |
| Replace `.gitignore` | ✅ | now `[Bb]in/`, `[Oo]bj/`, `.vs/`, `*.user`, `*.suo`, `TestResults/`, `*.nupkg` — no C++/CMake residue |
| Add `.editorconfig` | ✅ | `root = true`, `indent_style = space`, `indent_size = 4`, `csharp_style_namespace_declarations = file_scoped` |
| Add `global.json` | ✅ | SDK `11.0.100`, `rollForward: latestFeature` — resolved without NETSDK error |
| Add `Directory.Build.props` | ✅ | `Nullable`, `ImplicitUsings`, `LangVersion=latest`, `TreatWarningsAsErrors=true` |
| Build gate | ✅ | 3 fresh builds (2 solution-wide + 1 project) — **0 errors, 0 warnings**, restore `RestoreSuccess=True`, `EchoEnvironment.exe` produced |

**`TreatWarningsAsErrors` did not bite.** Flowing into restore policy (`allWarningsAsErrors: true` in `nuget.dgspec.json`), still succeeded. Generated WPF markup (`App.g.cs`, `MainWindow.g.cs`, `MainWindow.baml`) compiled clean.

**Gate caveat:** the build tool cannot force a configuration switch, so the gate was proven in **Debug only**. No `bin/Release/` artifacts exist. Confirm a Release build manually before relying on it.

**Five findings that change later phases — see §7.** (7.1 and 7.4 are the consequential ones.)

---

### Phase 1 — Tests before any move (2–3 h) ← highest-value phase

The tests exist to prove that Phases 2–5 changed nothing. Writing them *after* the extraction is worthless.

1. Add `<InternalsVisibleTo Include="EchoEnvironment.Tests" />` to `EchoEnvironment.csproj` — this is what lets tests reach `internal` `SensorFrameParser` / `OrientationFilter` **while they are still in the App project**.
2. Create `EchoEnvironment.Tests` (xunit, `net10.0-windows`) referencing `EchoEnvironment`.
3. Write the characterization tests.

**`SensorFrameParser.TryParse` — full behavior matrix.** Note the *quirk* that must be pinned, not "fixed" silently: `16 or 25 => 16` and `64 or 73 => 64`, i.e. 25 and 73 are legal inputs meaning "16/64 zones + IMU".

| Case | Input | Expected |
|---|---|---|
| 16 zones | `"100,101,…,115"` | `distances.Length==16`, `imu==null`, `true` |
| 16+IMU | 25 values | `distances.Length==16`, `imu.Length==9` |
| 64 zones | 64 values | `distances.Length==64`, `imu==null` |
| 64+IMU | 73 values | `distances.Length==64`, `imu.Length==9` |
| separators | `,` `;` ` ` tab, mixed | all accepted |
| whitespace | leading/trailing, blank line | trimmed / `false` |
| bad token | `"100,x,102,…"` | `false`, `distances` empty |
| illegal count | 17, 24, 26, 63, 72 values | `false` |
| negative mm | `-5` | parsed (verify this is intended) |
| culture | invariant only — assert a non-invariant locale doesn't change parsing | |

**`OrientationFilter` — the correctness tests.** These are what catch defect D1/D9.

- `Reset()` then first `Update` → returns the *initialized* matrix, no correction applied.
- Degenerate guards: `accel.LengthSquared < 1` and `mag.LengthSquared < 1` → returns current matrix unchanged.
- Collinear mag/accel (`eastMeasured.LengthSquared < 1e-6`) → unchanged.
- Calibration window: exactly `CalibrationSamples` (50) samples accumulate `_gyroBiasSum`; the 50th sets `_gyroBias`. Assert the boundary at 49 vs 50.
- Stationary path: `|gyro - bias| < StationaryThresholdCounts` (60) → bias adapts by `BiasAdaptationRate`, `omega == 0`, no rotation.
- Moving path: `dt` clamped to `[0, 0.5]` — assert a `dt=10` behaves identically to `dt=0.5`.
- **Known-pose test (the important one):** feed a synthetic gravity vector for a board resting flat, tilted 30°, and inverted; assert the resulting matrix rotates sensor-frame points into the documented world frame.
- Orthonormality invariant: for any random input sequence, the three basis rows stay unit-length and mutually perpendicular within tolerance. Add this as a property test — it catches drift bugs that unit tests miss.

**Gate:** tests green against the *current* code. Any test that fails is a real defect — record it, don't paper over it.

#### Phase 1 record — tests written, gate NOT yet passed (⚠ see §8)

Files created:

| File | Contents |
|---|---|
| `EchoEnvironment.Tests/EchoEnvironment.Tests.csproj` | xunit 2.9.2, Microsoft.NET.Test.Sdk 17.12.0, xunit.runner.visualstudio 2.8.2, `net10.0-windows`, `UseWPF=true`, ProjectReference to the App |
| `EchoEnvironment.Tests/AssertMath.cs` | tolerance-based `Close`, `WithinRelative`, full 16-element `Matrix` comparison, `OrthonormalRows` |
| `EchoEnvironment.Tests/SensorFrameParserTests.cs` | 394 lines, ~40 cases: the 4 legal shapes, separators, whitespace, rejections, illegal counts, culture invariance, out-param safety |
| `EchoEnvironment.Tests/OrientationFilterTests.cs` | 760 lines, 6 hand-derived poses, known-pose matrix + `Transform()` behaviour, guards, Reset semantics, dt clamping, calibration window, stationary/moving paths, orthonormality property test |
| `EchoEnvironment.Tests/OrientationAngleMappingTests.cs` | pins the matrix-element → reported-angle mapping, i.e. defect D1 |
| `EchoEnvironment.Tests/ArchitectureInvariantTests.cs` | records the current assembly layout; the Core-has-no-WPF guard is `Fact(Skip = ...)` until Phase 2 |
| `EchoEnvironment/EchoEnvironment.csproj` | added `<InternalsVisibleTo Include="EchoEnvironment.Tests" />` |
| `EchoEnvironment.slnx` | added the test project |

**Three findings the tests produced before any of them ran** — each was found by deriving the expected value independently and comparing it to the source:

1. **The calibration window is 50 *accumulating* samples, not 50 *calls*.** The first `Update` returns through the initialization path and never accumulates, so `_gyroBias` is established on the **51st** call, not the 50th. The plan's wording ("exactly `CalibrationSamples` (50) samples … the 50th sets `_gyroBias`") is correct about the sample count and misleading about the call count. `CalibrationWindow_Is50AccumulatingSamples_Not50Calls` pins the boundary at call 51 vs 52.
2. **`Reset()` does not clear the orientation axes.** It clears `_initialized`, `_calibrationCount` and the gyro bias, but **not** `_up` / `_east` / `_north`. So `Current` keeps reporting the pre-`Reset` pose until a valid frame arrives. In `MainWindow` this happens on every reconnect (`_imuFilter.Reset()` at line 268): the viewport holds the previous orientation. `Reset_DoesNotClearTheOrientationAxes_StalePoseIsStillReported` pins it.
3. **The migration plan's own description of D1 is wrong.** §6a calls `atan2(M12, M22)` "rotation about the up axis". It is not — it is rotation of the sensor about its **own optical (−Z) axis**, i.e. image roll. Proof: a board rolled 30° about its optical axis while level reads 30 from that expression while elevation and heading stay 0; a board turned from north to east (a genuine rotation about the world up axis) reads 0. All three expressions in `SetOrientation` are individually correct and the variable names are correct — **only the two labels are swapped**, so the fix is a two-token change to the format string. `RollAboutOpticalAxis_DoesNotChangeElevation_AndIsNotRotationAboutUp` is the test that would have failed had the plan's description been implemented as written.

Also pinned as deliberate quirks rather than bugs: `16 or 25 => 16` / `64 or 73 => 64`; negative millimetres accepted; `"1 15"` silently becoming two zones (`1` and `15`) because space is a frame separator — a malformed line can pass validation with corrupted values.

**Correction to the plan's test list:** the plan asks to assert a *collinear* mag/accel case. With `int[]` counts, `eastMeasured.LengthSquared < 1e-6` can only fire when mag is **exactly** parallel or anti-parallel to accel — any real tilt produces integer counts large enough to clear the threshold. Same for `accel.LengthSquared < 1`: with integers that is an all-zeros check and nothing subtler. `SmallestPossibleNonZeroAccel_PassesTheGuard` documents this.

**Gate status: NOT PASSED.** No test has been compiled or executed. See §8.

---

### Phase 2 — Extract `EchoEnvironment.Core` (3–4 h) ← the keystone

**2a. Introduce Core's own math.** New file `Vec3.cs` + `Mat3.cs` in Core.

Required surface (mirroring exactly what `OrientationFilter` uses today, so the port is mechanical):

```csharp
// EchoEnvironment.Core
public readonly struct Vec3 {
    public double X, Y, Z;
    public double LengthSquared { get; }
    public double Length { get; }
    public void Normalize();
    public static Vec3 Cross(Vec3 a, Vec3 b);
    public static Vec3 operator +(Vec3, Vec3);
    public static Vec3 operator -(Vec3, Vec3);
    public static Vec3 operator *(Vec3, double);
    public static Vec3 operator /(Vec3, double);
}

public readonly struct Mat3 {
    // Row-major, row-vector convention: v' = v * M  (must match today's Matrix3D layout)
    public double M11, M12, M13, M21, M22, M23, M31, M32, M33;
    public static Mat3 CreateOrientation(Vec3 east, Vec3 up, Vec3 north);
}
```

**Critical:** `OrientationFilter.ToMatrix()` currently builds a `Matrix3D` with the comment *"Row vectors: v' = v * M, so row j holds where sensor axis j ends up in (east, up, -north)."* `Mat3` must preserve that exact layout, including the **negated north column**. Get this wrong and the whole 3D view silently mirrors.

**2b. Move the two files** into `EchoEnvironment.Core/`, changing only:
- `namespace EchoEnvironment;` → `namespace EchoEnvironment.Core;`
- `Vector3D` → `Vec3`, `Vector3D.CrossProduct` → `Vec3.Cross`
- `Matrix3D` → `Mat3`
- `internal` → `public` (Core is a separate assembly; `internal` would be invisible to the App). Keep `InternalsVisibleTo` for the test project, or just make them `public` — they're the public API of Core.

**2c. Add the WPF adapter** — the *only* place allowed to convert, in `EchoEnvironment.Rendering/Math/WpfMath.cs`:

```csharp
public static class WpfMath {
    public static Matrix3D ToMatrix3D(this in Mat3 m);
    public static Vector3D ToVector3D(this in Vec3 v);
    public static Vec3 ToVec3(this Vector3D v);
}
```

**2d. Add the frame model** to Core, and keep a compatibility shim so `MainWindow` compiles unchanged during the transition:

```csharp
public readonly record struct SensorFrame(ReadOnlyMemory<int> Distances, int ZoneCount,
                                          ReadOnlyMemory<int> Imu, bool HasImu, DateTimeOffset Timestamp);
```

Migrate `TryParse` to `bool TryParse(string line, out SensorFrame frame)` and **keep the old `out int[]` overload as a thin wrapper** for one phase. Delete the wrapper in Phase 5.

**2e. Fix the axis convention in one place.** Create `EchoEnvironment.Core/WorldFrame.cs` as the single documented source of truth:

```csharp
/// World frame: X = east, Y = up, -Z = north. Sensor frame → world via Mat3.
public static class WorldFrame { /* named constants + doc comment */ }
```

This is what lets Phase 6 resolve the `AddAxes` vs `CreateWorldAxes` disagreement (defect D9) instead of leaving two conventions in one file.

**Gate:** build green, tests green **unchanged** (they now reference Core), app renders identically. This is the moment to visually compare before/after — a regression here is invisible to the parser tests.

---

### Phase 3 — Extract `EchoEnvironment.Hardware` (3 h)

**3a. `LineFramer`** — replaces `MainWindow.IndexOfNewline` + `_rxBuffer.Clear()` + `StringBuilder.Remove(0, …)`, which is O(n²) per line (defect D6).

```csharp
public sealed class LineFramer {
    public void Append(string chunk);
    public bool TryReadLine(out string line);   // cursor-based, no memmove
    public void Clear();
    public int DroppedBytes { get; }            // overflow is now observable, not silent
    public int MaxBuffer { get; set; }          // default 10_000, same as today
}
```

Tests for this class are trivial and high-value: split-across-chunk lines, multiple lines in one chunk, overflow accounting, `\r\n` handling.

**3b. `ISensorStream`** — the Strategy/Adapter seam. This is the abstraction that buys offline development.

```csharp
public interface ISensorStream : IDisposable {
    event EventHandler<SensorFrame> FrameReceived;
    event EventHandler<string> StatusChanged;
    event EventHandler<Exception> Faulted;
    IReadOnlyList<PortDescriptor> ListPorts();   // for RefreshPorts()
    bool IsOpen { get; }
    void Open(string portName, int baudRate);
    void Close();
}
```

**3c. Three implementations:**

| Implementation | Replaces | Unlocks |
|---|---|---|
| `SerialSensorStream` | the `SerialPort` block in `Connect_Click` / `Port_DataReceived` | production path |
| `ReplaySensorStream` | — | replay a captured `.log` → debug the visualizer without hardware |
| `SimulatedSensorStream` | — | synthetic zone sweep + **known-pose IMU**, which is how the Phase 1 orientation tests become an *interactive* demo |

`SimulatedSensorStream` is the highest-leverage item in the whole plan: it makes the app developable with no sensor attached, and it gives the XAML previewer something to render.

**3d. While in this code, fix the two resource defects:**
- **D5:** `new SerialPort(...)` + event subscription happen *before* `port.Open()`; if `Open()` throws, the port is never disposed. Construct inside `try`, dispose on failure.
- **D7:** `ReadTimeout = 1000` is set but `ReadExisting()` ignores it. Either honor it or delete the setting.

**Gate:** app connects to real hardware and behaves identically; `ReplaySensorStream` reproduces a captured log frame-for-frame.

---

### Phase 4 — Insert the coalescing stage (2 h) — fixes D2

Today every parsed line becomes its own `Dispatcher.BeginInvoke`, each doing a full frame render. At 115200 baud the producer outruns the UI dispatcher and latency grows without bound.

```csharp
// App layer
private static readonly Channel<SensorFrame> Frames =
    Channel.CreateBounded<SensorFrame>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropWrite,   // newest frame wins
        SingleReader = true, SingleWriter = false
    });
```

- Serial callback → `Frames.Writer.TryWrite(frame)` (never blocks, drops stale frames).
- One `DispatcherTimer` at a **UI-side policy rate** (e.g. 60 Hz) → drains and renders exactly one frame per tick.

This converts frame rate from a serial-side accident into an explicit, tunable app setting — and it makes `SetOrientation`'s UI-thread affinity explicit instead of implicit.

**Gate:** connect at max baud; UI latency stays flat. Add a frame-drop counter to `StatusText` so the drop policy is visible rather than mysterious.

---

### Phase 5 — Extract `EchoEnvironment.Rendering` (3 h)

Move the ~14 static mesh factories out of `MainWindow`. Map:

| Current `MainWindow` member | New home |
|---|---|
| `CreateCubeMesh`, `CreateBoxMesh`, `CreateColoredBox`, `CreateBoardModel`, `CreateGridModel`, `CreateLineModel`, `CreateSensorMarker`, `CreateReferenceSphere` | `Rendering/MeshFactory.cs` |
| `AddLine`, `AddAxes`, `CreateAxisLabel`, `CreateWorldAxes` | `Rendering/ScenePrimitives.cs` |
| `DistanceColor` | `Core/ColorScale.cs` (pure math — belongs in Core so it's testable) |
| *(new)* frozen-brush lookup table | `Rendering/BrushCache.cs` |
| `EnsureZoneModels`, `UpdateFrame`, `UpdateHeatmap`, `AddToMap`, `RenderMapIfDue` | `Rendering/ZoneRenderer.cs` + `Core/WorldMap.cs` |
| `CreateViewTransform`, `UpdateCamera`, `RotateView`, `_yaw/_pitch/_zoom` state, all `View_*` handlers | `App/OrbitCameraController.cs` |
| `Refresh_Click`, `Connect_Click`, `Disconnect`, `Window_PreviewKeyDown` | stay in `MainWindow` as thin command handlers |

**5a. Brush cache (fixes D3).** Today `UpdateFrame` *and* `UpdateHeatmap` allocate a new `SolidColorBrush` per zone per frame — 128 allocations/frame. `DistanceColor` already buckets; reuse that bucketing as a LUT index:

```csharp
public static class BrushCache {
    private static readonly FrozenBrushByBucket = /* 256 pre-frozen SolidColorBrush */;
    public static Brush For(int mm);
}
```

Reused `Frozen` brushes are safe to share across visuals. This also removes the mutable cast `((DiffuseMaterial)model.Material).Brush = …`.

**5b. Reuse transforms.** Replace the 64 `new TranslateTransform3D` per frame with 64 transforms created once in `EnsureZoneModels` and only mutated in `UpdateFrame`.

**5c. Explicit layer order (fixes D8).** `ShowGrid_Click` casts `(Model3DGroup)SensorVisual.Content` and inserts at `Children.Count - 1`, and the constructor appends the reference sphere "last" with a comment about WPF transparency blending. Replace positional insertion with named containers:

```csharp
private Model3DGroup _backLayer;    // grid, world axes
private Model3DGroup _mapLayer;     // accumulated map buckets
private Model3DGroup _sensorLayer;  // zones, board, sensor marker, reference sphere
```

Order becomes a declared property of the design, not an accident of insertion sequence.

**5d. Resolve defect D9 here.** `AddAxes` labels **Y at −Z** (blue) and **Z at +Y** (green); `CreateWorldAxes` uses **Y up** (green) and **+Z** (blue). Both derive from `WorldFrame` now, so the two viewports agree by construction.

**Gate:** pixel-compare the rendered scene against a pre-refactor screenshot. Visual regressions here are subtle and tests won't catch them.

---

### Phase 6 — Thin the App + correctness fix (2 h)

**6a. Fix defect D1 — the pitch/roll label swap.** In `SetOrientation`, the readout prints the *roll* variable under the **Pitch** label and the *pitch* variable under the **Roll** label:

```
Pitch:  {roll,6:0.0}°      // asin(-M32)  — sensor-axis tilt from horizontal
Roll:   {pitch,6:0.0}°     // atan2(M12, M22) — rotation about the up axis
```

**Do this as its own commit, isolated from all structural work.** Procedure: physically tilt the board to a known angle on one axis, read both numbers, decide which label is wrong, fix it, and add a regression test asserting the mapping from matrix element → reported angle.

**6b. `MainViewModel`** (`INotifyPropertyChanged` + `ICommand`): status text, port list, connect/disconnect, zone count, orientation readout, keep-map toggle. `MainWindow` becomes XAML + input only. `SetOrientation` stops being a public method on a `Window`.

**6c. Composition root** in `App.xaml.cs` — constructor injection, no DI container needed at this size:

```csharp
protected override void OnStartup(StartupEventArgs e) {
    ISensorStream stream = args.Length > 0
        ? new ReplaySensorStream(args[0])
        : new SerialSensorStream();
    var filter = new OrientationFilter();
    var view = new MainViewModel(stream, filter, new ZoneRenderer(), new BrushCache());
    new MainWindow(view).Show();
}
```

`--replay capture.log` and `--sim` become the offline workflow.

**Gate:** all tests green, manual checklist against real hardware passes, `--sim` renders.

---

### Phase 7 — Optional polish

- Relocate to `src/` + `tests/` (now safe — the architecture is settled).
- GitHub Actions: `windows-latest`, `dotnet build` + `dotnet test` on push/PR.
- README: wiring, serial line format (16 / 25 / 64 / 73 values, separator set, IMU ordering = ITG3200 gyro, BMA180 accel, HMC5883L mag), keyboard/mouse controls, `--sim` / `--replay`.
- `WorldMap` dirty-bucket incremental rebuild (fixes D4: `RenderMapIfDue` currently rebuilds every bucket mesh every 150 ms; buckets are bounded at 65,341, so worst case is ~500k positions re-emitted per tick).
- WPF 3D shading: `GeometryModel3D`s built from cubes/lines/map buckets have **no `Normals`**, so they shade inconsistently under the `DirectionalLight`. Add normals or switch those models to `EmissiveMaterial` if lighting isn't wanted.

---

## 3. Sequencing rationale

```
Phase 0  hygiene          → makes every later diff contain only code
Phase 1  tests            → locks CURRENT behavior; must precede all moves
Phase 2  Core extraction  → the keystone; removes the WPF dependency from domain logic
Phase 3  ISensorStream    → unlocks offline development
Phase 4  coalescing       → fixes the throughput ceiling
Phase 5  Rendering        → only now is it safe to touch the visual tree
Phase 6  ViewModel + D1   → behavior change isolated to its own commit
Phase 7  polish
```

**Two hard rules:**

1. **Never do Phase 5 (perf/visual) before Phase 1 (tests).** The perf work touches exactly the code the tests protect, and visual regressions are the hardest kind to detect.
2. **Never combine Phase 6a (the pitch/roll fix) with a structural move.** A correctness fix buried in a 758-line refactor is unauditable and un-revertable.

---

## 4. Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| `Mat3` row/column convention diverges from `Matrix3D` → mirrored or transposed scene | **High** | High | Phase 2 gate is a visual before/after comparison, not just a green build. Add a `WpfMath` round-trip test: `Vec3 → Mat3 → Matrix3D` applied to basis vectors must match `OrientationFilter`'s current output element-for-element. |
| Tests written *after* extraction, so they encode the refactor's bugs | Medium | High | Phase 1 is deliberately before Phase 2. Non-negotiable. |
| `16 or 25 => 16` / `64 or 73 => 64` treated as a bug and "fixed" | Medium | Medium | Pin it in a test with a comment explaining it means "zones + IMU". Confirm with the firmware before changing it. |
| Shared mutable `_orientation` `MatrixTransform3D` embedded in two visual trees breaks when a group is frozen | Medium | Medium | Keep the group unfrozen; document the invariant in `OrbitCameraController`. Do not call `Freeze()` on `_points` or `_boardGroup`. |
| `TreatWarningsAsErrors` breaks the build on generated WPF markup code | Low | Low | Scope it per-project, or revert to warning-only. |
| `net10.0` Core accidentally references a WPF type | Medium | High | Add a build-time guard: an analyzer rule or a simple test asserting `typeof(OrientationFilter).Assembly.GetReferencedAssemblies()` contains no `PresentationCore`/`WindowsBase`. |
| Behavior drift in `LineFramer` (framing bugs = dropped/corrupted frames) | Medium | High | `LineFramer` gets its own test suite in Phase 3a, plus a `ReplaySensorStream` parity check against a real capture. |

---

## 5. Acceptance criteria

- [ ] `Core` compiles as `net10.0` with zero WPF references (verified by test, not by inspection).
- [ ] `MainWindow.xaml.cs` < 200 lines, containing only XAML handlers + input.
- [ ] ≥ 40 tests, all passing, covering parser edge cases, filter known-pose, orthonormality invariant, `LineFramer`.
- [ ] App runs with `--sim` and `--replay <file>` — no hardware required.
- [ ] Pitch/Roll labels verified against a physical tilt, with a regression test.
- [ ] Main view and board view use one axis convention.
- [ ] Frame rate is a declared UI setting, not a baud-rate side effect.
- [ ] CI builds and tests on every push.
- [ ] README documents the serial protocol.
- [ ] Git history has one commit per phase with a descriptive message, from `c69881d` onward.
      ~~Squash the `wip`/`ok`/`q` commits~~ — **withdrawn, see §7**: those commits are already published on `origin/main`.

---

## 6. Effort summary

| Phase | Effort | Cumulative |
|---|---|---|
| 0 Hygiene ✅ **DONE** (`c69881d`) | 0.5 h | 0.5 h |
| 1 Tests first | 2–3 h | 3.5 h |
| 2 Core extraction | 3–4 h | 7.5 h |
| 3 Hardware abstraction | 3 h | 10.5 h |
| 4 Coalescing stage | 2 h | 12.5 h |
| 5 Rendering extraction | 3 h | 15.5 h |
| 6 ViewModel + D1 fix | 2 h | 17.5 h |
| 7 Polish | 3–4 h | ~20 h |

**Minimum viable value is delivered at the end of Phase 2 (~7.5 h):** testable domain logic, no WPF dependency, and a build that still works. If only one thing gets done, do Phases 0–2.

---

## 7. Phase 0 findings — corrections to the original plan

Phase 0 was intended as zero-risk hygiene. It is done and the gate passed, but verifying it surfaced five things that materially change how later phases must be executed. Recording them here rather than silently editing the phases above. **7.1 and 7.4 are the consequential ones.**

### 7.1 The squash recommendation was wrong — and would have been destructive

I recommended squashing the `wip`/`ok`/`q` commits "before the first push." **That premise was false.** At the time of the original analysis, `main` was 6 commits *ahead* of `origin/main`, which I read as "unpushed." It has since been pushed: `origin/main` is at `f3475a1` and contains `wip`, `q`, `ok that seems better`, and the rest.

**Do not rewrite that history.** Squashing published commits requires a force-push to `origin/main` — a protected branch — which rewrites shared history for every other clone of the repo.

**Withdrawn:** the squash criterion in §5.
**Replaced with:** descriptive messages from `c69881d` forward. `c69881d` ("Phase 0: repo hygiene") and `bc4413a` ("add migration plan") already satisfy it. The pre-`c69881d` noise is permanent and cosmetic — it is not worth a force-push to a protected branch.

### 7.2 `AssemblyInfo.cs` is at the project root, not `Properties/`

The build probe looked for `EchoEnvironment/Properties/AssemblyInfo.cs` and got *File not found*, which initially suggested `ThemeInfo` had vanished. It had not: the file is at `EchoEnvironment/AssemblyInfo.cs`.

Why this matters for Phase 2: the SDK *also* auto-generates `obj/Debug/net10.0-windows/EchoEnvironment.AssemblyInfo.cs`, and that generated file contains **no** `ThemeInfo`. Only the hand-written root file supplies it. If Phase 2 moves files by directory convention and assumes a `Properties/` folder, `ThemeInfo` can be dropped — which breaks theme resource-dictionary lookup at runtime in a way the compiler will not report.

### 7.3 `TreatWarningsAsErrors` is now repo-wide — a Phase 5 hazard

It passed today (0 warnings across 3 builds, including generated WPF markup and restore). But it was added to `Directory.Build.props`, so **every project created in Phases 1–6 inherits it from its first file onward.** A new test project pulling in xunit analyzers, or a `Rendering` project emitting a WPF-3D obsolete-API warning, will fail the build immediately.

Not a reason to remove it — locking in warning-freedom is the point. But expect to scope it per-project (`<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` in one csproj) rather than repo-wide revert, and expect it to bite at Phase 5, not Phase 1.

### 7.4 ⚠️ `TargetFramework` is now inherited — this weakens the plan's core invariant

This is the significant one. `Directory.Build.props` sets `net10.0-windows` for **all** projects. The plan's central guarantee is that `EchoEnvironment.Core` compiles as plain **`net10.0`** with zero WPF references — that is what makes the domain logic testable headlessly.

`Core.csproj` *can* override it (csproj is evaluated after `Directory.Build.props`), and that works. The problem is that the override is now **implicit and silent**: a Core project that forgets the override compiles as `net10.0-windows`, references no WPF types anyway, builds green, and passes every test — while the `net10.0` guarantee is quietly gone. A green build would no longer prove the invariant.

**Recommended follow-up (small, do it at the start of Phase 2):**

1. Remove `<TargetFramework>` from `Directory.Build.props`; keep the rest (`Nullable`, `ImplicitUsings`, `LangVersion`, `TreatWarningsAsErrors`) inherited. Declare the TFM explicitly in each csproj, where it is visible and intentional.
2. Add the build-time guard already listed in the risk register as a **hard test**, not a convention — assert `typeof(OrientationFilter).Assembly.GetReferencedAssemblies()` contains neither `PresentationCore` nor `WindowsBase`. That test is what actually protects the invariant; the TFM is only a proxy for it.

Until that guard exists, §5's first acceptance criterion ("verified by test, not by inspection") is the only thing standing between Phase 2 and a silent regression.

### 7.5 Gate was proven in Debug only

The build tool exposes no configuration switch. Three builds succeeded with 0 errors / 0 warnings and produced `bin/Debug/net10.0-windows/EchoEnvironment.exe`, but **no `bin/Release/` artifacts exist**. The Phase 0 gate ("build green") is satisfied for Debug. Run a Release build manually before treating Release as validated — worth doing now, while the tree is clean, rather than discovering a Release-only failure at Phase 6.

---

## 8. ⛔ Phase 1 blocker — the test project is not loaded in the solution

**The Phase 1 gate is not met.** The tests exist and are committed (`f534d57`), but **not one has been compiled or executed.** This is recorded as a blocker, not as a completed phase.

### What is verified

| Claim | Status | Evidence |
|---|---|---|
| App project still builds after the `InternalsVisibleTo` edit | ✅ | single-project build of `EchoEnvironment`: 0 errors, 0 warnings, `bin\Debug\net10.0-windows\EchoEnvironment.dll` produced |
| The seam is real | ✅ | `obj\Debug\net10.0-windows\EchoEnvironment.AssemblyInfo.cs` line 21: `[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("EchoEnvironment.Tests")]` |
| `EchoEnvironment.slnx` on disk lists both projects | ✅ | 4 lines, both `<Project Path=…/>` entries present |
| Test project restored | ❌ | `EchoEnvironment.Tests\obj\project.assets.json` does not exist |
| Test project compiled | ❌ | `EchoEnvironment.Tests\bin\Debug\net10.0-windows\EchoEnvironment.Tests.dll` does not exist |
| Any test executed | ❌ | `total_tests: 0` — "The test source file … was not found." |

### Root cause

`get_solution_overview` reports **`total_projects: 1`** — only `EchoEnvironment` — even though the `.slnx` file on disk lists two. Visual Studio is holding a stale in-memory solution and never picked up the added project. Because the project is not loaded, it is never restored or built, so the test assembly never exists and the runner aborts before discovery (`MSB4181: The "VSTestTask" task returned false but did not log an error`).

### What was tried and failed

| Attempt | Result |
|---|---|
| Add `<Project Path="EchoEnvironment.Tests/…" />` to `EchoEnvironment.slnx` | overview still 1 project |
| Delete and recreate `EchoEnvironment.slnx` | overview still 1 project |
| Delete `.vs/EchoEnvironment.slnx/DesignTimeBuild/.dtbcache.v2` and `.vs/EchoEnvironment.slnx/v18/.futdcache.v2` | overview still 1 project |
| Rewrite `EchoEnvironment.slnx` with a comment to force a change event | overview still 1 project |
| `set_file_project_status` on the test csproj | `Project not found in solution` — the tool cannot register a project into a solution it cannot see |

**No automated path exists to fix this.** It requires a human action in the IDE.

### Required manual step (do this before anything else)

1. In Visual Studio: **File → Close Solution**, then reopen `EchoEnvironment.slnx`. If it still shows one project, right-click the solution node → **Reload Solution**.
2. Confirm **Solution Explorer shows `EchoEnvironment.Tests`**. If it does not, right-click solution → **Add → Existing Project…** and select `EchoEnvironment.Tests\EchoEnvironment.Tests.csproj`.
3. Right-click `EchoEnvironment.Tests` → **Restore**, then **Build**.
4. **Test → Run All Tests**.

Only then is Phase 1's gate evaluable.

### What to expect when it finally runs — likely first failures, in order

1. **`Microsoft.NET.Test.Sdk 17.12.0` may not support `net10.0`.** This is the single most likely failure and it is a *tooling* failure, not a code defect. If restore or discovery fails, bump `Microsoft.NET.Test.Sdk` (and keep `xunit` 2.9.2 + `xunit.runner.visualstudio` 2.8.2, which are a matched pair) to the newest available 17.x. **Do not "fix" this by changing a test.**
2. **`TreatWarningsAsErrors` is inherited by the test project** (§7.3). xunit analyzer warnings would become build errors. Mitigation, if it fires: `<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` in `EchoEnvironment.Tests.csproj` only — scoped, per §7.3, not a repo-wide revert.
3. **`UseWPF=true` in the test project is mandatory, not incidental.** `OrientationFilter` exposes `Matrix3D`/`Vector3D` in its public signature, so the tests cannot compile without the WPF reference assemblies. Removing it produces a cascade of type errors that look like test bugs.
4. **One test reports as Skipped on purpose**: `ArchitectureInvariantTests.CoreDomainTypesExposeNoWpfTypes` carries `Fact(Skip = "Phase 2: …")`. A skipped test that says *why* it is skipped is honest; a passing test that asserts nothing would be worse. **Phase 2 must un-skip it.** Do not read "1 skipped" as a green gate.
5. The culture tests assign `CultureInfo.CurrentCulture` and restore it in `finally`. `CurrentCulture` is thread-local and xunit runs test collections on separate threads, so parallel execution is safe here — but if a culture test ever fails only in a full run and passes alone, that assumption is the first thing to re-examine.

### Expected counts once it runs

Roughly **110 test cases** across 4 classes (the parser theory cases expand), plus 1 intentionally skipped. A count far below that means discovery is broken, not that tests failed.

### Consequence for the sequencing rule

§3 rule 1 says *"Never do Phase 5 before Phase 1."* The same logic now blocks **Phase 2**: without a green test run, Phase 2's gate ("tests green **unchanged**") has nothing to compare against, and the `Mat3` layout risk in §4 — rated **High likelihood / High impact** — would be guarded only by tests that have never executed. **Phase 2 must not start until the manual reload above is done and the suite is green.**

---

## 9. Phase 2 record — code complete, gate NOT verified (⛔ see §9.4)

§8's blocker was resolved by the manual solution reload: the suite ran and Phase 1's gate passed — **117 tests, 116 passed, 0 failed, 1 skipped** (`8e19f97`). Five defects surfaced on first execution, all in test code, none in production; see the `8e19f97` commit message. The most instructive: **WPF's `Matrix3D` has no `M41`/`M42`/`M43`** — the fourth row's translation elements are `OffsetX`/`OffsetY`/`OffsetZ`. `M44` does exist, which is why only three of four looked-wrong lines failed to compile.

### 9.1 What Phase 2 created

| File | Contents |
|---|---|
| `EchoEnvironment.Core/EchoEnvironment.Core.csproj` | **`net10.0`**, no `UseWPF`, no package references |
| `EchoEnvironment.Core/Vec3.cs` | Core's vector: `LengthSquared`, `Length`, `Normalized()`, `Cross`, `+ - * /` |
| `EchoEnvironment.Core/Mat3.cs` | Core's 3×3 rotation, row-major row-vector, `CreateOrientation(east, up, north)` |
| `EchoEnvironment.Core/WorldFrame.cs` | Single source of the axis convention: `East`, `Up`, `North`, `South`, `SensorLookAxis` |
| `EchoEnvironment.Core/OrientationFilter.cs` | Moved from App; `Vector3D`→`Vec3`, `Matrix3D`→`Mat3`, `internal`→`public` |
| `EchoEnvironment.Core/SensorFrameParser.cs` | Moved from App; logic byte-for-byte identical |
| `EchoEnvironment/WpfMath.cs` | The only Core↔WPF conversion point: `ToMatrix3D`, `ToVector3D`, `ToVec3` |
| `EchoEnvironment.Tests/WpfMathTests.cs` | The §4 mitigation for the High/High layout risk: round-trip, transpose detector, mirrored-scene detector |
| `EchoEnvironment.Tests/ArchitectureInvariantTests.cs` | **Un-skipped.** 7 guards, 0 skipped |
| `Directory.Build.props` | `TargetFramework` **removed** (§7.4 follow-up 1) |
| `EchoEnvironment/MainWindow.xaml.cs` | `SetOrientation(Mat3)`; converts once internally; angle readout reads `Mat3` |

`InternalsVisibleTo` was deleted from the App csproj — the tests now reference Core directly, so the seam is no longer needed.

### 9.2 Four deliberate deviations from the plan's own text

1. **`public void Normalize()` on a `readonly struct` does not compile.** §2a specifies it. In a readonly struct no member may assign an instance field. `Vec3.Normalized()` returns a new value instead. This is also safer: WPF's `Vector3D.Normalize()` mutated a struct in place.
2. **`WpfMath` is in the App project, not `EchoEnvironment.Rendering/Math/`.** That project does not exist until Phase 5. It moves with it.
3. **`WpfMath`'s namespace is `EchoEnvironment`, not `EchoEnvironment.Math`.** A namespace named `Math` shadows `System.Math` in every file that imports it, and `MainWindow` uses `Math.Abs` throughout.
4. **`Mat3` models rotation only — it has no fourth row.** `Matrix3D`'s `M14/M24/M34/OffsetX/OffsetY/OffsetZ/M44` are supplied by the adapter, not by Core. Core cannot accidentally carry a translation it was never meant to have. `Mat3.Identity` likewise does not exist in Core (nothing in production needs one); the tests declare their own.

### 9.3 Two defects found by review *before* any test ran

Both were in the new architecture guards, and both would have made a guard assert nothing:

1. **`Assert.DoesNotContain("-windows", targetFramework)` would never fire.** A `net10.0-windows` assembly reports `FrameworkName` as `.NETCoreApp,Version=v10.0; Platform=Windows, Version=...` — it does **not** contain the substring `-windows`. The test would have passed under exactly the drift it exists to catch (§7.4's silent-TFM problem). Now asserts the absence of `Platform=`.
2. **`WpfConversionIsConfinedToOneType` would have failed on `App`.** It filtered public types in namespace `EchoEnvironment` to those with WPF signatures, then required each to be `WpfMath` or `MainWindow`. `App` is also in that namespace and inherits `System.Windows.Application`, so it has WPF members. Rewritten to allow `WpfMath` and any type that *is* a UI type, and to fail on anything else.

### 9.4 ⛔ The gate is not verified — `EchoEnvironment.Core` is not loaded in the solution

**No Phase 2 test has been compiled or executed.** Same root cause as §8, same class of failure:

`EchoEnvironment.slnx` on disk lists **three** projects; the loaded solution reports **two** (`EchoEnvironment`, `EchoEnvironment.Tests`). `EchoEnvironment.Core` is absent, so both `ProjectReference`s to it fail to resolve:

```
Unable to find project information for '...\EchoEnvironment.Core\EchoEnvironment.Core.csproj'.
If you are using Visual Studio, this may be because the project is unloaded or not part of
the current solution so run a restore from the command-line.
```

Tried and failed: rewriting `.slnx` (with and without a comment), deleting `.dtbcache.v2`, `.futdcache.v2`, and all three `.vs/ProjectEvaluation/*.v10.bin` files, and `set_file_project_status` on the Core csproj (`Project not found in solution`). **There is no automated fix.**

**Required manual step:** close and reopen `EchoEnvironment.slnx` (or right-click the solution → **Reload Solution**), confirm `EchoEnvironment.Core` appears in Solution Explorer, then **Add → Existing Project…** if it does not. Restore, build, run all tests.

### 9.5 What to expect when it runs

- **Expected count: ~135 tests, 0 skipped.** Phase 1's 117 (116 + 1 now un-skipped) plus 7 new architecture guards plus 8 `WpfMathTests` cases (the theory expands to 5). A count near 117 means Core did not load. A skipped test means the un-skip did not take.
- **The un-skipped guard is the one most likely to fail first**, because it is the only test asserting something that has never been proven: that Core compiles WPF-free. If `CoreAssemblyReferencesNoWpfAssembly` fails, the cause is a `ProjectReference` leaking a WPF reference into Core — fix the reference, do not weaken the assertion.
- **`MainWindow` is still 758 lines.** Phase 2 only changed `SetOrientation`'s signature and one `using`. The thinning is Phase 6b.
- **The gate's third clause — "app renders identically" — has not been checked at all**, and cannot be checked by any test. §4 rates a `Mat3`/`Matrix3D` layout divergence High/High precisely because a mirrored scene renders plausibly. `WpfMathTests` is the automated mitigation; a visual before/after comparison is still required and is still outstanding.

### 9.6 Consequence for sequencing

**Phase 3 must not start until §9.4 is resolved and the suite is green.** Phase 3 extracts `Hardware` on top of Core; building it on an unverified Core means the High/High layout risk is guarded only by tests that have never executed.
