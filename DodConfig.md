# Plan: Configurable DOF Sensor Orientation
> **Status (implemented):** P1, P3, P4 done; P2/P4 carried on `SensorSource` instead of `MainViewModel`
> (see §4 deviations); P5 optional UI not done. Build clean, **354 tests green** (8 new). The §6.3
> end-to-end `WorldMap` characterization test and the §7 physical axis/sign confirmation are still open.

## 1. Goal

Let the user configure the **physical mounting orientation** of the VL53L5CX depth-of-field
(DOF) sensor so that the 3D view and the accumulated world map are correct regardless of how
the sensor is physically mounted (yaw / pitch / roll offset relative to the IMU reference frame).

Today the mount is implicitly **identity**: the zone positions are computed in the DOF sensor's
own frame with a hard-coded look axis, and the only thing that orients them into the world is the
live IMU-derived matrix. There is no static, user-settable mounting offset.

**Out of scope (this plan):** persistence to disk, a DI container / options framework, and any
change to the IMU filtering math itself. See §8 for deferred items.

---

## 2. Current state (verified)

| Concern | Location | Finding |
|---|---|---|
| Sensor-frame zone geometry | `EchoEnvironment.Rendering/ZoneRenderer.cs` `UpdateFrame` (~L155–215) | Zone positions built in the DOF-sensor frame; look axis hard-coded to `-Z`, `X`=right, `Y`=up (comment ~L185). Index is row-major `index = row*size + col`; column→azimuth, row→elevation. |
| Sensor-frame → world transform | `EchoEnvironment/MainWindow.xaml.cs` `SetOrientation(Mat3)` (~L391–394) | `_orientation.Matrix = rotation.ToMatrix3D();` — the single shared sensor→world matrix. |
| Where orientation is applied | `EchoEnvironment/OrbitCameraController.cs` `CreateViewTransform` (~L154–169) | Chain order: `[axis remap] → [IMU orientation] → [fixed 90° Y turn]`. Pinned by `OrbitCameraControllerTests.OrientationIsAppliedBetweenTheRemapAndTheTurn_NotAfterBoth`. |
| IMU → world matrix | `EchoEnvironment.Core/OrientationFilter.cs` `Current` / `Update` (~L29, L66) | Returns a `Mat3` that rotates **sensor-frame** points into the **world** frame (X east, Y up, -Z north). |
| Axis convention (source of truth) | `EchoEnvironment.Core/WorldFrame.cs` (~L17–35) | `East=(1,0,0)`, `Up=(0,1,0)`, `North=(0,0,-1)`, `SensorLookAxis=(0,0,-1)`. |
| Rotation matrix type | `EchoEnvironment.Core/Mat3.cs` (~L15, L54) | Row-major, **row-vector** convention `v' = v * M`. `CreateOrientation(east, up, north)` builds sensor→world. |
| World map accumulation | `EchoEnvironment.Core/WorldMap.cs` `Add(Vec3,int)` (~L101–157) | Takes an **already-world-frame** `Vec3`; performs no orientation of its own. |
| Existing config mechanism | `MainViewModel.cs` consts (~L54–66, incl. `DefaultPort="COM14"`), `AppLaunchOptions.TryParse` (~L87), toolbar XAML | **No** appsettings.json, no `*.config`, no DI container, no options pattern. Tunables are `const` fields + CLI switches + toolbar controls. |

**Key insight:** `_orientation` (set in `MainWindow.SetOrientation`) is the *one* matrix that flows
into both the zone group and the board group transforms, and `ZoneRenderer.UpdateFrame` reads
`_zoneGroup.Transform.Value` to push points into `WorldMap.Add`. Therefore composing a mounting
offset into `_orientation` propagates it to **both** the rendered scene and the world map with a
single change — no separate map path needed.

---

## 3. Design

### 3.1 Concept: a fixed mounting rotation `M_mount`

`M_mount` maps a point expressed in the **DOF-sensor frame** into the **IMU reference frame** that
`OrientationFilter` already outputs to world. With the project's row-vector convention
(`v' = v * M`), a DOF-frame zone point `p` becomes:

```
p_world = p * (M_mount * M_imu_to_world)
```

So the mounting rotation is applied **before** the IMU orientation, and the two compose into one
matrix. This is the math to pin with a test (§6).

### 3.2 New Core value type — `SensorMount` (pure, testable)

Add `EchoEnvironment.Core/SensorMountOrientation.cs`:

- A small immutable struct/record holding **yaw / pitch / roll in degrees** (user-facing units),
  defaulting to `(0,0,0)` = identity = today's behavior.
- A method `Mat3 ToMatrix()` that builds the rotation from yaw/pitch/roll using a single, documented
  axis order (recommend **yaw about world-up `Y`, then pitch about `X`, then roll about look-axis `Z`**,
  matching `WorldFrame` axes). Reuse existing `Mat3` rotation helpers; do not hand-roll matrix math.
- Keep it in **Core** so it is unit-testable without WPF and respects the architecture invariant
  (`WpfConversionIsConfinedToOneType`).

### 3.3 Compose into the shared orientation (primary approach)

In `MainWindow.SetOrientation(Mat3 rotation)` (~L391), pre-compose the mount:

```csharp
_orientation.Matrix = (_mount * rotation).ToMatrix3D();   // row-vector: mount applied first
```

`_mount` is a `Mat3` built once from the configured `SensorMountOrientation`. Because `_orientation`
feeds both transform chains and `ZoneRenderer` reads that same matrix for the map, **one line covers
view + map**.

> **Alternative (rejected as primary):** insert `M_mount` as an extra child in
> `OrbitCameraController.CreateViewTransform`. Rejected because it would need to be added to *both*
> the zone and board chains and risks breaking the pinned "orientation between remap and turn" test;
> composing into `_orientation` keeps that chain untouched.

### 3.4 Configuration surface (follow existing minimalism)

No new framework. Layer the config the way the app already does:

1. **Core default** — `SensorMountOrientation.Identity` (`0,0,0`).
2. **ViewModel property** — add a mutable `SensorMountOrientation SensorMount { get; set; }` on
   `MainViewModel` (next to the other tunables at ~L54–66), with change notification so the view can
   react. Default identity → zero behavior change when unset.
3. **Launch-time config** — extend `AppLaunchOptions.TryParse` (~L87) with switches, e.g.
   `--mount-yaw <deg> --mount-pitch <deg> --mount-roll <deg>` (or a single `--mount yaw,pitch,roll`).
   Thread the parsed value through `SensorSource` / `App.OnStartup` into `MainViewModel`.
4. **Live tuning (optional phase)** — three toolbar controls bound to `SensorMount`, mirroring how
   the port combo / baud box already work, so orientation can be dialed in without restarting.

---

## 4. Implementation phases

| Phase | Change | Files | Risk | Status |
|---|---|---|---|---|
| **P1** | Add `SensorMountOrientation` + `ToMatrix()` in Core; unit-test yaw/pitch/roll composition and identity. | new `Core/SensorMountOrientation.cs`, new test | Low (additive, pure) | ✅ Done (+ `Mat3.Identity` and `Mat3.operator *` added to `Mat3.cs`) |
| **P2** | Add `SensorMount` property to `MainViewModel` (default identity). | `MainViewModel.cs` | Low | ⚠️ Done differently — see deviation note |
| **P3** | Compose mount into `_orientation` in `SetOrientation`; verify view + map both rotate. | `MainWindow.xaml.cs` | Medium — ordering; pin with test | ✅ Done (`_mount * rotation`) |
| **P4** | CLI switches in `AppLaunchOptions` + thread through `App.OnStartup`/`SensorSource`. | `AppLaunchOptions.cs`, `App.xaml.cs` | Low–Med | ⚠️ Done differently — see deviation note |
| **P5 (opt)** | Live toolbar controls bound to `SensorMount`. | `MainWindow.xaml(.cs)` | Medium (UI) | ⬜ Not done (optional; launch-time only via `--mount`) |

Each phase is a separate, independently-buildable commit; behavior changes (P3/P4) are separate from
the additive Core type (P1).

### Deviations from the plan (as implemented)

- **Carried `Mount` on `SensorSource`, not as a `MainViewModel` property.** P2/P4 were merged:
  `SensorSource` gained an optional `Mount { get; init; }` defaulting to `Identity`. This avoided
  changing `AppLaunchOptions.TryParse`'s signature (3 pinned test call sites) and kept MainWindow's
  single-constructor contract (pinned by a test). `MainWindow` already receives `source`, so it reads
  `source.Mount.ToMatrix()` in the constructor — no `MainViewModel`/`App.OnStartup` threading needed.
- **Single `--mount <yaw,pitch,roll>` switch** (not three separate `--mount-yaw/-pitch/-roll`). Parsed
  invariant-culture; rejects wrong field count or non-numeric input rather than defaulting.

---

## 5. Acceptance criteria

- [x] With mount unset (`0,0,0`), output is **byte-for-byte identical** to today (regression-safe default).
      Verified: `_mount` is `Mat3.Identity`, so `(_mount * rotation) == rotation`; full suite green.
- [ ] A known mount (e.g. yaw 90°) rotates the world map and the 3D zone cloud by the same amount —
      verified by a characterization test comparing `WorldMap` cell azimuth before/after.
      **Not yet done** — the §6.3 end-to-end characterization test was not written; correctness is
      pinned at the math level (§6.1/§6.2) instead.
- [x] The existing `OrbitCameraControllerTests.OrientationIsAppliedBetweenTheRemapAndTheTurn_NotAfterBoth`
      still passes (chain order untouched). Verified in the full-suite run.
- [x] All current **346** tests remain green; new Core tests added. Now **354** (8 new
      `SensorMountOrientationTests`); build clean, 0 warnings.

---

## 6. Test strategy

1. **Unit (`SensorMountOrientationTests`)** — identity → `Mat3.Identity`; single-axis yaw/pitch/roll
   match an independently computed expected rotation; composition order is pinned.
2. **Composition (`SetOrientation` math)** — assert `p * (M_mount * M_imu)` equals applying mount then
   IMU in sequence. This is the load-bearing correctness test for §3.1.
3. **End-to-end characterization** — feed a `SimulatedSensorStream` pose (reuse `SimulationPose`,
   `Hardware/SimulatedSensorStream.cs` ~L13) with a non-identity mount; assert resulting `WorldMap`
   entries land in the expected azimuth/elevation cells.
4. **CLI parsing** — `AppLaunchOptionsTests` for the new switches (valid, missing value, out-of-range).

---

## 7. Risks & open questions

- **Rotation order / handedness.** The exact yaw-pitch-roll axis order and whether mount composes as
  `M_mount * M_imu` vs `M_imu * M_mount` must be confirmed against `WorldFrame` axes with a test
  before P3 is trusted. (Row-vector convention makes this easy to get backwards.)
- **IMU vs DOF frame relationship.** This plan assumes the IMU orientation already maps the *DOF
  sensor* frame to world and that the mount is a fixed offset between the DOF sensor and the IMU. If
  the IMU is rigidly co-located with the DOF sensor, `M_mount` collapses to identity and the feature
  instead becomes "rotate the whole scene" — confirm the physical setup.
- **`WorldMap` azimuth convention.** `Add` uses `atan2(X, -Z)` (north-referenced). A mount that
  changes heading shifts azimuth; ensure the test expectation accounts for the north reference.

---

## 8. Deferred / out of scope

- Persisting orientation to a settings file (would introduce the project's first persistence layer —
  needs its own decision given the deliberate "no DI / no config" stance, `App.xaml.cs` ~L17).
- Per-zone or per-frame dynamic orientation beyond the IMU filter.
- Auto-calibration of mount from data.
