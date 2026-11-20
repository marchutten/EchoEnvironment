# Baseline captures

Reference images for the visual gate in `MIGRATION_PLAN.md` §12.1 and the connected-state evidence in §12.10.
Images cannot be written by the tooling used for the migration, so **each file here is added by hand.**

| File | State it must show | Status |
|---|---|---|
| `pre-phase5.png` | **Disconnected.** The §12.1 gate image: both views rendered, `Show grid` **on** ⚠️ **disputed — §12.1 says `off`; resolve before capturing**, `Keep map` **off**, `Render 60 Hz`. Compared pixel-for-pixel against the §12.1 description. | **missing** |
| `pre-phase5-connected.png` | **Connected.** Live device, `COM12` @ 115200, `Show grid` **off**, `Keep map` **off**, `Render 60 Hz`, zones populated. | **present** — committed at `a995af9`, 84,855 bytes |
| `pre-phase5e.png` | **Unknown — control state not recorded.** Captured after 5a/5b/5c/5d; the `pre-` in the name means "before 5e", not "before Phase 5". | **present** — 1192 × 838 px. **Not a reference for anything yet.** See below. |

## What `pre-phase5-connected.png` does and does not settle

`pre-phase5-connected.png` was captured from a build **before** the capture commit, so it shows a toolbar with
no `Capture log` control. That is not a defect in it — it is the reference for the scene as it stood at Phase 5
commit 2, and it is the only connected-state image whose state is **recorded**. But it cannot be used as the
reference for anything that compares toolbars, and it is **not** the §12.1 gate image: §12.1's baseline is
disconnected and says in its closing paragraph that it cannot validate zone colours, lines, map accumulation or
the grid.

## Why the two §12.1 gate images are two and not one

They are different scenes and **cannot be diffed against each other.** §12.1's baseline is disconnected and says
in its closing paragraph that it "cannot validate zone colours, lines, map accumulation or the grid." The
connected capture is the opposite state. Each is a reference for a different question:

- `pre-phase5.png` — did Phase 5's structural moves change the rendered scene?
- `pre-phase5-connected.png` — do zone colours, lines, map accumulation and the heat grid survive Phase 5c/5d,
  which deliberately change brushes, layers and axis labels?

## The two §12.1 gate images must both come from a build that has the `Capture log` checkbox

MIGRATION_PLAN §12.11 Finding 13. The capture commit (`74e9a55`) added a control to the top bar, and §12.1's
baseline description predates it. A screenshot taken from an older build differs from every later one by a
control that is not a regression — which is exactly the kind of difference a baseline exists to rule out.

**Consequence, stated plainly:** `pre-phase5-connected.png` was captured at `a995af9`, before `74e9a55`, so it
does not show the checkbox. It remains valid as a scene reference for Phase 5 commit 2. It is not a valid
reference for a toolbar comparison, and the §12.1 disconnected image should be captured from a current build so
the pair is consistent.

## `pre-phase5e.png` — present, and a reference for nothing until its state is recorded

Added by hand after Phase 5d. **1192 × 838 px**, 8-bit RGBA, verified from its PNG `IHDR` chunk.
`pre-phase5-connected.png` is **1186 × 748 px**, so the two windows are different *sizes* as well as different
scenes: a pixel-for-pixel diff between them is meaningless on the dimensions alone.

**Its control state is not recorded, so it is not yet evidence for anything.** A baseline image only becomes
evidence when the settings that produced it are written down beside it — connected or disconnected, `Show grid`,
`Keep map`, render rate. Until those are recorded, this file cannot close the §12.1 gate and cannot be used to
call any difference a regression.

**The name contradicts the naming rule below it.** `pre-` here means "before 5e", but the file was captured
*after* steps 5a, 5b, 5c and 5d — landed as three commits (`95638ec`, `75b0179`, `5bc2814`) that deliberately
changed brushes, materials, transforms, layer order and axis labels. A reader who takes `pre-` to mean "before
Phase 5" will read 5d's axis relabel as a regression. Rename it to a name that records the state once the state
is known.

**And the plan's own description of the §12.1 baseline now describes a scene that no longer exists.**
§12.1's control table records the Board view as "red X, green Y (up), **cyan Z (into −Z)**". That is the pre-5d
appearance: the description records defect D9 *as if it were the expected appearance*. Anyone diffing a current
build against that row will see 5d's fix as a defect. §12.12 has marked the row as pre-5d, but it must ultimately
be re-recorded from a current build.

**Two documents disagree about a control in an image that does not exist.** This README's table says the §12.1
gate image has `Show grid` **on**; §12.1's own control table says **off**. Whoever captures the gate image will
follow one of the two, and they produce different scenes. Resolve it *before* the capture, not after.

**Cite these documents by section and quoted text, never by line number.** `MIGRATION_PLAN.md` §11.2 already
warns that its line numbers drift because sections are inserted in the middle; §12.12 is exactly such an
insertion, and it shifted §12.1's closing paragraph. Several older sentences in these files quoted "line 1044"
and are now stale for that reason.

## Naming rule

Prefix the state, not the phase: `pre-`, `post-`. Phase 5c changes brushes and 5d changes axis labels, so a
`post-phase5d*.png` will be needed and will legitimately differ from these. A file whose name records only a
phase number cannot tell a future reader whether a difference is expected or a regression.



