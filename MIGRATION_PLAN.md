# EchoEnvironment — Migration Plan

**Baseline:** `pre-refactor` @ `f3475a1` (tagged, "Code state before refactor" — the last commit before Phase 0). **Current head:** the Phase 4 commit `f6a75a5`, **16 commits ahead** of `origin/main`, **working tree clean** — Phase 4 is committed. Solution builds 0 errors / 0 warnings across **four** projects **in Debug**. ⚠️ **Release has not been verified since Phase 2** (§7.5 closed it for three projects; §10 and §11 both carry it open for the fourth and for Phase 4's changes).
**Phase status:** 0 ✅ · 1 ✅ gate passed (`8e19f97`) · 2 ✅ **gate fully closed** — automated (`295d5bf`) and visual (§9.5) · ✅ step 2d now DONE, in Phase 3 (§9.7 closed by §10.2) · 3 ✅ code complete, gate PARTIALLY closed (§10) · **4 ✅ code complete, gate PARTIALLY closed (§11)** · 5–7 pending.
**Test suite:** **193 tests, 193 passed, 0 failed, 0 skipped** (130 before Phase 3, 181 before Phase 4).
**Goal:** split the 755-line `MainWindow` into a testable core + hardware abstraction + rendering layer, **without** a rewrite and **without** a big-bang refactor.

**Governing rule:** *seam first, then move.* Every phase must leave the app runnable and the build green. No phase may mix a behavior change with a structural move.

---

## 0. Current state (verified against the tree at `2c81aa2`)

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

Repo hygiene: `.editorconfig`, `global.json`, `Directory.Build.props`, a .NET `.gitignore`, a test suite, and a `pre-refactor` tag all exist. The three empty `apply_grid*.ps1` are gone. **Still missing: CI workflow, README.** (`.github/copilot-instructions.md` exists; it is not CI.)

**The blocking coupling is resolved.** `OrientationFilter` no longer returns `Matrix3D` or uses `Vector3D`; it returns `Mat3` and uses `Vec3`, and it lives in an assembly that cannot see `PresentationCore`. That was the keystone of this plan, and it is done.

**Phase 2 is closed on all three gate clauses** — build, tests, and the app was run after the extraction and renders and behaves correctly (§9.5, §9.6). Nothing is carried forward as an open obligation into Phase 3 except the deferred `SensorFrame` work (§9.7), which Phase 3 does anyway.

**What remains blocking:** `MainWindow` is still 763 lines with 5 responsibilities, and serial I/O is still inline in `Connect_Click` / `Port_DataReceived`.

> ⚠️ **This section describes the tree at `2c81aa2` and is now three phases out of date.** It is kept as the original reading rather than rewritten. Against the current tree: `EchoEnvironment.Hardware` **exists** (Phase 3, §10), `EchoEnvironment.Core` also contains `SensorFrame.cs` (§10.2) and `FrameCoalescer.cs` (Phase 4, §11), `MainWindow.xaml.cs` is **827 lines** (§11.1), the App no longer references `System.IO.Ports` (§10.1), and the suite is 193 tests. Use §10 and §11 for current state.

---

## 1. Target layout

Chosen for **minimal churn**: the existing project directory is *not* moved, so git history, `.csproj.user`, and VS window state survive untouched. New projects are added as siblings.

```
EchoEnvironment.slnx
EchoEnvironment/                 → becomes the App (WPF, thin)      net10.0-windows   exists, NOT yet thin (763 lines)
EchoEnvironment.Core/            ✅ CREATED in Phase 2               net10.0        ← no WPF, no System.IO.Ports
EchoEnvironment.Hardware/        ✅ CREATED in Phase 3 (§10)                net10.0-windows   ← owns System.IO.Ports; no UseWPF
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

Status after Phase 3: `Tests → Core`, `Tests → Hardware`, `Tests → App`, `App → Core`, `App → Hardware` and `Hardware → Core` all exist. `Rendering` still does not. **The graph is acyclic and the contract holds, and it is now enforced by a test rather than by care:** `HardwareAssemblyReferencesNothingButCore` fails if Hardware acquires a reference to the App or to Rendering, *and* fails if Hardware stops referencing `System.IO.Ports` (§10.6). The edge this section warned about — nothing in the graph preventing `Hardware → Rendering` — is now closed by that guard rather than left to vigilance.

Enforced rule: **`Core` must never reference a `WindowsBase`/`PresentationCore`/`PresentationFramework` type.** If a type needs `Point3D`, it belongs in `Rendering`. This is now enforced by a test, not by convention (§9.5).

> **Phase 4 tested this rule from the other side.** `FrameCoalescer` was placed in **Core** (§11.3 deviation 1) precisely because Core is the assembly that can be tested headlessly. It uses `System.Threading.Channels` and `System.Threading` only, so `CoreAssemblyReferencesNoWpfAssembly` still passes with the new type in place — the guard held without being loosened, which is the evidence that matters.

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
6. **`.slnx` project entries are not self-documenting about load state.** `38813e1 "Add plan"` added both `EchoEnvironment.Core` and `EchoEnvironment.Tests` to `EchoEnvironment.slnx` and hung `MIGRATION_PLAN.md` in a `/Solution/` folder. For the whole of Phases 1–2 the `.slnx` on disk listed 3 projects while the solution overview reported 1 or 2. **§9.10 shows the disagreement is a stale-solution symptom, not a permanent property:** after a reopen, the overview reported 3 and all three projects built individually. Still verify by assembly + test count, and still expect the disagreement when Phase 3 adds a fourth project.
7. **`replace_file_content` is a full overwrite, not a merge.** *Added after Phase 4, when this destroyed `MIGRATION_PLAN.md` outright.* A placeholder written with it replaced all 911 lines. It was recovered from `git show <commit> -- <path>`, which cost two extra steps and produced no diff to review. For partial edits use `replace_file_lines` with `start_line` + `old_lines` from a fresh read. **A plan document that has been maintained across four phases is the worst possible target for a full overwrite** — it has no cheap reconstruction path other than git.

---

## 2. Phase sequence

Each phase = one commit with a real message. Tag `pre-refactor` before Phase 1.

> **Status note — corrected: the tag DOES exist.** `pre-refactor` → `f3475a1` ("Code state before refactor"), the last commit before Phase 0. An earlier pass of this plan claimed no tag had been created and recommended `git tag pre-refactor bc4413a`; that command now fails with *tag already exists*. The existing tag is the **better** baseline anyway — `bc4413a` is "add migration plan", which already contains the plan itself, whereas `f3475a1` is the genuine pre-refactor code state. Use `f3475a1` as the revert point for every later phase.

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

**Gate caveat (originally Debug-only) — now closed.** The build tool available during Phase 0 could not force a configuration switch, so this gate was originally proven in **Debug only**. It was re-run in Release after Phase 2: 0 errors / 0 warnings, Release artifacts present for all three projects, and the full 130-test suite green in Release. See §7.5.

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

> **Confirmed again in Phase 4 (§11.4):** three of Phase 4's twelve new tests failed on first execution and **all three were test defects**, not defects in the coalescer. The procedure above is what prevented the implementation from being "fixed" to satisfy a wrong assertion.

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

### Phase 2 — Extract `EchoEnvironment.Core` (3–4 h) ← the keystone — ✅ DONE, **gate fully closed** (`8d933b4`, automated `295d5bf`, visual verified §9.5; ⚠ step 2d deferred to Phase 3)

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

> **Gate outcome:** **all three clauses now green.** Build ✅ (0 errors / 0 warnings). Tests ✅ **130 passed, 0 failed, 0 skipped** — and the count *changed* from 117 to 130, which is correct: Phase 2 added tests, it did not alter existing ones. App renders identically ✅ **verified by running the app after the extraction** — it renders and behaves correctly (§9.5). The gate is closed.

**Full Phase 2 record: §9.**

---

### Phase 3 — Extract `EchoEnvironment.Hardware` (3 h) ✅ CODE COMPLETE — gate partially closed, see §10

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

### Phase 4 — Insert the coalescing stage (2 h) — fixes D2 — ✅ CODE COMPLETE, gate partially closed, see §11

Today every parsed line becomes its own `Dispatcher.BeginInvoke`, each doing a full frame render. At 115200 baud the producer outruns the UI dispatcher and latency grows without bound.

```csharp
// Core layer (see §11.3 deviation 1 — NOT the App layer)
private static readonly Channel<SensorFrame> Frames =
    Channel.CreateBounded<SensorFrame>(new BoundedChannelOptions(1) {
        FullMode = BoundedChannelFullMode.DropOldest,   // newest frame wins
        SingleReader = true, SingleWriter = false
    });
```

> ⚠️ **CORRECTED IN PHASE 4 — this line originally read `DropWrite`, and the sketch was a bug.** `DropWrite` discards the item being *written* when the channel is full, so a full one-slot channel keeps the frame it already holds — the **stale** one — and throws away the fresh one. That is the exact opposite of the comment next to it. `DropOldest` is the policy that makes "newest frame wins" true. Implementing the sketch as written would have shipped the wrong behaviour while looking correct in review, because the comment was right and the code was not. Full analysis in §11.2; the guard is `KeepsTheNewestFrame_DiscardsTheStaleOne`.

- Serial callback → `Frames.Writer.TryWrite(frame)` (never blocks, drops stale frames).
- One `DispatcherTimer` at a **UI-side policy rate** (e.g. 60 Hz) → drains and renders exactly one frame per tick.

This converts frame rate from a serial-side accident into an explicit, tunable app setting — and it makes `SetOrientation`'s UI-thread affinity explicit instead of implicit.

> Depends on Phase 3: `Channel<SensorFrame>` needs `SensorFrame`, which does not exist yet. Phase 3's first step is what makes this compilable.

**Gate:** connect at max baud; UI latency stays flat. Add a frame-drop counter to `StatusText` so the drop policy is visible rather than mysterious.

> **Gate outcome (§11):** clause 2 — the drop counter — ✅ **done**, and `DroppedFramesCountsOnlyCoalescedAwayFrames_NotClearedOnes` pins what the number means. Clause 1 — max-baud latency — ⚠️ **not measured; needs a hardware session.** What *is* proven headlessly is the mechanism that clause 1 depends on: pending never exceeds one slot under a 10 000-frame burst, and `Write` never blocks the producer.

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

> **Prerequisite still not satisfied:** there is still no reference screenshot. Phase 2's visual check was a run-and-look (§9.5), which closes Phase 2's gate but produces no baseline image. Capture one **before** Phase 5 starts, not after — after Phase 5 there is nothing left to compare against, and the whole point of this gate is a diff.

> **Phase 4 changed one input to this phase.** `UpdateFrame` / `UpdateHeatmap` are now called from exactly one place — `RenderFrame`, driven by the render timer — not from a per-frame dispatcher queue. 5a and 5b can therefore measure allocations per *rendered* frame rather than per *received* frame, which is the number that actually matters after Phase 4. The `ToArray()` copies are still there, now inside `RenderFrame` (§11.6).

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

> **Phase 4 added a third thing 6b must absorb:** the render timer, the `FrameCoalescer` instance and the render-rate combo box are currently `MainWindow` state. The *policy* (rate, drop counter) belongs on the view model; the *timer* is a UI-thread mechanism and can stay with the view. Splitting them wrongly is how the rate ends up as a field on a `Window` again.

**Gate:** all tests green, manual checklist against real hardware passes, `--sim` renders.

---

### Phase 7 — Optional polish

- Relocate to `src/` + `tests/` (now safe — the architecture is settled).
- GitHub Actions: `windows-latest`, `dotnet build` + `dotnet test` on push/PR. **Highest-value item in this list now:** 193 tests exist and nothing runs them automatically. A guard that nobody executes is not protection.
- README: wiring, serial line format (16 / 25 / 64 / 73 values, separator set, IMU ordering = ITG3200 gyro, BMA180 accel, HMC5883L mag), keyboard/mouse controls, `--sim` / `--replay`.
- `WorldMap` dirty-bucket incremental rebuild (fixes D4: `RenderMapIfDue` currently rebuilds every bucket mesh every 150 ms; buckets are bounded at 65,341, so worst case is ~500k positions re-emitted per tick).
- WPF 3D shading: `GeometryModel3D`s built from cubes/lines/map buckets have **no `Normals`**, so they shade inconsistently under the `DirectionalLight`. Add normals or switch those models to `EmissiveMaterial` if lighting isn't wanted.

---

## 3. Sequencing rationale

```
Phase 0  hygiene          → makes every later diff contain only code      ✅ done
Phase 1  tests            → locks CURRENT behavior; must precede all moves ✅ done
Phase 2  Core extraction  → the keystone; removes the WPF dependency       ✅ done, gate closed
Phase 3  ISensorStream    → unlocks offline development                    ✅ code complete (§10)
Phase 4  coalescing       → fixes the throughput ceiling                   ✅ code complete (§11)
Phase 5  Rendering        → only now is it safe to touch the visual tree
Phase 6  ViewModel + D1   → behavior change isolated to its own commit
Phase 7  polish
```

**Two hard rules:**

1. **Never do Phase 5 (perf/visual) before Phase 1 (tests).** The perf work touches exactly the code the tests protect, and visual regressions are the hardest kind to detect.
2. **Never combine Phase 6a (the pitch/roll fix) with a structural move.** A correctness fix buried in a 763-line refactor is unauditable and un-revertable.

**Both rules held through Phase 2.** Rule 2 is the one Phase 3 is most likely to break, because 3d (defects D5 and D7) is a behavior change sitting inside a structural extraction. Apply rule 2 to 3d as well: **separate commits.**

**Phase 4 is itself a behavior change** — D2 is defect removal, not a structural move — so it is its own commit under rule 2, and it is the reason §5's "frame rate is a declared UI setting" criterion moved from unchecked to satisfied in one step.

---

## 4. Risk register

| Risk | Likelihood | Impact | Mitigation | Status |
|---|---|---|---|---|
| `Mat3` row/column convention diverges from `Matrix3D` → mirrored or transposed scene | **High** | High | Phase 2 gate is a visual before/after comparison, not just a green build. Add a `WpfMath` round-trip test: `Vec3 → Mat3 → Matrix3D` applied to basis vectors must match `OrientationFilter`'s current output element-for-element. | ✅ **Closed for Phase 2.** Both halves now done: the automated half passes (`WpfMathTests`: element-for-element copy, transpose detector, determinant + `row1 × row2 == row3` handedness detector, basis-vector comparison across all five poses), and the app was run after the extraction and renders and behaves correctly (§9.5). **Reopens at Phase 5**, which rewires the visual tree; that gate needs a pixel comparison, and no reference screenshot exists yet — capture one before Phase 5 starts. |
| Tests written *after* extraction, so they encode the refactor's bugs | Medium | High | Phase 1 is deliberately before Phase 2. Non-negotiable. | ✅ **Held.** Phase 2 did not start until Phase 1's gate was green. That ordering is what let §9.4's three failures be attributed confidently to test defects. |
| `16 or 25 => 16` / `64 or 73 => 64` treated as a bug and "fixed" | Medium | Medium | Pin it in a test with a comment explaining it means "zones + IMU". Confirm with the firmware before changing it. | 🟡 **Pinned in tests and in a doc comment** on `SensorFrameParser`. **Still unconfirmed with the firmware** — that half has never been done. |
| Shared mutable `_orientation` `MatrixTransform3D` embedded in two visual trees breaks when a group is frozen | Medium | Medium | Keep the group unfrozen; document the invariant in `OrbitCameraController`. Do not call `Freeze()` on `_points` or `_boardGroup`. | ⬜ Untouched. `MainWindow` still holds `_orientation` as a shared `MatrixTransform3D`; `OrbitCameraController` does not exist until Phase 5. |
| `TreatWarningsAsErrors` breaks the build on generated WPF markup code | Low | Low | Scope it per-project, or revert to warning-only. | ✅ **Did not bite** — 0 warnings across every build through Phase 2, including the test project with xunit analyzers. §7.3's prediction that it would bite at Phase 5 is still untested. |
| `net10.0` Core accidentally references a WPF type | Medium | High | Add a build-time guard: an analyzer rule or a simple test asserting `typeof(OrientationFilter).Assembly.GetReferencedAssemblies()` contains no `PresentationCore`/`WindowsBase`. | ✅ **Done, and the recommended form was insufficient.** The literal suggestion — assert absence of `PresentationCore`/`WindowsBase` — was implemented first and **passed while asserting nothing**, because `Window`/`Application` live in `PresentationFramework`. See §9.4 item 2. The shipped guard covers all three assemblies from one shared list. |
| Behavior drift in `LineFramer` (framing bugs = dropped/corrupted frames) | Medium | High | `LineFramer` gets its own test suite in Phase 3a, plus a `ReplaySensorStream` parity check against a real capture. | 🟡 **Half done.** The test suite exists (16 tests) and its centrepiece replays 402 chunks through a verbatim copy of the old MainWindow algorithm, requiring identical lines in identical order and an identical drop count — so 3a's behaviour-neutrality is measured, not asserted. **The parity half is still open:** the parity check needs a **real captured log**, which the repo still does not have. Capture one during a hardware session before trusting `ReplaySensorStream`. See §10.5. |
| **A test that asserts nothing is mistaken for protection** — *added after Phase 2* | **High** | High | Four such guards were written in Phase 2 and only revealed themselves by failing or by review: a `-windows` substring check that can never fire, a WPF-signature check matching only `WindowsBase`, a namespace filter that omitted `IsPublic`, and a cross-product assertion that was mathematically false. **A green suite is not evidence that the guards assert anything.** Review each guard by asking what would have to change for it to fail; if the answer is "nothing," it is decoration. | 🟡 **Ongoing discipline, not a one-time fix — and it recurred immediately in Phase 4.** Two of Phase 4's tests asserted an identity that held *by construction* (`DroppedFrames` is defined as `written − read − cleared − pending`, so any equation built from those four terms is unfalsifiable). Neither was caught by failing; both were caught by re-reading. Replaced with hand-computed expectations. See §11.5. Applies to every guard added in Phases 5–6. |
| **Solution state is unreliable** — *added after §8 and §9.4, which were the same failure twice* | Medium | Medium | `get_solution_overview` reported 1 project in §8 and 2 projects in every Phase 2 run, including green ones, while the `.slnx` on disk listed 3. Neither the overview nor the `.slnx` on disk proves a project is loaded. **Evidence that a project is loaded: its assembly was produced, and the test count matches expectation.** A test count *lower* than expected means discovery silently skipped something. | ✅ **Diagnosed and confirmed, §9.10.** Reopening the solution made the overview report **3** projects and all three built individually — so the disagreement was a stale in-memory solution, not a property of `.slnx`. The mitigation stands (verify by assembly + test count, expect the disagreement when Phase 3 adds a fourth project), but the fix is known to work: reopen, don't re-diagnose. |
| **A manual "looks fine" check is mistaken for a pixel comparison** — *added when Phase 2's visual gate closed* | Medium | High | Phase 2's gate asked only that the app "renders identically", and a run-and-look is the right strength for that: a mirrored or transposed `Mat3` is glaring in a 3D view. Phase 5's gate asks for a **pixel diff**, and its changes are subtle by design (brush reuse, transform reuse, named layers, axis relabelling). Recording "I ran it and it looked fine" as satisfying both conflates two different bars. **Capture a reference screenshot before Phase 5 starts**, and state which bar each check actually meets. | ⬜ Open. No reference screenshot exists. Must be done before Phase 5, not after. |
| **Coalescing hides frames the user expects to see** — *the risk Phase 4 was told to mitigate* | Medium | Medium | §2 Phase 4's own mitigation: "make the drain explicit (`async` read loop + render timer)" and show a drop counter. | 🟡 **Half done (§11).** The drop counter is in `StatusText` and its semantics are pinned by test. The drain is a timer only — no `async` read loop (§11.3 deviation 3) — so the mitigation is partly unimplemented. The residual risk is now *rate*, not *queue*: at 15 Hz the app deliberately shows 1 of 60 frames, and nothing measures whether the user can tell. |
| **A plan document destroyed by a full-file overwrite** — *added after Phase 4* | Medium | High | `replace_file_content` replaces the whole file; it is not a merge. A 911-line document maintained across four phases has no cheap reconstruction path except git. Use `replace_file_lines` for partial edits, and restore with `git show <commit> -- <path>` rather than re-typing from memory. | ✅ **Recovered, at cost.** `MIGRATION_PLAN.md` was overwritten with a placeholder during Phase 4 and restored from `ba4d61a`. Nothing was lost, but the incident is why §1 gotcha 7 exists. |

---

## 5. Acceptance criteria

- [x] `Core` compiles as `net10.0` with zero WPF references (verified by test, not by inspection). **Done in Phase 2.** `CoreAssemblyReferencesNoWpfAssembly` walks the reference graph; a separate guard asserts the assembly's `FrameworkName` has no `Platform=`; a third confines WPF-typed public types to `WpfMath`. All pass. **Still passes after Phase 4 added `FrameCoalescer` to Core** (§11.3) — the guard was not loosened to accommodate the new type.
- [ ] `MainWindow.xaml.cs` < 200 lines, containing only XAML handlers + input. **Currently 827 lines** — 763 at Phase 1, 768 after Phase 2, 755 after Phase 3, **827 after Phase 4** (+72). Phase 4 moved *away* from this target, which §2 anticipates by assigning the reduction to Phase 6b; recorded rather than hidden. Phase 6b.
- [~] ≥ 40 tests, all passing, covering parser edge cases, filter known-pose, orthonormality invariant, `LineFramer`. **193 tests, 193 passing.** Parser ✅, known-pose ✅, orthonormality ✅, architecture invariants ✅ (not originally listed — added in Phase 2, extended in Phases 3 and 4), `LineFramer` ✅ **done in Phase 3 (§10)**, `FrameCoalescer` ✅ **added in Phase 4 (§11)**.
- [~] App runs with `--sim` and `--replay <file>` — no hardware required. **The two streams now exist** (`SimulatedSensorStream`, `ReplaySensorStream` — Phase 3), **but nothing in the App can select them**: there is no `--sim`, no `--replay`, and `MainWindow` constructs `SerialSensorStream` directly. The seam is built; the composition root is Phase 6c.
- [~] Pitch/Roll labels verified against a physical tilt, with a regression test. **Regression test exists** (`OrientationAngleMappingTests`, and it is the test that would have failed had the plan's own wrong description of D1 been implemented). **The physical tilt has not been done** — that half is inherently manual and is still the only way to settle which label is wrong in the user's terms. Phase 6a.
- [ ] Main view and board view use one axis convention. `WorldFrame` now exists as the single source of truth, but `AddAxes` and `CreateWorldAxes` have not yet been rewired to it. Phase 5d.
- [x] Frame rate is a declared UI setting, not a baud-rate side effect. **Done in Phase 4 (§11).** `RenderRateComboBox` in the toolbar offers 15 / 30 / 60 / 120 Hz with 60 Hz as the default, and it drives `DispatcherTimer.Interval` live — the rate can be changed while connected. `Stream_FrameReceived` no longer contains a `Dispatcher.BeginInvoke`. Pinned by `RenderRateIsAUserVisibleSetting_AndFrameReceivedNoLongerMarshalsPerFrame` and `DefaultRenderRateIsThePlannedSixtyHertz`. **Note what is *not* covered:** the choice is visible and enforced, but no measurement shows the app sustaining 60 Hz against real frames (§11.6).
- [ ] CI builds and tests on every push. Phase 7. Note `.github/copilot-instructions.md` exists but there is no workflow file.
- [ ] README documents the serial protocol. Phase 7.
- [x] Git history has one commit per phase with a descriptive message, from `c69881d` onward. **Satisfied** — `c69881d`, `bc4413a`, `f534d57`, `8e19f97`, `8d933b4`, `3daf4b7`, `295d5bf`, `0e08043`, … `3288a3d` (Phase 3), `f6a75a5` (Phase 4).
      ~~Squash the `wip`/`ok`/`q` commits~~ — **withdrawn, see §7.1**: those commits are already published on `origin/main`.
- [~] **The app renders identically after every structural phase.** *Added after Phase 2.* This criterion was stated in the Phase 2 and Phase 5 gates but never listed here, so it could be left unsatisfied without any open checkbox. It is inherently manual, and it is the only check that reaches the register's High/High mirrored-scene risk. **Satisfied for Phase 2** — the app was run after the extraction and renders and behaves correctly (§9.5). **⚠️ Re-opened and NOT satisfied since Phase 3:** the app has not been run since Phase 3 rewired `MainWindow`'s input path (§10.7), and Phase 4 rewired the frame/render path again. Two consecutive phases with no run in between. Note the bar rises for Phase 5: a run-and-look closes Phase 2's gate but not Phase 5's pixel-compare gate, because Phase 5's changes are subtle by design. Capture a reference screenshot *before* starting Phase 5.

---

## 6. Effort summary

| Phase | Effort | Cumulative | Status |
|---|---|---|---|
| 0 Hygiene | 0.5 h | 0.5 h | ✅ DONE (`c69881d`) |
| 1 Tests first | 2–3 h | 3.5 h | ✅ DONE (`f534d57`, gate `8e19f97`) |
| 2 Core extraction | 3–4 h | 7.5 h | ✅ DONE, **gate fully closed** (`8d933b4`; automated `295d5bf`, visual §9.5) — ⚠ 2d deferred to Phase 3 |
| 3 Hardware abstraction | 3 h | 10.5 h | ✅ **CODE COMPLETE** (§10) — gate partially closed: build + 181 tests green, hardware parity and log capture still open |
| 4 Coalescing stage | 2 h | 12.5 h | ✅ **CODE COMPLETE** (§11) — gate **partially** closed: build + 193 tests green, drop counter done; the max-baud latency measurement and a run of the app are still open |
| 5 Rendering extraction | 3 h | 15.5 h | pending |
| 6 ViewModel + D1 fix | 2 h | 17.5 h | pending |
| 7 Polish | 3–4 h | ~20 h | pending |

**Minimum viable value is delivered at the end of Phase 2 (~7.5 h) — and Phase 2 is finished, gate closed:** testable domain logic, no WPF dependency, a build that still works, 130 passing tests, and a confirmed-working app. If nothing else is done, the migration has already paid for itself.

Two costs the effort table did not anticipate: the two solution-reload blockers (§8, §9.4) and the four test defects that had to be diagnosed as *test* defects rather than code defects (§9.4). Budget similar overhead for Phase 3's new project. **§9.10 narrows this:** the reload itself is cheap — one reopen — and it is now confirmed to work. The expensive part is *diagnosing* that a reload is needed, which §8 spent a full session on. Do not re-spend that: when a new project appears missing, reopen first and verify by test count.

**Phase 4 spent its 2 h on code and none of it on the gate.** The 2 h estimate was for the coalescer, and it was accurate; what the estimate never accounted for is that the gate's remaining clause needs a device and a run, which no amount of coding time covers. Phases 3 and 4 are now both "code complete, gate open" for the same reason.

---

## 7. Phase 0 findings — corrections to the original plan

Phase 0 was intended as zero-risk hygiene. It is done and the gate passed, but verifying it surfaced five things that materially change how later phases must be executed. Recording them here rather than silently editing the phases above. **7.1 and 7.4 are the consequential ones.**

### 7.1 The squash recommendation was wrong — and would have been destructive

I recommended squashing the `wip`/`ok`/`q` commits "before the first push." **That premise was false.** At the time of the original analysis, `main` was 6 commits *ahead* of `origin/main`, which I read as "unpushed." It has since been pushed: `origin/main` is at `f3475a1` and contains `wip`, `q`, `ok that seems better`, and the rest.

**Do not rewrite that history.** Squashing published commits requires a force-push to `origin/main` — a protected branch — which rewrites shared history for every other clone of the repo.

**Withdrawn:** the squash criterion in §5.
**Replaced with:** descriptive messages from `c69881d` forward. `c69881d` ("Phase 0: repo hygiene") and `bc4413a` ("add migration plan") already satisfy it. The pre-`c69881d` noise is permanent and cosmetic — it is not worth a force-push to a protected branch.

**Note the coincidence worth understanding:** `origin/main` sits at `f3475a1`, and the `pre-refactor` tag also points at `f3475a1`. So the published boundary and the revert point are the same commit — everything from `c69881d` forward is local. That is why §7.1's warning matters concretely: the commits that would have been squashed are exactly the ones that are already public.

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

### 7.5 Gate was proven in Debug only — ✅ CLOSED (Release build verified)

**Resolved after Phase 2, before Phase 3.** A second MCP server was connected alongside the original one, and it exposes `build_configuration_get` / `build_configuration_set` — the capability the original toolset genuinely lacked. The gate was then re-run in Release:

```bash
build_configuration_set  configuration="Release"  platform="Any CPU"
build_solution
build_status
build_configuration_set  configuration="Debug"    platform="Any CPU"
```

`build_status` → `State: Done`, `FailedProjects: 0`. Error List → **0 errors, 0 warnings, 0 messages**, checked separately against both the `build` and `intellisense` sources.

Release artifacts confirmed on disk for all three projects:

| Project | `bin/Release/<TFM>` | Contents |
|---|---|---|
| `EchoEnvironment` | `net10.0-windows` | `EchoEnvironment.exe`, `.dll`, `.pdb`, `EchoEnvironment.Core.dll`/`.pdb`, `System.IO.Ports.dll`, `deps.json`, `runtimeconfig.json`, `runtimes/` |
| `EchoEnvironment.Core` | **`net10.0`** | `EchoEnvironment.Core.dll` + `.pdb` + `deps.json` — **no WPF assembly, no `System.IO.Ports`** |
| `EchoEnvironment.Tests` | `net10.0-windows` | `EchoEnvironment.Tests.dll` + all three project `.pdb` + xunit / TestPlatform runners |

**Core's Release output folder is `net10.0`, not `net10.0-windows`.** That is direct on-disk evidence for §7.4's claim that the TFM is the load-bearing difference between Core and the WPF projects, and it corroborates the `CoreAssemblyReferencesNoWpfAssembly` guard from outside the test itself.

**The full suite was then run in Release: 130 passed, 0 failed, 0 skipped.** The §8 and §9 green record is therefore no longer Debug-only.

**The failure mode this gate was guarding against was exercised and did not occur.** No Release-only warning appeared, so `TreatWarningsAsErrors` had nothing to escalate, and no Release-only analyzer complaint appeared in the test project. Phase 3 can add a fourth project inheriting `TreatWarningsAsErrors` without the §7.5 risk of a Phase 5 rendering change being blamed for a pre-existing Release-only defect.

**Retained as a lesson, not as a to-do:** the earlier conclusion that this was "confirmed impossible with the available tooling" was accurate for the toolset available at the time — that build tool really did expose only `project_name` and `timeout_seconds`. It was a tooling gap, not a plan defect, and it was closed by connecting a second server rather than by rephrasing the request. When a verification step is blocked, record *which* capability is missing; that is what makes it findable later.

> ⚠️ **This gate is open again, and has been since Phase 3.** The closure above covers **three** projects at Phase 2. `EchoEnvironment.Hardware` (Phase 3) and the Phase 4 changes have **never been built in Release** — §10 and §11 both carry it. `dotnet build EchoEnvironment.slnx -c Release` in a terminal is the fix, and it has now survived two phases without being run. "§7.5 closed" is true only of the tree it was run against.

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

> **Refined by §9.10:** step 1 alone was sufficient in both later occurrences. Steps 2–4 have never been needed — once the solution is reopened, the projects restore, build, and test on their own. Do the reload first and verify by test count before reaching for **Add → Existing Project…**.

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

## 9. Phase 2 record — **gate fully closed** ✅ (automated §9.4, visual §9.5)

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

Note also that the solution overview reported only 2 projects in **every** run, including the green ones, even though Core resolved for build and for both `ProjectReference`s. The overview is not a reliable indicator of what is actually loaded, and neither is the `.slnx` on disk. Trust the build and the test count. **§9.10 supersedes the last sentence's framing:** after a reopen the overview did report 3, so the overview is unreliable *while the solution is stale*, which is exactly the state a new project puts it in.

### 9.5 Standing facts after verification

- **`CoreAssemblyReferencesNoWpfAssembly` passes.** The keystone invariant is now enforced by a test rather than by a convention someone must remember.
- **`OrientationFilterNoLongerExposesMatrix3D` passes**, asserting the inverse of what Phase 1 asserted.
- **`MainWindow` is 763 lines** (758 at `bc4413a`; Phase 2 added 5). Phase 2 changed only `SetOrientation`'s signature and one `using`. The thinning is Phase 6b, and §5's "< 200 lines" target is untouched.
- **The gate's third clause — "app renders identically" — is now satisfied.** The app was run after Phase 2 and renders and behaves correctly. `WpfMathTests` remains the automated mitigation (element-for-element copy, transpose detector, determinant + `row1 × row2 == row3` handedness detector, Core-vs-WPF basis-vector comparison across all five poses), and those all pass.
- **What that check was, stated precisely.** A manual run-and-look, not a pixel diff against a pre-Phase-2 screenshot. That is the right strength for Phase 2's gate, which only asks whether the app "renders identically": a mirrored or transposed `Mat3` layout is glaringly visible in the 3D view, and the whole point of the gate was to catch that one failure mode. It is the wrong strength for Phase 5, whose changes are subtle by design. Do not carry "I ran it and it looked fine" forward as if it satisfied §5's pixel-compare bar for Phase 5.

### 9.6 Consequence for sequencing

**Phase 2's gate is fully green** — automated half verified (§9.4) and the app was run and renders and behaves correctly (§9.5). **Phase 3 may proceed with no carried-forward obligation.**

One caveat about what "fully green" means, so it is not over-read: the visual check was a manual run-and-look, not a pixel comparison against a pre-Phase-2 screenshot. It is sufficient to close Phase 2's gate clause, which asked only that the app "renders identically" — a mirrored or transposed scene would be obvious on sight. It is **not** sufficient for Phase 5's gate, which asks for a pixel comparison, because Phase 5 changes the visual tree in ways that are subtle by design (brush reuse, transform reuse, named layers, `AddAxes` relabelling). Take a screenshot of the current state before starting Phase 5, not after.

### 9.7 ⚠️ Phase 2 step 2d was NOT done — `SensorFrame` does not exist
> ✅ **CLOSED IN PHASE 3 — see §10.2.** Everything above was true at the time it was written and is no longer true against the current tree: `SensorFrame.cs` exists, `TryParse(string, out SensorFrame)` exists, the `out int[]` overload is kept as a thin wrapper over a shared private `TryParseValues`, and seven tests pin the new overload. There **is** now something to delete in Phase 5.

Verified against the tree: **there is no `SensorFrame.cs`, no `SensorFrame` type, and no `TryParse` overload returning one.** The only public member of `SensorFrameParser` is still:

```csharp
public static bool TryParse(string line, out int[] distances, out int[]? imu)
```

So §2d's "add the frame model … keep the old `out int[]` overload as a thin wrapper … delete the wrapper in Phase 5" was **skipped entirely** — including the wrapper, which means there is nothing to delete in Phase 5.

**Why this is acceptable rather than a defect:** Phase 2's stated purpose was to remove the WPF dependency from domain logic, and it did that. `SensorFrame` is a *convenience* model, not a seam. The parser moved byte-for-byte, its quirks are pinned, and the App compiles against it unchanged. Adding the record now would be a behaviour-neutral structural change, which is exactly what §2d wanted — but it should be done deliberately in **Phase 3**, because `ISensorStream.FrameReceived` (§3b) is the first consumer that actually needs a single value type to pass around. Doing it there means it is driven by a real caller instead of being speculative.

**Consequence:** §3b's `event EventHandler<SensorFrame>` currently has no type to bind to. Phase 3 must introduce `SensorFrame` as its first step, not assume it exists.

### 9.8 ✅ CONFIRMED in Phase 3: an empty `EchoEnvironment/Math/` directory exists — delete it by hand

One directory listing reported `EchoEnvironment\Math\` as existing and **empty** — residue from the first attempt at §2c, before §9.2 deviation 3 established that a namespace called `Math` shadows `System.Math` in every file importing it (and `MainWindow` uses `Math.Abs` throughout). A second, direct probe of the same path reported **not found**, which is why this stayed open for two phases.

**Now settled.** A raw filesystem listing (not a solution-file listing) confirms `EchoEnvironment\Math\` **exists and is empty**. The reason the disagreement happened: git does not track empty directories, so it is invisible to `git status` and appears in no diff, and any tool that enumerates *solution files* cannot see a directory with no files in it. Only a raw directory listing can.

**Action:** delete it manually (`rmdir EchoEnvironment\Math`). No automated file tool can do it — they operate on files, and there is no file to operate on. It harms nothing except by being misleading about what §2c left behind. `WpfMath.cs` lives at the `EchoEnvironment/` project root with namespace `EchoEnvironment`, which is the part that matters.

### 9.9 Open obligations carried into Phase 3

1. ~~**Visual before/after render comparison for Phase 2**~~ ✅ **DONE** — the app was run after Phase 2 and renders and behaves correctly. What that does and does not prove is recorded in §9.5; Phase 5's stricter pixel-compare bar is still open.
2. ~~**Release build has never been run**~~ ✅ **DONE** — verified in Release after Phase 2 via `build_configuration_set`: 0 errors / 0 warnings, Release artifacts present for all three projects, and the full 130-test suite green in Release. See §7.5. **⚠️ Re-opened by Phase 3 and still open after Phase 4** — the fourth project and Phase 4's changes have never been built in Release (§10, §11).
3. ~~**`pre-refactor` tag was never created**~~ ✅ **It exists** — `pre-refactor` → `f3475a1` ("Code state before refactor"), the last commit before Phase 0. A better baseline than the `bc4413a` this plan recommended, since `bc4413a` already contains the plan itself.
4. **Firmware confirmation of the 25/73-value quirk** (§4) has never been done.
5. **Physical tilt to settle the Pitch/Roll labels** (§5, Phase 6a). The regression test exists; the human verification does not.

### 9.10 Solution reopen verified the stale-solution diagnosis — first time the overview reported 3 projects

A close-and-reopen of `EchoEnvironment.slnx` was performed after Phase 2's gate closed, and it produced a fact this plan had implicitly predicted would never happen: **the solution overview reported 3 projects.** For the entirety of Phases 1 and 2 it had reported 1 (§8) or 2 (§9.4) while the `.slnx` on disk listed 3.

| Check | Result |
|---|---|
| Overview project count | **3** — App, Core, Tests |
| `EchoEnvironment.Core` targetable by name | ✅ built individually, 0 errors / 0 warnings |
| `EchoEnvironment` targetable by name | ✅ built individually, 0 errors / 0 warnings |
| `EchoEnvironment.Tests` targetable by name | ✅ built individually, 0 errors / 0 warnings |
| Full suite | **130 total, 130 passed, 0 failed, 0 skipped** |

**What this settles.** The disagreement between the `.slnx` on disk and the overview was a **stale in-memory solution**, not a property of `.slnx` files or of the projects. The diagnosis in §8's "Root cause" was correct, and the fix in §8's "Required manual step" works — step 1 alone, no **Add → Existing Project…**, no manual restore.

**What this does not change.** The mitigation in §4 stays: verify by assembly + test count, not by the overview. The overview became accurate only *after* a reload, and a reload is exactly what Phase 3 will need when it adds a fourth project. The durable evidence is still "the assembly was produced and the test count matches expectation."

**Practical consequence for Phase 3:** when the new `EchoEnvironment.Hardware` project appears missing, **reopen the solution first** and re-check by test count. Do not re-run §8's five failed automated attempts — they were diagnosing a problem whose fix was already known. §6's effort note is updated accordingly: the reload is cheap, the *diagnosis* was expensive.

---

## 10. Phase 3 record — code complete, gate **partially** closed ⚠️

**Build:** Debug, whole solution, **0 errors / 0 warnings** across **four** projects. `EchoEnvironment.Hardware\bin\Debug\net10.0-windows\EchoEnvironment.Hardware.dll` was produced and verified on disk (PE headers checked). ⚠️ **Release was NOT verified for Hardware:** `EchoEnvironment.Hardware\bin\Release` and `obj\Release` do not exist, and the available build tool exposes no configuration parameter. §7.5's Release gate was closed for three projects; a **fourth** project now inherits `TreatWarningsAsErrors` and has never been built in Release. Run `dotnet build EchoEnvironment.slnx -c Release` in a terminal before Phase 4."
**Tests:** **181 total, 181 passed, 0 failed, 0 skipped** (was 130; **+51 new tests**).
**Gate:** clause 1 (real hardware behaves identically) — **NOT verified, requires a hardware session.** Clause 2 (`ReplaySensorStream` reproduces a captured log frame-for-frame) — **NOT satisfied: the repo still has no capture.** What was substituted is a synthetic log, which proves the class replays a log correctly and proves nothing about the device.

> ⚠️ **The instruction in the Build line — "Run `dotnet build … -c Release` before Phase 4" — was not carried out, before or during Phase 4.** See §11's Build line. The obligation is now two phases old.

### 10.1 What Phase 3 created

| File | Lines | Covers |
|---|---|---|
| `EchoEnvironment.Hardware/LineFramer.cs` | 149 | 3a. Cursor-based framing; `Append` / `TryReadLine` / `TryReadPendingLine` / `TrimOverflow` / `Clear` / `DroppedBytes` / `MaxBuffer` |
| `EchoEnvironment.Hardware/SerialSensorStream.cs` | 130 | 3b/3c production transport; the moved `SerialPort` + receive + parse path |
| `EchoEnvironment.Hardware/SimulatedSensorStream.cs` | 216 | 3c simulator: zone sweep + known-pose IMU, `SimulationPose` enum |
| `EchoEnvironment.Hardware/ReplaySensorStream.cs` | 204 | 3c log replay |
| `EchoEnvironment.Tests/LineFramerTests.cs` | 389 | 16 tests, incl. line-for-line equivalence against a verbatim copy of the old algorithm |
| `EchoEnvironment.Tests/SensorStreamTests.cs` | 409 | 26 test cases across the seam and its implementations |
| `SensorFrameParserTests.cs` +134 | — | 7 tests for the `TryParse(out SensorFrame)` overload |
| `ArchitectureInvariantTests.cs` +63 | — | 2 new guards (9 total) |

`MainWindow.xaml.cs`: **763 → 755 lines**, `using System.IO.Ports;` **removed**, `_rxBuffer` / `IndexOfNewline` / `Port_DataReceived` **deleted**, replaced by `Stream_FrameReceived` / `Stream_LineRejected` / `Stream_StatusChanged`. `EchoEnvironment.csproj`'s `System.IO.Ports` `PackageReference` **removed**.

### 10.2 §9.7 is now closed — `SensorFrame` exists

`EchoEnvironment.Core/SensorFrame.cs` exists, `TryParse(string, out SensorFrame)` exists, and the original `out int[]` overload was **kept as a thin wrapper** delegating to a shared private `TryParseValues`, exactly as §2d required. So **there is now something to delete in Phase 5**, which §9.7 said there was not. Seven tests pin the new overload against the old one on every shape the parser accepts or rejects, so the App (still on `out int[]`) and the transport (on `SensorFrame`) cannot disagree about whether a line is a frame.

### 10.3 Deviations from the plan's own text

1. **`ISensorStream` has a fourth event, `LineRejected`,** which §3b's sketch omits. Dropping it would have deleted the app's `"Rejected line: …"` status message (`MainWindow.xaml.cs:335`) — a behaviour change smuggled into a structural move. Kept as its own event rather than folded into `StatusChanged` so the two diagnostics cannot be confused.
2. **`LineFramer` has `TrimOverflow()` as a separate call;** §3a's sketch has no overflow method, implying `Append` drops on overflow. It must not: the old code checked the buffer **after** draining every complete line, so a chunk holding three complete frames plus a wall of garbage delivered those three frames. Dropping inside `Append` would have discarded them. Pinned by `Overflow_DoesNotDiscardCompleteLinesAlreadyDrained`.
3. **`DroppedBytes` is `long`, not the sketch's `int`.** At 115200 baud a day of garbage passes 2³¹ bytes, and a counter that wraps is worse than none.
4. **`Close()` does not raise `StatusChanged`.** MainWindow writes `"Disconnected"` unconditionally, including when no port was ever open; having the transport write it too would change the never-connected case. The disconnect message stays the consumer's.
5. **`LineFramer.MaxBuffer` is a mutable property, not the sketch's `get; set;` on a class with a default** — same thing, but `0` or negative **disables** the guard rather than meaning "drop everything", which is the only reading that makes the guard testable in both directions.
6. **`SimulatedSensorStream.LineRejected` is written with explicit empty accessors.** `TreatWarningsAsErrors` turns CS0067 ("event is never used") into a build error, and a simulator that claimed it could reject a line would be lying about the contract.
7. **`ReplaySensorStream` reads in 4096-character chunks** rather than one whole read, so chunk-boundary framing is exercised on replay the way it is on a real port.
8. **`LineFramer` gained `TryReadPendingLine`,** which §3a's sketch does not have. A log file has an end; a final frame with no trailing newline is still a frame. `SerialSensorStream` deliberately never calls it — a port has no end to drain at.

### 10.4 Behaviour deliberately preserved (this was a move, not a fix)

- **A terminator is a single `'\n'` OR a single `'\r',** so `"\r\n"` yields the line **and then an empty line**. Pinned by `CarriageReturnNewline_YieldsTheLine_AndThenAnEmptyLine` and by the CRLF-vs-LF replay equivalence test. Invisible in the app because blank lines are filtered before the parser. "Fixing" it in 3a would have been the exact drift §4's LineFramer row warns about.
- **Overflow discards everything unread,** exactly as `_rxBuffer.Clear()` did — now observably.
- **D5 preserved:** `SerialSensorStream.Open` still constructs and subscribes the `SerialPort` before `Open()`, so a failed `Open()` leaks it. Marked in the source.
- **D7 preserved:** `ReadTimeout = 1000` is still set and still ignored by `ReadExisting()`.
- **`_rxBuffer` was not cleared between connections** before Phase 3, and `SerialSensorStream` still creates a fresh framer per connection rather than reusing one — the same net effect.
- **A port that dies mid-read is swallowed.** `Faulted` is raised so a consumer *can* report it, but `MainWindow` does not subscribe: surfacing it would add a status message the app has never shown.
- **One observable nuance, stated rather than hidden:** `"Connected to COM12"` used to be written directly by `Connect_Click`; it is now raised by `SerialSensorStream.Open` as `StatusChanged` and marshalled through `Dispatcher.BeginInvoke`. Same text, same order relative to the button label, but it now lands on the next dispatcher turn instead of inside the click handler. No test covers this, because no test drives `Connect_Click` (see §10.7).

### 10.5 The equivalence proof, and why a hand-written test would not have been enough

`LineFramerTests.MatchesTheOldMainWindowFramer_LineForLine` replays a deterministic 402-chunk sequence (16- and 64-zone frames, `'\n'` / `"\r\n"` / bare `'\r'` terminators, garbage lines, blank lines produced by CRLF, unterminated tails, and a 12 000-character wall of garbage) through **a verbatim copy of the algorithm MainWindow used before the seam existed** and through `LineFramer`, requiring identical lines in identical order and an identical drop count. The test then asserts `expected.Count > 100` and `expectedDropped > 0`, because a guard that never exercised the overflow path would pass while asserting nothing (§4, High/High).

### 10.6 Two new guards, and what each would have to change to fail

| Guard | Fails if |
|---|---|
| `HardwareAssemblyReferencesNothingButCore` | Hardware acquires a reference to the App or (at Phase 5) Rendering; **or** Hardware stops referencing `System.IO.Ports` — i.e. the serial transport creeps back into the App. Also asserts Hardware references no WPF assembly. |
| `AppAssemblyDoesNotReferenceSystemIoPorts` | The App regains a direct `System.IO.Ports` reference, or loses `EchoEnvironment.Hardware`. |

The second half of the first guard is what makes it falsifiable in both directions; a one-directional "references nothing" check would pass happily after the transport moved back out of Hardware.

### 10.7 Known gaps and one test that was written and then deleted

- **`SerialSensorStream.Open`'s success path is untested.** A first version of `SensorStreamTests` included a test that opened the first available port and asserted the status message. It was **deleted**: on this machine `COM12` is the development sensor, so the test would take the device away from a hardware session, and on a machine with no ports it would return early and pass while asserting nothing. The gap is now stated in the test file rather than papered over.
- **`MainWindow`'s rewiring is not covered by any test.** No test drives `Connect_Click`, `Disconnect`, or the `Dispatcher.BeginInvoke` marshalling. The App's own behaviour is verified only by running it, which has **not** been done since Phase 3.
- **`_portProbe`** is a `SerialSensorStream` that is never opened and exists only so `RefreshPorts()` can ask the seam which ports exist. It is a placeholder for Phase 6c's composition root and should be replaced there, not admired.
- **`frame.Distances.ToArray()` / `frame.Imu.ToArray()`** are copied in `Stream_FrameReceived` because `UpdateFrame` and `UpdateHeatmap` still take `int[]`. Two small allocations per frame — the same the old path paid. Retargeting them to `ReadOnlyMemory<int>` is Phase 5's job.
- **§9.8's empty `EchoEnvironment/Math/` directory is now CONFIRMED present** (raw filesystem listing; solution-file listings cannot see an empty directory). It is **not yet deleted**: no available tool can remove a directory containing no files. Manual step: `rmdir EchoEnvironment\Math`.

### 10.8 What Phase 4 may now assume

`ISensorStream` exists and is the only place frames enter the app, so Phase 4's coalescing stage has one seam to insert rather than one to build. `SensorFrame.Timestamp` exists for exactly that purpose and is currently consumed by nothing. The `Dispatcher.BeginInvoke` calls in `MainWindow` are the thing Phase 4's bounded channel replaces — and per `ISensorStream`'s threading contract, the obligation to marshal stays with the consumer, not the stream.

### 10.9 Open obligations carried into Phase 4

1. ⚠️ **Run the app and confirm it behaves identically after Phase 3.** Not done. Phase 3 rewired `MainWindow`'s entire input path; a green build and 181 green tests do not reach it (§5, final criterion).
2. ⚠️ **Capture a real serial log**, then run the `ReplaySensorStream` parity check against it. Phase 3's gate clause 2 is unsatisfied until this exists.
3. ⚠️ **D5 and D7 are still unfixed** and must be **separate commits** (§3 rule 2). They were deliberately carried through the extraction rather than bundled into it.
4. ⚠️ **Hardware is in `EchoEnvironment.slnx` and compiles, but `get_solution_overview` still reports 3 projects, not 4.** §9.10's mitigation continues to hold: verify by assembly + test count. The assembly was produced and the count rose 130 → 181, which is the evidence that matters.
5. **Firmware confirmation of the 25/73 quirk** (§4) — still never done.
6. **Physical tilt to settle the Pitch/Roll labels** (§5, Phase 6a) — still never done.
7. **Reference screenshot before Phase 5** (§4) — still none.

---

## 11. Phase 4 record — code complete, gate **partially** closed ⚠️

**Build:** Debug, whole solution, **0 errors / 0 warnings** across **four** projects.
**Tests:** **193 total, 193 passed, 0 failed, 0 skipped** (was 181; **+12 new tests**).
**Gate:** clause 2 ("add a frame-drop counter to `StatusText` so the drop policy is visible rather than mysterious") — **DONE**. Clause 1 ("connect at max baud; UI latency stays flat") — **NOT VERIFIED, requires a hardware session.** Phase 4 is therefore in the same shape Phase 3 was left in: automated evidence green, the one clause that needs the device open.

⚠️ **Release is still unverified for `EchoEnvironment.Hardware`,** and now also for the Phase 4 changes. §10's instruction to run `dotnet build EchoEnvironment.slnx -c Release` in a terminal was **not carried out before or during Phase 4.** The available build tool exposes no configuration parameter. This obligation has now survived two phases.

### 11.1 What Phase 4 created

| File | Lines | Covers |
|---|---|---|
| `EchoEnvironment.Core/FrameCoalescer.cs` | 128 | The coalescing stage: one-slot bounded channel, `Write` / `TryRead` / `Clear` / `Pending` / `Written` / `Read` / `Cleared` / `DroppedFrames`, `CoalescingPolicy` enum |
| `EchoEnvironment.Tests/FrameCoalescerTests.cs` | 415 | 12 tests: newest-wins, the never-exceeds-one bound under a 10 000-frame burst, one-frame-per-drain, drop-counter semantics, non-blocking producer, 8-writer concurrency, `long` counters, Core placement, UI-setting visibility, default rate |
| `MainWindow.xaml` +5 | — | `RenderRateComboBox` in the toolbar (15 / 30 / 60 / 120 Hz, default 60) |
| `MainWindow.xaml.cs` **+92 / −20** | **755 → 827** | `FrameCoalescer` field, `DispatcherTimer`, `RenderTimer_Tick`, `RenderFrame`, `RenderInterval`, `RenderRate_SelectionChanged`, coalescer clear on connect and disconnect, drop counter in `StatusText` |

`Stream_FrameReceived` **no longer contains a `Dispatcher.BeginInvoke`.** It calls `_coalescer.Write(frame)` and returns. The render body moved verbatim into `RenderFrame`, which runs on the UI thread from the timer.

### 11.2 Corrections to the plan's own text — one of them was a real bug in the sketch

> ⚠️ **Line numbers in this document drift.** Sections were inserted at the end and edited in place, so cross-references by line number go stale. §11.2 originally cited "§2 line 384"; the sketch it means is now at line 391. Cross-reference by **section and quoted text**, never by line number.
**§2 line 384 said `FullMode = BoundedChannelFullMode.DropWrite` with the comment "newest frame wins." Those two contradict each other, and the comment is the correct intent.** `DropWrite` discards the item being *written* when the channel is full, so a full one-slot channel keeps the frame it already holds — the **stale** one — and throws away the fresh one. "Newest frame wins" is `DropOldest`. Implementing the sketch verbatim would have produced the exact opposite of the policy Phase 4 exists to provide, and it would have looked correct in review because the comment said the right thing.

The sketch is corrected in place at §2. `KeepsTheNewestFrame_DiscardsTheStaleOne` is the guard: it writes sequence 1, 2, 3 and requires 3. Under `DropWrite` it would return 1 and fail.

### 11.3 Deviations from the plan's own text

1. **`FrameCoalescer` lives in `EchoEnvironment.Core`, not the App layer** (§2 says "App layer"). D2 is a *behaviour* fix, and Core is the assembly that can be tested headlessly (`net10.0`, no WPF, enforced by `ArchitectureInvariantTests`). A coalescer written as a field of a `Window` could only have been verified by running the app against a sensor — a check the suite does not contain. All 12 tests run without the App. `CoalescingPolicyLivesInCore_NotInTheApp` pins the deviation in both directions: it fails if the type moves to the App **and** if the App stops using it.
2. **A named `FrameCoalescer` type instead of a raw `Channel<SensorFrame>`.** The gate requires a *drop counter*. `System.Threading.Channels` discards silently and exposes no count, so the gate's clause 2 is not expressible with a bare channel. `FrameCoalescer` wraps a `Channel<SensorFrame>` with the §2 options (`BoundedChannelOptions(1)`, `SingleReader = true`, `SingleWriter = false`) and adds the accounting. The channel is still there; it is not the public surface.
3. **`DispatcherTimer` instead of an `async` reader loop.** §4's mitigation for the "coalescing hides frames" risk says to "make the drain explicit (`async` read loop + render timer)". Only the timer half was used. A reader loop on the transport thread would have to marshal to the UI thread anyway, which reintroduces the per-frame dispatcher queue D2 is about removing. One timer, one tick, one frame, on the thread that already owns the UI.
4. **`DroppedFrames` is computed as an identity** (`written − read − cleared − pending`), not incremented at the point of discard, because under `DropOldest` the discard happens inside `TryWrite` and is not observable from outside it. See §11.5 for the trap this created.
5. **The rate choices are populated in code, not XAML,** so `RenderRateChoices` and `RenderInterval` share one source and `DefaultRenderRateIsThePlannedSixtyHertz` can assert the default against the same array the combo box is built from.
6. **`_coalescer.Clear()` on both connect and disconnect.** The coalescer outlives the connection; without this, a frame arriving just before the port closed would be rendered by the next connection's first tick. `Clear` is deliberately *not* counted as a drop — a frame discarded because the connection ended is not a frame the policy coalesced away.

### 11.4 Three tests failed on the first run — and all three were test defects, not coalescer defects

This is worth recording because the instinct in each case was to blame the implementation.

1. **`OneFramePerDrain` asserted `Pending == 1` *after* `TryRead`.** A 60-frame burst leaves exactly 1 waiting; the read consumes it; 0 is correct. The assertion that matters — 60 arrivals collapsed to 1 renderable frame — was moved to *before* the read, where it is directly observed instead of inferred.
2. **`CoalescingPolicyLivesInCore` walked parent directories from `typeof(MainWindow).Assembly.Location`.** `MainWindow.xaml` is not under the App assembly's output directory: the App assembly is copied into the **test** project's `bin`, so the walk from it can never reach the source tree. It failed on the first run.
3. **The first fix for (2) was also wrong.** It anchored on the App project directory via `AppContext.BaseDirectory`, which only holds because the test project happens to reference the App project — an accident, not a fact about the App. Replaced with `CodeBaseDirectory()`, which anchors on `EchoEnvironment.Tests.csproj` and walks up to the repo root. It now asserts both halves: the type is under `EchoEnvironment.Core/`, **and** `MainWindow.xaml.cs` contains `_coalescer.Write` and does **not** contain a `Dispatcher.BeginInvoke` in the frame-received handler.

### 11.5 Two tests that passed, and were still worthless — caught by review, not by failing

§4's High/High ongoing item says: *"Review each guard by asking what would have to change for it to fail; if the answer is 'nothing,' it is decoration."* That rule caught two of my own Phase 4 tests.

- **`WrittenEqualsReadPlusClearedPlusPendingPlusDropped`** asserted `written == read + cleared + pending + DroppedFrames`. But `DroppedFrames` is **defined** as `written − read − cleared − pending`. The equation held by construction, however wrong the three counters became. It asserted nothing.
- **`ConcurrentWritersKeepTheBoundAndTheCountersExact`** asserted `written − 1 == DroppedFrames + Pending`. Same flaw, same shape.

Both were replaced with **hand-computed expectations derived from specific sequences**: 40 for the burst case, 1599 for the 8-writer case. Lose a single `Interlocked.Increment` under contention and the concurrency test now reports 1598 and fails. The identity is still documented in `FrameCoalescer`'s comment — with a note that the identity alone proves nothing, and that the value is pinned in tests instead.

### 11.6 Known gaps

- **§10.7's gap is unchanged and is now more relevant:** no test drives `Connect_Click`, `Disconnect`, or the timer wiring. The App's own behaviour is verified only by running it, which **has not been done since Phase 3** — and Phase 4 rewired the frame path again, so the outstanding run obligation now applies to Phase 4's code.
- **The gate's clause 1 is unmeasured.** "UI latency stays flat at max baud" needs `COM12` at 115200 baud. Nothing in the suite reaches it. What *is* proven headlessly is the mechanism that makes it plausible: the pending backlog never exceeds one frame regardless of arrival rate, and `Write` never blocks the producer.
- **`MainWindow.xaml.cs` grew by 72 lines** — 755 → 827 — moving *away* from §5's "< 200 lines" criterion. Expected: §2 assigns the reduction to Phase 6b, and Phase 4 adds UI policy the app previously had none of. Recorded rather than hidden.
- **`frame.Distances.ToArray()` / `frame.Imu.ToArray()` are still there,** now inside `RenderFrame`. Phase 5's job, unchanged.
- **120 Hz is offered but unmeasured.** The combo box's top choice is above the planned 60 Hz default; nothing demonstrates the app can actually sustain it, because nothing measures frame time.
- **`SensorFrame.Timestamp` is still consumed by nothing.** Phase 4 had a natural use for it — reporting frame age alongside the drop counter — and did not take it.

### 11.7 What Phase 5 may now assume

Frames reach the UI through exactly one path: `ISensorStream.FrameReceived` → `FrameCoalescer.Write` → `DispatcherTimer.Tick` → `RenderFrame`. Phase 5's rendering extraction can treat `RenderFrame` as the single entry point and does not need to reason about the transport thread at all — the coalescer is the boundary that made that true. `SensorFrame.Timestamp` is still consumed by nothing. The `ToArray()` copies are Phase 5's to remove.

### 11.8 Open obligations carried into Phase 5

1. ⚠️ **Run the app and confirm it behaves identically after Phase 4** — and, still, after Phase 3. This is now the single most important outstanding item: two consecutive phases rewired the input and render paths with no run in between.
2. ⚠️ **Run `dotnet build EchoEnvironment.slnx -c Release` in a terminal.** Carried from §10, still not done.
3. ⚠️ **Measure UI latency at 115200 baud** to close Phase 4's gate clause 1.
4. ⚠️ **Capture a real serial log**, then the `ReplaySensorStream` parity check. Phase 3's gate clause 2, still unsatisfied.
5. ⚠️ **D5 and D7 are still unfixed** and must be **separate commits** (§3 rule 2).
6. ⚠️ **`get_solution_overview` still reports 3 projects, not 4.** §9.10's mitigation continues to hold: verify by assembly + test count. The count rose 181 → 193, which is the evidence.
7. **Firmware confirmation of the 25/73 quirk** (§4) — still never done.
8. **Physical tilt to settle the Pitch/Roll labels** (§5, Phase 6a) — still never done.
9. **Reference screenshot before Phase 5** (§4) — still none, and Phase 5 is now next.
10. **§9.8's empty `EchoEnvironment/Math/` directory** — manual `rmdir EchoEnvironment\Math`, still not done.

---

## 12. Phase 5 record — **in progress**: first half of the move done, gate **not closed** ⚠️

**Build:** Debug, whole solution, **0 errors / 0 warnings** across **five** projects.
**Tests:** **196 total, 196 passed, 0 failed, 0 skipped** (was 193; **+3 new guards**).
**Gate:** **NOT closed.** §2 Phase 5's gate is "pixel-compare the rendered scene against a pre-refactor screenshot." The baseline now exists (§12.1); **no comparison has been performed**, because no tool available here can capture or diff a window. What commit 1 offers instead is a diff that can be *read*: every removed method body reappears verbatim in its new file, so the change is checkable as a relocation rather than trusted as one. Running the app against §12.1 is the user's step and is **still open**.

§9.10's evidence rule was applied rather than the overview: the count rose **193 → 196**, exactly the three guards added, and `LoadSolution` then reported **5** projects including `EchoEnvironment.Rendering`. The reload was performed first and the count checked, per §8's refined procedure — no **Add → Existing Project…**, no manual restore. §11.8 item 6 is closed: the overview is accurate after a reload, as §9.10 predicted.

### 12.1 The reference screenshot exists — captured **before** Phase 5, as §4 and §2 both demanded

Provided by the user as a window capture. No tool here can write image bytes, so the **PNG itself is the user's to save** (suggested `docs/baseline/pre-phase5.png`); what is recorded here is the state it shows, which is the half a pixel comparison needs in order to be interpretable.

| Control | Value in the baseline |
|---|---|
| Connection | **Disconnected** (`Connect` button shown, not `Disconnect`) |
| Port / Baud | `COM12` / `115200` |
| Show grid | **off** |
| Keep map | **off** |
| Render | **60 Hz** |
| Left 3D view | Empty except one small **orange** sensor marker near centre |
| Right panel | `Zone distances (mm)` heading, heat grid **empty** |
| Board view | Green board plane, **red X**, **green Y (up)**, **cyan Z (into −Z)**, with matching letter labels |
| Status line | `Orientation: no IMU data` |

Two things this baseline is *not*: it is not a connected scene, so it cannot validate zone colours, lines, map accumulation or the grid; and it is not a substitute for the §11.8 item 3 latency measurement. A comparison against it proves the disconnected scene is unchanged — which is exactly what commit 1 claims, and nothing more.

### 12.2 What commit 1 created

| File | Lines | Contents |
|---|---|---|
| `EchoEnvironment.Rendering/EchoEnvironment.Rendering.csproj` | 21 | `net10.0-windows` + `UseWPF`, references **Core only** |
| `EchoEnvironment.Rendering/MeshFactory.cs` | 174 | 8 static factories from the plan's row 1, bodies verbatim |
| `EchoEnvironment.Rendering/ScenePrimitives.cs` | 104 | `AddLine`, `AddAxes`, `CreateAxisLabel`, `CreateWorldAxes`, bodies verbatim |
| `EchoEnvironment.Rendering/WpfMath.cs` | 50 | The adapter, moved from the App; `+ ToColor(this in Rgb)` |
| `EchoEnvironment.Core/ColorScale.cs` | 37 | `Rgb` record struct + `ColorScale.For(mm, maxDistance)` |
| `EchoEnvironment.Tests/ArchitectureInvariantTests.cs` | 252 → **381** | `WpfConversionIsConfinedToOneType` **retargeted and its rule rewritten**; 3 new guards; `HasCoreSignature` / `IsCoreAssembly` helpers |
| `EchoEnvironment/WpfMath.cs` | **deleted** (38) | Superseded by the Rendering copy |
| `EchoEnvironment/MainWindow.xaml.cs` | **827 → 595** (−232) | 12 static members deleted, call sites rewired |

`MainWindow.xaml.cs` is now **595 lines** — the largest single reduction the migration has produced, and the first movement *toward* §5's "< 200 lines" criterion. It is still ~3× that target; the rest is Phase 6b.

> **Two of the line counts in the table above were wrong when first written** (388 and 593; the files are 381 and 595). They came from adding the edit tool's per-call line deltas instead of measuring the files. Corrected against a direct line count. The commit message for `bce4b00` still says 593 — it is not amended, because amending would rewrite a published-in-history commit, and the error is a line count rather than a claim about behaviour.

### 12.3 Three corrections to the plan's own text — one of them would not have compiled

1. **§2 Phase 5's mapping table cannot be implemented as written.** It maps `DistanceColor` to `Core/ColorScale.cs` with the parenthetical "pure math — belongs in Core so it's testable." The method's signature is `private static Color DistanceColor(double mm)`, and `System.Windows.Media.Color` lives in **PresentationCore**. Moving it verbatim would have broken `CoreAssemblyReferencesNoWpfAssembly` and, because Core is plain `net10.0` with no `UseWPF`, would not have compiled at all. The plan is right about the *intent* and silent about the *mechanism*: the arithmetic is Core's, the `Color` construction is WPF's. Split accordingly — `ColorScale.For` returns a WPF-free `Rgb`, and the conversion to `Color` is `WpfMath.ToColor`, which keeps the single-boundary invariant instead of creating a second conversion point in `MainWindow`. `ColorScaleRampIsInCore_AndIsWpfFree` pins the split so the ramp cannot drift back.
2. **§2 5e's stated reason for retargeting the guard is wrong, and the truth is more favourable.** It predicts that leaving the guard alone makes it "keep inspecting the App assembly, find no WPF-typed public types there, and pass for the wrong reason." It does not. The old body asserted `Assert.Contains(typeof(WpfMath), appTypes)`, and `WpfMath` left the App assembly in this commit, so the untouched guard **fails loudly**, not silently. The retarget is still required — for a different reason: the rule the guard was meant to state was never stated by it.
3. **§2 5e's path `Rendering/Math/WpfMath.cs` reintroduces a trap the plan itself documented.** §9.2 deviation 3 exists because a namespace named `Math` shadows `System.Math` in every file importing it. A namespace following that folder would be `EchoEnvironment.Rendering.Math`, in the assembly with the most `Math.Abs` / `Math.Clamp` / `Math.PI` calls in the codebase. The file sits flat at `Rendering/WpfMath.cs`, namespace `EchoEnvironment.Rendering`. The deviation is recorded in the file's own doc comment so the folder is not "helpfully" recreated later.

### 12.4 The guard's rule had to change, not just its target

The old rule — *only `WpfMath` and UI types may expose WPF types* — cannot survive Phase 5, because **Rendering's entire job is exposing WPF types**. `MeshFactory.CreateCubeMesh` returns `MeshGeometry3D`; that is the design working, not a hole. What has to stay confined is the **conversion**: a public signature naming Core and WPF in the same breath.

The retargeted guard therefore scans **App + Rendering**, and fails on any public type that speaks *both* unless it is `WpfMath` or a `Window`/`Application`. `HasWpfSignature` alone could not express that; `HasCoreSignature` was added so the two together state the Phase 5 rule.

**A defect in my own new guard, caught by reading before it ran.** The first version exempted UI types but **not `WpfMath` itself** — which speaks Core and WPF by definition, so the guard would have failed on the very type it exists to protect. It never reached the build. §4's High/High discipline ("what would have to change for it to fail?") is what caught it; the 196-green run is not the evidence that the guard is sound.

### 12.5 Three new guards, and what each would have to change to fail

| Guard | Fails if |
|---|---|
| `RenderingAssemblyReferencesCoreAndNoOtherSolutionAssembly` | Rendering reaches into the App, Tests or Hardware — the edge §1's dependency graph forbids and nothing in the graph prevents; **or** Rendering stops referencing `PresentationCore`, i.e. someone re-declares it plain `net10.0` and every factory stops compiling. |
| `CoreAssemblyDoesNotReferenceRendering` | Core acquires Rendering — the cause of the WPF leak that `CoreAssemblyReferencesNoWpfAssembly` only detects as a symptom. |
| `ColorScaleRampIsInCore_AndIsWpfFree` | `ColorScale.For` returns a WPF type or takes one as a parameter — i.e. the ramp stops being testable headlessly, which is the only reason it was moved. |

Each asserts a positive half as well as a prohibition, per §4's rule that a one-directional check passes happily under the drift it exists to catch.

### 12.6 Behaviour deliberately preserved (this commit is a move, not a fix)

- **Every geometry body is verbatim** — triangle winding, `Freeze()` placement, magic sizes (`0.02`, `3.98, 0.5`, `0.0005`, `0.06`, `4.0`, `1.5`), the sphere's 12×24 tessellation, the cube's 12-triangle order. A visual difference after this commit is evidence of a broken move, not of a design change.
- **`DistanceColor`'s arithmetic is unchanged element-for-element**; only the return type moved. `MaxDistanceMm` is now passed explicitly at all three call sites, which is the same value the ramp was always calibrated to.
- **`AddAxes` and `CreateWorldAxes` were NOT reconciled.** D9 is defect 5d, and relabelling axes changes pixels. This commit is the one the pixel comparison is supposed to prove neutral, so the two disagreeing axis sets were moved as-is.
- **The positional layer rule is still positional.** `ShowGrid_Click` still inserts at `Children.Count - 1` and the constructor still appends the reference sphere "last". 5c replaces that; until then a comment records why the order is not free.
- **`_orientation` is still a shared mutable `MatrixTransform3D` embedded in two visual trees**, still unfrozen — §4's Medium/Medium row is untouched, because `OrbitCameraController` does not exist yet.

### 12.7 The commit split, and where it deviates from the agreed plan

The agreed split was three commits: **move** / **5a+5b allocation** / **5c+5d layer order + axis convention**, so a visual regression is attributable to exactly one change. That split is being honoured, with one honest adjustment: **the move itself is larger than one commit**, because the plan's mapping table contains two very different kinds of member.

- **Commit 1 (this one):** the 12 **static, stateless** members, `WpfMath`, and the `DistanceColor` split. Checkable by reading the diff, because there is no state to preserve.
- **Commit 2 (still to do):** the **stateful** members — `UpdateFrame`, `UpdateHeatmap`, `EnsureZoneModels`, `AddToMap`, `RenderMapIfDue` → `Rendering/ZoneRenderer.cs` + `Core/WorldMap.cs`, and `CreateViewTransform` / `UpdateCamera` / `RotateView` / the `View_*` handlers / `_camYaw` `_camPitch` `_camDistance` `_viewRotation` `_lastMouse` `_dragging` → `App/OrbitCameraController.cs`. These carry mutable state across frames, so "verbatim" is not sufficient evidence for them and they deserve their own revertable step.

Splitting a move is not the same as splitting a behaviour change, so §3 rule 2 is not bent here: no correctness fix is bundled into either commit.

### 12.8 Known gaps and open obligations carried forward

1. ⚠️ **The pixel comparison has not been done.** §12.1 is the baseline; the comparison is a human step. Until it happens, Phase 5's gate stays open and commit 1 is *unverified visually* — the strongest claim available from the automated evidence is "the build is clean and the diff is a relocation."
2. ⚠️ **The app has not been run since this change.** §11.8 item 1 was closed by the user's run *before* Phase 5; that run predates commit 1, so the obligation reopens immediately.
3. ⚠️ **Release build still unverified** — now for five projects and three phases of changes. §11.8 item 2, carried from §10.
4. ⚠️ **`ToArray()` copies are still in `RenderFrame`,** and §10.2's "there is now something to delete in Phase 5" — the `out int[]` wrapper — **has not been deleted.** Both are Phase 5 work that commit 1 did not reach.
5. ⚠️ **`MainWindow`'s command handlers are still covered by no test.** Partly narrowed by 12.9: the render path it used to own (`UpdateFrame`, `UpdateHeatmap`, `EnsureZoneModels`, `AddToMap`, `RenderMapIfDue`) is now covered by `ZoneRendererTests`, and the camera arithmetic by `MainWindowNoLongerOwnsCameraOrZoneState`. Still uncovered: `Connect_Click`, `Disconnect`, `RefreshPorts`, the `DispatcherTimer`, and `RenderFrame`'s status-line formatting. §10.7 / §11.6 otherwise unchanged.
6. ⚠️ **D5 and D7 remain unfixed**, still owed as their own commits (§3 rule 2).
7. ⚠️ **Latency at 115200 baud unmeasured** (§11.8 item 3), **no real serial log** for the `ReplaySensorStream` parity check (§11.8 item 4), **firmware 25/73 confirmation** never done, **physical tilt** never done.
8. ⚠️ **`EchoEnvironment\Math\` still exists and is empty** — manual `rmdir`, invisible to git and to solution-file listings (§9.8).
9. **`MainWindow.xaml.cs` is 389 lines** (595 after 12.2's commit 1, 827 before Phase 5), not < 200. Phase 6b.
10. **`TreatWarningsAsErrors` still has not bitten at Phase 5.** §7.3 predicted it would; five projects built clean, 0 warnings, including the new one. The prediction remains untested rather than disproved — it may still bite when XAML is touched in 5c.

---

## 12.9 Phase 5 commit 2 — the stateful members moved, and four findings

Commit 2 moved the members that carry state across frames: `UpdateFrame`, `UpdateHeatmap`,
`EnsureZoneModels`, `AddToMap`, `RenderMapIfDue` → `Rendering/ZoneRenderer.cs` + `Core/WorldMap.cs`;
`CreateViewTransform`, `UpdateCamera`, `RotateView`, the camera state and the `View_*` handlers →
`App/OrbitCameraController.cs`.

| File | Lines | Contents |
|---|---|---|
| `EchoEnvironment.Core/WorldMap.cs` | 76 | `Add` / `Clear` / `Entries` / `Count`, from `AddToMap`; `Vec3` not `Point3D` |
| `EchoEnvironment.Rendering/ZoneRenderer.cs` | 261 | `UpdateFrame`, `UpdateHeatmap`, `EnsureZoneModels`, `RenderMapIfDue`, `ClearMap` |
| `EchoEnvironment/OrbitCameraController.cs` | 170 | camera state, `UpdateCamera`, `Nudge*`, `Zoom`, `Reset`, `RotateScene`, drag, `CreateViewTransform` |
| `EchoEnvironment.Tests/WorldMapTests.cs` | 210 | 10 tests — the first tests ever written against this arithmetic |
| `EchoEnvironment.Tests/ZoneRendererTests.cs` | 445 | 13 tests, all on STA threads |
| `EchoEnvironment.Tests/StaRunner.cs` | 65 | STA-thread helper; see finding 3 |
| `EchoEnvironment.Tests/ArchitectureInvariantTests.cs` | 381 → **486** | 3 new guards; `HasWpfSignature` / `HasCoreSignature` **fixed** (finding 2) |
| `EchoEnvironment/MainWindow.xaml.cs` | **595 → 389** (−206) | 13 fields + 1 constant + 8 methods deleted, call sites rewired, one unused import dropped |

`MainWindow.xaml.cs` is 827 → 595 → **389** across the two commits. Still ~2× §5's target; the rest is 6b.

### Finding 1 — the plan's field names were wrong, and the guard now pins the real ones

§5's table names the camera state `_yaw/_pitch/_zoom`. The fields `MainWindow` actually held were
`_camYaw`, `_camPitch`, `_camDistance`, `_viewRotation`, `_lastMouse`, `_dragging` — **six**, not three —
plus five more on the zone/map side (`_map`, `_mapGroup`, `_mapModels`, `_lastMapRender`, `_zoneModels`).
`MainWindowNoLongerOwnsCameraOrZoneState` asserts all eleven are absent **by name**, and asserts that
`_orbit` and `_zones` exist, so the guard cannot pass by deleting the state along with the thing meant
to hold it.

A name check rather than a count: a count would keep passing while a field crept back under a new name
with the behaviour still attached.

### Finding 2 — the confined-conversion guard could not see constructors

`ZoneRendererDoesNotExposeCoreTypes` failed on its own **positive** half first: `HasWpfSignature`
reported that `ZoneRenderer` exposes no WPF types. It does not — none of its public methods return or
take one. The three WPF types it needs arrive through its **constructor**, and neither
`HasWpfSignature` nor `HasCoreSignature` had ever inspected constructors.

So a type constructed from `Model3DGroup`, `Model3DGroup` and `UniformGrid` was invisible to the guard
whose job is noticing WPF in public surface. Both helpers now inspect public instance constructors.
This is a hole in a guard written in commit 1, found by a new test failing for the right reason.

### Finding 3 — `ZoneRenderer` mixes an apartment-free job with a Dispatcher-affine one

All 13 `ZoneRenderer` tests failed identically on the first run, at `new UniformGrid()`:

> `System.InvalidOperationException : The calling thread must be STA, because many UI components require this.`
> `at System.Windows.Input.InputManager..ctor()` → `FrameworkElement..ctor()` → `Controls.Panel..ctor()`

Nothing else in the type needs an apartment: `Model3DGroup`, `GeometryModel3D`, `MeshGeometry3D` and the
brushes all construct on a pooled thread. What needs one is the **2D heat grid** — a `FrameworkElement`.

That is evidence about the design, not about the tests: `ZoneRenderer` holds two responsibilities, and
the smaller one (the right-panel heat grid) is what forces every test of the larger one (3D zone
geometry) onto a dedicated STA thread. Splitting `HeatGrid` out would let most of the renderer be tested
on the thread pool. **Not done in this commit** — commit 2 is a move, and separating two responsibilities
is a design change that owes its own step. Recorded as the strongest candidate for 6b.

`StaRunner` is the cost of not splitting yet. Its doc comment records the stack trace, because the trace
is the finding.

### Finding 4 — promoting `AddToMap` to public Core API made two unreachable things reachable

Both were unreachable *as a private method of a Window* and are still unreachable *from the app*; both
are now pinned because `WorldMap.Add` is public API a future caller can misuse.

- **Divide by zero.** `asin(Y / r)` with `r == 0` produced `(int)Round(Asin(NaN))` — a valid-looking key
  computed from a non-number. `MainWindow` only ever reached this with `mm >= MinDistanceMm`, so `r` was
  always positive. `Add` now throws `ArgumentException` for a zero-length position.
- **Signed zero in the azimuth.** For a point on the Y axis, `atan2(world.X, -world.Z)` is
  `atan2(+0.0, -0.0)`, which IEEE 754 defines as **+π**. So a vertical reading is filed at azimuth 180,
  not 0, and `+0.0` versus `-0.0` in `X` puts the same direction in two different cells.
  `VerticalDirectionSplitsOnTheSignOfZeroInX` pins this rather than leaving it to be discovered later.
  `UpdateFrame` only ever produces elevations of ±19.6875° (8×8 fan at 45° FOV), so `world.Z` is never
  exactly zero in production.

### The design decision that the guard exists to protect

`ZoneRenderer` owns a `WorldMap` (Core) in a **private** field and never names one in its public
surface. Exposing it — a `Map` property, or an `AddReading` returning a bucket — would put a Core type
and a WPF type in the same public signature, which is exactly what `WpfConversionIsConfinedToOneType`
calls a conversion point, and that guard exempts only `WpfMath` and UI types.

The alternative was to widen the exemption. It was not taken: `MainWindow` calls `ClearMap()` and never
sees a `WorldMap`. `ZoneRendererDoesNotExposeCoreTypes` is the record that the invariant survived by
changing the design rather than by relaxing the rule, and its failure message says so, so the next
person who needs the map does not read the rule as the obstacle.

`keepMap` became a **parameter** of `UpdateFrame` for the same kind of reason: reading
`KeepMapCheckBox.IsChecked` inside the renderer would have made a rendering class depend on the window's
XAML element names, which is worse coupling than the one it removes.

### Evidence

Build: Debug, whole solution, **0 errors / 0 warnings** across 5 projects.
Tests: **222 total, 222 passed, 0 failed, 0 skipped** (was 196; +10 `WorldMapTests`, +13
`ZoneRendererTests`, +3 guards).

Two compile errors were found and fixed before any test could run: `Geometry3D` is the abstract base and
has no `Positions` (it is on `MeshGeometry3D`), and `UniformGrid.Children` yields `UIElement`, which has
no `Child` — the cast must go through `Border`. Both were mine, both were in new test code.

`MainWindow` also lost `using System.Windows.Media;`: after the move the window touches no brush,
material or colour, so the import became unused. Verified by listing every `System.Windows.Media` type
name in the file — zero occurrences. (`System.Windows.Media.Media3D` is a separate namespace and is
still needed.)

### Gate status

**Still open, and now open against a second commit.** §12.1's baseline predates commit 1. The user's
run after commit 1 was reported as "seems to run fine" — a human eyeball check, not the pixel diff §5
names, and it is unknown whether the grid/map toggles were exercised. **No run has happened since commit
2**, which moved the camera drag, the key handler, the zone geometry, the heat grid and the map. Those
are the exact things a visual check is for, and they are now the most exposed code in the migration.
