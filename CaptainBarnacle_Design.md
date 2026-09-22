# Captain Barnacle — Design & Mechanics Reference

This document is the evidence trail for exactly how Captain Barnacle, the first
animatronic implemented in Abyssal Amusements, works under the hood and *why*
each rule ended up the way it did. It is a reference for future development,
not a task list — the implementation is `Assets/Scripts/CaptainBarnacle.cs`,
alongside a small set of additive public-API changes made to
`Assets/Scripts/GameMaster.cs` for him to hook into.

Captain Barnacle fills the game bible's "Main Stalker" role — functionally the
Springtrap-equivalent of this game: a single relentless threat that patrols a
fixed map toward the office and must be pushed back using the Sonar Ping and
Release Pressure systems.

---

## 1. The Map

Captain Barnacle patrols 7 regular cameras (Cam1–Cam7) plus two special,
non-pingable waiting spots: the **Window** (the "ghost camera" position
between Cam1 and Cam4, visible through the office's front glass) and the
**Door** (directly outside the office — reaching it and then moving again is
how he kills the player).

The room graph (which camera connects to which) is a *theta graph*: two hub
rooms, Cam2 and Cam4, are each connected to three other places, and between
them run three separate paths:

- **Short path**: Cam2 – Cam3 – Cam4
- **Window path**: Cam2 – Cam1 – Window – Cam4 (**one-way only**, in the
  Cam1→Window→Cam4 direction — he can never come back through the window
  from Cam4)
- **Long path**: Cam2 – Cam7 – Cam6 – Cam5 – Cam4

This is why Cam2 and Cam4 need their own special movement rules below —
everywhere else, a room has exactly one neighbor "toward the office" and one
neighbor "away from it," which is all a simple forward/backward roll needs.

---

## 2. The Movement Timer & AI Level

Every `movementCheckIntervalSeconds` (default 5s, matching the game bible),
Barnacle rolls a single random float against `aiLevel` (an inspector-tunable
`[0,1]` chance):

```
roll = Random.value        // 0.0 .. 1.0
opportunitySucceeded = roll <= aiLevel
```

A failed roll does nothing at all — no movement, no side effects, timer just
resets and starts counting again. A successful roll is a **movement
opportunity**, and what that opportunity actually *does* depends entirely on
where Barnacle currently is (Section 3). This mirrors classic FNAF "AI level"
difficulty scaling: a higher `aiLevel` means more frequent opportunities,
which is the intended knob for ramping difficulty across nights later.

---

## 3. Resolving a Movement Opportunity

| Current location | Rule |
|---|---|
| Cam1 | Forward/backward roll. Forward → **Window**. Backward → Cam2. |
| Cam3 | Forward/backward roll. Forward → Cam4. Backward → Cam2. |
| Cam5 | Forward/backward roll. Forward → Cam4. Backward → Cam6. |
| Cam6 | Forward/backward roll. Forward → Cam5. Backward → Cam7. |
| Cam7 | Forward/backward roll. Forward → Cam2. Backward → Cam6. |
| **Cam2** (branch) | Forward/backward roll. **Forward succeeds** → 50/50 split between Cam1 and Cam3. **Forward fails** → always Cam7. |
| **Cam4** (branch) | **No forward/backward roll at all.** Flat 1-in-3 pick among {Cam3, Cam5, **Door**}. Cam1 is deliberately excluded from this pick — the Window path only ever runs one way. |
| **Window** | No forward/backward roll — *any* successful opportunity advances him straight to Cam4. |
| **Door** | *Any* successful opportunity triggers his jumpscare, which leads to Game Over a short beat later (see "The Door Jumpscare" below) — this is literally how he kills the player. |

The five "plain" rooms (Cam1, Cam3, Cam5, Cam6, Cam7) all share one
`moveForwardChance` roll (also `[0,1]`, inspector-tunable): a second
`Random.value` compared against that chance decides forward vs. backward,
then the room's single forward or backward neighbor is looked up directly.

### Why Cam2 and Cam4 are different

Both are hub rooms with three neighbors instead of two, so "forward" or
"backward" alone can't fully describe the choice:

- **Cam4** is explicitly spelled out in the game bible as a flat three-way
  choice *including the door itself as one of the three outcomes* — there is
  no separate "did he decide to advance" roll at Cam4; reaching the door is
  just as likely per successful opportunity as retreating to Cam3 or Cam5.
  This is deliberately tense: sitting at Cam4 is genuinely dangerous every
  single movement check, not just occasionally.
- **Cam2** keeps the standard forward/backward roll, but "forward" has two
  possible destinations (Cam1 or Cam3, since both lead toward the office by
  different paths), so a coin flip decides between them once forward is
  chosen. Backward at Cam2 always means retreating into the long path via
  Cam7 — there's only one way back from this hub.

### Why the Window and the Door are "wait states," not instant transitions

Both are treated identically on purpose: arriving at either one does **not**
immediately do anything further. Barnacle *waits* there, continuing to roll
his normal 5-second movement-opportunity timer, and it's specifically his
*next* successful roll that resolves the state — advancing Window→Cam4, or
killing the player from the Door. This symmetry is what makes both states
readable and fair to react to: the player gets a window of real time (roughly
one AI-level roll's worth, on average `movementCheckIntervalSeconds / aiLevel`
seconds) to notice him and respond with Release Pressure before the next roll
resolves against them.

### The Door Jumpscare

Reaching the kill condition at the Door doesn't end the night on the spot.
`ResolveMovement()`'s `AtDoor` case calls `gameMaster.TriggerCaptainBarnacleJumpscare()`
instead of ending the night directly, which:

1. Immediately switches `GameMaster`'s state to a new `GameState.Jumpscare`
   value (sitting alongside `Playing`/`Victory`/`GameOver`). Both
   `GameMaster.Update()` and `CaptainBarnacle.Update()` already early-return
   on anything other than `GameState.Playing`, so this one state change
   freezes the clock, oxygen, sonar overheat cooldown, and Barnacle's own
   movement timer all at once, for free — no new guard code needed anywhere.
2. Hides every panel/button the same way `TriggerGameOver()`/`TriggerVictory()`
   already do, and shows `barnacleJumpscareObject` (a fixed overlay GameObject,
   not part of the panning office art, so it reads as "in front of you"
   regardless of where the view was scrolled).
3. After `barnacleJumpscareDurationSeconds` (default 2.5s, inspector-tunable)
   elapses, hides the jumpscare object and calls the existing
   `TriggerGameOver()` — which is otherwise unchanged and still used as-is for
   Suffocation's genuinely instant Game Over (no jumpscare beat there, per the
   game bible).

This state lives on `GameMaster` rather than `CaptainBarnacle` because ending
the night is already a `GameMaster`-owned concern, and a generic `Jumpscare`
state (rather than a Barnacle-specific one) means a future animatronic with
its own jumpscare can reuse the exact same state and freeze behavior.

---

## 4. Sonar Ping Counterplay

Sonar Ping only ever reaches Cam1–Cam7 — it has no effect on the Window or
Door states (there's no camera button for either), and no effect on the vent
layer (Barnacle never uses the vent at all, per the game bible).

`GameMaster` already rolls a `pushbackSuccessChance` (the bible's "RNG Fail
State") once per ping and reports the result via its existing
`OnSonarPing(cameraIndex, isVentCamera, pushbackRollSucceeded)` event.
Barnacle listens for pings that land on whichever camera he currently
occupies. If the roll failed, nothing happens — the player must ping again.
If it succeeded, he retreats using a **fixed sequential chain that is
deliberately separate from his own roaming graph above**:

```
Cam1 → Cam2 → Cam3 → Cam4 → Cam5 → Cam6 → Cam7 → Cam2 (wraps)
```

This is not the same as "always retreat away from the office" — notice that
pinging him at **Cam3 pushes him to Cam4**, which is *closer* to the door,
not farther. This is intentional, straight from the game bible's own callout
("dangerous for player!") — Sonar is a slightly blunt instrument, not a
perfectly safe tool, and spamming it without checking his position first can
actively make things worse. The chain wraps at the end (Cam7 → Cam2) using
the graph's existing 7–2 edge, so a successful ping always sends him
somewhere rather than fizzling at the far end of the numbering.

### Syncing the "lit" sprite to the actual flash

Showing an "occupied + sonar-lit" sprite variant *during* the flash the
player sees is handled entirely inside `GameMaster` now (see Section 7) —
`GameMaster` already tracks which camera Barnacle occupies
(`barnacleCameraIndex`), so `SonarPingRoutine()` can swap that camera's
`Image` to the lit sprite the instant the flash starts and revert it the
instant the flash ends, with no event round-trip to Barnacle needed for the
visual itself. Barnacle's `HandleSonarPing` handler only ever decides
whether he *retreats* — it never touches a sprite. A separate
`OnSonarPingStarted(cameraIndex, isVentCamera)` event still exists on
`GameMaster` for this same "flash is starting" moment, kept as a general
extension point for a future animatronic that might want to react to a ping
starting for a non-sprite reason (e.g. a sound cue) — Barnacle no longer
subscribes to it.

---

## 5. Release Pressure Counterplay

Release Pressure is the *opposite* tool: it does nothing at all while
Barnacle occupies any of Cam1–Cam7 (Sonar is the tool there), and only
matters while he is waiting at the **Window** or the **Door**.

The instant the player starts holding Release Pressure, if Barnacle is in
either wait state, a random hold requirement is rolled:

```
requiredHoldSeconds = Random.Range(pushbackHoldMinSeconds, pushbackHoldMaxSeconds)
// default range: 1-3 seconds
```

If the player keeps holding for that entire rolled duration, he's pushed
back:

- From **Window** → a random pick between Cam1 and Cam2.
- From **Door** → a random pick among Cam3, Cam5, Cam6, and Cam7 (matching
  the game bible's exact list).

If the player releases the button before the rolled duration elapses,
**nothing happens** — the attempt is simply wasted, and Barnacle's own
5-second movement timer keeps running independently in the background the
whole time, regardless of whether Release Pressure is being held. This means
a mistimed or too-short hold doesn't "buy" any extra safety — the only thing
that matters is whether the hold is completed before Barnacle's own timer
resolves the wait state against the player.

Both pushback destinations are picked uniformly at random rather than fixed,
so the player can't fully predict (and therefore can't fully trivialize)
where he'll end up after a successful pushback.

---

## 6. `GameMaster.cs` API This Relies On

All additive, none of them change or remove any existing behavior:

| Addition | Purpose |
|---|---|
| `public GameState CurrentState => currentState;` | Lets Barnacle stop acting the instant Victory/GameOver triggers, matching how GameMaster itself freezes. |
| `TriggerGameOver()` changed from `private` to `public` | Lets `TriggerCaptainBarnacleJumpscare()` (below) hand off to it once the jumpscare delay elapses; still used as-is for Suffocation's instant Game Over. |
| `public static event Action OnReleasePressureStarted;` | Fires once, exactly when the player begins holding Release Pressure — refactored to go through a shared `StartReleasingPressure()` helper so it fires from the one real "press" entry point. |
| `public static event Action OnReleasePressureStopped;` | Fires once whenever Release Pressure is stopped for *any* reason — pointer-up, closing the Maintenance Panel, turning the lights off mid-hold, or the night ending — via a shared `StopReleasingPressure()` helper, so Barnacle's hold-timer coroutine is reliably told to cancel in every case, not just a clean release. |
| `public static event Action<int, bool> OnSonarPingStarted;` | Fires the instant a ping's illumination flash begins (see Section 4) — the existing `OnSonarPing` still fires once it ends. `GameMaster` also uses this same moment internally (not via the event) to show its own lit sprite. |
| `public void SetCaptainBarnacleCameraIndex(int cameraIndex)` | Called by Barnacle every time his location changes (see Section 7). This is how `GameMaster` learns "where he is" without holding any reference back to `CaptainBarnacle` itself. |
| `GameState.Jumpscare` value + `public void TriggerCaptainBarnacleJumpscare()` | Called by Barnacle instead of `TriggerGameOver()` when he catches the player at the Door (see "The Door Jumpscare" above). |

All events mirror the existing `OnSonarPing` static-event pattern already
established in the codebase, so Captain Barnacle needs no singleton to use
any of them. He does hold one plain serialized reference to the scene's
`GameMaster` object (dragged in the Inspector) for `CurrentState`,
`TriggerCaptainBarnacleJumpscare()`, and `SetCaptainBarnacleCameraIndex()` —
the direction of this reference is deliberate: `GameMaster` never references
`CaptainBarnacle` back, so a future second or third animatronic can follow
the exact same one-way pattern without `GameMaster` needing to know each
animatronic type by name (beyond owning that animatronic's own sprites and
one `SetXCameraIndex`/`TriggerXJumpscare`-shaped method pair, as described
next).

---

## 7. Visual Integration — GameMaster Owns Every Sprite

Sprite/rendering concerns were deliberately kept **entirely out of
`CaptainBarnacle.cs`** — it has no `Image` or `Sprite` fields at all.
Instead, `GameMaster` owns every camera sprite, and Barnacle's only
responsibility is reporting *where he currently is*:

- **Occupancy tracking.** `GameMaster` holds a single `private int
  barnacleCameraIndex` (-1 when he's not in any of the 7 regular cameras).
  Barnacle calls `gameMaster.SetCaptainBarnacleCameraIndex(index)` from
  exactly two places: `SpawnAtRandomCamera()` (on night start) and
  `MoveTo()` (every subsequent location change, including Sonar and Release
  Pressure pushbacks) — passing `-1` whenever he's not on one of the 7
  regular cameras (i.e. he's at the Window or the Door).
- **Sprite ownership.** `GameMaster` holds `barnacleOccupiedCameraSprites[7]`
  and `barnacleOccupiedLitCameraSprites[7]` as its own serialized fields. It
  also captures each camera's starting ("empty room") sprite once, in
  `Awake()` — the same pattern `Start()` uses for `officeLitSprite`, just
  running one Unity lifecycle step earlier than that. This has to happen in
  `Awake()` rather than `Start()`: `CaptainBarnacle` reports his spawn
  location from his own `Start()`, and Unity does not guarantee which of two
  different scripts' `Start()` methods runs first. If this cache (and the
  `barnacleCameraIndex = -1` reset alongside it) lived in `GameMaster.Start()`
  instead, a run where Barnacle's `Start()` happened to fire first would
  either drop his spawn report (cache not populated yet) or have it silently
  wiped back to -1 moments later — leaving him invisible on his spawn camera
  until his first move. This was a real bug, fixed by moving just this block
  to `Awake()`, which Unity guarantees runs before any object's `Start()`.
- **Real-time refresh, not lazy-on-select.** `SetCaptainBarnacleCameraIndex()`
  immediately updates the `Image.sprite` on *both* the camera he left and
  the camera he arrived at, via a private `RefreshCameraFeedSprite(int)`
  helper. This means a camera the player is already looking at updates
  live the instant Barnacle walks into or out of it — `SelectCamera()` and
  `ActivateOnlyIndex()` needed zero changes, since by the time any camera's
  GameObject becomes visible, its `Image` already holds the correct sprite.
- **The Sonar "lit" sprite is fully internal to `GameMaster`.** Because it
  already knows `barnacleCameraIndex`, `SonarPingRoutine()` can show the lit
  sprite the instant the flash starts (right where `OnSonarPingStarted`
  fires) and revert it to the correct non-flash state the instant the flash
  ends (right before `OnSonarPing` fires) — entirely on its own, no
  subscription needed on Barnacle's side. The visible sequence during a
  successful ping is: *occupied* → *lit* (during the flash) → *occupied*
  (flash ends, roll not yet acted on) → *empty* (the instant Barnacle's
  `HandleSonarPing` handler decides to retreat and reports his new index) —
  all within the same frame the ping resolves.
- **The Window state stays local to `CaptainBarnacle`.** `windowVisualObject`
  is a plain scene `GameObject` that Barnacle activates/deactivates directly
  in `MoveTo()` — not a camera-feed sprite swap, so it doesn't fit the
  GameMaster-owned pattern above and was left as-is. It lives as a **child**
  of the existing office sprite GameObject (the same object that is both
  `officeViewRoot` and `officeSpriteRenderer` today), specifically so it
  inherits the office's left/right pan for free — `UpdateLookAndEdgeButtons()`
  only ever moves `officeViewRoot.localPosition`, and Unity's normal
  parent/child transform inheritance does the rest with zero extra code. The
  full window hierarchy, back-to-front:
  ```
  OfficeSprite (officeViewRoot / officeSpriteRenderer - front PNG, transparent glass cutout)
  ├── WindowDarkBackground  (always active - a permanent dark backdrop so the
  │                          glass cutout never shows empty void)
  └── BarnacleWindowSprite  (starts inactive - this is windowVisualObject)
  ```
  Sorting order (Order in Layer, same Sorting Layer): `WindowDarkBackground` <
  `BarnacleWindowSprite` < `OfficeSprite`, so the office's opaque art still
  occludes both children while its transparent glass area reveals them.
- **The Door state** shows Barnacle's jumpscare sprite for a tunable delay
  before Game Over (see "The Door Jumpscare" above) — `barnacleJumpscareObject`
  is a separate, fixed overlay GameObject (not parented under the panning
  office sprite, since a jumpscare should read as "in front of you"
  regardless of where the view is scrolled), owned and toggled by
  `GameMaster` alongside its other end-state screens.

---

## 8. Deliberately Out of Scope For Now

- The room graph's *shape* (which cameras connect to which, and the Cam2/
  Cam4 branch special-cases) is hardcoded directly in C# rather than
  represented as a generic serialized graph asset. The layout described by
  the game bible is small, fixed, and genuinely irregular (a theta graph
  with one one-way edge and two differently-resolving branch nodes) — a
  generic Inspector-editable graph system would be meaningfully more
  engineering for a shape that isn't expected to change. Every *number*
  (chances, timers, spawn range, hold durations) remains fully
  inspector-tunable; only the topology itself lives in code.
- No difficulty scaling across nights yet (e.g. raising `aiLevel` night over
  night) — this script exposes the knob, but wiring it to night number is
  future work once a night-progression system exists.
- No animation/jumpscare art for the Door state yet — debug logging only,
  as scoped for this stage of the prototype.
