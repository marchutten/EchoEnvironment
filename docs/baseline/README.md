# Baseline captures

Reference images for the visual gate in `MIGRATION_PLAN.md` §12.1 and the connected-state evidence in §12.10.
Images cannot be written by the tooling used for the migration, so **each file here is added by hand.**

| File | State it must show | Status |
|---|---|---|
| `pre-phase5.png` | **Disconnected.** The §12.1 gate image: both views rendered, `Show grid` **on**, `Keep map` **off**, `Render 60 Hz`. Compared pixel-for-pixel against the §12.1 description. | **missing** |
| `pre-phase5-connected.png` | **Connected.** Live device, `COM12` @ 115200, `Show grid` **off**, `Keep map` **off**, `Render 60 Hz`, zones populated. The only connected-state reference that exists. | **missing** — the capture exists in chat, not on disk |

## Why two images and not one

They are different scenes and **cannot be diffed against each other.** §12.1's baseline is disconnected and
states at line 1044 that it "cannot validate zone colours, lines, map accumulation or the grid." The
connected capture is the opposite state. Each is a reference for a different question:

- `pre-phase5.png` — did Phase 5's structural moves change the rendered scene?
- `pre-phase5-connected.png` — do zone colours, lines, map accumulation and the heat grid survive Phase 5c/5d,
  which deliberately change brushes, layers and axis labels?

## Both images must come from a build that has the `Capture log` checkbox

MIGRATION_PLAN §12.11 Finding 13. The capture commit added a control to the top bar, and §12.1's baseline
description predates it. A screenshot taken from an older build differs from every later one by a control that
is not a regression — which is exactly the kind of difference a baseline exists to rule out. Capture both
images from the same build.

## Naming rule

Prefix the state, not the phase: `pre-`, `post-`. Phase 5c changes brushes and 5d changes axis labels, so a
`post-phase5d*.png` will be needed and will legitimately differ from these. A file whose name records only a
phase number cannot tell a future reader whether a difference is expected or a regression.



