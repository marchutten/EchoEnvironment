# Phase 6 — Manual Hardware Verification Checklist

This is the artifact the Phase 6 gate (MIGRATION_PLAN.md §2, line 499) calls *"manual checklist
against real hardware passes."* It had been referenced as prose in six places across the plan and
`SESSION_HANDOFF.md` but never written down, which is one reason the gate stayed open. This file is
that checklist.

**Run it at the bench with the sensor on COM14.** Every item below is something a test or `--sim`
cannot settle — that is precisely why it is here and not in `EchoEnvironment.Tests`. Tick a box only
when you have *observed* the expected result; do not tick from inference.

The gate closes when every **Gate** item is ticked. The **Follow-up** items are §16's "unverified
after this run" list — worth doing in the same session while the hardware is connected, but they do
not by themselves block Phase 6.

---

## Gate items (these three close Phase 6)

### G1 — Live serial connect on COM14
- [ ] Launch the app with no arguments (real `SerialSensorStream`, not `--sim`).
- [ ] The port picker shows **COM14** selected by default. If it falls back to another port, that
      means COM14 is not enumerated right now — note what *is* listed and why (`RefreshPorts`
      selects `DefaultPort` only if present, else `Ports[0]`).
- [ ] Press Connect. The status line reports the real transport (baud 115200), **not** the
      "Simulated source" string.
- [ ] Frames arrive and the view updates continuously. Note the frame rate you actually see against
      the render-rate combo box (15/30/60/120 Hz) — §11.6 recorded that no measurement had ever
      shown the app sustaining 60 Hz against real frames.
- **Expected:** live orientation data streams and renders. **Observed:** ______________________

### G2 — Pitch/Roll label correctness (defect D1, Phase 6a)
This is the one item only a physical tilt can settle. The regression test
(`OrientationAngleMappingTests`) pins the matrix→angle mapping, but it cannot say which *label*
matches your mental model of "pitch" vs "roll."
- [ ] With the board level on the bench, record all three readouts (Heading / Pitch / Roll).
      Level should read near 0° on the two tilt axes.
- [ ] Tilt the board to a **known angle about one specific axis** (e.g. lift one edge to 30° with a
      known wedge). Read both numbers.
- [ ] Decide: does the number that changed match the label it is printed under, in *your* terms?
      (D1 was a label swap — `SetOrientation` printed roll under "Pitch" and pitch under "Roll".)
- [ ] If a label still reads wrong, that is a live D1 regression — record which axis and the angle.
- **Expected:** each tilt moves the correctly-labelled number. **Observed:** ______________________

### G3 — `--sim` and `--replay` offline workflow on this machine
- [ ] Launch with `--sim`. Status line reads `"Simulating {Pose}, {ZoneCount} zones"` after connect
      (this exact string was never confirmed on screen — §16 line 1843).
- [ ] Launch with `--replay <a captured .log>`. Frames replay and render.
- [ ] Confirm the port field is irrelevant in `--sim` (it ignores the stream) but an empty box is
      still reported rather than silently accepted.
- **Expected:** both offline modes render without hardware. **Observed:** ______________________

---

## Follow-up items (§16 "unverified after this run" — do while connected, not gate-blocking)

### F1 — Capture/replay parity, frame-by-frame
Never compared. Capture a short live session on COM14, replay it, and confirm the replayed frames
match the captured ones (counts and values), not just that "something renders."
- [ ] Parity confirmed. **Notes:** ______________________

### F2 — Capture log written in sim mode
`_capturePath` is assigned in `MainViewModel`; whether a capture file is actually written during
`--sim` was never inspected. Run `--sim`, then check whether a capture file appeared and whether
that is the intended behaviour.
- [ ] Checked. **Result:** ______________________

### F3 — Status line clipping (§12.10 Finding 9)
Finding 9 (status text clipped at the current window width) remains unfixed and unchecked. Resize
the window and confirm the status string is fully readable or acceptably truncated.
- [ ] Checked. **Result:** ______________________

### F4 — D5 / D7 on hardware
The two behaviour decisions deferred to hardware were never exercised on real frames.
- [ ] Checked. **Result:** ______________________

### F5 — Latency at 115200 baud
§11.6's max-baud latency measurement was never taken against a real stream.
- [ ] Measured. **Result:** ______________________

---

## After this checklist is run
Record the outcome in `MIGRATION_PLAN.md` §17 and flip the Phase 6 row in §6 (line 583) from
"Gate 2/3 closed" to "Gate closed" once G1–G3 are ticked. If any Gate item fails, that is a real
finding — capture it rather than re-running until it passes.
