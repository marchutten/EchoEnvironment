# Session handoff — read this first

Snapshot taken at end of session, HEAD `cb3fbf2`. Everything below was verified against the repo at that
commit. Anything marked **UNVERIFIED** is a claim, not a fact.

## Where the work stands

| | |
|---|---|
| Branch | `main`, **ahead 20 / behind 0** of `origin/main`. **Not pushed.** |
| Working tree | clean |
| HEAD | `cb3fbf2` docs: record the first live-hardware run (12.10) |
| Last code commit | `321c7bc` Phase 5 commit 2 |
| Build | Debug, whole solution, **0 errors / 0 warnings**, 5 projects |
| Tests | **222 passed / 0 failed / 0 skipped** |
| `MainWindow.xaml.cs` | **389 lines** (was 827 at the start of Phase 5) |
| Plan of record | `MIGRATION_PLAN.md`, 1360 lines, ends at §12.10 |

Commits since Phase 4: `7f93030` Phase 4 → `bce4b00` Phase 5a → `d025422` docs → `321c7bc` Phase 5b →
`cb3fbf2` docs.

## Phase 5 status

- **5a — done** (`bce4b00`). New `EchoEnvironment.Rendering` project; 12 static members moved to
  `MeshFactory.cs`, `ScenePrimitives.cs`, `WpfMath.cs`.
- **5b — done** (`321c7bc`). `Core/WorldMap.cs` (76), `Rendering/ZoneRenderer.cs` (261),
  `App/OrbitCameraController.cs` (170). `MainWindow` no longer owns camera or zone state by field name.
- **5c — NOT STARTED. This is the next unit of work.**
- **5d — not started** (defect D9, axis conventions).

## 5c scope, as agreed

Behavioural, not structural: brush reuse, transform reuse, named layers, axis relabelling.
**These change pixels.** §12.10 records that 5d is the commit where the two axis conventions in the board
viewport visibly disagree, and that the connected capture already shows it.

## The gate: open in letter, changed in kind

The §12.1 pixel diff has **still not been done**, and it **cannot** be done against the connected capture —
§12.1's baseline is a *disconnected* scene and says so itself at line 1044. What §12.10 closed instead is
weaker but real: commit 5b's checkbox→renderer→map rewiring is demonstrated working on live hardware,
which no test in the suite covers.

## Two things to decide at the very start of the next session

1. **Save the connected capture as `docs/baseline/pre-phase5-connected.png`.**
   **`docs/` does not exist** — verified, not inferred (`Directory not found`). The folder must be created.
   No tool available here can write image bytes; this is a human step. It is the **only** connected-state
   reference that exists, and 5c/5d are about to change brushes, layers and axis labels — i.e. exactly what
   it shows.
2. **Insert a serial-capture commit before 5c, or go straight to 5c.**
   Recommendation was to insert it. Reason, verified by search across all four non-test projects: **no writer
   of any kind exists** — zero matches for `StreamWriter`, `TextWriter`, `FileStream`, `File.Create`,
   `File.OpenWrite`, `File.AppendAllText`, `SaveFileDialog`, `LogPath`. `SerialSensorStream` reads and
   discards; `ReplaySensorStream` can only read and `MainWindow` never constructs it. So "capture a real
   serial log" (carried since Phase 3, six times) is not merely undone — **it is impossible with the current
   code.** ~15 lines, changes no rendering. Cheap now because the hardware is already connected.

## Outstanding, unchanged

- **Release build never run**: `dotnet build EchoEnvironment.slnx -c Release`.
- **No baseline screenshot** at `docs/baseline/pre-phase5.png` (§12.1's own precondition).
- `EchoEnvironment\Math` is an **empty directory**, still present. `rmdir` outstanding.
- Latency at 115200 baud untested; D5/D7 fixes untested on hardware.
- **All 20 commits are unsigned.** `GIT_SIGN_COMMITS` is enabled but gpg fails:
  `skipped "Marc <marc@hutten.name>": No secret key`. Signing is a machine-configuration problem, not a
  repo problem, and it has been silently failing on every commit.

## New findings recorded in §12.10 (not yet acted on)

- **D1 now visible on hardware.** Readout `Pitch -149.7 / Roll -86.8`. `pitch = asin(-M32)` is bounded to
  ±90° and is printed under **Roll**; `roll = atan2(M12, M22)` is unbounded and is printed under **Pitch**.
  -149.7 cannot be a pitch, so the label is provably wrong from the capture alone. **Not closed**: the
  board's real pose is still unknown, and 6a's human half is confirming which label is wrong *in the user's
  terms*.
- **D9 visible, and not a Phase 5 regression.** `AddAxes` (labelled gizmo) is in `_boardGroup`, which carries
  `CreateViewTransform(true, _orientation)` and rotates with the sensor; `CreateWorldAxes` is in untransformed
  `boardRoot` and stays fixed. Two conventions in one viewport.
- **Device accel ≈ 8.9× the test fixture's scale** (4445 counts vs `IdealAccelCounts = 500.0`). Harmless
  today only because `OrientationFilter` normalizes and magnitude cancels. `StationaryThresholdCounts = 60`
  is in **raw counts**.
- **Status line is clipped** in the capture (`mag -2`) — the app's only diagnostic surface, last third
  unreadable at that window width.

## UNVERIFIED — do not treat as done

- 5a and 5b are **not visually verified**. A crash was reported after 5a's check and never attributed.
  5b moved camera drag, key handlers and zone geometry.
- The angles in the capture are **early-run values** (31 frames; `CalibrationSamples = 50`). Worth re-reading
  after a longer run.
- Whether the filter classifies the captured board as at rest depends on the learned gyro bias — not
  established.

## Git protocol that worked this session

- Always pass `path: "C:\Projects\GitHub\EchoEnvironment"` explicitly.
- Restore files with `git_reset`, not by rewriting them.
- `git_checkout` target must be a ref name.
- Doc-only commits are fine and are how findings get preserved; the plan document is the memory of this
  migration.
