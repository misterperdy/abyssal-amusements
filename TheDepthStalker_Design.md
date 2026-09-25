# The Depth Stalker — Design & Mechanics Reference

This document explains how The Depth Stalker works under the hood and *why*
each rule is the way it is. It is a reference for future development, not a
task list. The implementation is `Assets/Scripts/TheDepthStalker.cs`. He adds
no new systems to `Assets/Scripts/GameMaster.cs`; he only uses a few hooks
that already exist there (Section 8).

The Depth Stalker is the game's **anti-camper**. His one and only job is to
stop the player sitting in the dark forever to stay safe from The Diver.
Turning the lights off is how the player hides from The Diver, and it also
throws away both of The Diver's "appear" rolls (see the Diver doc, Section 3).
Without a counterweight, the safest strategy would be to keep the lights off
all night. The Depth Stalker puts a hard limit on that.

He never moves between rooms and never appears on a camera. He is a single
number, a **darkness meter**, plus one office sprite.

---

## 1. The Darkness Meter

Everything he does is driven by one float, `darknessTimer`, measured in
seconds:

```
0s ............ appearDelaySeconds ............ appearDelay + jumpscareDelay
|---- safe ----|---- he is visible in the office ----| JUMPSCARE
                5s (default)                          10s (default)
```

Every frame (`UpdateDarknessTimer()`), by polling `gameMaster.LightsOn`:

```
if lights ON:
    darknessTimer -= deltaTime * recoveryRateMultiplier
else:
    darknessTimer += deltaTime

darknessTimer = Clamp(darknessTimer, 0, appearDelaySeconds + jumpscareDelaySeconds)
```

Then, in the same frame:

```
visible = darknessTimer >= appearDelaySeconds        // Section 4
if darknessTimer >= appearDelaySeconds + jumpscareDelaySeconds:
    JUMPSCARE                                        // Section 5
```

The clamp keeps the meter from going negative (so a long stretch of light
doesn't "bank" safety for later). It also keeps it from growing past the
jumpscare point.

---

## 2. The Settings

| Setting | Default | Meaning |
|---|---|---|
| `appearDelaySeconds` | 5 | Seconds of darkness before he appears in the office |
| `jumpscareDelaySeconds` | 5 | How many *more* seconds of darkness, after he appears, before he jumpscares |
| `recoveryRateMultiplier` | 1 | How fast the meter drains while the lights are ON, compared to how fast it fills while they're OFF |

The total darkness allowed is `appearDelaySeconds + jumpscareDelaySeconds`,
which is **10 seconds** by default.

`recoveryRateMultiplier` examples:

| Value | Effect |
|---|---|
| `1` | Same speed both ways: 10s of darkness takes 10s of light to fully forget. |
| `2` | He forgets twice as fast: 10s of darkness is gone after 5s of light. |
| `0.5` | He forgets half as fast: 10s of darkness takes 20s of light. |
| `0` | He never forgets. Every second of darkness this night counts forever. |

---

## 3. Why One Meter Instead of Two Timers

The design has two phases, "appear" and "jumpscare", and the rule is that the
timers go back down at the same rate they went up. That could be built as
two separate timers with bookkeeping for which one to drain first. A single
meter does it for free:

- Filling the meter walks forward through the safe zone, then the visible zone.
- Draining it walks backward: it undoes the visible zone first, then the safe
  zone, in exactly the reverse order.

**Worked example** (default settings):

| Step | Lights | Meter after | What the player sees |
|---|---|---|---|
| Start | — | 0s | nothing |
| 4s | OFF | 4s | nothing (still safe) |
| 2s | ON | 2s | nothing |
| 3s | OFF | **5s** | **he appears** |
| 5s | OFF | **10s** | **jumpscare** |

Turning the lights on for 2s only bought back 2s. The player had 3s left
before he appeared, not a fresh 5s.

---

## 4. The Visibility Rule

His office sprite is visible whenever `darknessTimer >= appearDelaySeconds`,
**regardless of whether the lights are currently on or off.**

This is deliberate. If the player turns the lights back on after he has
appeared, he is still standing there until the meter drains back below the
appear point. That is a clear warning: *he is still close, so turning the
lights off again right now is dangerous.* Without it, the player would have
no way to read how full the meter is.

The sprite is only toggled on the frame the visibility actually changes
(`SetVisible()` compares against `isVisible`). The GameObject isn't re-set
every frame, and the Console gets one "materialized" / "faded back" log per
change rather than one per frame.

---

## 5. The Jumpscare

When the meter reaches `appearDelaySeconds + jumpscareDelaySeconds`, he
catches the player:

1. His office sprite is hidden first, directly, not through `SetVisible()`
   (so the misleading "faded back" log isn't printed). The jumpscare overlay
   *is* him, so leaving the office sprite on would show two of him.
2. `gameMaster.TriggerDepthStalkerJumpscare()` is called.

Because the meter can only go **up** while the lights are **off**, this
jumpscare can only ever happen in the dark.

`TriggerDepthStalkerJumpscare()` goes through GameMaster's shared
`BeginJumpscare()`, the same one Barnacle and The Diver use (see the Diver
doc, Section 8):
- Only one lethal jumpscare can ever play.
- The first one switches the game state to `Jumpscare`.
- Every `Update()` in the game, including his own, then early-returns on
  anything other than `Playing`.

So his meter also **freezes** while any other animatronic's jumpscare plays,
and it can't trigger a second time.

---

## 6. Counterplay & Interactions

**The only tool that works on him is the lights.** Turn them on and keep them
on for a while. He subscribes to none of GameMaster's events, so cameras,
Sonar Ping and Release Pressure have no effect on him at all.

### With The Diver

The two are designed as a pair:

- The Diver's hide requirement is a random **1–3s** of darkness, comfortably
  under the Stalker's **5s** appear threshold. A single, clean hide from The
  Diver never even makes the Stalker appear.
- Hides **stack** if the lights don't stay on long enough in between. For
  example, hiding from The Diver at the window, turning the lights on for 1s,
  then hiding again from him in the office can put the meter near 5s.
- Camping in the dark blocks both of The Diver's "appear" rolls, and it is
  capped by the Stalker at **10 seconds** of continuous darkness (by
  default). After that, the player has to spend time in the light.

### With Lumina

Lumina's jumpscare is **non-lethal and does not freeze the game** (see
`Lumina_Design.md`, Section 6). Its overlay also blocks clicks while it is on
screen. If the lights are off when she strikes, the Stalker's meter keeps
filling for the length of her jumpscare, and the player can't reach the
light switch during it. This is an intended combo: being caught in the dark
by Lumina is extra dangerous.

### With Captain Barnacle

No direct interaction. Indirectly, the lights being off disables Release
Pressure, which is Barnacle's counterplay at the Window and the Door. So
every second spent in the dark is also a second Barnacle can't be pushed
back.

---

## 7. Visuals

- **`officeVisualObject`**: the "Depth Stalker inside the office" GameObject.
  - It is a plain scene object, placed as a **child of the office sprite**, so
    it pans with the view for free (the same setup as the Diver and Barnacle
    window visuals).
  - It should start inactive in the editor, and `Start()` forces it off anyway.
  - Only `SetVisible()` (and the jumpscare step above) turns it on or off.
- **His jumpscare sprite** is owned by GameMaster (`depthStalkerJumpscareObject`),
  like every other lethal jumpscare. It is a fixed overlay, not a child of the
  panning office, so it reads as "in front of you" wherever the view is
  scrolled.

He has **no camera sprites**. GameMaster's camera decision table doesn't
know about him.

---

## 8. `GameMaster.cs` API This Relies On

| API | Purpose |
|---|---|
| `public GameState CurrentState` | Stops him the instant the night stops being played (Victory, Game Over, or any lethal jumpscare). |
| `public bool LightsOn` | Polled every frame to fill or drain the meter. Originally added for The Diver. |
| `TriggerDepthStalkerJumpscare()` + `depthStalkerJumpscareObject` / `depthStalkerJumpscareDurationSeconds` | His jumpscare, sharing `BeginJumpscare()` with Barnacle's and The Diver's. |

He holds one plain serialized reference to the scene's `GameMaster`, dragged
in the Inspector. GameMaster never references him back, which is the same
one-way pattern every animatronic follows.

---

## 9. Deliberately Out of Scope For Now

- **No audio cue** (for example breathing that grows louder as the meter
  fills). The visibility rule is currently the only feedback.
- **No difficulty scaling across nights.** All three numbers are exposed in
  the Inspector, but wiring them to the night number is future work.
- **No camera presence.** He only ever exists in the office.
- **He doesn't care about panels.** Having the Maintenance Panel or the
  Camera Monitor open makes no difference to him; only the lights matter.
