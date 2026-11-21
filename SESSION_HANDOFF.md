# Session handoff — read this first

Snapshot taken at end of session. Everything below was verified against the repo at that point. Anything
marked **UNVERIFIED** is a claim, not a fact.

## Where the work stands

| | |
|---|---|
| Branch | `main`, **not pushed**. Run `git_status` for the current ahead/behind rather than trusting any number written here — this document has been wrong about it twice. |
| Working tree | clean |
| Last commit | 6b — `MainViewModel` extracted from `MainWindow` (§15) |
| Build | Debug, whole solution, **0 errors / 0 warnings**, 5 projects |
| Tests | **343 passed / 0 failed / 0 skipped** |
| `MainWindow.xaml.cs` | **449 lines** (216 code, 179 comment, 54 blank) — 827 before Phase 5, 633 after it, 449 after 6b. §5's "< 200" is **still not met**. |
| `MainViewModel.cs` | **439 lines**, new at 6b |
| Plan of record | `MIGRATION_PLAN.md`, ends at §15 |

## Phase 6 is CODE COMPLETE (6a, 6b, 6c)

- **6a — D1 label fix** (`406c2bf`). The format string only; the three expressions were already correct.
- **6b — `MainViewModel`** (§15). Status text, port list, connect/disconnect and the connection lifecycle,
  the status-line arithmetic, the orientation readout, keep-map, capture request, render-rate policy.
  `SetOrientation` is now **private** on the window.
- **6c — composition root** (`572da12`). `App.OnStartup` constructs the window; `StartupUri` removed from
  `App.xaml`; `--sim` / `--replay` handled in `AppLaunchOptions.cs`.

## What 6b actually is, in one page

`MainViewModel` is WPF-free. That is not a style choice — `WpfConversionIsConfinedToOneType` forbids any
public type in the App or Rendering assemblies (other than `WpfMath`, `Window`, `Application`) from naming a
Core type and a WPF type in the same public surface, and `MainViewModel` must speak Core (`Mat3`,
`SensorFrame`) and Hardware (`ISensorStream`, `SerialSensorStream`, `PortDescriptor`).

Consequences, all recorded in §15:

- **§2's "`INotifyPropertyChanged` + `ICommand`" could not be followed literally.** `ICommand` is in
  PresentationCore. The commands are ordinary public methods; `MainWindow.xaml` keeps its `Click=` handlers.
  `INotifyPropertyChanged` is fine because it lives in `System.ComponentModel`.
- **Two status channels, not one.** `Dispatcher` is in PresentationFramework, so the view model cannot
  marshal. `Status` is written on the UI thread; `TransportAnnounced` carries transport-thread strings that
  the window queues through its own `Dispatcher`. One property would have reversed the documented ordering
  where the capture-path message is queued *behind* `Open()`'s own.
- **The window declares no scene constants any more.** `FieldOfViewDegrees`, `MaxDistanceMm`,
  `MinDistanceMm`, `MapBuckets` are `MainViewModel`'s and the window reads them. They are **public** consts
  for that reason, which is why the reflection-based tests needed `BindingFlags.Public` added.
- **`_portProbe` moved rather than being replaced.** §10.7 said 6c should replace it. It was carried to the
  view model instead, still a never-opened `SerialSensorStream`, because the port list is a question about
  the machine and asking `SimulatedSensorStream` answers it with the fake name "SIM".
- **One ordering change.** `Disconnect` now stops the render timer before disposing the stream (it used to
  dispose first). Not observable from the UI thread, but it is a real change and it is stated rather than
  folded into "pure refactor".

## Tests that had to move, and the two that failed first

- `DefaultRenderRateIsThePlannedSixtyHertz`, `TheVisibleBandTheStatusLineCountsAgainstIsTheAppsOwnConstants`,
  `AppVisibleBand` → now read `MainViewModel`, with `BindingFlags.Public` added. **Seven
  `NullReferenceException`s on the first run** because the lookup still asked only for `NonPublic` members
  that had legitimately become public.
- `MainWindowFormatStringPairsEachLabelWithTheVariableOfTheSameName`,
  `TheAngleExpressionsThemselvesAreUnchangedByTheLabelFix` → now read `MainViewModel.cs`, and additionally
  assert the pairing and the three expressions are **absent** from `MainWindow.xaml.cs`, so a duplicated copy
  fails instead of passing in both places.
- `RenderRateIsAUserVisibleSetting_AndFrameReceivedNoLongerMarshalsPerFrame` → no longer looks for
  `Stream_FrameReceived` in the window (the window no longer subscribes to the transport). Asserts the
  window's `_viewModel.FrameArrived += (_, frame) => _coalescer.Write(frame);` subscription and the forwarding
  handlers in `MainViewModel.cs`.
- **A guard tripped on its own documentation.** `DoesNotContain("Dispatcher.BeginInvoke")` over
  `MainViewModel.cs` failed on the class comment that quotes that call while explaining why the type cannot
  use it. Fixed with `StripLineComments` (duplicated into `FrameCoalescerTests`), not by weakening the
  assertion. `ExtractMethod` could not be used: the three handlers are expression-bodied, so brace matching
  would have measured a different method.

New guards: `MainViewModelIsTheAppState_AndSpeaksNoWpfAtAll`,
`MainWindowNoLongerOwnsTheConnectionOrTheStatusLine`,
`TheRenderRateIsDeclaredOnce_NotOnBothTheViewAndTheViewModel`. Each has a positive half so it cannot pass on
deleted state.

## A tool-use incident, recorded because it nearly destroyed the plan

`replace_file_content` is a **full overwrite**. It was used on `MIGRATION_PLAN.md` to change two lines and
replaced all 1,793 lines with 4. Recovery attempts made it worse: `git_checkout` was called with a numeric
`target` (rejected twice, in violation of the repo's own rule 2), and `git_reset --mixed <path>` only
unstages — it does **not** restore file content. `git_reset --hard` with paths is refused outright by git.
**The user restored the file.**

Rules that follow, and they are binding for this repo:

- `MIGRATION_PLAN.md` is the single most valuable artifact in the repo and is 1,825 lines. **Never** use
  `replace_file_content` on it. Use `replace_file_lines` / `insert_file_lines`, bottom-up, from one read.
- `replace_file_content` is for creating a file or rewriting a whole small file. Before calling it on
  anything larger, say in one line what it removes in full.
- To restore one file from HEAD: `git_checkout` with `target: "HEAD"` **and** `paths: ["<file>"]`. Not a
  number, and not `git_reset --mixed`.
- If a git call is rejected, stop. A repeated rejected call changes nothing.

## The gates that are still open

1. **§12.1 pixel comparison — never run.** `docs/baseline/pre-phase5.png` does not exist.
   `pre-phase5-connected.png` (1186 × 748) and `post-phase5d-connected.png` (1192 × 838) cannot be diffed at
   all. Human step, steps below.
2. **The app has not been run since Phase 3.** §5's "renders identically after every structural phase" has
   been re-opened since Phase 3 and never re-satisfied. Phase 6b changes no pixels by claim, and that claim
   has not been looked at either.
3. **6a's physical tilt** — inherently manual, and the only way to settle which label is wrong in the user's
   terms.
4. **§5's `MainWindow.xaml.cs` < 200 lines** — 449 total / 216 code. Reaching 200 means moving the scene
   graph out of the window, which §2 does not schedule in Phase 6.
5. **Release build never verified.** `dotnet build EchoEnvironment.slnx -c Release`. The build tool exposes no
   configuration parameter. `EchoEnvironment.Hardware` and `EchoEnvironment.Rendering` have no Release output
   on disk.

**The human step, in order:**

1. Capture `docs/baseline/pre-phase5.png` — disconnected, `COM12` / `115200`, `Show grid` **off**, `Keep map`
   **off**, `Render` **60 Hz**, at a **recorded window size**, using §12.1's control table (Finding 30).
2. Diff it against §12.1 + §12.14. §12.14's Board row is the current expectation; §12.1's old Board row is
   not. Heed §12.14's two warnings: the board slab reads as a **foreshortened sliver**, and one viewport
   contains **two directions for the same letter**. Neither is a regression.
3. Re-capture the connected pair at **one** window size.
4. Run the app, and run `--sim`, which is the Phase 6 gate's own wording ("`--sim` renders").

## Outstanding, unchanged

- Latency at 115200 baud untested; D5/D7 fixes untested on hardware.
- **All commits are unsigned.** `GIT_SIGN_COMMITS` is enabled but gpg fails on every commit:
  `skipped "Marc <marc@hutten.name>": No secret key`. Machine configuration, not a repo problem.
- Status line is clipped at the current window width (§12.10 Finding 9).
- Phase 7 still owes the `src/` + `tests/` relocation and the WPF 3D normals item.

## UNVERIFIED — do not treat as done

- **No commit since 5a has been visually verified.** 5a, 5b, 5c, 5d all change pixels. 6b claims to change
  none; that claim has not been checked against a screenshot.
- A crash was reported after 5a and never attributed.
- Capture/replay parity has not been compared frame-by-frame. Expect the documented trailing-frame divergence
  (§12.11 Finding 10).
- The angles in the §12.10 capture are **early-run values** (31 frames; `CalibrationSamples = 50`).

## Next unit of work

**Not code.** The §12.1 gate screenshot and a run of the app, including `--sim`. Five phases of commits change
pixels and none has ever been looked at.

Then Phase 7: the `src/` + `tests/` relocation, the WPF 3D normals item, and the Release build.
