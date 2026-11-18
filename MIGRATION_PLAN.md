# EchoEnvironment — Migration Plan

**Baseline:** `main` @ `f3475a1`, working tree clean, solution builds 0 errors / 0 warnings.
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

### Two gotchas that will bite if ignored

1. **`AssemblyInfo.cs` / `ThemeInfo`** is a WPF attribute and lives in the App project only. Do not copy it into Core/Hardware/Rendering.
2. **SDK globbing.** These are SDK-style projects with no `<Compile Include>` entries, so *moving a file on disk is enough* — no `.csproj` edits per file. Only `EchoEnvironment.slnx` needs new `<Project Path="…"/>` entries.
3. **TFM direction.** `net10.0-windows` may reference `net10.0`, never the reverse. This is what forces `Vec3`/`Mat3` into Core.

---

## 2. Phase sequence

Each phase = one commit with a real message. Tag `pre-refactor` before Phase 1.

---

### Phase 0 — Hygiene (30 min, zero risk)

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
- [ ] Git history has one commit per phase with a descriptive message; the six unpushed `wip`/`ok`/`q` commits squashed **before** the first push.

---

## 6. Effort summary

| Phase | Effort | Cumulative |
|---|---|---|
| 0 Hygiene | 0.5 h | 0.5 h |
| 1 Tests first | 2–3 h | 3.5 h |
| 2 Core extraction | 3–4 h | 7.5 h |
| 3 Hardware abstraction | 3 h | 10.5 h |
| 4 Coalescing stage | 2 h | 12.5 h |
| 5 Rendering extraction | 3 h | 15.5 h |
| 6 ViewModel + D1 fix | 2 h | 17.5 h |
| 7 Polish | 3–4 h | ~20 h |

**Minimum viable value is delivered at the end of Phase 2 (~7.5 h):** testable domain logic, no WPF dependency, and a build that still works. If only one thing gets done, do Phases 0–2.
