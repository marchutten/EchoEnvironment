# EchoEnvironment — Migration Plan

**Baseline:** `main` @ `bc4413a` (Phase 0 complete). **Current head:** `0e08043`, working tree clean, 7 commits ahead of `origin/main`, solution builds 0 errors / 0 warnings.
**Phase status:** 0 ✅ · 1 ✅ gate passed (`8e19f97`) · 2 ✅ automated gate verified (`295d5bf`), ⚠ visual check outstanding (§9.5) and ⚠ step 2d skipped (§9.7) · **3 ← next** · 4–7 pending.
**Test suite:** **130 tests, 130 passed, 0 failed, 0 skipped.**
**Goal:** split the 763-line `MainWindow` into a testable core + hardware abstraction + rendering layer, **without** a rewrite and **without** a big-bang refactor.

**Governing rule:** *seam first, then move.* Every phase must leave the app runnable and the build green. No phase may mix a behavior change with a structural move.

---

## 0. Current state (verified against the tree at `0e08043`)

```
EchoEnvironment.slnx                 3 projects: App, Core, Tests
Directory.Build.props                Nullable, ImplicitUsings, LangVersion=latest,
                                     TreatWarningsAsErrors=true   (NO TargetFramework — §7.4)
global.json                          SDK 11.0.100, rollForward=latestFeature

EchoEnvironment/                     net10.0-windows, WinExe, UseWPF
├── App.xaml            StartupUri="MainWindow.xaml"
├── App.xaml.cs         namespace EchoEnvironment  (empty body)
├── AssemblyInfo.cs     [assembly: ThemeInfo(...)]   ← WPF-only, project ROOT not Properties/
├── EchoEnvironment.csproj   ProjectReference → Core; PackageReference System.IO.Ports 11.0.0
├── MainWindow.xaml / MainWindow.xaml.cs   763 lines, 5 responsibilities
└── WpfMath.cs          the ONLY Core↔WPF conversion point (namespace EchoEnvironment)

EchoEnvironment.Core/                net10.0, no UseWPF, no package references
├── Vec3.cs             readonly struct; Normalized() not Normalize() (§9.2)
├── Mat3.cs             readonly struct; rotation only, no fourth row (§9.2)
├── WorldFrame.cs       single source of the axis convention
├── OrientationFilter.cs   public sealed; was internal in the App
└── SensorFrameParser.cs   public static; logic byte-for-byte identical to pre-Phase-2

EchoEnvironment.Tests/               net10.0-windows, UseWPF, xunit 2.9.2, Test.Sdk 17.12.0
├── AssertMath.cs, SensorFrameParserTests.cs, OrientationFilterTests.cs,
├── OrientationAngleMappingTests.cs, WpfMathTests.cs, ArchitectureInvariantTests.cs
```

**Not yet created:** `EchoEnvironment.Hardware/` (Phase 3), `EchoEnvironment.Rendering/` (Phase 5).

Repo hygiene: `.editorconfig`, `global.json`, `Directory.Build.props`, a .NET `.gitignore`, and a test suite all exist. The three empty `apply_grid*.ps1` are gone. **Still missing: CI workflow, README.** (`.github/copilot-instructions.md` exists; it is not CI.)

**The blocking coupling is resolved.** `OrientationFilter` no longer returns `Matrix3D` or uses `Vector3D`; it returns `Mat3` and uses `Vec3`, and it lives in an assembly that cannot see `PresentationCore`. That was the keystone of this plan, and it is done.

**What remains blocking:** `MainWindow` is still 763 lines with 5 responsibilities, and serial I/O is still inline in `Connect_Click` / `Port_DataReceived`.

---

## 1. Target layout

Chosen for **minimal churn**: the existing project directory is *not* moved, so git history, `.csproj.user`, and VS window state survive untouched. New projects are added as siblings.

```
EchoEnvironment.slnx
EchoEnvironment/                 → becomes the App (WPF, thin)      net10.0-windows   exists, NOT yet thin (763 lines)
EchoEnvironment.Core/            ✅ CREATED in Phase 2               net10.0        ← no WPF, no System.IO.Ports
EchoEnvironment.Hardware/        Phase 3                             net10.0-windows
EchoEnvironment.Rendering/       Phase 5                             net10.0-windows
EchoEnvironment.Tests/           ✅ CREATED in Phase 1               net10.0-windows
Directory.Build.props            ✅ CREATED in Phase 0               (shared props, NO TargetFramework)
global.json                      ✅ CREATED in Phase 0               (pin SDK — net10.0-windows is a brand-new TFM)
```

**Deviation to carry forward:** §2c placed `WpfMath.cs` in the **App** project, not in `EchoEnvironment.Rendering/Math/`, because that project does not exist until Phase 5 (§9.2 deviation 2). It moves with Phase 5. Until then, the "only place allowed to convert" rule is enforced by `WpfConversionIsConfinedToOneType`, which inspects the App assembly — so the guard must be **retargeted at Phase 5** when the adapter moves, or it will silently start checking the wrong assembly.

> **Optional later step (Phase 7):** relocate everything under `src/` + `tests/`. Deliberately deferred: doing it now costs a repo-wide rename diff that would obscure every real change in this migration.

### Dependency graph (acyclic, and this is the contract)

```
Tests ──→ Core, Hardware, Rendering
App   ──→ Core, Hardware, Rendering
Rendering ──→ Core
Hardware  ──→ Core
Core      ──→ (nothing)
```

Status: `Tests → Core` and `Tests → App` and `App → Core` exist. `Hardware` and `Rendering` do not yet exist. **The graph is currently acyclic and the contract holds**, but nothing in the graph yet prevents `Hardware` from referencing `Rendering` when Phase 3 creates it — that edge must be kept out deliberately.

Enforced rule: **`Core` must never reference a `WindowsBase`/`PresentationCore`/`PresentationFramework` type.** If a type needs `Point3D`, it belongs in `Rendering`. This is now enforced by a test, not by convention (§9.5).

### Namespace strategy

| Assembly | Namespace | Rationale |
|---|---|---|
| `EchoEnvironment.Core` | `EchoEnvironment.Core` | explicit; makes the boundary visible in code |
| `EchoEnvironment.Hardware` | `EchoEnvironment.Hardware` | |
| `EchoEnvironment.Rendering` | `EchoEnvironment.Rendering` | |
| `EchoEnvironment` (App) | `EchoEnvironment` | **unchanged** — `MainWindow.xaml`'s `x:Class="EchoEnvironment.MainWindow"` must not be edited |

Use file-scoped namespaces everywhere (`namespace EchoEnvironment.Core;`) — the existing codebase is already mixed (file-scoped in the two domain files, block-scoped in `MainWindow`/`App`). Normalize on file-scoped when a file is touched, not before.

> ⚠️ **Do not create a namespace named `EchoEnvironment.Math`** (or any namespace whose last segment is a type name used unqualified in the importing file). Phase 2 avoided `EchoEnvironment.Math` precisely because it shadows `System.Math` in every file that imports it, and `MainWindow` uses `Math.Abs` throughout. `WpfMath.cs` therefore sits in namespace `EchoEnvironment`. See §9.2 deviation 3.

### Gotchas that will bite if ignored

1. **`AssemblyInfo.cs` / `ThemeInfo`** is a WPF attribute. It exists at `EchoEnvironment/AssemblyInfo.cs` — project **root**, not `Properties/` (verified at `bc4413a`). Do not copy it into Core/Hardware/Rendering. Note the SDK also generates its own `EchoEnvironment.AssemblyInfo.cs` under `obj/`, which contains **no** `ThemeInfo`; the hand-written one is load-bearing.
2. **SDK globbing.** These are SDK-style projects with no `<Compile Include>` entries, so *moving a file on disk is enough* — no `.csproj` edits per file. Only `EchoEnvironment.slnx` needs new `<Project Path="…"/>` entries.
3. **TFM direction.** `net10.0-windows` may reference `net10.0`, never the reverse. This is what forces `Vec3`/`Mat3` into Core.
4. ~~⚠️ `TargetFramework` now lives in `Directory.Build.props` as `net10.0-windows` — inherited by every new project.~~ **✅ FIXED in Phase 2.** `TargetFramework` was **removed** from `Directory.Build.props`; each csproj declares its own, where it is visible and intentional. See §7.4 for why the inherited form was dangerous, and §9.4 item 2 for the guard that now enforces the invariant it was meant to protect. **This gotcha must be re-applied to the new projects:** `EchoEnvironment.Core` is `net10.0`; `Hardware` and `Rendering` must each state `net10.0-windows` explicitly. Do not reintroduce a shared TFM.
5. **Adding a project to `.slnx` on disk does not make Visual Studio load it.** This bit twice (§8, §9.4) and no automated tool could fix it. When a phase creates a new project, **expect to reload the solution manually** and verify by test count, not by the solution overview.

---

## 2. Phase sequence

Each phase = one commit with a real message. Tag `pre-refactor` before Phase 1.

> **Status note:** the tag was never created. `main` is 9 commits past `bc4413a` with no tag. It is still worth creating now — `git tag pre-refactor bc4413a` gives every later phase a known-good revert point, and it costs one command.

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

> ⚠️ **As written this file was wrong, and it is no longer what is on disk.** `<TargetFramework>` was removed in Phase 2 per §7.4. The snippet above is kept to show what was originally proposed; the current file has `Nullable`, `ImplicitUsings`, `LangVersion`, `TreatWarningsAsErrors` and **no** TFM.

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

**`TreatWarningsAsErrors` did not bite.** Flowing into restore policy (`allWarningsAsErrors: true` in `nuget.dgspec.json`), still succeeded. Generated WPF markup (`App.g.cs`, `MainWindow.g.cs`, `MainWindow.baml`) compiled clean. Still true through Phase 2, including the test project with xunit analyzers.

**Gate caveat:** the build tool cannot force a configuration switch, so the gate was proven in **Debug only**. No `bin/Release/` artifacts exist. Confirm a Release build manually before relying on it. **This caveat has now survived Phases 1 and 2 unaddressed — see §7.5.**

**Five findings that change later phases — see §7.** (7.1 and 7.4 are the consequential ones.)

---

### Phase 1 — Tests before any move (2–3 h) ← highest-value phase — ✅ DONE (`f534d57`, gate `8e19f97`)

The tests exist to prove that Phases 2–5 changed nothing. Writing them *after* the extraction is worthless.

1. Add `<InternalsVisibleTo Include="EchoEnvironment.Tests" />` to `EchoEnvironment.csproj` — this is what lets tests reach `internal` `SensorFrameParser` / `OrientationFilter` **while they are still in the App project**.
   > **Removed in Phase 2.** Once the types moved to Core as `public`, the seam was no longer needed and was deleted. Do not re-add it.
2. Create `EchoEnvironment.Tests` (xunit, `net10.0-windows`) referencing `EchoEnvironment`.
   > In Phase 2 this gained a second `ProjectReference`, directly to Core.
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
- Degenerate guards: `accel.LengthSquared < 1` and `mag.LengthSquared < 1` → returns current matrix unchanged. ⚠️ *Correction:* with `int[]` counts these are **all-zeros checks and nothing subtler** — see the correction below.
- Collinear mag/accel (`eastMeasured.LengthSquared < 1e-6`) → unchanged. ⚠️ *Correction:* with integer counts this only fires when mag is **exactly** parallel or anti-parallel to accel. The case as written is not reachable by any real tilt.
- Calibration window: exactly `CalibrationSamples` (50) samples accumulate `_gyroBiasSum`; the 50th sets `_gyroBias`. Assert the boundary at 49 vs 50. ⚠️ *Correction:* correct about the **sample** count, misleading about the **call** count — `_gyroBias` is established on the **51st** call. See finding 1 below; writing this test as worded here produces a test that fails for the wrong reason.
- Stationary path: `|gyro - bias| < StationaryThresholdCounts` (60) → bias adapts by `BiasAdaptationRate`, `omega == 0`, no rotation.
- Moving path: `dt` clamped to `[0, 0.5]` — assert a `dt=10` behaves identically to `dt=0.5`.
- **Known-pose test (the important one):** feed a synthetic gravity vector for a board resting flat, tilted 30°, and inverted; assert the resulting matrix rotates sensor-frame points into the documented world frame.
- Orthonormality invariant: for any random input sequence, the three basis rows stay unit-length and mutually perpendicular within tolerance. Add this as a property test — it catches drift bugs that unit tests miss.

**Gate:** tests green against the *current* code. Any test that fails is a real defect — record it, don't paper over it.

> **Refined after Phase 2 (§9.4):** "any test that fails is a real defect" is too strong. Three of Phase 2's four failures were defects **in the tests themselves**, not in the code under test. The working procedure is: before changing production code to satisfy a failure, derive the expected value independently from first principles. If the derivation disagrees with the test, the test is wrong — and fixing it means correcting its *claim*, never loosening its tolerance.

#### Phase 1 record — ✅ gate PASSED (`8e19f97`); was blocked on §8 until the manual solution reload

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
2. **`Reset()` does not clear the orientation axes.** It clears `_initialized`, `_calibrationCount` and the gyro bias, but **not** `_up` / `_east` / `_north`. So `Current` keeps reporting the pre-`Reset` pose until a valid frame arrives. In `MainWindow` this happens on every reconnect (`_imuFilter.Reset()`): the viewport holds the previous orientation. `Reset_DoesNotClearTheOrientationAxes_StalePoseIsStillReported` pins it.
3. **The migration plan's own description of D1 is wrong.** §6a calls `atan2(M12, M22)` "rotation about the up axis". It is not — it is rotation of the sensor about its **own optical (−Z) axis**, i.e. image roll. Proof: a board rolled 30° about its optical axis while level reads 30 from that expression while elevation and heading stay 0; a board turned from north to east (a genuine rotation about the world up axis) reads 0. All three expressions in `SetOrientation` are individually correct and the variable names are correct — **only the two labels are swapped**, so the fix is a two-token change to the format string. `RollAboutOpticalAxis_DoesNotChangeElevation_AndIsNotRotationAboutUp` is the test that would have failed had the plan's description been implemented as written.

Also pinned as deliberate quirks rather than bugs: `16 or 25 => 16` / `64 or 73 => 64`; negative millimetres accepted; `"1 15"` silently becoming two zones (`1` and `15`) because space is a frame separator — a malformed line can pass validation with corrupted values.

**Correction to the plan's test list:** the plan asks to assert a *collinear* mag/accel case. With `int[]` counts, `eastMeasured.LengthSquared < 1e-6` can only fire when mag is **exactly** parallel or anti-parallel to accel — any real tilt produces integer counts large enough to clear the threshold. Same for `accel.LengthSquared < 1`: with integers that is an all-zeros check and nothing subtler. `SmallestPossibleNonZeroAccel_PassesTheGuard` documents this.

**Gate status: ✅ PASSED** at `8e19f97` — **117 tests, 116 passed, 0 failed, 1 skipped** (the skip was the architecture guard, by design). The blocker that had prevented this is §8, now resolved. Five further defects surfaced on first execution, all in test code; see §9's opening and the `8e19f97` commit message.

---

### Phase 2 — Extract `EchoEnvironment.Core` (3–4 h) ← the keystone — ✅ DONE (`8d933b4`, gate verified `295d5bf`; ⚠ 2d skipped, ⚠ visual check outstanding)

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

> ⚠️ **`public void Normalize()` does not compile on a `readonly struct`** — no member may assign an instance field. Shipped as `Vec3 Normalized()` returning a new value. See §9.2 deviation 1.

**Critical:** `OrientationFilter.ToMatrix()` currently builds a `Matrix3D` with the comment *"Row vectors: v' = v * M, so row j holds where sensor axis j ends up in (east, up, -north)."* `Mat3` must preserve that exact layout, including the **negated north column**. Get this wrong and the whole 3D view silently mirrors.

> ✅ **Preserved, and now proven.** `WpfMathTests` compares Core's basis vectors against WPF's element-for-element across all five poses, with a transpose detector and a handedness detector. The layout did not diverge. What *was* wrong was a test asserting the wrong cross-product identity — §9.4 item 3.

**2b. Move the two files** into `EchoEnvironment.Core/`, changing only:
- `namespace EchoEnvironment;` → `namespace EchoEnvironment.Core;`
- `Vector3D` → `Vec3`, `Vector3D.CrossProduct` → `Vec3.Cross`
- `Matrix3D` → `Mat3`
- `internal` → `public` (Core is a separate assembly; `internal` would be invisible to the App). Keep `InternalsVisibleTo` for the test project, or just make them `public` — they're the public API of Core.

> Done exactly as specified, **except** `InternalsVisibleTo` was deleted rather than kept. Making the types `public` made the seam redundant.

**2c. Add the WPF adapter** — the *only* place allowed to convert, in `EchoEnvironment.Rendering/Math/WpfMath.cs`:

```csharp
public static class WpfMath {
    public static Matrix3D ToMatrix3D(this in Mat3 m);
    public static Vector3D ToVector3D(this in Vec3 v);
    public static Vec3 ToVec3(this Vector3D v);
}
```

> ⚠️ **Path and namespace both changed** — see §9.2 deviations 2 and 3. It is at `EchoEnvironment/WpfMath.cs` with namespace `EchoEnvironment`, because `EchoEnvironment.Rendering` does not exist until Phase 5 and a namespace called `EchoEnvironment.Math` would shadow `System.Math`.

**2d. Add the frame model** to Core, and keep a compatibility shim so `MainWindow` compiles unchanged during the transition:

```csharp
public readonly record struct SensorFrame(ReadOnlyMemory<int> Distances, int ZoneCount,
                                          ReadOnlyMemory<int> Imu, bool HasImu, DateTimeOffset Timestamp);
```

Migrate `TryParse` to `bool TryParse(string line, out SensorFrame frame)` and **keep the old `out int[]` overload as a thin wrapper** for one phase. Delete the wrapper in Phase 5.

> ⚠️ **NOT DONE in Phase 2 — see §9.7.** No `SensorFrame` type exists; `TryParse` still has only its original `out int[] distances, out int[]? imu` signature, and no wrapper was added, so there is nothing to delete in Phase 5. **Moved to Phase 3**, where `ISensorStream.FrameReceived` becomes the first real consumer that needs a single value type. Doing it there makes it driven by a caller instead of speculative.

**2e. Fix the axis convention in one place.** Create `EchoEnvironment.Core/WorldFrame.cs` as the single documented source of truth:

```csharp
/// World frame: X = east, Y = up, -Z = north. Sensor frame → world via Mat3.
public static class WorldFrame { /* named constants + doc comment */ }
```

This is what lets Phase 6 resolve the `AddAxes` vs `CreateWorldAxes` disagreement (defect D9) instead of leaving two conventions in one file.

> ✅ **DONE, and the convention stated above is now *proven*, not just asserted.** `WorldFrame.cs` exists with `East`, `Up`, `North`, `South`, `SensorLookAxis`, and a guard verifies the constants are orthonormal and right-handed. Note the guard initially asserted `East × Up == North`, which is **mathematically false** — `+x × +y = +z`, and world `+Z` is *south*. The correct invariant is `East × Up == South == −North` (§9.4 item 3). The `-Z = north` in the doc comment above was right all along; the test was wrong.

**Gate:** build green, tests green **unchanged** (they now reference Core), app renders identically. This is the moment to visually compare before/after — a regression here is invisible to the parser tests.

> **Gate outcome:** build green ✅ (0 errors / 0 warnings). Tests green ✅ **130 passed, 0 failed, 0 skipped** — and the count *changed* from 117 to 130, which is correct: Phase 2 added tests, it did not alter existing ones. App renders identically ⚠️ **NOT CHECKED.** The visual comparison this gate exists to force has not been done. See §9.5.

**Full Phase 2 record: §9.**

---

### Phase 3 — Extract `EchoEnvironment.Hardware` (3 h) ← **NEXT**

**First step, before 3a:** introduce `SensorFrame` in Core (§2d, deferred here per §9.7). `ISensorStream.FrameReceived` in 3b is the consumer that needs it, so it should be written against a real caller rather than added speculatively.

**Second step:** create the `EchoEnvironment.Hardware` project with an **explicit** `<TargetFramework>net10.0-windows</TargetFramework>` (§1 gotcha 4) and a `ProjectReference` to Core only — never to Rendering or the App (§1 dependency graph). **Expect the new project not to load in Visual Studio** and plan for a manual solution reload; this has happened in both prior phases (§8, §9.4).

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

> ⚠️ **Do not change framing semantics while writing this.** The current code's behaviour on a `\r\n` line ending, and its behaviour when the buffer overflows, are unpinned by any test. Capture the current behaviour first — the same characterization discipline that made Phase 1 worth doing.

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

> `SensorFrame` and `PortDescriptor` **do not exist yet**. Both must be introduced in this phase.

**3c. Three implementations:**

| Implementation | Replaces | Unlocks |
|---|---|---|
| `SerialSensorStream` | the `SerialPort` block in `Connect_Click` / `Port_DataReceived` | production path |
| `ReplaySensorStream` | — | replay a captured `.log` → debug the visualizer without hardware |
| `SimulatedSensorStream` | — | synthetic zone sweep + **known-pose IMU**, which is how the Phase 1 orientation tests become an *interactive* demo |

`SimulatedSensorStream` is the highest-leverage item in the whole plan: it makes the app developable with no sensor attached, and it gives the XAML previewer something to render.

> ⚠️ **`ReplaySensorStream` needs a real captured log, and the repo has none.** Capture one during the Phase 3 hardware session *before* writing the class, otherwise the §4 parity check has nothing to run against and the class ships untested.

**3d. While in this code, fix the two resource defects:**
- **D5:** `new SerialPort(...)` + event subscription happen *before* `port.Open()`; if `Open()` throws, the port is never disposed. Construct inside `try`, dispose on failure.
- **D7:** `ReadTimeout = 1000` is set but `ReadExisting()` ignores it. Either honor it or delete the setting.

> ⚠️ **§'s governing rule applies to 3d.** D5 and D7 are behavior changes. They must be **separate commits** from the structural move to `ISensorStream`, exactly as §3 rule 2 requires for D1. Bundling them into the extraction makes a hardware regression unattributable to one of the three changes.

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

> Depends on Phase 3: `Channel<SensorFrame>` needs `SensorFrame`, which does not exist yet. Phase 3's first step is what makes this compilable.

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

> `WorldFrame` already exists and its orthonormality is now test-verified, so 5d has its prerequisite. Note the labels are not merely cosmetic: `AddAxes` currently labels the **south** axis as "Z", which is the convention the corrected cross-product test now documents (§9.4 item 3).

**5e. Move `WpfMath.cs` into Rendering** (§9.2 deviation 2) and **retarget `WpfConversionIsConfinedToOneType`** at the new assembly. If the guard is not retargeted it keeps inspecting the App assembly, finds no WPF-typed public types there, and passes for the wrong reason.

**Gate:** pixel-compare the rendered scene against a pre-refactor screenshot. Visual regressions here are subtle and tests won't catch them.

> **Prerequisite not yet satisfied:** there is no pre-refactor screenshot. Take one **now**, at the current green state, and it serves both this gate and Phase 2's still-open visual check (§9.5).

---

### Phase 6 — Thin the App + correctness fix (2 h)

**6a. Fix defect D1 — the pitch/roll label swap.** In `SetOrientation`, the readout prints the *roll* variable under the **Pitch** label and the *pitch* variable under the **Roll** label:

```
Pitch:  {roll,6:0.0}°      // asin(-M32)  — sensor-axis tilt from horizontal
Roll:   {pitch,6:0.0}°     // atan2(M12, M22) — rotation about the up axis
```

**Do this as its own commit, isolated from all structural work.** Procedure: physically tilt the board to a known angle on one axis, read both numbers, decide which label is wrong, fix it, and add a regression test asserting the mapping from matrix element → reported angle.

> **Correction to the comment above, per §7 finding 3:** `atan2(M12, M22)` is **not** "rotation about the up axis" — it is rotation of the sensor about its own optical (−Z) axis, i.e. image roll. The expressions and variable names in `SetOrientation` are correct; only the two printed labels are swapped. The regression test already exists (`OrientationAngleMappingTests`); what is still missing is the **physical tilt**.

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

> ⚠️ `MainWindow` currently has no constructor taking a view model, and `App.xaml` sets `StartupUri="MainWindow.xaml"`. A composition root in `OnStartup` **conflicts with `StartupUri`** — one of the two must go. Removing `StartupUri` is the correct choice, but it is a change to `App.xaml`, which is XAML and easy to get wrong: keep it in 6c's own commit.

**Gate:** all tests green, manual checklist against real hardware passes, `--sim` renders.

---

### Phase 7 — Optional polish

- Relocate to `src/` + `tests/` (now safe — the architecture is settled).
- GitHub Actions: `windows-latest`, `dotnet build` + `dotnet test` on push/PR. **Highest-value item in this list now:** 130 tests exist and nothing runs them automatically. A guard that nobody executes is not protection.
- README: wiring, serial line format (16 / 25 / 64 / 73 values, separator set, IMU ordering = ITG3200 gyro, BMA180 accel, HMC5883L mag), keyboard/mouse controls, `--sim` / `--replay`.
- `WorldMap` dirty-bucket incremental rebuild (fixes D4: `RenderMapIfDue` currently rebuilds every bucket mesh every 150 ms; buckets are bounded at 65,341, so worst case is ~500k positions re-emitted per tick).
- WPF 3D shading: `GeometryModel3D`s built from cubes/lines/map buckets have **no `Normals`**, so they shade inconsistently under the `DirectionalLight`. Add normals or switch those models to `EmissiveMaterial` if lighting isn't wanted.

---

## 3. Sequencing rationale

```
Phase 0  hygiene          → makes every later diff contain only code      ✅ done
Phase 1  tests            → locks CURRENT behavior; must precede all moves ✅ done
Phase 2  Core extraction  → the keystone; removes the WPF dependency       ✅ done
Phase 3  ISensorStream    → unlocks offline development                    ← next
Phase 4  coalescing       → fixes the throughput ceiling
Phase 5  Rendering        → only now is it safe to touch the visual tree
Phase 6  ViewModel + D1   → behavior change isolated to its own commit
Phase 7  polish
```

**Two hard rules:**

1. **Never do Phase 5 (perf/visual) before Phase 1 (tests).** The perf work touches exactly the code the tests protect, and visual regressions are the hardest kind to detect.
2. **Never combine Phase 6a (the pitch/roll fix) with a structural move.** A correctness fix buried in a 763-line refactor is unauditable and un-revertable.

**Both rules held through Phase 2.** Rule 2 is the one Phase 3 is most likely to break, because 3d (defects D5 and D7) is a behavior change sitting inside a structural extraction. Apply rule 2 to 3d as well: **separate commits.**

---

## 4. Risk register

| Risk | Likelihood | Impact | Mitigation | Status |
|---|---|---|---|---|
| `Mat3` row/column convention diverges from `Matrix3D` → mirrored or transposed scene | **High** | High | Phase 2 gate is a visual before/after comparison, not just a green build. Add a `WpfMath` round-trip test: `Vec3 → Mat3 → Matrix3D` applied to basis vectors must match `OrientationFilter`'s current output element-for-element. | 🟡 **Half done.** The automated half exists and passes (`WpfMathTests`: element-for-element copy, transpose detector, determinant + `row1 × row2 == row3` handedness detector, basis-vector comparison across all five poses). **The visual comparison has not been done.** This is the register's only High/High risk whose mitigation is still incomplete. |
| Tests written *after* extraction, so they encode the refactor's bugs | Medium | High | Phase 1 is deliberately before Phase 2. Non-negotiable. | ✅ **Held.** Phase 2 did not start until Phase 1's gate was green. That ordering is what let §9.4's three failures be attributed confidently to test defects. |
| `16 or 25 => 16` / `64 or 73 => 64` treated as a bug and "fixed" | Medium | Medium | Pin it in a test with a comment explaining it means "zones + IMU". Confirm with the firmware before changing it. | 🟡 **Pinned in tests and in a doc comment** on `SensorFrameParser`. **Still unconfirmed with the firmware** — that half has never been done. |
| Shared mutable `_orientation` `MatrixTransform3D` embedded in two visual trees breaks when a group is frozen | Medium | Medium | Keep the group unfrozen; document the invariant in `OrbitCameraController`. Do not call `Freeze()` on `_points` or `_boardGroup`. | ⬜ Untouched. `MainWindow` still holds `_orientation` as a shared `MatrixTransform3D`; `OrbitCameraController` does not exist until Phase 5. |
| `TreatWarningsAsErrors` breaks the build on generated WPF markup code | Low | Low | Scope it per-project, or revert to warning-only. | ✅ **Did not bite** — 0 warnings across every build through Phase 2, including the test project with xunit analyzers. §7.3's prediction that it would bite at Phase 5 is still untested. |
| `net10.0` Core accidentally references a WPF type | Medium | High | Add a build-time guard: an analyzer rule or a simple test asserting `typeof(OrientationFilter).Assembly.GetReferencedAssemblies()` contains no `PresentationCore`/`WindowsBase`. | ✅ **Done, and the recommended form was insufficient.** The literal suggestion — assert absence of `PresentationCore`/`WindowsBase` — was implemented first and **passed while asserting nothing**, because `Window`/`Application` live in `PresentationFramework`. See §9.4 item 2. The shipped guard covers all three assemblies from one shared list. |
| Behavior drift in `LineFramer` (framing bugs = dropped/corrupted frames) | Medium | High | `LineFramer` gets its own test suite in Phase 3a, plus a `ReplaySensorStream` parity check against a real capture. | ⬜ Not started. Note the parity check needs a **real captured log**, which the repo does not have. Capture one during Phase 3's hardware session, before writing `ReplaySensorStream`. |
| **A test that asserts nothing is mistaken for protection** — *added after Phase 2* | **High** | High | Four such guards were written in Phase 2 and only revealed themselves by failing or by review: a `-windows` substring check that can never fire, a WPF-signature check matching only `WindowsBase`, a namespace filter that omitted `IsPublic`, and a cross-product assertion that was mathematically false. **A green suite is not evidence that the guards assert anything.** Review each guard by asking what would have to change for it to fail; if the answer is "nothing," it is decoration. | 🟡 **Ongoing discipline, not a one-time fix.** Applies to every guard added in Phases 3–6. |
| **Solution state is unreliable** — *added after §8 and §9.4, which were the same failure twice* | Medium | Medium | `get_solution_overview` reported 1 project in §8 and 2 projects in every Phase 2 run, including green ones, while the `.slnx` on disk listed 3. Neither the overview nor the `.slnx` on disk proves a project is loaded. **Evidence that a project is loaded: its assembly was produced, and the test count matches expectation.** A test count *lower* than expected means discovery silently skipped something. | ✅ **Understood and applied.** Do not re-attempt automated fixes; go straight to the manual reload. |

---

## 5. Acceptance criteria

- [x] `Core` compiles as `net10.0` with zero WPF references (verified by test, not by inspection). **Done in Phase 2.** `CoreAssemblyReferencesNoWpfAssembly` walks the reference graph; a separate guard asserts the assembly's `FrameworkName` has no `Platform=`; a third confines WPF-typed public types to `WpfMath`. All pass.
- [ ] `MainWindow.xaml.cs` < 200 lines, containing only XAML handlers + input. **Currently 763 lines** — Phase 2 added 5 rather than reducing. Phase 6b.
- [~] ≥ 40 tests, all passing, covering parser edge cases, filter known-pose, orthonormality invariant, `LineFramer`. **130 tests, 130 passing.** Parser ✅, known-pose ✅, orthonormality ✅, architecture invariants ✅ (not originally listed — added in Phase 2), `LineFramer` ❌ Phase 3.
- [ ] App runs with `--sim` and `--replay <file>` — no hardware required. Phase 3 (`ISensorStream`) + Phase 6c (composition root).
- [~] Pitch/Roll labels verified against a physical tilt, with a regression test. **Regression test exists** (`OrientationAngleMappingTests`, and it is the test that would have failed had the plan's own wrong description of D1 been implemented). **The physical tilt has not been done** — that half is inherently manual and is still the only way to settle which label is wrong in the user's terms. Phase 6a.
- [ ] Main view and board view use one axis convention. `WorldFrame` now exists as the single source of truth, but `AddAxes` and `CreateWorldAxes` have not yet been rewired to it. Phase 5d.
- [ ] Frame rate is a declared UI setting, not a baud-rate side effect. Phase 4.
- [ ] CI builds and tests on every push. Phase 7. Note `.github/copilot-instructions.md` exists but there is no workflow file.
- [ ] README documents the serial protocol. Phase 7.
- [x] Git history has one commit per phase with a descriptive message, from `c69881d` onward. **Satisfied** — `c69881d`, `bc4413a`, `f534d57`, `8e19f97`, `8d933b4`, `3daf4b7`, `295d5bf`, `0e08043`.
      ~~Squash the `wip`/`ok`/`q` commits~~ — **withdrawn, see §7.1**: those commits are already published on `origin/main`.
- [ ] **The app renders identically after every structural phase.** *Added after Phase 2.* This criterion was stated in the Phase 2 and Phase 5 gates but never listed here, so it could be left unsatisfied without any open checkbox. It is inherently manual, it is the only check that reaches the register's High/High mirrored-scene risk, and **it is currently unsatisfied for Phase 2.**

---

## 6. Effort summary

| Phase | Effort | Cumulative | Status |
|---|---|---|---|
| 0 Hygiene | 0.5 h | 0.5 h | ✅ DONE (`c69881d`) |
| 1 Tests first | 2–3 h | 3.5 h | ✅ DONE (`f534d57`, gate `8e19f97`) |
| 2 Core extraction | 3–4 h | 7.5 h | ✅ DONE (`8d933b4`, gate `295d5bf`) — ⚠ 2d deferred to 3, ⚠ visual check open |
| 3 Hardware abstraction | 3 h | 10.5 h | ← **NEXT** (starts with `SensorFrame`, §9.7) |
| 4 Coalescing stage | 2 h | 12.5 h | pending |
| 5 Rendering extraction | 3 h | 15.5 h | pending |
| 6 ViewModel + D1 fix | 2 h | 17.5 h | pending |
| 7 Polish | 3–4 h | ~20 h | pending |

**Minimum viable value is delivered at the end of Phase 2 (~7.5 h) — and Phase 2 is finished:** testable domain logic, no WPF dependency, a build that still works, and 130 passing tests. If nothing else is done, the migration has already paid for itself.

Two costs the effort table did not anticipate: the two solution-reload blockers (§8, §9.4) and the four test defects that had to be diagnosed as *test* defects rather than code defects (§9.4). Budget similar overhead for Phase 3's new project.

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

Why this mattered for Phase 2: the SDK *also* auto-generates `obj/Debug/net10.0-windows/EchoEnvironment.AssemblyInfo.cs`, and that generated file contains **no** `ThemeInfo`. Only the hand-written root file supplies it. If a phase moves files by directory convention and assumes a `Properties/` folder, `ThemeInfo` can be dropped — which breaks theme resource-dictionary lookup at runtime in a way the compiler will not report.

**Outcome:** Phase 2 left `AssemblyInfo.cs` in place and the App still builds clean, so `ThemeInfo` survived. The hazard remains live for Phase 5, which moves files again.

### 7.3 `TreatWarningsAsErrors` is now repo-wide — a Phase 5 hazard

It passed today (0 warnings across 3 builds, including generated WPF markup and restore). But it was added to `Directory.Build.props`, so **every project created in Phases 1–6 inherits it from its first file onward.** A new test project pulling in xunit analyzers, or a `Rendering` project emitting a WPF-3D obsolete-API warning, will fail the build immediately.

Not a reason to remove it — locking in warning-freedom is the point. But expect to scope it per-project (`<TreatWarningsAsErrors>false</TreatWarningsAsErrors>` in one csproj) rather than repo-wide revert, and expect it to bite at Phase 5, not Phase 1.

**Outcome so far:** it did **not** bite at Phase 1 — the test project with xunit analyzers built with 0 warnings. The Phase 5 prediction is still untested.

### 7.4 ⚠️→✅ `TargetFramework` inheritance weakened the plan's core invariant — RESOLVED in Phase 2

This was the significant one. `Directory.Build.props` set `net10.0-windows` for **all** projects. The plan's central guarantee is that `EchoEnvironment.Core` compiles as plain **`net10.0`** with zero WPF references — that is what makes the domain logic testable headlessly.

`Core.csproj` *can* override it (csproj is evaluated after `Directory.Build.props`), and that works. The problem was that the override was **implicit and silent**: a Core project that forgot the override compiles as `net10.0-windows`, references no WPF types anyway, builds green, and passes every test — while the `net10.0` guarantee is quietly gone. A green build would no longer prove the invariant.

**Both follow-ups DONE in Phase 2.**

1. `<TargetFramework>` **removed** from `Directory.Build.props`; each csproj now declares its own TFM explicitly (`net10.0` in Core, `net10.0-windows` in App and Tests). Verified by reading all three csproj files.
2. The guard exists as a **hard test**, and it is stronger than recommended here. `ArchitectureInvariantTests` now has 7 guards, 0 skipped, including `CoreAssemblyReferencesNoWpfAssembly` (walks the full reference graph, not just `GetReferencedAssemblies` of one type) and a `FrameworkName` check.

**One correction to the wording of follow-up 2:** it says to assert the absence of `PresentationCore`/`WindowsBase`. That alone is insufficient — the first version of the guard did exactly that and **passed while asserting nothing**, because `Matrix3D`/`Vector3D` are in `PresentationCore` but `Window`/`Application` are in `PresentationFramework`, and the helper only matched `WindowsBase`. The shipped guard uses a single shared `WpfAssemblies` list covering all three, feeding both the reference-graph check and the signature check so they cannot drift. See §9.4 item 2.

**§7.4's core warning is now defused but not by the TFM.** The TFM is still only a proxy. What actually protects the invariant is the test, and it now runs on every suite execution.

### 7.5 Gate was proven in Debug only — ⚠️ STILL OUTSTANDING

The build tool exposes no configuration switch. Three builds succeeded with 0 errors / 0 warnings and produced `bin/Debug/net10.0-windows/EchoEnvironment.exe`, but **no `bin/Release/` artifacts exist**. The Phase 0 gate ("build green") is satisfied for Debug.

**This has now survived Phases 1 and 2 unaddressed.** Every green result recorded in §8 and §9 is a Debug result. Run a Release build manually — the cost is one command, and the failure mode it is guarding against (a Release-only warning that `TreatWarningsAsErrors` turns into an error, or a Release-only analyzer complaint in the test project) has so far never been exercised.

---

## 8. ✅ RESOLVED — Phase 1 blocker: the test project was not loaded in the solution

**Historical record.** This section documents a blocker that no automated tool could clear. It is retained because the same failure mode recurred in Phase 2 (§9.4) and because §8's "what to expect" predictions are scored in §8's own table below — three of the four lessons here are about *how to recognize* this class of failure, not about this instance of it.

**Resolution:** the manual reload below was performed. The suite then compiled and ran: **117 tests, 116 passed, 0 failed, 1 skipped** (`8e19f97`). Phase 1's gate passed.

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

### What to expect when it finally runs — scored against what actually happened

Kept as written, with the outcome recorded, because four of the five predictions were wrong and the misses are informative:

| # | Prediction | Outcome |
|---|---|---|
| 1 | `Microsoft.NET.Test.Sdk 17.12.0` may not support the TFM — "single most likely failure" | ❌ **Did not happen.** 17.12.0 restored and discovered cleanly on `net10.0-windows`. The real first failures were in my own test code (§9.4), not tooling. |
| 2 | `TreatWarningsAsErrors` inherited by the test project would bite | ❌ **Did not happen.** No analyzer warnings. §7.3's "expect it to bite at Phase 5" is still untested. |
| 3 | `UseWPF=true` in the test project is mandatory | ✅ **Correct while Phase 1 held.** As of Phase 2 it is *no longer* mandatory for Core tests — `OrientationFilter` now exposes `Mat3`/`Vec3`. It is still required for `WpfMathTests`, which deliberately reference `Matrix3D` to prove the adapter copies it faithfully. Do not remove it. |
| 4 | The skipped guard must be un-skipped in Phase 2 | ✅ **Done.** Phase 2 has 7 guards, 0 skipped. |
| 5 | Culture tests are thread-safe because `CurrentCulture` is thread-local | ✅ Held — no culture test failed in a full run. |

**"Expected counts" was also wrong:** forecast ~110 + 1 skipped; Phase 1 actually ran **117** (116 passed, 1 skipped). The forecast was low, and a low forecast is the dangerous direction — it makes a partially-broken discovery look plausible.

### Consequence for the sequencing rule — honored

§3 rule 1 says *"Never do Phase 5 before Phase 1."* The same logic blocked **Phase 2**, and the block was respected: Phase 2 did not start until the manual reload was done and the suite was green (`8e19f97`). That ordering is the reason the §9.4 failures could be attributed confidently to test defects rather than to the extraction — there was a green baseline to diff against.

---

## 9. Phase 2 record — code complete, automated gate VERIFIED ✅ (visual check outstanding, §9.5)

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

### 9.4 The §8-class blocker RESOLVED — automated gate now verified

The manual solution reload cleared it, and the suite has now been compiled and executed.

**First run after the reload: 130 tests, 127 passed, 3 failed, 0 skipped.** All three failures were defects in the *new tests*, not in the extracted code:

1. **`CoreContainsNoPublicTypeOutsideItsOwnNamespace` failed on `<PrivateImplementationDetails>`.** `Assembly.GetTypes()` returns the compiler-generated helper type, which has no namespace at all. The filter tested namespace only and omitted `IsPublic`. Ironically the failure proved Core is clean — the offender was compiler scaffolding, not a UI type.
2. **`WpfConversionIsConfinedToOneType` reported "WpfMath is expected to expose WPF types."** `HasWpfSignature` only matched assembly name `WindowsBase`. **`Matrix3D` and `Vector3D` live in `PresentationCore`**; `Window`/`Application` in `PresentationFramework`. So the helper saw no WPF on the adapter at all — a guard that would have silently accepted any amount of WPF leakage. Fixed by a shared `WpfAssemblies` list used by both the signature helper and the reference-graph guard, so the two cannot drift apart.
3. **`WorldFrameConstantsAreOrthonormalAndRightHanded` failed with "expected -1, got 1".** The assertion `East × Up == North` was **mathematically false**: `+x × +y = +z` by definition, and world `+Z` is *south*. The frame is simply right-handed; North is the `-Z` axis. The test was corrected to `East × Up == South == −North`. This one matters more than the other two: had I "fixed" it by flipping the tolerance or deleting the assertion, the suite would have carried a falsehood as documentation of the axis convention — the exact convention §4 rates High/High.

**Second run: 130 total, 130 passed, 0 failed, 0 skipped.** Build clean: 0 errors, 0 warnings.

Two compile errors were also needed on the way: `nameof(FlatFacingNorth)` and friends in a `[InlineData]` — those are `switch` cases in `PoseMatrix`, not symbols, so `nameof` had nothing to resolve. And a `using System.Windows.Media;` where `Media3D` was required.

**The pre-run forecast was wrong in one respect:** it expected ~135 tests; the real count is **130**. The estimate double-counted the `WpfMathTests` theory expansion. The useful half of the prediction held: the count was nowhere near 117, and nothing was skipped, so Core loaded and the un-skip took. (The forecast itself no longer exists in this document — it was in the earlier §9.5, which was overwritten once the run happened. Recorded here so the miss is not silently lost.)

**Also confirmed:** in one run, `EchoEnvironment.Core` could be targeted for a build directly and succeeded with zero messages — i.e. Core genuinely compiles WPF-free. In a later run it could not be targeted by name at all. Do not rely on that route. The durable evidence is the passing `CoreAssemblyReferencesNoWpfAssembly` guard, plus the fact that the App builds clean while consuming `Mat3`/`Vec3`: Core must have compiled for that to happen.

Note also that the solution overview reported only 2 projects in **every** run, including the green ones, even though Core resolved for build and for both `ProjectReference`s. The overview is not a reliable indicator of what is actually loaded, and neither is the `.slnx` on disk. Trust the build and the test count.

### 9.5 Standing facts after verification

- **`CoreAssemblyReferencesNoWpfAssembly` passes.** The keystone invariant is now enforced by a test rather than by a convention someone must remember.
- **`OrientationFilterNoLongerExposesMatrix3D` passes**, asserting the inverse of what Phase 1 asserted.
- **`MainWindow` is 763 lines** (758 at `bc4413a`; Phase 2 added 5). Phase 2 changed only `SetOrientation`'s signature and one `using`. The thinning is Phase 6b, and §5's "< 200 lines" target is untouched.
- **The gate's third clause — "app renders identically" — is still unverified.** No test can check it. `WpfMathTests` is the automated mitigation (element-for-element copy, transpose detector, determinant + `row1 × row2 == row3` handedness detector, Core-vs-WPF basis-vector comparison across all five poses), and those all pass. **The visual before/after comparison is still outstanding and is still required.**

### 9.6 Consequence for sequencing

Phase 2's gate is green on the automated half. **Phase 3 may proceed**, with one carried-forward obligation: the visual render comparison from Phase 2 has not been done, and it is the only check that addresses the High/High mirrored-scene risk that a passing unit suite cannot reach.

### 9.7 ⚠️ Phase 2 step 2d was NOT done — `SensorFrame` does not exist

Verified against the tree: **there is no `SensorFrame.cs`, no `SensorFrame` type, and no `TryParse` overload returning one.** The only public member of `SensorFrameParser` is still:

```csharp
public static bool TryParse(string line, out int[] distances, out int[]? imu)
```

So §2d's "add the frame model … keep the old `out int[]` overload as a thin wrapper … delete the wrapper in Phase 5" was **skipped entirely** — including the wrapper, which means there is nothing to delete in Phase 5.

**Why this is acceptable rather than a defect:** Phase 2's stated purpose was to remove the WPF dependency from domain logic, and it did that. `SensorFrame` is a *convenience* model, not a seam. The parser moved byte-for-byte, its quirks are pinned, and the App compiles against it unchanged. Adding the record now would be a behaviour-neutral structural change, which is exactly what §2d wanted — but it should be done deliberately in **Phase 3**, because `ISensorStream.FrameReceived` (§3b) is the first consumer that actually needs a single value type to pass around. Doing it there means it is driven by a real caller instead of being speculative.

**Consequence:** §3b's `event EventHandler<SensorFrame>` currently has no type to bind to. Phase 3 must introduce `SensorFrame` as its first step, not assume it exists.

### 9.8 Possible stray artifact: an empty `EchoEnvironment/Math/` directory — UNCONFIRMED

One directory listing reported `EchoEnvironment\Math\` as existing and **empty** — residue from the first attempt at §2c, before §9.2 deviation 3 established that a namespace called `Math` shadows `System.Math` in every file importing it (and `MainWindow` uses `Math.Abs` throughout). A second, direct probe of the same path reported **not found**.

Git does not track empty directories, so it is invisible to `git status` and appears in no diff, and file-listing tools disagree about it. **Treat this as unresolved, not as a known defect.** If it is there, delete it; if it is not, nothing needs doing. `WpfMath.cs` definitely lives at the `EchoEnvironment/` project root with namespace `EchoEnvironment`, which is the part that matters.

### 9.9 Open obligations carried into Phase 3

1. **Visual before/after render comparison for Phase 2** (§9.5). Take a screenshot now and compare against the pre-Phase-2 app. Also unblocks Phase 5's pixel-compare gate.
2. **Release build has never been run** (§7.5).
3. **`pre-refactor` tag was never created** (§2). `git tag pre-refactor bc4413a`.
4. **Firmware confirmation of the 25/73-value quirk** (§4) has never been done.
5. **Physical tilt to settle the Pitch/Roll labels** (§5, Phase 6a). The regression test exists; the human verification does not.
