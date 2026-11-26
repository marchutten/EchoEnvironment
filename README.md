# EchoEnvironment

A WPF visualiser for a VL53L5CX time-of-flight zone sensor with a 9-DOF IMU: 16 or 64 zone
distances drawn as a fan of cubes in 3D, a heat grid on the right panel, an optional accumulated
world map, and a board view with an axis gizmo that rotates with the sensor's attitude.

The architecture, the migration that produced it and the reasoning behind each decision are in
[`MIGRATION_PLAN.md`](MIGRATION_PLAN.md). Baseline screenshots for the visual gates are in
[`docs/baseline/`](docs/baseline/README.md).

---

## Running it

```
dotnet run --project EchoEnvironment
```

No hardware is required for either offline mode.

| Command | What it does |
|---|---|
| `EchoEnvironment` | Serial source. Pick a port and baud in the toolbar and press **Connect**. |
| `EchoEnvironment --sim` | `SimulatedSensorStream`: a travelling wave across the zones and a fixed IMU pose. No device. |
| `EchoEnvironment --replay <file>` | `ReplaySensorStream`: replays a captured log through the **same** framing and parsing the live serial path uses. |

An unrecognised argument is **rejected**, not ignored: the app prints the usage line to stderr and
exits with code 2. A mistyped `--replay` that opened a serial session instead would produce no
frames, no capture file, and a status line blaming the port for a mistake made in the shell.

`--sim` and `--replay` disable the **Capture log** checkbox: raw capture is a property of the serial
transport, and a replay source has nothing to capture.

---

## The serial line format

One frame per line. Values are integers, millimetres.

**Separators:** comma `,`, semicolon `;`, space, and tab. All four are equivalent.

**A line is accepted by its value count:**

| Values | Meaning |
|---|---|
| **16** | 16 zone distances (4×4), no IMU |
| **64** | 64 zone distances (8×8), no IMU |
| **25** | 16 zones **+ 9 IMU counts** |
| **73** | 64 zones **+ 9 IMU counts** |
| anything else | rejected, reported as `Rejected line: …` |

**25 and 73 are not zone counts.** They are the legal way of sending zones plus IMU. This is
deliberate and is pinned by tests (`TwentyFiveValues_IsLegal_AndMeans16ZonesPlusImu`,
`SeventyThreeValues_IsLegal_AndMeans64ZonesPlusImu`). **Confirm with the firmware before changing
it** — `MIGRATION_PLAN` §4 rates "treating this as a bug and fixing it" as a real risk, and the
firmware has never been asked.

**IMU channel order**, when present — the trailing values, never the leading ones:

```
ITG3200  gyro   x  y  z
BMA180   accel  x  y  z
HMC5883L mag    x  y  z
```

**Two further behaviours that are current behaviour, not bugs:**

- **Negative millimetres are accepted.** The visible band is 20 mm to 4000 mm, so a negative reading
  parses fine and is simply not drawn.
- **Space is a separator, so a space inside a number splits it — and the line is still accepted.**
  `100,…,113,1 15` parses as 16 zones where the last two are `1` and `15` instead of `114` and `115`.
  The token count is still legal, so the frame passes validation and reaches the renderer with two
  wrong values and a silently shifted zone alignment. This is characterised by tests, not approved by
  them — see `ASpaceInsideANumber_SplitsItIntoTwoTokens_AndCorruptsTheFrame`, whose own comment calls
  it "CHARACTERIZATION, NOT APPROVAL … the hole that a stricter framing layer should close". A device
  that sends space-separated groups will be read as more zones than it intended.

Parsing is culture-invariant: a comma-separated line parses identically in locales where `,` is the
decimal separator.

---

## Controls

| Input | Effect |
|---|---|
| **Arrow keys** | Orbit the camera (yaw / pitch). Pitch is clamped to ±89° so the look direction stays finite. |
| **Left-drag** in the main view | Orbit the camera. |
| **Mouse wheel** | Zoom, clamped to 0.5–30 scene units. |
| **X / Y / Z** | Rotate the *scene* about that world axis. **Shift** inverts the direction. |
| **Space** | Reset the camera to its startup angles and clear the scene rotation. |
| **Show grid** | Toggle the floor grid. Off at startup. |
| **Keep map** | Accumulate readings into the world map. Off at startup. |
| **Capture log** | Tee everything the port receives to a file. Serial source only. Off at startup. |
| **Render** | Frame rate: 15 / 30 / 60 / 120 Hz, default **60 Hz**. Changeable while connected. |

Keys are ignored while focus is in a text box or combo box.

**Frame rate is a UI setting, not a baud-rate side effect** (defect D2, fixed in Phase 4). Frames
arrive on the transport thread and are drained by a timer on the UI thread, one per tick. Frames
that arrive faster than the chosen rate are dropped, and the drop count is shown in the status line
as `| dropped N` — a coalescing UI that discards 40 of 60 frames otherwise looks like a stuttering
app with no explanation attached to it.

---

## The world frame

```
X = east      Y = up      -Z = north
```

`EchoEnvironment.Core/WorldFrame.cs` is the single source of truth; everything that names an axis
derives from it. **North is −Z, not +Z.** That is why `Mat3.CreateOrientation` negates its north
column, why `WorldMap` measures azimuth as `atan2(X, −Z)`, and why the axis letter **Z** points
south. Drop the minus sign and the whole 3D view mirrors, silently.

Convention: row-major, row-vector — `v' = v * M`.

### The two viewports are not the same view

The board viewport applies a model remap to the gizmo; the sensor viewport applies a 180° turn about
X instead. So in the board view the labelled gizmo's **Z arm points downward** and its **Y arm points
right**, while the unlabelled fixed reference axes in the same viewport keep their world directions.
That is not a defect and it is not new — the gizmo rotates with the sensor's attitude and the
reference axes do not, which is the point of having both. What is fixed is that a **letter** means one
axis and a **colour** means one letter everywhere (`AxisConvention`).

The exact matrices, and what each arm points at on screen, are pinned by
`OrbitCameraControllerTests` — see `MIGRATION_PLAN` §12.14.

---

## Capturing a session

**Capture log** tees the raw text the port receives. It is opt-in, per connection, and cannot break
the connection it observes: a capture that fails to start is reported and the session continues.

Capture files land in:

```
bin\<configuration>\net10.0-windows\captures\<port>-<baud>-<timestamp>.txt
```

The name carries the port and the baud rate because a log whose baud rate is unknown cannot be
compared against a run at a different one. The status line shows the path during the session and
repeats it with a character count on disconnect.

**That folder is inside `bin/`, so it is gitignored** — a capture is never committed by accident, and
it is also not preserved unless deliberately copied somewhere durable.

**One known divergence between capture and replay.** A session interrupted mid-frame leaves a final
frame that replay will deliver and the live app never drew: a serial port has no end to drain at, a
log file does. Every capture-vs-replay comparison must expect it. Pinned by
`CapturedText_TrailingFrameWithoutNewline_ExistsOnReplayButNeverReachedTheLiveApp`.

---

## Layout

| Project | Target | Sees |
|---|---|---|
| `EchoEnvironment.Core` | `net10.0` — **no WPF, deliberately** | nothing from this solution |
| `EchoEnvironment.Hardware` | `net10.0-windows` | Core only |
| `EchoEnvironment.Rendering` | `net10.0-windows`, `UseWPF` | Core only |
| `EchoEnvironment` (App) | `net10.0-windows`, `UseWPF` | Core, Hardware, Rendering |
| `EchoEnvironment.Tests` | `net10.0-windows`, `UseWPF` | all four |

Core compiles as plain `net10.0` so its "cannot see WPF" guarantee is testable headlessly. That is
enforced from the compiled assemblies, not from csproj text:
`CoreAssemblyReferencesNoWpfAssembly`, `CoreIsNotATargetFrameworkWindowsProject`,
`HardwareAssemblyReferencesNothingButCore`, `RenderingAssemblyReferencesCoreAndNoOtherSolutionAssembly`,
`CoreAssemblyDoesNotReferenceRendering`, `AppAssemblyDoesNotReferenceSystemIoPorts`.

`WpfMath` is the only type permitted to name Core and WPF in the same signature.
`WpfConversionIsConfinedToOneType` enforces it.

---

## Building and testing

```
dotnet build EchoEnvironment.slnx
dotnet build EchoEnvironment.slnx -c Release
dotnet test EchoEnvironment.Tests/EchoEnvironment.Tests.csproj
```

`TreatWarningsAsErrors` is set in `Directory.Build.props` for every project, so a warning fails the
build.

CI runs the build and the tests in **Release** on `windows-latest` on every push and PR
([`.github/workflows/build-and-test.yml`](.github/workflows/build-and-test.yml)). Windows is not a
preference: four of the five projects target `net10.0-windows` and WPF does not build elsewhere.

The SDK is pinned by `global.json` to `11.0.100` with `rollForward: latestFeature`.
