# Baseline captures

Reference images for the visual gate in `MIGRATION_PLAN.md` §12.1 and the connected-state evidence in §12.10.
Images cannot be written by the tooling used for the migration, so **each file here is added by hand.**

| File | State it must show | Status |
|---|---|---|
| `pre-phase5.png` | **Disconnected.** The §12.1 gate image: both views rendered, `Show grid` **on**, `Keep map` **off**, `Render 60 Hz`. Compared pixel-for-pixel against the §12.1 description. | **missing** |
| `pre-phase5-connected.png` | **Connected.** Live device, `COM12` @ 115200, `Show grid` **off**, `Keep map` **off**, `Render 60 Hz`, zones populated. | **present** — committed at `a995af9`, 84,855 bytes |

## What the one image that exists does and does not settle

`pre-phase5-connected.png` was captured from a build **before** the capture commit, so it shows a toolbar with
no `Capture log` control. That is not a defect in it — it is the reference for the scene as it stood at Phase 5
commit 2, and it is the only connected-state image that exists. But it cannot be used as the reference for
anything that compares toolbars, and it is **not** the §12.1 gate image: §12.1's baseline is disconnected and
says at line 1044 that it cannot validate zone colours, lines, map accumulation or the grid.

## Why two images and not one

They are different scenes and **cannot be diffed against each other.** §12.1's baseline is disconnected and
states at line 1044 that it "cannot validate zone colours, lines, map accumulation or the grid." The
connected capture is the opposite state. Each is a reference for a different question:

- `pre-phase5.png` — did Phase 5's structural moves change the rendered scene?
- `pre-phase5-connected.png` — do zone colours, lines, map accumulation and the heat grid survive Phase 5c/5d,
  which deliberately change brushes, layers and axis labels?

## Both images must come from a build that has the `Capture log` checkbox

MIGRATION_PLAN §12.11 Finding 13. The capture commit (`74e9a55`) added a control to the top bar, and §12.1's
baseline description predates it. A screenshot taken from an older build differs from every later one by a
control that is not a regression — which is exactly the kind of difference a baseline exists to rule out.

**Consequence, stated plainly:** `pre-phase5-connected.png` was captured at `a995af9`, before `74e9a55`, so it
does not show the checkbox. It remains valid as a scene reference for Phase 5 commit 2. It is not a valid
reference for a toolbar comparison, and the §12.1 disconnected image should be captured from a current build so
the pair is consistent.

## Naming rule

Prefix the state, not the phase: `pre-`, `post-`. Phase 5c changes brushes and 5d changes axis labels, so a
`post-phase5d*.png` will be needed and will legitimately differ from these. A file whose name records only a
phase number cannot tell a future reader whether a difference is expected or a regression.



