# Lumina — Design & Mechanics Reference

This document explains how Lumina works under the hood and *why* each rule is
the way it is. It is a reference for future development, not a task list.
Her implementation is spread over a few files:

- `Assets/Scripts/Lumina.cs`: Lumina herself.
- `Assets/Scripts/BarnacleHallucination.cs`: the fake Captain Barnacle her
  jumpscare causes (Section 8).
- `Assets/Scripts/CaptainBarnacle.cs`: his Lumina rage (Section 7).
- `Assets/Scripts/GameMaster.cs`: a set of additive hooks (Section 10).

Lumina is the **Hypoxia Phantom**, a FNAF3-style phantom. She can jumpscare
the player as many times as she likes, but her jumpscares **never end the
night**. Her job is to:

1. **distract** the player from fending off Captain Barnacle and The Diver,
2. **empower Barnacle**: every jumpscare she lands enrages him, and
3. **confuse the player**: every jumpscare she lands also makes them
   hallucinate fake Barnacles for a while.

---

## 1. When She Exists

Lumina only exists while the player is **Hypoxic**: oxygen below
GameMaster's `hypoxiaThresholdPercent` (50% by default). She polls
`gameMaster.IsHypoxic` every frame:

```
if not hypoxic:
    if she is anywhere but Absent: go Absent (hide everything)
    stop
if she is Absent:
    she spawns -> Waiting
```

Above the threshold she is **Absent**: she rolls nothing, shows nothing and
reacts to nothing.

Oxygen currently only ever drains, so in practice Hypoxia is one-way: once
she spawns, she stays for the rest of the night. The "back to Absent" branch
is a safety net for a future mechanic that could refill oxygen.

---

## 2. The State Machine

```
                      oxygen < threshold
   [Absent] ───────────────────────────────► [Waiting] ◄──────────────────────────┐
                                               │   │                               │
             camera view change + roll ────────┘   └──── window roll (every 5s)    │
                          │                                    │                   │
                          ▼                                    ▼                   │
                     [OnCamera]                           [AtWindow]               │
          switched / closed in time → Waiting    left after 10s → Waiting ─────────┤
          stared 2s → force-close + JUMPSCARE    looked at 5s → JUMPSCARE ─────────┘
```

| State | Visible where | What it does | Ends when |
|---|---|---|---|
| **Absent** | Nowhere | Nothing. Oxygen is above the threshold. | Hypoxia starts → Waiting |
| **Waiting** | Nowhere | Waits out her cooldown, then rolls to appear (camera rolls on view changes, window rolls on a timer) | A roll succeeds → OnCamera / AtWindow |
| **OnCamera** | Her overlay sprite, on top of whatever camera feed is showing | Stare timer (Section 4) | Player switches or closes → Waiting. Stared too long → jumpscare → Waiting |
| **AtWindow** | Her sprite behind the office glass | Look timer + leave timer (Section 5) | Leave timer runs out → Waiting. Look timer runs out → jumpscare → Waiting |

Rules that apply to every state:

- **She is only ever in one place at a time.** She can't be on camera and at
  the window at once.
- **A shared cooldown.** Every time she enters Waiting (when she first spawns,
  and after *every* appearance, whether it ended in a jumpscare or not), she
  must wait `appearanceCooldownSeconds` before she can appear again. This
  stops her appearing back to back.
- **`EnterState()` is the single place her state changes.** It shows only the
  visual that matches the new state and resets *every* timer, so each state
  and each cooldown starts fresh.

---

## 3. The Settings

| Setting | Default | Used in | Meaning |
|---|---|---|---|
| `appearanceCooldownSeconds` | 10 | Waiting | Wait after spawning and after every appearance before she can appear again |
| `cameraAppearChance` | 0.1 | Waiting | Chance per camera view change to appear on the feed |
| `cameraStareSeconds` | 2 | OnCamera | How long the player can look at her on camera before she strikes |
| `windowCheckIntervalSeconds` | 5 | Waiting | Seconds between window rolls |
| `windowAppearChance` | 0.15 | Waiting | Chance per window roll to appear at the window |
| `windowLookSeconds` | 5 | AtWindow | Total seconds of "looking at her" allowed before she strikes |
| `windowLeaveSeconds` | 10 | AtWindow | How long she stays at the window before leaving on her own |

Every roll uses the same pattern as the other animatronics:
`Random.value <= chance`.

---

## 4. Camera Appearance

### What counts as a "camera view change"

GameMaster raises `OnCameraViewChanged` **exactly once** for every player
action that puts a new feed on screen:

- opening the Camera Monitor
- clicking a camera or vent button
- flipping the Map between the Facility Floorplan and the Vent Network

The Vent Network counts too. Her overlay covers the feed area of both
layers, so she can appear over CAM 08 as well.

"Exactly once" matters. The first time the monitor opens each night,
GameMaster also has to pick the default camera. If that went through the
public `SelectCamera()`, one click would raise the event twice: two rolls,
and the second one would instantly dismiss her. So the camera code is split
in two:

| Method | Used by | Raises the event? |
|---|---|---|
| `SelectCamera(int)` / `SelectVentCamera(int)` | Camera buttons (player-facing) | Yes |
| `ShowCameraFeed(int)` / `ShowVentCameraFeed(int)` (private) | `OpenCameraMonitor()`, `SetMapLayer()` (default picks) | No |

`OpenCameraMonitor()` and `ToggleMapView()` each raise the event themselves,
once, at their end.

### The algorithm

```
on OnCameraViewChanged:
    if she is OnCamera:
        she vanishes -> Waiting            // the player switched away in time
        stop
    if she is Waiting and her cooldown is over:
        if Random.value <= cameraAppearChance:
            -> OnCamera                    // appears over the feed on screen right now

on OnCameraMonitorClosed:
    if she is OnCamera:
        she vanishes -> Waiting            // the player closed the monitor in time

every frame while OnCamera:
    stareTimer += deltaTime
    if stareTimer >= cameraStareSeconds:
        -> Waiting                         // FIRST, see below
        gameMaster.ForceCloseCameraMonitor()
        gameMaster.TriggerLuminaJumpscare()
```

**Why she leaves OnCamera *before* force-closing the monitor.** Closing the
monitor raises `OnCameraMonitorClosed`. If she were still OnCamera at that
moment, her own close handler would treat it as the player escaping in time,
and she would "vanish" instead of striking. Switching to Waiting first makes
that event a no-op for her.

`ForceCloseCameraMonitor()` ignores the Sonar Ping lock on purpose, the same
way turning the lights off already can.

### The Sonar Ping trap

Firing a Sonar Ping locks the monitor for 1.5s: no switching cameras, no
closing. If the player pings while she is on screen, the lock can eat most of
her 2s stare window, and escape may be impossible. Her timer deliberately
keeps running during the lock. Pinging a camera she is standing on is the
player's mistake to make.

---

## 5. Window Appearance

While Waiting (and off cooldown), every `windowCheckIntervalSeconds` she
rolls `windowAppearChance` to appear behind the office window.

Once she is there, two timers run every frame (`UpdateAtWindow()`):

```
leaveTimer += deltaTime                       // always
if no panel is open:                          // the player is "looking at her"
    lookTimer += deltaTime

if lookTimer >= windowLookSeconds:
    -> Waiting, then JUMPSCARE
else if leaveTimer >= windowLeaveSeconds:
    -> Waiting                                // she drifts away, no jumpscare
```

- **"Looking at her" means no panel is open**: neither the Camera Monitor
  nor the Maintenance Panel (`gameMaster.IsAnyPanelOpen`). The office's
  pan position and the lights don't matter. This is a deliberate
  simplification: the window is the office's main feature, and hiding
  behind a panel is the counterplay.
- **The look timer never resets while she is there.** It adds up across
  opening and closing panels. For example, looking for 3s, opening a panel,
  closing it, then looking for 2s more gets you jumpscared.
- **The leave timer counts her whole visit**, looked at or not. With the
  defaults, she leaves after 10s unless the player gives her 5s of looking
  within that visit.

---

## 6. Her Jumpscare (Non-Lethal)

Both appearance modes end the same way: `gameMaster.TriggerLuminaJumpscare()`.
Unlike every other jumpscare, it does **not** go through `BeginJumpscare()`:

| | Lethal jumpscares (Barnacle, Diver, Stalker) | Lumina's |
|---|---|---|
| Changes `GameState` | Yes → `Jumpscare`, then `GameOver` | **No**, it stays `Playing` |
| The world freezes | Yes, every `Update()` early-returns | **No**. Clock, oxygen and every animatronic keep running. |
| Ends the night | Yes | No. The sprite hides after `luminaJumpscareDurationSeconds` (1.5s). |

What makes it a real threat:

- **The world keeps running underneath it.** Barnacle can advance, The Diver
  can come up, and oxygen keeps draining while the player is stunned.
- **Her jumpscare overlay blocks clicks** (it's a UI Image with Raycast
  Target on), so the player can't press anything for 1.5s.

Details:

- A second call while one is already on screen is ignored.
- It raises the static `GameMaster.OnLuminaJumpscare` event (Sections 7 and 8).
- If a lethal jumpscare, Victory or Game Over lands while hers is still on
  screen, `HideAllPanelsAndButtons()` cancels her coroutine and hides her
  sprite, so it never gets stuck over the end screen.

See also `TheDepthStalker_Design.md`, Section 6: being caught by Lumina with
the lights off is extra dangerous.

---

## 7. Consequence 1: Captain Barnacle's Rage

Captain Barnacle subscribes to `OnLuminaJumpscare`. Every Lumina jumpscare
sets `luminaRageTimeRemaining = luminaRageDurationSeconds`:

| Setting (on CaptainBarnacle) | Default | Meaning |
|---|---|---|
| `luminaRageSpeedMultiplier` | 2 | How much faster his timers tick while enraged |
| `luminaRageDurationSeconds` | 25 | How long the rage lasts |

While enraged, both of his timers tick `multiplier` times faster:

- **his movement-check timer**: a 5s interval becomes 2.5s, and
- **his Door deadline**: 10s becomes 5s.

His AI Level, forward chances and branch weights are unchanged. He gets
*more* chances to act, not better ones. A new Lumina jumpscare during the
rage **restarts** it from full; it does not stack.

---

## 8. Consequence 2: Barnacle Hallucinations

`BarnacleHallucination.cs` also subscribes to `OnLuminaJumpscare`. Each Lumina
jumpscare (while Hypoxic) starts, or restarts from full, a **hallucination
window**. During it, the player may see a **fake Captain Barnacle** that
looks exactly like the real one.

### The settings

| Setting | Default | Meaning |
|---|---|---|
| `hallucinationDurationSeconds` | 15 | Length of the hallucination window |
| `cameraAppearChance` | 0.35 | Chance per camera view change that a fake is on the feed |
| `windowCheckIntervalSeconds` | 4 | Seconds between window rolls |
| `windowAppearChance` | 0.25 | Chance per window roll that a fake is at the Window |
| `fakeLifetimeMinSeconds` / `Max` | 5 / 10 | Random range for how long a fake stays |
| `pushbackHoldMinSeconds` / `Max` | 1 / 3 | Random range for the Release Pressure hold that removes a Window fake |

### How a fake appears

- **On camera:** on every `OnCameraViewChanged`, if no fake is out, roll
  `cameraAppearChance`. On a success the fake appears on the **regular
  camera the player just switched to**, read from
  `gameMaster.WatchedRegularCameraIndex`. The fake:
  - is never placed in the vents (Barnacle never goes there), and
  - never appears on the camera the real Barnacle is on.
- **At the Window:** while no fake is out, every `windowCheckIntervalSeconds`
  roll `windowAppearChance`. This is skipped if the real Barnacle is already
  at the Window.
- **Only one fake at a time.** He never goes to the Door and never
  jumpscares anyone.

### How a fake behaves

- **He stays put for a random 5–10s**, like the real one waiting for his next
  movement opportunity. Switching away and back still shows him. When his
  time is up he silently "moves on" (vanishes). The real Barnacle also
  disappears from a feed when he moves, so this isn't a giveaway.
- **He mimics counterplay**, and the resources the player spends are the
  real cost of a hallucination:
  - **Sonar Ping** on his camera: the flash shows the lit sprite as usual.
    If GameMaster's pushback roll (`pushbackSuccessChance`) succeeded, he
    vanishes, as if pushed back. If it failed, he "holds his ground". Either
    way, the sonar charge is spent.
  - **Release Pressure** while he's at the Window: after a rolled 1–3s hold
    he vanishes. The oxygen spent is wasted.
- **Merging:** if the real Barnacle walks into the fake's spot, the fake is
  quietly removed, so the player never sees two Barnacles.
- **The window ends** after 15s: any fake still out vanishes, and only the
  real Barnacle is left.

**The real Barnacle *can* land on the fake's spot**, through his own
movement or through a ping. For example, with the real one on Cam2 and the
fake on Cam3, a successful ping on Cam2 pushes the real one onto Cam3. The
merge rule covers every such case. For at most one frame both indexes point
at the same camera, which draws exactly the same single Barnacle sprite.
Then the fake is removed and only the real one remains.

### Identical sprites

- **On camera**, GameMaster keeps a `fakeBarnacleCameraIndex` next to the real
  `barnacleCameraIndex`. `RefreshCameraFeedSprite()` treats either one as
  "Barnacle is here", so the fake automatically gets exactly the same sprite
  in every combination: occupied, Sonar-lit, and the Diver + Barnacle combo
  on CAM 05.
- **At the Window**, `fakeWindowVisualObject` is a copy of Barnacle's own
  window object (`FakeBarnacleAtWindow`), with the same sprite and position.

For now there is **no tell** at all. The player can only work out that he
was fake from how he behaves: vanishing when his lifetime ends, or a camera
that should be occupied turning out to be empty.

---

## 9. Visuals & Ownership

| Object | Owned by | Setup |
|---|---|---|
| `cameraOverlayObject` | Lumina | A single UI Image inside the Camera Monitor, covering the feed area. It sits in front of both the regular and vent feed roots and behind the Sonar flash overlay. **Raycast Target off**, so it doesn't block the camera buttons. Starts inactive. |
| `windowVisualObject` | Lumina | A sprite behind the office glass, a **child of the office sprite**, so it pans with the view (like `BarnacleAtWindow`). Starts inactive. |
| `luminaJumpscareObject` | GameMaster | A fixed overlay, not parented to the panning office. **Raycast Target on**, so it blocks clicks while it's up. Starts inactive. |
| `fakeWindowVisualObject` | BarnacleHallucination | A copy of `BarnacleAtWindow`. Starts inactive. |
| Fake Barnacle on camera | GameMaster | No new object. It reuses Barnacle's camera sprites (Section 8). |

The window can have several visitors at once: Lumina, Barnacle (real or
fake) and The Diver. Which one draws on top is controlled by each sprite's
**Order in Layer**.

---

## 10. API Changes This Relies On

All of these are additive. The existing behaviour of every other script is
unchanged.

### `GameMaster.cs`

| Addition | Purpose |
|---|---|
| `public bool IsHypoxic` | Lumina polls it to spawn and despawn. |
| `public bool IsAnyPanelOpen` | Lumina's "is the player looking at the window" check. |
| `public static event Action OnCameraViewChanged` | Raised exactly once per player action that puts a new feed on screen. Used by Lumina's and the hallucinations' camera rolls. |
| `public static event Action OnCameraMonitorClosed` | Raised from `CloseCameraMonitor()`, for any reason the monitor closes. |
| Private `ShowCameraFeed()` / `ShowVentCameraFeed()` split out of `SelectCamera()` / `SelectVentCamera()` | Guarantees the "exactly once" rule (Section 4). |
| `public void ForceCloseCameraMonitor()` | Lumina slams the monitor shut before her camera jumpscare. |
| `TriggerLuminaJumpscare()` + `luminaJumpscareObject` / `luminaJumpscareDurationSeconds` | Her non-lethal jumpscare (Section 6). |
| `public static event Action OnLuminaJumpscare` | Barnacle's rage and the hallucinations listen to it. |
| `fakeBarnacleCameraIndex` + `public void SetFakeBarnacleCameraIndex(int)` | Draws a fake Barnacle on a camera with the real one's sprites. |
| `public int WatchedRegularCameraIndex` | The regular camera on screen right now (-1 if the monitor is closed or on the vents). |

### `CaptainBarnacle.cs`

| Addition | Purpose |
|---|---|
| `luminaRageSpeedMultiplier` / `luminaRageDurationSeconds` + `HandleLuminaJumpscare()` | His rage (Section 7). |
| `public BarnacleLocation CurrentLocation` | Lets the hallucination avoid, and merge with, the real Barnacle. |
| `TryGetCameraIndex()` / `CameraNumberToLocation()` made `public static` | Shared location ↔ camera-index mapping, so the hallucination doesn't duplicate it. |

As with every animatronic, Lumina and BarnacleHallucination hold plain
serialized references to the scene's `GameMaster` (and, for the
hallucination, `CaptainBarnacle`). Neither is referenced back.

---

## 11. Known Interactions & Out of Scope

- **Lights don't affect her.** She appears, stares and strikes the same with
  the lights on or off.
- **She can appear over any camera the real animatronics are on.** Her
  overlay simply draws on top.
- **Only two appearance modes for now:** camera and window.
- **No audio cues yet** (for example a scream or whisper when she appears).
- **No tell for fake Barnacles yet.** They use the same sprites, as agreed.
  A subtle tell (a flicker or a slightly different sprite) is a future
  option.
- **No difficulty scaling across nights.** Every number above is exposed in
  the Inspector, but wiring them to the night number is future work.
