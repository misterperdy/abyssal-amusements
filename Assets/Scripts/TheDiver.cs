using UnityEngine;

/// <summary>
/// The Diver (the "Empty Suit") - the game's stealthy, lights-based threat.
/// Like CaptainBarnacle, this script is completely separate from
/// GameMaster: GameMaster owns the lights, cameras, camera sprites, and the
/// jumpscare/Game Over flow, and this script only REACTS to those systems
/// and reports where The Diver currently is.
///
/// HOW HE WORKS (see TheDiver_Design.md at the project root for the full
/// reasoning) - he walks one fixed path through six states:
///
///   Idle -> AtWindow -> AtCam05 -> AtCam08 -> LurkingUnderGrate -> InOffice
///
///   - Idle: invisible. After a start delay, every few seconds he rolls a
///     small chance to show up outside the office window.
///   - AtWindow: the player has a few seconds to turn the lights OFF (and
///     keep them off for a random 1-3s) to make him leave (back to Idle).
///     Too slow, and he gets into the facility at CAM 05 instead.
///   - AtCam05 / AtCam08: he rolls his AI Level every few seconds to crawl
///     deeper (CAM 05 -> the vent at CAM 08 -> under the office grate). He
///     can NOT move while the player is watching him on that camera.
///   - LurkingUnderGrate: off-camera. Every successful movement opportunity
///     has a 1-in-4 chance of bringing him up INTO the office.
///   - InOffice: same lights-off test as the window. Pass it and he sinks
///     back under the grate; fail it and he jumpscares the player.
///
/// HOW THE PLAYER FIGHTS BACK:
///   - Only the office LIGHTS work on him. Sonar Ping does nothing to him
///     (so this script doesn't listen to GameMaster's sonar events at all),
///     and neither does Release Pressure.
///   - Keeping the camera on him while he's at CAM 05 or CAM 08 stops him
///     from moving on.
/// </summary>
public class TheDiver : MonoBehaviour
{
    // -------------------------------------------------------------------
    // STATES
    // -------------------------------------------------------------------
    // Every state The Diver can be in, in the order he normally moves
    // through them over a night.
    public enum DiverState
    {
        Idle,               // Not in the facility yet - invisible, rolling to appear at the window.
        AtWindow,           // Visible outside the office glass - turn the lights off!
        AtCam05,            // Inside the facility, visible on CAM 05.
        AtCam08,            // Inside the vent, visible on CAM 08.
        LurkingUnderGrate,  // Under the office floor grate - off-camera, rolling to come up.
        InOffice            // Inside the office - turn the lights off or get jumpscared!
    }

    // Which camera each on-camera state shows up on. These are INDEXES into
    // GameMaster's camera arrays (counting from 0), not the camera NUMBERS
    // printed on screen: CAM 05 is the 5th regular camera, so index 4, and
    // CAM 08 is the first (and only) vent camera, so vent index 0. Like
    // Barnacle's room graph, the map layout is fixed and lives in code.
    private const int CAM05_CAMERA_INDEX = 4;
    private const int CAM08_VENT_CAMERA_INDEX = 0;


    // -------------------------------------------------------------------
    // REGION: REFERENCES
    // -------------------------------------------------------------------
    [Header("References")]

    [Tooltip("Drag the scene's GameMaster object here. Used to read the lights and camera state, to report which camera The Diver is on, and to trigger his jumpscare.")]
    [SerializeField] private GameMaster gameMaster;


    // -------------------------------------------------------------------
    // REGION: IDLE SETTINGS
    // -------------------------------------------------------------------
    [Header("Idle Settings")]

    [Tooltip("How many seconds into the night The Diver waits before he starts rolling to appear at the window at all. Only applies once, at the start of the night.")]
    [SerializeField] private float idleActivationDelaySeconds = 60f;

    [Tooltip("While Idle: the chance (0-1) that each movement check makes him appear outside the office window. Keep this LOW - this is a rare event.")]
    [Range(0f, 1f)]
    [SerializeField] private float idleAppearChance = 0.05f;

    [Tooltip("How often (in real seconds) The Diver rolls for a movement opportunity, in every state that rolls (Idle, CAM 05, CAM 08, Under Grate).")]
    [SerializeField] private float movementCheckIntervalSeconds = 5f;


    // -------------------------------------------------------------------
    // REGION: ROAMING SETTINGS
    // -------------------------------------------------------------------
    [Header("Roaming Settings (CAM 05, CAM 08, Under Grate)")]

    [Tooltip("Chance (0-1) that each movement check actually results in a movement opportunity while he is at CAM 05, CAM 08, or under the grate. His 'AI level' - works exactly like Captain Barnacle's.")]
    [Range(0f, 1f)]
    [SerializeField] private float aiLevel = 0.5f;

    [Tooltip("While under the grate: the chance (0-1) that a SUCCESSFUL movement opportunity actually brings him up into the office. 0.25 = 1 in 4.")]
    [Range(0f, 1f)]
    [SerializeField] private float officeAppearChance = 0.25f;


    // -------------------------------------------------------------------
    // REGION: LIGHTS REACTION SETTINGS
    // -------------------------------------------------------------------
    [Header("Lights Reaction Settings (Window & Office)")]

    [Tooltip("How long (in real seconds) the player has to turn the lights OFF once he appears at the window or in the office. This timer only counts down while the lights are ON.")]
    [SerializeField] private float reactionWindowSeconds = 5f;

    [Tooltip("The shortest time (in real seconds) the lights must stay OFF for him to leave. A fresh random value between this and the Max is rolled every time the lights go off.")]
    [SerializeField] private float hideDurationMinSeconds = 1f;

    [Tooltip("The longest time (in real seconds) the lights might need to stay OFF for him to leave.")]
    [SerializeField] private float hideDurationMaxSeconds = 3f;


    // -------------------------------------------------------------------
    // REGION: VISUALS
    // -------------------------------------------------------------------
    [Header("Visuals (Office)")]

    [Tooltip("The 'Diver outside the office glass' GameObject, placed in the office scene (a child of the office sprite, so it pans with it). Should start INACTIVE - this script turns it on only while he is At Window.")]
    [SerializeField] private GameObject windowVisualObject;

    [Tooltip("The 'Diver inside the office' GameObject, placed in the office scene (a child of the office sprite, so it pans with it). Should start INACTIVE - this script turns it on only while he is In Office.")]
    [SerializeField] private GameObject officeVisualObject;


    // -------------------------------------------------------------------
    // REGION: RUNTIME STATE
    // -------------------------------------------------------------------

    // Which state The Diver is in right now. Only ever changed by
    // EnterState() below, so there is exactly one place that changes it.
    private DiverState currentState;

    // How many seconds of night have passed. Only used to hold him back
    // until idleActivationDelaySeconds have passed.
    private float nightElapsedSeconds;

    // Counts up every frame until it reaches movementCheckIntervalSeconds,
    // at which point we roll for a movement opportunity and reset it to 0.
    private float movementCheckTimer;

    // While At Window / In Office: how long the lights have been ON since
    // he appeared. Reaching reactionWindowSeconds means the player was too
    // slow. It pauses (but does NOT reset) while the lights are off.
    private float reactionTimer;

    // While At Window / In Office: true while the lights are off and we are
    // timing how long they have stayed off.
    private bool isHiding;

    // How long the lights have stayed off during the current hide attempt.
    private float hideTimer;

    // How long the lights need to stay off during the current hide attempt
    // for him to leave. Rolled fresh every time the lights go off.
    private float requiredHideSeconds;


    // -------------------------------------------------------------------
    // REGION: UNITY LIFECYCLE
    // -------------------------------------------------------------------

    /// <summary>Starts the night with The Diver Idle and both office visuals hidden.</summary>
    private void Start()
    {
        // Hide both visuals no matter how they were left in the editor.
        // EnterState() below only hides a visual when LEAVING its state, so
        // we do it by hand here for the very first state.
        SetVisualActive(windowVisualObject, false);
        SetVisualActive(officeVisualObject, false);

        nightElapsedSeconds = 0f;
        EnterState(DiverState.Idle);
    }

    /// <summary>Runs whichever logic matches his current state, for as long as the night is still in progress.</summary>
    private void Update()
    {
        // Don't act at all once the night is no longer being played:
        // Victory, Game Over, or ANY animatronic's jumpscare (including
        // Barnacle's) freezes him in place, exactly like Barnacle.
        if (gameMaster == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        nightElapsedSeconds += Time.deltaTime;

        switch (currentState)
        {
            case DiverState.Idle:
                UpdateIdle();
                break;

            case DiverState.AtCam05:
            case DiverState.AtCam08:
            case DiverState.LurkingUnderGrate:
                // All three roll on the same timer - what a roll does is
                // decided in ResolveMovementCheck() below.
                if (TickMovementTimer())
                {
                    ResolveMovementCheck();
                }
                break;

            case DiverState.AtWindow:
            case DiverState.InOffice:
                UpdateLightsReaction();
                break;
        }
    }


    // -------------------------------------------------------------------
    // REGION: MOVEMENT - THE TIMER ROLLS
    // -------------------------------------------------------------------

    /// <summary>
    /// Advances the movement check timer by one frame. Returns true (and
    /// resets the timer) on the frame a movement check is due.
    /// </summary>
    private bool TickMovementTimer()
    {
        movementCheckTimer += Time.deltaTime;
        if (movementCheckTimer < movementCheckIntervalSeconds)
        {
            return false;
        }

        movementCheckTimer = 0f;
        return true;
    }

    /// <summary>
    /// Idle: does nothing until idleActivationDelaySeconds of night have
    /// passed, then rolls idleAppearChance every movement check to appear
    /// outside the office window.
    /// </summary>
    private void UpdateIdle()
    {
        if (nightElapsedSeconds < idleActivationDelaySeconds)
        {
            return;
        }

        if (!TickMovementTimer())
        {
            return;
        }

        float roll = Random.value;
        bool appears = roll <= idleAppearChance;
        Debug.Log("[Diver] Idle check: rolled " + roll.ToString("F2") + " vs Appear Chance " + idleAppearChance.ToString("F2") + " -> " + (appears ? "appears at the window!" : "stays away."));

        if (!appears)
        {
            return;
        }

        // He can only show up while the lights are ON - showing up while the
        // player is already hiding in the dark would be a free pass, so the
        // roll is simply thrown away.
        if (!gameMaster.LightsOn)
        {
            Debug.Log("[Diver] ...but the lights are OFF, so the appearance is discarded.");
            return;
        }

        EnterState(DiverState.AtWindow);
    }

    /// <summary>
    /// Handles a due movement check at CAM 05, CAM 08, or under the grate:
    /// auto-fails if the player is watching him, otherwise rolls his AI
    /// Level and, on success, moves him one step further along his path.
    /// </summary>
    private void ResolveMovementCheck()
    {
        // Being watched on camera pins him in place - every movement check
        // fails automatically for as long as the player keeps looking at him.
        if (IsPlayerWatchingMe())
        {
            Debug.Log("[Diver] Movement check at " + currentState + ": the player is watching him -> no movement.");
            return;
        }

        float roll = Random.value;
        bool opportunitySucceeded = roll <= aiLevel;
        Debug.Log("[Diver] Movement check at " + currentState + ": rolled " + roll.ToString("F2") + " vs AI Level " + aiLevel.ToString("F2") + " -> " + (opportunitySucceeded ? "movement opportunity!" : "no movement."));

        if (!opportunitySucceeded)
        {
            return;
        }

        switch (currentState)
        {
            case DiverState.AtCam05:
                EnterState(DiverState.AtCam08);
                break;

            case DiverState.AtCam08:
                EnterState(DiverState.LurkingUnderGrate);
                break;

            case DiverState.LurkingUnderGrate:
                TryAppearInOffice();
                break;
        }
    }

    /// <summary>
    /// Under the grate, a successful movement opportunity isn't enough on
    /// its own - he also has to win an officeAppearChance roll (1 in 4 by
    /// default) to actually come up into the office.
    /// </summary>
    private void TryAppearInOffice()
    {
        float roll = Random.value;
        bool appears = roll <= officeAppearChance;
        Debug.Log("[Diver] Under-grate roll: rolled " + roll.ToString("F2") + " vs Office Appear Chance " + officeAppearChance.ToString("F2") + " -> " + (appears ? "comes up into the office!" : "stays under the grate."));

        if (!appears)
        {
            return;
        }

        // Same rule as the window: no appearing while the lights are off.
        if (!gameMaster.LightsOn)
        {
            Debug.Log("[Diver] ...but the lights are OFF, so the appearance is discarded.");
            return;
        }

        EnterState(DiverState.InOffice);
    }

    /// <summary>True if he is on a camera right now AND the player is looking at that exact camera.</summary>
    private bool IsPlayerWatchingMe()
    {
        switch (currentState)
        {
            case DiverState.AtCam05:
                return gameMaster.IsPlayerWatchingCamera(CAM05_CAMERA_INDEX, false);

            case DiverState.AtCam08:
                return gameMaster.IsPlayerWatchingCamera(CAM08_VENT_CAMERA_INDEX, true);

            default:
                // Not on any camera (e.g. under the grate), so he can't be watched.
                return false;
        }
    }


    // -------------------------------------------------------------------
    // REGION: LIGHTS REACTION (WINDOW & OFFICE)
    // -------------------------------------------------------------------

    /// <summary>
    /// Runs every frame while he is At Window or In Office, checking the
    /// lights each frame:
    ///   - Lights ON:  the reaction deadline counts down. If it runs out,
    ///                 the player was too slow.
    ///   - Lights OFF: we time how long they stay off. Once that reaches a
    ///                 randomly rolled 1-3s, he leaves.
    /// Because the deadline only counts down while the lights are ON,
    /// turning them off within the reaction window is always enough - the
    /// hide itself is allowed to finish after the window would have closed.
    /// Turning the lights back on too early cancels the hide (he stays) and
    /// the deadline carries on from where it paused.
    /// </summary>
    private void UpdateLightsReaction()
    {
        if (gameMaster.LightsOn)
        {
            if (isHiding)
            {
                isHiding = false;
                Debug.Log("[Diver] Lights turned back ON too early (" + hideTimer.ToString("F2") + "s of " + requiredHideSeconds.ToString("F2") + "s) - he's still there!");
            }

            reactionTimer += Time.deltaTime;
            if (reactionTimer >= reactionWindowSeconds)
            {
                OnReactionFailed();
            }
            return;
        }

        // Lights are OFF. On the first dark frame, start a new hide attempt
        // with a freshly rolled duration.
        if (!isHiding)
        {
            isHiding = true;
            hideTimer = 0f;
            requiredHideSeconds = Random.Range(hideDurationMinSeconds, hideDurationMaxSeconds);
            Debug.Log("[Diver] Lights OFF while he is " + currentState + " - they need to stay off for " + requiredHideSeconds.ToString("F2") + "s.");
        }

        hideTimer += Time.deltaTime;
        if (hideTimer >= requiredHideSeconds)
        {
            OnHideSucceeded();
        }
    }

    /// <summary>The player kept the lights off long enough - he leaves.</summary>
    private void OnHideSucceeded()
    {
        if (currentState == DiverState.AtWindow)
        {
            Debug.Log("[Diver] The player hid from him at the window - he swims away (back to Idle).");
            EnterState(DiverState.Idle);
        }
        else
        {
            Debug.Log("[Diver] The player hid from him in the office - he sinks back under the grate.");
            EnterState(DiverState.LurkingUnderGrate);
        }
    }

    /// <summary>The player didn't turn the lights off in time.</summary>
    private void OnReactionFailed()
    {
        if (currentState == DiverState.AtWindow)
        {
            Debug.Log("[Diver] The player missed him at the window - he gets into the facility at CAM 05.");
            EnterState(DiverState.AtCam05);
        }
        else
        {
            // GameMaster ignores this if another animatronic's jumpscare (or
            // any other end of the night) has already started, so two
            // jumpscares can never play at once.
            Debug.Log("[Diver] The player didn't turn the lights off in time - jumpscare!");
            gameMaster.TriggerDiverJumpscare();
        }
    }


    // -------------------------------------------------------------------
    // REGION: STATE CHANGES / VISUALS HELPERS
    // -------------------------------------------------------------------

    /// <summary>
    /// The single place that actually changes currentState. Every
    /// transition above funnels through here, so the office visuals and
    /// GameMaster's camera sprites can never fall out of sync with where he
    /// really is. It:
    ///   1. shows only the office visual that matches the new state,
    ///   2. tells GameMaster which camera (if any) he now occupies, and
    ///   3. resets every timer so each state starts fresh.
    /// </summary>
    private void EnterState(DiverState next)
    {
        currentState = next;

        // 1. Office visuals: the window sprite only while At Window, the
        //    in-office sprite only while In Office.
        SetVisualActive(windowVisualObject, next == DiverState.AtWindow);
        SetVisualActive(officeVisualObject, next == DiverState.InOffice);

        // 2. Camera sprites are owned by GameMaster - we just report where
        //    he is (-1 means "not on any camera of that kind"), and it
        //    redraws both the camera he left and the one he arrived at.
        gameMaster.SetDiverCameraIndex(next == DiverState.AtCam05 ? CAM05_CAMERA_INDEX : -1);
        gameMaster.SetDiverVentCameraIndex(next == DiverState.AtCam08 ? CAM08_VENT_CAMERA_INDEX : -1);

        // 3. Fresh timers. This gives the player a full movement interval
        //    after he arrives somewhere new, and a full reaction window
        //    every time he appears at the window or in the office.
        movementCheckTimer = 0f;
        reactionTimer = 0f;
        isHiding = false;
        hideTimer = 0f;

        Debug.Log("[Diver] Entered state: " + next + ".");
    }

    /// <summary>Small helper to safely show/hide a visual GameObject that might not be assigned yet.</summary>
    private void SetVisualActive(GameObject visualObject, bool active)
    {
        if (visualObject != null)
        {
            visualObject.SetActive(active);
        }
    }
}
