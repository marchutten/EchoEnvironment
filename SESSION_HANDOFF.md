# Session handoff — read this first

Snapshot taken at end of session, HEAD `74e9a55` (the serial-capture commit, §12.11). Everything below was verified
against the repo at that point. Anything marked **UNVERIFIED** is a claim, not a fact.

## Where the work stands

| | |
|---|---|
| Branch | `main`, **ahead 23 / behind 0** of `origin/main`. **Not pushed.** |
| Working tree | clean |
| Last commit | `74e9a55` serial capture (§12.11) — `RawTextCapture`, `Capture log` checkbox |
| Last code commit before it | `321c7bc` Phase 5 commit 2 |
| Build | Debug, whole solution, **0 errors / 0 warnings**, 5 projects |
| Tests | **248 passed / 0 failed / 0 skipped** (26 new) |
| `ArchitectureInvariantTests` | 15 / 15 |
| `MainWindow.xaml.cs` | **481 lines** (827 at the start of Phase 5) |
| Plan of record | `MIGRATION_PLAN.md`, ends at §12.11 |

## Phase 5 status

- **5a — done** (`bce4b00`). `EchoEnvironment.Rendering` created; 12 static members moved.
- **5b — done** (`321c7bc`). `Core/WorldMap.cs`, `Rendering/ZoneRenderer.cs`, `App/OrbitCameraController.cs`.
- **Interposed — serial capture** (§12.11). Not a 5x step; inserted because §12.10 Finding 8 showed the
  capture obligation was structurally impossible, and the hardware was already connected.
- **5c — NOT STARTED. This is the next unit of work.**
- **5d — not started** (defect D9, axis conventions).

## 5c scope, as agreed

Behavioural, not structural: brush reuse, transform reuse, named layers, axis relabelling. **These change
pixels.**

## The gate: open in letter, changed in kind

The §12.1 pixel diff has **still not been done**. It cannot be done against the connected capture — §12.1's
baseline is a *disconnected* scene and says so at line 1044. What §12.10 closed instead: commit 5b's
checkbox→renderer→map rewiring is demonstrated working on live hardware.

## Now possible, and the next human step

**Tick `Capture log`, connect, run, and save the file.** That closes an obligation carried since Phase 3
(§2 line 371, §4 line 544, §10 line 859, §10.9 line 931, §11.8 line 1010, §12.8 line 1112).

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

`docs/baseline/` exists and `pre-phase5-connected.png` is committed at `a995af9` (84,855 bytes) — but it was
captured **before** `74e9a55`, so it shows no `Capture log` control. It is valid as a scene reference for Phase 5
commit 2 and is not valid for a toolbar comparison. The disconnected §12.1 image is **still missing**.

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

## UNVERIFIED — do not treat as done

- **5a and 5b are not visually verified.** A crash was reported after 5a and never attributed. 5b moved camera
  drag, key handlers and zone geometry.
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
