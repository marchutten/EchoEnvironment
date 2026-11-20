# Session handoff — read this first

Snapshot taken at end of session, HEAD `5bc2814` (Phase 5d, §12.12). Everything below was verified against the
repo at that point. Anything marked **UNVERIFIED** is a claim, not a fact.

## Where the work stands

| | |
|---|---|
| Branch | `main`, **ahead 28 / behind 0** of `origin/main`. **Not pushed.** |
| Working tree | clean apart from untracked `.mcp/` and `docs/baseline/pre-phase5e.png` |
| Last commit | `5bc2814` Phase 5d (§12.12) — `AxisConvention`, defect D9 |
| Commits since the last handoff | `f15ae33` captured fixture · `95638ec` 5a/5b (D3) · `75b0179` 5c (D8) · `5bc2814` 5d (D9) |
| Build | Debug, whole solution, **0 errors / 0 warnings**, 5 projects |
| Tests | **290 passed / 0 failed / 0 skipped** (was 248) |
| `ArchitectureInvariantTests` | **19 / 19** (was 15) |
| `MainWindow.xaml.cs` | **511 lines** ⚠️ — was **481** at the last handoff, 827 at the start of Phase 5. **It grew by 30 during a phase whose goal is to thin it.** Unexplained; no test accounts for it. |
| Plan of record | `MIGRATION_PLAN.md`, ends at §12.12 |

## Phase 5 status

- **5a — done** (`bce4b00`). `EchoEnvironment.Rendering` created; 12 static members moved.
- **5b — done** (`321c7bc`). `Core/WorldMap.cs`, `Rendering/ZoneRenderer.cs`, `App/OrbitCameraController.cs`.
- **Interposed — serial capture** (`74e9a55`, §12.11) and **captured fixture** (`f15ae33`). Not 5x steps; inserted because §12.10 Finding 8 showed the capture obligation was structurally impossible, and the hardware was already connected.
- **5a/5b brush and transform reuse — done** (`95638ec`, defect D3). ~128 `SolidColorBrush` + 64 `TranslateTransform3D` allocations per frame replaced with cached, mutated instances.
- **5c — done** (`75b0179`, defect D8). `SceneLayers`: named containers replace the `(Model3DGroup)SensorVisual.Content` cast and `Children.Count - 1` positional insertion.
- **5d — done** (`5bc2814`, defect D9). `AxisConvention`: one letter → axis → colour mapping; both gizmos loop over it. **D9 turned out to be two defects — see §12.12.**
- **5e — NOT STARTED. This is the next unit of work.**

## 5e scope, as agreed

Behavioural, not structural: remove the `ToArray()` copies in `RenderFrame` and the `out int[]` parser wrapper
(§12.8 item 4). The wrapper has **zero production call sites**; deleting it touches 29 test call sites and
nothing else. Unlike 5a–5d this should change **no pixels**, which makes it the safe commit to land after a
human has looked at 5d's axis change.

## The gate: open in letter, and now open against seven commits

The §12.1 pixel diff has **still not been done**. It cannot be done against the connected capture — §12.1's
baseline is a *disconnected* scene and says so in its own closing paragraph. What §12.10 closed instead: commit 5b's
checkbox→renderer→map rewiring is demonstrated working on live hardware.

**What changed since then:** 5a/5b, 5c and 5d have all landed, and all three change pixels — brushes and
materials, layer order, and axis labels. The gate was always going to be read against those changes; it is now
the only remaining evidence for them, and no automated test can substitute for it. §12.12 records three
problems that must be resolved **before** a capture is taken, not after.

## Now possible, and the next human step

**Closed since the last handoff:** a real capture has been taken and committed as a test fixture (`f15ae33`,
257 tests at that point). The obligation carried since Phase 3 (§2, §4, §10, §10.9, §11.8 and §12.8 each repeat
it) is discharged.

**The next human step is the one no tool here can do: take the §12.1 gate screenshot.** Before taking it,
resolve the three problems §12.12 lists — which `Show grid` value the gate image uses, and the fact that §12.1's
description of the board view describes the pre-5d scene. Capturing first and discovering the disagreement after
produces an image that cannot be interpreted.

- Captures land in `bin\<configuration>\net10.0-windows\captures\`, named
  `<port>-<baud>-<timestamp>.txt`. That folder is **inside `bin/`, so it is gitignored** — a capture is not
  committed by accident, and it is also not preserved unless deliberately copied somewhere durable.
- The status line shows the path during the session and repeats it with a character count on disconnect.
- **Account for one known divergence:** a session interrupted mid-frame produces a final frame on replay that
  the app never drew. `LineFramer` documents why — a port has no end to drain at. Pinned by
  `CapturedText_TrailingFrameWithoutNewline_ExistsOnReplayButNeverReachedTheLiveApp`. Every capture-vs-replay
  comparison must expect it.

**Baseline images must be captured from a build that includes the `Capture log` checkbox** (§12.11 Finding 13).
A baseline taken before the checkbox exists differs from every later screenshot by a control that is not a
regression.

`docs/baseline/` holds `pre-phase5-connected.png` (committed at `a995af9`, 84,855 bytes, **1186 × 748 px**) and
the new `pre-phase5e.png` (**1192 × 838 px**). The connected image was captured **before** `74e9a55`, so it shows
no `Capture log` control: valid as a scene reference for Phase 5 commit 2, not valid for a toolbar comparison.
`pre-phase5e.png` records **no control state**, so it is not yet evidence for anything, and its `pre-` prefix
means "before 5e" — not "before Phase 5". The disconnected §12.1 gate image is **still missing**, and §12.1's own
Board view row now describes the **pre-5d** scene, i.e. it records defect D9 as if it were the expected
appearance. See `docs/baseline/README.md`.

## Outstanding, unchanged

- **Release build never run**: `dotnet build EchoEnvironment.slnx -c Release`. Attempted at this commit and
  **not closed** — the tooling available cannot select a build configuration, so this must be run by hand.
- `EchoEnvironment\Math` is an **empty directory**, still present. `rmdir` outstanding.
- Latency at 115200 baud untested; D5/D7 fixes untested on hardware.
- **All commits are unsigned.** `GIT_SIGN_COMMITS` is enabled but gpg fails on every commit:
  `skipped "Marc <marc@hutten.name>": No secret key`. Machine configuration, not a repo problem, and it has
  been failing silently the whole migration.
- Status line is clipped at the current window width (§12.10 Finding 9), and the capture path is the longest
  string it ever produces.

## New findings recorded in §12.11

- **Finding 10** — capture and replay legitimately disagree on a trailing unterminated frame. A property of
  the parity check, not a bug.
- **Finding 11** — `LineFramer` is not thread-safe and `SerialPort.DataReceived` is not documented to be
  serialised. Latent since Phase 3; the capture made it consequential. **Not fixed here** — fixing it would be
  a behaviour change in a commit whose job is capture.
- **Finding 12** — the status line races with itself: `Open()` raises its message via `Dispatcher.BeginInvoke`,
  which lands after anything `Connect_Click` sets synchronously.
- **Finding 13** — this commit adds a toolbar control, so it invalidates both baseline images.

## New findings recorded in §12.12

- **Finding 23** — the negative control reused `AddAxes`' interleaved layout against the legacy gizmo's grouped
  layout and read an **arm** where it expected a **label**. A test that assumes a scene graph's ordering inspects
  the wrong object.
- **Finding 24** — compared a unit vector (component 1) against a measured arm end (0.5). The gizmo was right;
  the assertion was wrong. The load-bearing content is sign and axis, not magnitude.
- **Finding 25** — a measurement helper asserted eight mesh corners and failed on axis labels, which are
  four-corner quads.
- **Finding 26** — `Vector3D` has no indexer (three CS0021).
- **Finding 27** — `set_file_project_status` injected a `<Compile Include>` for a **test** file into
  `EchoEnvironment.Rendering.csproj`. Reverted. `EchoEnvironment.Tests` is SDK-style with implicit globs: **new
  test files need no project registration at all.**

## UNVERIFIED — do not treat as done

- **No commit since 5a has been visually verified.** 5a, 5b, 5c and 5d all change pixels. A crash was reported
  after 5a and never attributed. 5b moved camera drag, key handlers and zone geometry; 5c reordered the scene
  layers; 5d moved the Z axis arm from −Z to +Z and relabelled two arms.
- **`MainWindow.xaml.cs` grew 481 → 511** during Phase 5. No test accounts for the 30 lines and no finding
  explains them. This is a claim about the file, not about a regression: it is unattributed.
- **No real capture has been taken.** The capture code is tested against synthetic port-shaped chunks; parity
  with the actual device still needs a run with `Capture log` ticked.
- The angles in the §12.10 capture are **early-run values** (31 frames; `CalibrationSamples = 50`).
- Whether the filter classifies the captured board as at rest depends on the learned gyro bias — not established.

## Git protocol that worked

- Always pass `path: "C:\Projects\GitHub\EchoEnvironment"` explicitly.
- Restore files with `git_reset`, not by rewriting them.
- `git_checkout` target must be a ref name.
- Doc-only commits are fine and are how findings get preserved.
- **Order multiple edits to one file bottom-up, and take every line number from a single read.** Guessing
  positions caused an insert to land inside the wrong method and a test helper to be duplicated in a way that
  broke the build.

## Test-authoring lesson recorded in §12.11

A test that probes "unusable path" behaviour must point at something that **certainly exists**. Pointing at the
test class's own not-yet-created scratch folder made the constructor create a *file* named like the folder, which
then broke 17 unrelated tests and made the probe assert a rejection that never happened. One test, 19 failures.
