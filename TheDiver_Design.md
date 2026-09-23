# The Diver — Design & Mechanics Reference

This document explains how The Diver, the second animatronic in Abyssal
Amusements, works under the hood and *why* each rule is the way it is. It is a
reference for future development, not a task list. The implementation is
`Assets/Scripts/TheDiver.cs`, plus a small set of additive changes to
`Assets/Scripts/GameMaster.cs` for him to hook into (Section 8).

The Diver (the "Empty Suit") is the game's **lights-based** threat. Captain
Barnacle is fought with Sonar Ping and Release Pressure; The Diver ignores both.
The only thing that works on him is turning the office lights **off** at the
right moment, and keeping an eye on him through the cameras.

His behaviour follows the design agreed for this implementation. That design
deliberately differs from the game bible's original Diver description in
several places (Section 9).

---

## 1. The Path

Unlike Barnacle's theta-shaped room graph, The Diver walks a single, fixed,
**one-directional** path. He never moves backward except when the player
successfully hides from him:

```
            hid in time                                   hid in time
         ┌──────────────┐                            ┌──────────────────┐
         ▼              │                            ▼                  │
      [Idle] ──► [AtWindow] ──► [AtCam05] ──► [AtCam08] ──► [LurkingUnderGrate] ──► [InOffice] ──► JUMPSCARE
              low roll    missed      AI roll      AI roll            AI roll + 1-in-4        missed
                          (5s)                                                                (5s)
```

| State | Visible where | What it does | Success → | Failure / timeout → |
|---|---|---|---|---|
| **Idle** | Nowhere | After `idleActivationDelaySeconds`, rolls `idleAppearChance` every movement check | AtWindow | stays Idle |
| **AtWindow** | Office window sprite | Lights reaction test (Section 4) | hid → **Idle** | missed → **AtCam05** |
| **AtCam05** | CAM 05 (regular camera index 4) | Rolls `aiLevel`; auto-fails while watched (Section 5) | AtCam08 | stays |
| **AtCam08** | CAM 08 (vent camera index 0) | Rolls `aiLevel`; auto-fails while watched | LurkingUnderGrate | stays |
| **LurkingUnderGrate** | Nowhere (under the floor grate) | Rolls `aiLevel`, then `officeAppearChance` | InOffice | stays |
| **InOffice** | In-office sprite | Lights reaction test | hid → **LurkingUnderGrate** | missed → **Jumpscare** |

Two loops are built into the path:

- **Window loop:** hiding from him at the window sends him back to Idle, so he
  can come back to the window later in the night. The start delay does *not*
  apply again; it only holds him back at the very start of the night.
- **Grate loop:** once he is inside the vents, hiding from him in the office
  only sends him back under the grate. He stays there, rolling to come up
  again, for the rest of the night. Once he's in, he never fully goes away.

---

## 2. The Timers & Chances

Every `movementCheckIntervalSeconds` (default 5s), in every state that rolls,
he gets a single movement check. The Inspector-tunable numbers:

| Setting | Default | Used in | Meaning |
|---|---|---|---|
| `idleActivationDelaySeconds` | 60 | Idle | Seconds into the night before he rolls at all |
| `idleAppearChance` | 0.05 | Idle | Chance per check to appear at the window. Deliberately low. |
| `movementCheckIntervalSeconds` | 5 | All rolling states | Seconds between movement checks |
| `aiLevel` | 0.5 | CAM05, CAM08, Under Grate | Chance per check of a movement opportunity, exactly like Barnacle's |
| `officeAppearChance` | 0.25 | Under Grate | After a successful opportunity, the chance he actually comes up into the office (1 in 4) |
| `reactionWindowSeconds` | 5 | Window, Office | How long the lights may stay ON after he appears |
| `hideDurationMinSeconds` / `Max` | 1 / 3 | Window, Office | Random range for how long the lights must stay OFF for him to leave |

Every roll uses the same pattern as Barnacle: `Random.value <= chance`.

Under the grate, both rolls must pass. The effective chance per check is
`aiLevel × officeAppearChance` (0.5 × 0.25 = 12.5% by default, roughly once
every 40 seconds on average).

**Timers reset on every state change** (`EnterState()`). So when he arrives
somewhere new, the player always gets a full movement interval before his
first roll there. Each appearance at the window or in the office also gets a
full reaction window.

---

## 3. "Appear" Rolls Need the Lights ON

Both "appear" rolls, Idle → Window and Under Grate → Office, are **thrown
away** if they succeed while the lights are already off. He tries again on
the next check.

The reason is that the lights-off mechanic is about *reacting* to him.
Appearing while the player is already sitting in the dark would either be a
free pass or a strange invisible appearance. Camping in the dark is
already costly: the cameras and Release Pressure are disabled while the
lights are off. The future Depth Stalker is designed to punish it further
(Section 9).

---

## 4. The Lights Reaction Test (Window & Office)

The same algorithm runs every frame while he is `AtWindow` or `InOffice`
(`UpdateLightsReaction()`), by polling `gameMaster.LightsOn`:

```
if lights ON:
    if a hide attempt was in progress:
        cancel it                      // turned the lights back on too early
    reactionTimer += deltaTime
    if reactionTimer >= reactionWindowSeconds:
        FAIL                           // Window -> CAM05, Office -> jumpscare

if lights OFF:
    if no hide attempt in progress:
        start one: hideTimer = 0,
                   requiredHideSeconds = Random.Range(hideMin, hideMax)
    hideTimer += deltaTime
    if hideTimer >= requiredHideSeconds:
        SUCCESS                        // Window -> Idle, Office -> Under Grate
```

Consequences of this shape:

- **Turning the lights off within the reaction window is always enough.** The
  deadline only advances while the lights are ON, so it can never expire
  while the player is hiding. The 1–3s hide is allowed to finish *after* the
  5s would have run out.
- **Turning the lights back on too early** cancels the hide attempt, and he
  stays. The deadline is *paused*, not reset, while the lights are off. It
  picks up from where it stopped, so flicking the switch doesn't buy a fresh
  5 seconds. The next lights-off rolls a brand-new hide duration, so the
  player can't learn a "safe" number.
- The hide duration is hidden from the player on purpose. They have to wait
  in the dark without knowing exactly when it's safe to turn the lights back
  on.

**The UI trap:** The Diver can come up into the office while the Camera
Monitor is open. To survive, the player must close the monitor, pan to and
open the Maintenance Panel, and toggle the lights, all within the reaction
window. If a Sonar Ping is mid-flash, the monitor stays locked for up to 1.5s
of that window. This is the "UI Trap" from the game bible's gameplay loop.

---

## 5. The "Being Watched" Rule

While he is at **CAM 05** or **CAM 08**, every movement check **automatically
fails** if the player is looking at him right now. "Looking" means the Camera
Monitor is open, on the correct layer (regular or vent), with that exact
camera selected. This comes from `GameMaster.IsPlayerWatchingCamera(index,
isVent)` and is checked at the moment of the roll. Glancing away for a few
frames between rolls is safe, but being away at the moment of the roll isn't.

This rule does **not** apply under the grate: he is off-camera there, so there
is nothing to watch.

---

## 6. Sonar Ping & Release Pressure: No Effect

The Diver does not subscribe to any of GameMaster's sonar or release-pressure
events, so neither tool has any gameplay effect on him. Pinging CAM 05 while
he's there still plays the flash and shows his lit sprite (Section 7). That
is purely visual; he doesn't move or slow down.

---

## 7. Visuals

As with Barnacle, **GameMaster owns every camera-feed sprite**. `TheDiver.cs`
has no `Sprite` or `Image` fields. It only reports where he is, through
`gameMaster.SetDiverCameraIndex()` and `gameMaster.SetDiverVentCameraIndex()`,
both called from `EnterState()`. It passes `-1` for "not on a camera of that
kind".

### Camera sprites (all set on GameMaster)

`RefreshCameraFeedSprite()` is the single source of truth for a regular
camera's look. It works as a decision table:

| Who is in the room | No sonar flash | During a sonar flash |
|---|---|---|
| Diver + Barnacle (CAM 05 only) | `diverAndBarnacleCameraSprite` | `diverAndBarnacleLitCameraSprite` |
| Diver only (CAM 05) | `diverOccupiedCameraSprite` | `diverOccupiedLitCameraSprite` |
| Barnacle only | `barnacleOccupiedCameraSprites[i]` | `barnacleOccupiedLitCameraSprites[i]` |
| Nobody | the room's original sprite | the room's original sprite |

CAM 08 (vent): `diverOccupiedVentCameraSprite` while he's there, otherwise the
vent camera's original sprite. There is no vent "lit" variant: Sonar Ping
can't be fired from the Vent Network at all. Its button, and the "SONAR
REBOOTING" text, are hidden while the vent layer is showing.

Any sprite not yet assigned in the Inspector **falls back to the room's
original empty sprite**, rather than Unity's white box.

**How the sonar "lit" state works:** before The Diver, `SonarPingRoutine()`
wrote Barnacle's lit sprite directly into the camera's `Image`. That would
have needed separate branches for every combination of occupants. Instead,
GameMaster now keeps a `sonarLitCameraIndex`:
- It's set when the flash starts and cleared when the flash ends, or when the
  night ends mid-ping.
- `RefreshCameraFeedSprite()` simply includes it in the table above.

This also means that if an occupant moves while the flash is still playing,
the room redraws correctly.

The regular and vent layers now cache their `Image` components and original
sprites through one shared helper, `CacheFeedImages()`, in `Awake()`. It runs
in `Awake()` for the same start-order reason explained in the Barnacle doc.

### Office sprites (owned by TheDiver)

`windowVisualObject` and `officeVisualObject` are plain scene GameObjects that
`EnterState()` switches on only in `AtWindow` and `InOffice` respectively.
Both are children of the office sprite, so they pan with the view for free,
the same setup as `BarnacleAtWindow`.

---

## 8. Jumpscare Exclusivity & GameMaster API Changes

**Only one jumpscare can ever play.** Both animatronics' jumpscare methods now
share one private `BeginJumpscare(object, duration, name)` in GameMaster. Its
first line is `if (currentState != GameState.Playing) return;`. The first
animatronic to catch the player switches the state to `Jumpscare`, and after
that:

- any other jumpscare call is ignored, even one in the same frame
- `GameMaster.Update()`, `CaptainBarnacle.Update()` and `TheDiver.Update()`
  all early-return, so every timer freezes during the jumpscare

All GameMaster changes are additive, and Barnacle's behaviour is unchanged:

| Addition | Purpose |
|---|---|
| `public bool LightsOn` | The Diver polls this during his lights reaction test. |
| `public bool IsPlayerWatchingCamera(int cameraIndex, bool isVentCamera)` | The "being watched" rule (Section 5). |
| `SetDiverCameraIndex(int)` / `SetDiverVentCameraIndex(int)` | Occupancy reporting, mirroring `SetCaptainBarnacleCameraIndex()`. |
| Five `diver...Sprite` fields | His camera art (Section 7). |
| `sonarLitCameraIndex` and the decision-table `RefreshCameraFeedSprite()` | Combined occupant and flash sprites. |
| `RefreshVentFeedSprite(int)` + vent `Image` cache + `CacheFeedImages()` | CAM 08 sprite swapping. |
| `TriggerDiverJumpscare()` + `diverJumpscareObject` / `diverJumpscareDurationSeconds` | His jumpscare, sharing `BeginJumpscare()` with Barnacle's. |

`TriggerCaptainBarnacleJumpscare()` keeps its name and its serialized fields,
so `CaptainBarnacle.cs` and the existing Inspector wiring didn't need to change.

---

## 9. Differences From the Game Bible / Out of Scope

- **Sonar doesn't slow him.** The bible says pings slow him down; in this
  design they do nothing.
- **Watching him on camera freezes him** at CAM 05 and CAM 08. This rule is
  not in the bible.
- **Where he enters and how he reaches the office.** The bible has him enter
  at Cam 1 and "periodically peek out" of the grate. Here he goes to CAM 05,
  then CAM 08, and comes up into the office with a 1-in-4 roll.
- **Not implemented:** the bible's "pulling up the Camera Monitor while he's
  at the grate = instant jumpscare". The Monitor is only a danger here
  because it slows the player's reaction.
- **The Depth Stalker** (punishing lights kept off too long) doesn't exist
  yet. Until it does, the player can camp in the dark to block both of his
  appearance rolls, at the cost of losing cameras and Release Pressure.
- **No difficulty scaling across nights yet.** The knobs are exposed
  (`idleAppearChance`, `aiLevel`, `officeAppearChance`,
  `reactionWindowSeconds`), but wiring them to the night number is future
  work.
- **No audio cues yet** (for example a sound when he appears at the window or
  comes up through the grate).
