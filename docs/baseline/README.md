# Baseline captures

Reference images for the visual gate in `MIGRATION_PLAN.md` §12.1 and the connected-state evidence in §12.10.
Images cannot be written by the tooling used for the migration, so **each file here is added by hand.**

**A capture is only evidence when its control state is written down beside it.** Connected or disconnected,
`Show grid`, `Keep map`, render rate, and the window size. An image with no recorded state cannot close a gate and
cannot be used to call any difference a regression.

| File | State it shows | Status |
|---|---|---|
| `pre-phase5.png` | **Disconnected.** The §12.1 gate image: both views rendered, `Show grid` **off**, `Keep map` **off**, `Render 60 Hz`. Compared pixel-for-pixel against the §12.1 control table. | **missing** |
| `pre-phase5-connected.png` | **Connected.** Live device, `COM12` @ 115200, `Show grid` **off**, `Keep map` **off**, `Render 60 Hz`, zones populated. Window **1186 × 748 px**. | **present** — committed at `a995af9`, 84,855 bytes |
| `post-phase5d-connected.png` | **Connected**, `Show grid` **off**, `Keep map` **off**, `Render` **default = 60 Hz**. Captured after 5a/5b, 5c and 5d. Port/Baud **not recorded**. Window **1192 × 838 px**. | **present** — 96,503 bytes |

## The `Show grid` dispute is resolved, and §12.1 was the correct side

`docs/baseline/README.md` used to say `Show grid` **on** for the §12.1 gate image while §12.1's own control table
said **off**. §12.12 recorded that as an unresolved blocker. It is now resolved **in favour of §12.1**, on two
independent grounds:

1. **The app's own startup state.** Neither `ShowGridCheckBox` nor `KeepMapCheckBox` declares `IsChecked` in
   `MainWindow.xaml` (lines 17–18), and `MainWindow.xaml.cs` never sets `IsChecked` on either. Both are therefore
   **unchecked at startup** — `off` is the state the app produces when nobody touches it.
2. **The render rate default is a named constant.** `MainWindow.xaml.cs` line 73 declares
   `private const int DefaultRenderRateHz = 60;` and line 124 selects it. So "default" is not vague: it **is
   60 Hz**, which is what §12.1's table records. The choices are `[15, 30, 60, 120]` (line 72).

The `on` in this README was simply an error in this file. **§12.1's control table is the agreed state for the
gate image.**

## `post-phase5d-connected.png` — renamed, and what it can and cannot settle

Renamed from `pre-phase5e.png` once its state was reported. The old name said "before Phase 5"; the capture was
taken *after* 5a/5b, 5c and 5d — the commits that deliberately changed brushes, materials, transforms, layer
order and axis labels. A reader who took `pre-` literally would have read 5d's axis relabel as a regression. The
new name records the state, per the naming rule at the bottom of this file.

**It is not the §12.1 gate image and cannot become one.** §12.1's baseline is **disconnected** and says so in its
own closing paragraph: it "cannot validate zone colours, lines, map accumulation or the grid." This capture is
connected. The §12.1 gate image is still missing.

**What it does enable:** with its state recorded, it is the *post* half of a matched connected-state pair —

| | `pre-phase5-connected.png` | `post-phase5d-connected.png` |
|---|---|---|
| Build | before `74e9a55`, before 5a/5b/5c/5d | after 5d |
| `Show grid` / `Keep map` / Render | off / off / 60 Hz | off / off / 60 Hz (default) |
| Window | 1186 × 748 | **1192 × 838** |

Same control settings on both sides, so a difference between them is attributable to the Phase 5 commits rather
than to the controls. That is the comparison §12.1 was never able to make.

**One thing still blocks it: the two windows are different sizes.** A pixel-for-pixel diff is meaningless on
dimensions alone, before any question of content. The cause is not recorded — DPI scaling, or a resized window
(`MainWindow.xaml` declares `Height="650" Width="900"`, so neither capture is at the XAML size). **Re-capture at
the same window size as `pre-phase5-connected.png`** and the pair becomes usable.

## Why the two §12.1 gate images are two and not one

They are different scenes and **cannot be diffed against each other.** §12.1's baseline is disconnected and says
in its closing paragraph that it "cannot validate zone colours, lines, map accumulation or the grid." The
connected captures are the opposite state. Each is a reference for a different question:

- `pre-phase5.png` — did Phase 5's structural moves change the rendered scene?
- `pre-phase5-connected.png` ↔ `post-phase5d-connected.png` — do zone colours, lines, map accumulation and the
  heat grid survive 5c/5d, which deliberately change brushes, layers and axis labels?

## Both §12.1 gate images must come from a build that has the `Capture log` checkbox

MIGRATION_PLAN §12.11 Finding 13. The capture commit (`74e9a55`) added a control to the top bar, and §12.1's
baseline description predates it. A screenshot taken from an older build differs from every later one by a control
that is not a regression — which is exactly the kind of difference a baseline exists to rule out.

**Consequence:** `pre-phase5-connected.png` was captured at `a995af9`, before `74e9a55`, so it does not show the
checkbox. It remains valid as a scene reference. It is not a valid reference for a toolbar comparison, and
`pre-phase5.png` should be captured from a current build so the pair is consistent.

## §12.1's Board view row describes a scene that no longer exists

§12.1's control table records the Board view as "red X, green Y (up), **cyan Z (into −Z)**". That is the pre-5d
appearance: the description recorded defect D9 *as if it were the expected appearance*. Anyone diffing a current
build against that row will see 5d's fix (`5bc2814`, Z arm moved to **+Z**, labels corrected) as a defect. The row
is annotated as pre-5d, and **must be re-recorded from a current build** before it is used as a gate.

## Cite these documents by section and quoted text, never by line number

`MIGRATION_PLAN.md` §11.2 already warns that its line numbers drift because sections are inserted in the middle;
§12.12 is exactly such an insertion, and it shifted §12.1's closing paragraph. Several older sentences in these
files quoted "line 1044" and are now stale for that reason.

## Naming rule

Prefix the state, not the phase: `pre-`, `post-`. Phase 5c changes brushes and 5d changes axis labels, so a
`post-phase5d*.png` will be needed and will legitimately differ from these. A file whose name records only a
phase number cannot tell a future reader whether a difference is expected or a regression.
