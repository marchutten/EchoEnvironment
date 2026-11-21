# Session handoff — read this first

Snapshot taken at end of session. HEAD is `8ebfe6d` (§14, Phase 5 closure). Everything below was verified
against the repo at that point. Anything marked **UNVERIFIED** is a claim, not a fact.

## Where the work stands

| | |
|---|---|
| Branch | `main`, **not pushed**. `git_status` at the end of this session: **46 ahead**, **0 behind**, `isClean: true`. Run `git_status` rather than trusting this row — it drifts with every commit, and this document has been wrong about it before. |
| Working tree | clean |
| Last commits | `8ebfe6d` refactor — delete the `out int[]` parser wrapper (§12.8 item 4) · `1f84881` perf — read the frame's own storage instead of copying it per frame (§12.8 item 4) |
| Commits since the last handoff | `7ea6526` docs (5e reconciled, §12.13) · `218fab1` docs (§12.14 Board row re-recorded) · `7fd0efe` chore (.mcp ignored) · `406c2bf` 6a (D1) · `6152525` CI · `572da12` 6c · `a65203f` README · `bf85d0f` 7 (D4) · `1f84881` · `8ebfe6d` |
| Build | Debug, whole solution, **0 errors / 0 warnings**, 5 projects |
| Tests | **340 passed / 0 failed / 0 skipped** |
| `ArchitectureInvariantTests` | **19 / 19** |
| `SensorFrameParserTests` | **64 / 64** |
| `MainWindow.xaml.cs` | **633 lines** ⚠️ — was 511 at the last handoff, 827 before Phase 5. **It grew again, during a phase whose goal is to thin it.** Still unattributed; no test accounts for it. |
| Plan of record | `MIGRATION_PLAN.md`, ends at §14 |

## Phase 5 is CODE COMPLETE

- **5a — done** (`bce4b00`). `EchoEnvironment.Rendering` created; 12 static members moved.
- **5b — done** (`321c7bc`). `Core/WorldMap.cs`, `Rendering/ZoneRenderer.cs`, `App/OrbitCameraController.cs`.
- **Interposed — serial capture** (`74e9a55`, §12.11) and **captured fixture** (`f15ae33`).
- **5a/5b brush and transform reuse — done** (`95638ec`, defect D3).
- **5c — done** (`75b0179`, defect D8). `SceneLayers`: named containers replace the positional insertion.
- **5d — done** (`5bc2814`, defect D9). `AxisConvention`: one letter → axis → colour mapping.
- **5e — delivered inside 5a** (§12.13). Both named deliverables are at `bce4b00`.
- **§12.8 item 4 — done** (`1f84881`, `8ebfe6d`, §14). The `ToArray()` copies are gone; the `out int[]`
  parser wrapper is deleted. **This was the last code obligation Phase 5 carried.**

## What the two closure commits changed

**`1f84881`.** `OrientationFilter.Update` and `ZoneRenderer.UpdateFrame` / `UpdateHeatmap` take
`ReadOnlySpan<int>`. `RenderFrame` reads `frame.Imu.Span` and `frame.Distances.Span` in place. The status
line's `distances.Count(...)` / `distances.Max()` became one loop because a span is not `IEnumerable`.
No index, threshold, clamp or ordering changed. Three new guards in `FrameCoalescerTests`.

**`8ebfe6d`.** `SensorFrameParser.TryParse(string, out int[], out int[]?)` deleted. Call sites verified
before deleting: **zero production**, **28 in `SensorFrameParserTests`**. Those 28 keep their original
assertions and reach the parser through a private test-side adapter, `TryParseLegacy`.
`SensorFrameOverload_AcceptsAndRejectsExactlyWhatTheArrayOverloadDoes` — which can no longer mean anything
with one signature left — is replaced by `TheSingleOverload_AcceptsAndRejectsExactlyTheDocumentedShapes`.

## One test failed on the way, and the failure was the test's

`TheStatusLineLoopAgreesWithTheLinqItReplaced([])` threw `InvalidOperationException : Sequence contains no
elements` from `distances.Max()`. The empty case was **removed, not special-cased**: LINQ throws on an empty
sequence while the loop leaves `maxMm` at `int.MinValue`, so the two sides cannot both be evaluated, and the
input is unreachable — `IllegalValueCounts_AreRejected` pins that every token count except 16, 25, 64 and 73
is rejected. A comment in that test first cited a test name that **does not exist** in the suite; it was
corrected to name the two real ones. **Check that a test exists before citing it in a comment.**

## The gate: still open, and it is now the ONLY thing Phase 5 owes

The §12.1 pixel diff has **still never been run**. No tool here can capture or diff a window. §12.14
re-recorded §12.1's Board view row from a current build, so the comparison now has a target worth comparing
against — previously a diff against §12.1 would have been scored against a description of a convention rather
than a description of a picture.

**The human step, in order:**

1. **Capture `docs/baseline/pre-phase5.png`** — disconnected, `COM12` / `115200`, `Show grid` **off**,
   `Keep map` **off**, `Render` **60 Hz**, from a build that has the `Capture log` checkbox (§12.11 Finding
   13), at a **recorded window size**. Use §12.1's control table: it is the agreed state (Finding 30).
2. **Diff it against §12.1 + §12.14.** §12.14's Board row is the current expectation; §12.1's old Board row
   is not. Heed §12.14's two warnings: the board slab reads as a **foreshortened sliver**, not a plane, and
   one viewport contains **two directions for the same letter** (the transformed gizmo against the
   untransformed reference axes). Neither is a regression.
3. **Re-capture the connected pair at ONE window size.** `pre-phase5-connected.png` is 1186 × 748,
   `post-phase5d-connected.png` is 1192 × 838. A pixel diff is meaningless on dimensions alone.
4. **Run the app** (§12.8 item 2) — no run has happened since 5a.

## Outstanding, unchanged

- **Release build never run**: `dotnet build EchoEnvironment.slnx -c Release`. Attempted again at this commit
  and **still not closed** — the build tool exposes no configuration parameter. Evidence it is genuinely open:
  the only Release artifacts on disk belong to `EchoEnvironment.Core`, `EchoEnvironment` and
  `EchoEnvironment.Tests`, all stamped `1.0.0+8254fe8…` (Phase 2). **`EchoEnvironment.Hardware` and
  `EchoEnvironment.Rendering` have no Release output at all.**
- ~~`EchoEnvironment\Math` empty directory~~ — **CLOSED.** `rmdir` done, verified by direct listing:
  `EchoEnvironment` now lists nine entries, all files, no subdirectories. Git never saw the directory, so git
  could not have confirmed this either way.
- Latency at 115200 baud untested; D5/D7 fixes untested on hardware.
- **All commits are unsigned.** `GIT_SIGN_COMMITS` is enabled but gpg fails on every commit:
  `skipped "Marc <marc@hutten.name>": No secret key`. Machine configuration, not a repo problem.
- Status line is clipped at the current window width (§12.10 Finding 9).

## UNVERIFIED — do not treat as done

- **No commit since 5a has been visually verified.** 5a, 5b, 5c and 5d all change pixels. A crash was reported
  after 5a and never attributed. `1f84881` and `8ebfe6d` are claimed to change **no pixels** — the span change
  touches only how values are carried, and the deleted wrapper had no production caller — but that claim has
  not been checked against a screenshot either.
- **`MainWindow.xaml.cs` grew 511 → 633.** Unattributed. A claim about the file, not about a regression.
- Capture/replay parity has not been compared frame-by-frame. The fixture exists (`f15ae33`); the comparison
  does not. Expect the documented trailing-frame divergence (§12.11 Finding 10).
- The angles in the §12.10 capture are **early-run values** (31 frames; `CalibrationSamples = 50`).

## A documentation defect found while closing Phase 5

`MIGRATION_PLAN.md`'s header said **"6–7 pending"**. It is not accurate, and §14 now records the correction.
Verified against the tree:

| | |
|---|---|
| 6c composition root | **EXISTS** — `App.xaml.cs` `OnStartup`, `new MainWindow(source!)` |
| `StartupUri` in `App.xaml` | **REMOVED** at 6c |
| `--sim` / `--replay` | **EXISTS** — `AppLaunchOptions.cs` |
| Phase 7 CI | **EXISTS** — `.github/workflows/build-and-test.yml` |
| D4 dirty-bucket rebuild | **EXISTS** — `WorldMap._dirtyBuckets` / `TakeDirtyBuckets`, consumed by `ZoneRenderer` |
| **6b `MainViewModel`** | **DOES NOT EXIST** — no such type in the solution |
| `MainWindow` view-model ctor | **DOES NOT EXIST** — takes a `SensorSource` (`MainWindow.xaml.cs:96`) |
| `SetOrientation` public on a `Window` | **STILL PUBLIC** (`MainWindow.xaml.cs:205`) |
| Phase 7 `src/` + `tests/` relocation | not done |
| Phase 7 WPF 3D normals | not done |

Phase 5's row in §6's effort table is corrected. Rows 6 and 7 need the same treatment and were **left alone**
here: this session's job was closing Phase 5, not auditing Phase 6.

## Git protocol that worked

- Always pass `path: "C:\Projects\GitHub\EchoEnvironment"` explicitly.
- `git_checkout` target must be a ref name, never a number. Restore a file with `git_reset`, not `git_checkout`.
- Order multiple edits to one file **bottom-up**, all from a single read. This session's 27-edit batch to
  `SensorFrameParserTests.cs` applied cleanly on that rule.
- `replace_file_lines` reports `auto_corrected` / `applied_start_line` when a line shifted; read those fields
  instead of assuming the requested position.
- A `read_file_lines` result can carry a JSON artifact at the end of a line. Line 4 of `MIGRATION_PLAN.md` was
  quoted with a trailing `"` that is not in the file, and the first edit against it failed. Re-read before
  retrying rather than trimming by hand.
- Report tool output, not intent: `currentBranch`, `ahead`, `behind`, `isClean`, `filesChanged`.

## Next unit of work

**Not code.** The §12.1 gate screenshot, steps 1–4 above. Four Phase 5 commits change pixels and none of them
has ever been looked at.

After that: **Phase 6b** (`MainViewModel`, view-model-injected `MainWindow`, `SetOrientation` stops being
public), and **6a's physical tilt**, the one part of the D1 fix that needs hardware.
