using System.Collections;   // Gives us IEnumerator, needed for the Release Pressure pushback timer coroutine.
using UnityEngine;

/// <summary>
/// Captain Barnacle - the game's main stalker (the Springtrap-equivalent
/// role from the game bible). This script is completely separate from
/// GameMaster on purpose: GameMaster owns the office/oxygen/camera/sonar/
/// release-pressure SYSTEMS, and this script is just one more thing that
/// reacts to those systems - the same way a second or third animatronic
/// script (The Diver, The Depth Stalker, Lumina) will later sit alongside
/// this one without needing to touch GameMaster's internals.
///
/// HOW HE MOVES (see CaptainBarnacle_Design.md at the project root for the
/// full reasoning) - in short:
///   - Every few seconds he rolls a chance ("AI Level") to get a
///     "movement opportunity" at all.
///   - If he gets one, most rooms roll a second chance (that room's
///     "Forward Chance") to decide whether he advances toward the office
///     or retreats away from it.
///   - Two rooms (Cam2 and Cam4) are "branch" rooms where more than one
///     path is available, so they pick a destination directly instead,
///     each with its own Inspector-tunable weights. See
///     ResolveCam2Branch() and ResolveCam4Branch() below.
///   - The defaults are balanced so that most of his attacks come through
///     the Window route (Cam2 - Cam1 - Window - Cam4), not the short
///     Cam2 - Cam3 - Cam4 route.
///   - Reaching the Window or the Door is not instant death/danger - he
///     WAITS there, and his next successful movement opportunity is what
///     actually advances him from the Window to Cam4, or kills the player
///     from the Door.
///   - Every time Lumina jumpscares the player he becomes ENRAGED for a
///     while, rolling his movement checks faster (see Lumina Rage Settings).
///
/// HOW THE PLAYER FIGHTS BACK:
///   - Sonar Ping (fired by GameMaster) can push him back while he is
///     anywhere in Cam1-Cam7, using a FIXED chain (1-2-3-4-5-6-7-2) that is
///     deliberately different from his own roaming logic above.
///   - Release Pressure (held by GameMaster) can push him back ONLY while
///     he is waiting at the Window or the Door, and only if the player
///     holds it for a randomly rolled duration.
/// </summary>
public class CaptainBarnacle : MonoBehaviour
{
    // -------------------------------------------------------------------
    // LOCATIONS
    // -------------------------------------------------------------------
    // Every place Captain Barnacle can be. Cam1..Cam7 match the game
    // bible's camera numbering exactly. AtWindow is the "ghost camera"
    // position between Cam1 and Cam4, visible through the office's front
    // glass. AtDoor is the final, lethal waiting spot right outside the
    // office - the next movement he scores while here ends the night.
    public enum BarnacleLocation
    {
        Cam1,
        Cam2,
        Cam3,
        Cam4,
        Cam5,
        Cam6,
        Cam7,
        AtWindow,
        AtDoor
    }


    // -------------------------------------------------------------------
    // REGION: REFERENCES
    // -------------------------------------------------------------------
    [Header("References")]

    [Tooltip("Drag the scene's GameMaster object here. Used to check whether the night is still in progress, and to end the night if Barnacle reaches the Door.")]
    [SerializeField] private GameMaster gameMaster;


    // -------------------------------------------------------------------
    // REGION: AI SETTINGS
    // -------------------------------------------------------------------
    [Header("AI Settings")]

    [Tooltip("Chance (0-1) that each movement check actually results in a movement opportunity. This is his overall 'AI level' - higher means he acts more often. Rolled every Movement Check Interval Seconds.")]
    [Range(0f, 1f)]
    [SerializeField] private float aiLevel = 0.5f;

    [Tooltip("How often (in real seconds) Barnacle rolls his AI Level to see if he gets a movement opportunity. Game bible: every 5 seconds.")]
    [SerializeField] private float movementCheckIntervalSeconds = 5f;

    [Header("Forward Chances (per room)")]
    // On a successful movement opportunity in one of these five rooms, he
    // rolls that room's chance to move FORWARD (toward the office) rather
    // than backward (away from it). Each room has its own chance so the
    // routes can be balanced separately: by default the Window route
    // (Cam1) pulls him forward hard, while Cam3 and Cam5 are "leaky" - he
    // usually backs out of them - so most of his attacks come through the
    // Window. Cam2 and Cam4 are branch rooms and use their weights below.

    [Tooltip("Cam1: chance to move FORWARD to the Window. Backward goes to Cam2.")]
    [Range(0f, 1f)]
    [SerializeField] private float cam1ForwardChance = 0.8f;

    [Tooltip("Cam3: chance to move FORWARD to Cam4 (the door room). Backward goes to Cam2. Keep this low so the short Cam2-Cam3-Cam4 route stays rare.")]
    [Range(0f, 1f)]
    [SerializeField] private float cam3ForwardChance = 0.15f;

    [Tooltip("Cam5: chance to move FORWARD to Cam4 (the door room). Backward goes to Cam6. Keep this low so he doesn't just bounce Cam4-Cam5-Cam4.")]
    [Range(0f, 1f)]
    [SerializeField] private float cam5ForwardChance = 0.15f;

    [Tooltip("Cam6: chance to move FORWARD to Cam5. Backward goes to Cam7.")]
    [Range(0f, 1f)]
    [SerializeField] private float cam6ForwardChance = 0.5f;

    [Tooltip("Cam7: chance to move FORWARD to Cam2. Backward goes to Cam6.")]
    [Range(0f, 1f)]
    [SerializeField] private float cam7ForwardChance = 0.5f;

    [Header("Cam2 Branch Weights")]
    // Cam2 is a hub with three exits, so a successful movement opportunity
    // there picks one of them directly using these weights. They are
    // RELATIVE weights, not percentages - they don't need to add up to 100.
    // Each room's chance is its weight divided by the total of all three,
    // e.g. 60 / 15 / 25 -> Cam1 60%, Cam3 15%, Cam7 25%, and 2 / 1 / 1 ->
    // Cam1 50%, Cam3 25%, Cam7 25%.

    [Tooltip("Relative weight for moving from Cam2 to Cam1 (toward the Window route). Chance = this weight / the total of all three Cam2 weights.")]
    [Min(0f)]
    [SerializeField] private float cam2ToCam1Weight = 60f;

    [Tooltip("Relative weight for moving from Cam2 to Cam3 (the short route toward the Door). Chance = this weight / the total of all three Cam2 weights.")]
    [Min(0f)]
    [SerializeField] private float cam2ToCam3Weight = 15f;

    [Tooltip("Relative weight for moving from Cam2 back to Cam7 (retreating into the long route). Chance = this weight / the total of all three Cam2 weights.")]
    [Min(0f)]
    [SerializeField] private float cam2ToCam7Weight = 25f;

    [Header("Cam4 Branch Weights")]
    // Cam4 is the door room. A successful movement opportunity here picks
    // one of these three using the same relative-weight rule as Cam2.
    // Setting all three to 1 gives the game bible's original flat 1-in-3.
    // By default he retreats to Cam3 more than Cam5, because Cam3 usually
    // sends him back to Cam2 - and from there, toward the Window route.

    [Tooltip("Relative weight for retreating from Cam4 to Cam3. Chance = this weight / the total of all three Cam4 weights.")]
    [Min(0f)]
    [SerializeField] private float cam4ToCam3Weight = 2f;

    [Tooltip("Relative weight for retreating from Cam4 to Cam5. Chance = this weight / the total of all three Cam4 weights.")]
    [Min(0f)]
    [SerializeField] private float cam4ToCam5Weight = 1f;

    [Tooltip("Relative weight for moving from Cam4 to the Door (the lethal waiting spot). Chance = this weight / the total of all three Cam4 weights.")]
    [Min(0f)]
    [SerializeField] private float cam4ToDoorWeight = 1.5f;


    // -------------------------------------------------------------------
    // REGION: SPAWN SETTINGS
    // -------------------------------------------------------------------
    [Header("Spawn Settings")]

    [Tooltip("The lowest camera NUMBER (1-7, matching the game bible's Cam01-Cam07 labels) he can spawn in.")]
    [Range(1, 7)]
    [SerializeField] private int spawnMinCameraNumber = 3;

    [Tooltip("The highest camera NUMBER (1-7) he can spawn in. Game bible: he should spawn somewhere in Cam03-Cam07.")]
    [Range(1, 7)]
    [SerializeField] private int spawnMaxCameraNumber = 7;


    // -------------------------------------------------------------------
    // REGION: RELEASE PRESSURE PUSHBACK SETTINGS
    // -------------------------------------------------------------------
    [Header("Release Pressure Pushback Settings")]

    [Tooltip("The shortest amount of time (in real seconds) the player might need to hold Release Pressure to push Barnacle back from the Window or the Door. A fresh random value between this and the Max is rolled every time the player starts holding.")]
    [SerializeField] private float pushbackHoldMinSeconds = 1f;

    [Tooltip("The longest amount of time (in real seconds) the player might need to hold Release Pressure to push Barnacle back from the Window or the Door.")]
    [SerializeField] private float pushbackHoldMaxSeconds = 3f;


    // -------------------------------------------------------------------
    // REGION: LUMINA RAGE SETTINGS
    // -------------------------------------------------------------------
    [Header("Lumina Rage Settings")]

    [Tooltip("How much faster Barnacle's movement check timer ticks right after a Lumina jumpscare. 2 = checks twice as often (a 5s interval becomes 2.5s). His AI Level itself is unchanged.")]
    [Min(1f)]
    [SerializeField] private float luminaRageSpeedMultiplier = 2f;

    [Tooltip("How long (in real seconds) the rage lasts after a Lumina jumpscare. Another Lumina jumpscare during the rage restarts this timer from full (it does not stack).")]
    [Min(0f)]
    [SerializeField] private float luminaRageDurationSeconds = 25f;


    // -------------------------------------------------------------------
    // REGION: VISUALS - WINDOW (GHOST CAMERA)
    // -------------------------------------------------------------------
    [Header("Visuals - Window (Ghost Camera)")]

    [Tooltip("The 'Barnacle behind the office glass' GameObject, placed directly in the office scene (not inside the Camera Monitor). Should start INACTIVE in the editor - this script activates it only while Barnacle is waiting At Window, and deactivates it the moment he leaves.")]
    [SerializeField] private GameObject windowVisualObject;


    // -------------------------------------------------------------------
    // REGION: RUNTIME STATE
    // -------------------------------------------------------------------

    // Where Barnacle currently is. Set once at spawn, then only ever
    // changed by MoveTo() below, so there is exactly one place in this
    // whole script that changes it.
    private BarnacleLocation currentLocation;

    /// <summary>
    /// Read-only access to where Barnacle really is right now. Added so
    /// BarnacleHallucination never puts a fake Barnacle on top of the real
    /// one, without needing a public setter.
    /// </summary>
    public BarnacleLocation CurrentLocation => currentLocation;

    // Counts up every frame (see Update()) until it reaches
    // movementCheckIntervalSeconds, at which point we roll for a movement
    // opportunity and reset this back to 0.
    private float movementCheckTimer;

    // The in-progress "is the player holding Release Pressure long enough
    // to push Barnacle back" coroutine, if one is currently running. Kept
    // so HandleReleasePressureStopped() can cancel it if the player lets
    // go before the rolled duration elapses.
    private Coroutine pushbackHoldCoroutine;

    // How many seconds of Lumina rage are left. Above 0 means he is
    // enraged and his movement check timer ticks luminaRageSpeedMultiplier
    // times faster. Set by HandleLuminaJumpscare(), counted down in Update().
    private float luminaRageTimeRemaining;


    // -------------------------------------------------------------------
    // REGION: UNITY LIFECYCLE
    // -------------------------------------------------------------------

    /// <summary>
    /// Subscribes to every GameMaster event this script cares about. Using
    /// OnEnable/OnDisable (rather than Start/never) is the correct pattern
    /// for STATIC events - it guarantees we are always subscribed exactly
    /// once, even if this GameObject is disabled and re-enabled later.
    /// </summary>
    private void OnEnable()
    {
        GameMaster.OnSonarPing += HandleSonarPing;
        GameMaster.OnReleasePressureStarted += HandleReleasePressureStarted;
        GameMaster.OnReleasePressureStopped += HandleReleasePressureStopped;
        GameMaster.OnLuminaJumpscare += HandleLuminaJumpscare;
    }

    /// <summary>Unsubscribes from every event we subscribed to in OnEnable(), to avoid leaking a subscription onto a destroyed/disabled object.</summary>
    private void OnDisable()
    {
        GameMaster.OnSonarPing -= HandleSonarPing;
        GameMaster.OnReleasePressureStarted -= HandleReleasePressureStarted;
        GameMaster.OnReleasePressureStopped -= HandleReleasePressureStopped;
        GameMaster.OnLuminaJumpscare -= HandleLuminaJumpscare;
    }

    /// <summary>Picks Barnacle's spawn room for the night. All sprite work happens on GameMaster - see SpawnAtRandomCamera()/MoveTo() below.</summary>
    private void Start()
    {
        // Make sure the window visual starts hidden, regardless of how it
        // was left in the editor - it should only ever be visible while
        // currentLocation == AtWindow.
        if (windowVisualObject != null)
        {
            windowVisualObject.SetActive(false);
        }

        SpawnAtRandomCamera();
    }

    /// <summary>Ticks the movement-opportunity timer for as long as the night is still in progress.</summary>
    private void Update()
    {
        // Don't act at all once the night has ended (Victory or GameOver) -
        // GameMaster itself freezes everything at that point, and we should
        // match that rather than keep rolling movement checks into the void.
        if (gameMaster == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        // While enraged by a Lumina jumpscare, his timer ticks faster, so
        // movement checks come around more often.
        float timerSpeed = 1f;
        if (luminaRageTimeRemaining > 0f)
        {
            timerSpeed = luminaRageSpeedMultiplier;
            luminaRageTimeRemaining -= Time.deltaTime;

            if (luminaRageTimeRemaining <= 0f)
            {
                Debug.Log("[Barnacle] Lumina rage ended - back to his normal pace.");
            }
        }

        movementCheckTimer += Time.deltaTime * timerSpeed;
        if (movementCheckTimer >= movementCheckIntervalSeconds)
        {
            movementCheckTimer = 0f;
            RollMovementOpportunity();
        }
    }


    // -------------------------------------------------------------------
    // REGION: SPAWNING
    // -------------------------------------------------------------------

    /// <summary>Picks a random camera number between spawnMinCameraNumber and spawnMaxCameraNumber (inclusive) and starts Barnacle there.</summary>
    private void SpawnAtRandomCamera()
    {
        // Random.Range(int, int) treats the upper bound as EXCLUSIVE, so we
        // add 1 to make spawnMaxCameraNumber itself reachable.
        int spawnCameraNumber = Random.Range(spawnMinCameraNumber, spawnMaxCameraNumber + 1);
        currentLocation = CameraNumberToLocation(spawnCameraNumber);

        Debug.Log("[Barnacle] Spawned at Cam" + spawnCameraNumber + ".");

        // Tell GameMaster he now occupies this camera - there is no
        // "previous location" to clean up on the very first placement, so
        // we report it directly rather than going through MoveTo().
        TryGetCameraIndex(currentLocation, out int spawnCameraIndex);
        gameMaster.SetCaptainBarnacleCameraIndex(spawnCameraIndex);
    }


    // -------------------------------------------------------------------
    // REGION: MOVEMENT - THE TIMER ROLL
    // -------------------------------------------------------------------

    /// <summary>Rolls his AI Level to see if this check produces a movement opportunity, and resolves one if it does.</summary>
    private void RollMovementOpportunity()
    {
        float roll = Random.value;
        bool opportunitySucceeded = roll <= aiLevel;

        Debug.Log("[Barnacle] Movement check at " + currentLocation + ": rolled " + roll.ToString("F2") + " vs AI Level " + aiLevel.ToString("F2") + " -> " + (opportunitySucceeded ? "movement opportunity!" : "no movement."));

        if (!opportunitySucceeded)
        {
            return;
        }

        ResolveMovement();
    }

    /// <summary>
    /// Decides what a successful movement opportunity actually does, based
    /// on where Barnacle currently is. See CaptainBarnacle_Design.md for the
    /// full reasoning behind each room's rule - in short: most rooms use the
    /// shared forward/backward roll, Cam2 and Cam4 are branch rooms with
    /// their own resolution methods, and AtWindow/AtDoor don't roll
    /// forward/backward at all - any success there just advances them.
    /// </summary>
    private void ResolveMovement()
    {
        switch (currentLocation)
        {
            case BarnacleLocation.AtDoor:
                // The game bible's kill condition: the next movement he
                // scores while waiting at the door catches the player.
                // GameMaster plays his jumpscare first and only shows the
                // real Game Over screen once that beat finishes.
                Debug.Log("[Barnacle] He moved from the Door. Jumpscare triggered.");
                gameMaster.TriggerCaptainBarnacleJumpscare();
                return;

            case BarnacleLocation.AtWindow:
                // No forward/backward roll here - any success advances him
                // from the window straight to Cam4, mirroring the door.
                MoveTo(BarnacleLocation.Cam4);
                return;

            case BarnacleLocation.Cam2:
                ResolveCam2Branch();
                return;

            case BarnacleLocation.Cam4:
                ResolveCam4Branch();
                return;

            default:
                ResolveStandardForwardBackward();
                return;
        }
    }

    /// <summary>
    /// Handles every room that has exactly one forward neighbor and one
    /// backward neighbor (Cam1, Cam3, Cam5, Cam6, Cam7). Rolls that room's
    /// own forward chance and moves to whichever neighbor that roll selects.
    /// </summary>
    private void ResolveStandardForwardBackward()
    {
        float forwardChance = GetForwardChance(currentLocation);
        bool movingForward = Random.value <= forwardChance;
        BarnacleLocation destination = movingForward
            ? GetForwardNeighbor(currentLocation)
            : GetBackwardNeighbor(currentLocation);

        Debug.Log("[Barnacle] Forward/backward roll at " + currentLocation + " (forward chance " + forwardChance.ToString("F2") + ") -> " + (movingForward ? "FORWARD" : "backward") + " to " + destination + ".");
        MoveTo(destination);
    }

    /// <summary>
    /// Cam2 is a branch room with three neighbors (Cam1, Cam3, Cam7). A
    /// successful movement opportunity here makes ONE weighted pick between
    /// them using cam2ToCam1Weight / cam2ToCam3Weight / cam2ToCam7Weight
    /// (default 60 / 15 / 25) - there is no forward/backward roll.
    /// </summary>
    private void ResolveCam2Branch()
    {
        int pick = PickWeighted(cam2ToCam1Weight, cam2ToCam3Weight, cam2ToCam7Weight, "Cam2", out float chosenPercent);
        BarnacleLocation destination = (pick == 0) ? BarnacleLocation.Cam1 : (pick == 1) ? BarnacleLocation.Cam3 : BarnacleLocation.Cam7;

        Debug.Log("[Barnacle] Cam2 branch roll (weighted) -> " + destination + " (" + chosenPercent.ToString("F0") + "% chance).");
        MoveTo(destination);
    }

    /// <summary>
    /// Cam4 is the other branch room: the door room. A successful movement
    /// opportunity here makes ONE weighted pick between Cam3, Cam5 and the
    /// Door using cam4ToCam3Weight / cam4ToCam5Weight / cam4ToDoorWeight
    /// (default 2 / 1 / 1.5 - set all three to 1 for the game bible's
    /// original flat 1-in-3). Cam1 is deliberately excluded - the Window
    /// edge only ever runs Cam1 -> Cam4, never the reverse.
    /// </summary>
    private void ResolveCam4Branch()
    {
        int pick = PickWeighted(cam4ToCam3Weight, cam4ToCam5Weight, cam4ToDoorWeight, "Cam4", out float chosenPercent);
        BarnacleLocation destination = (pick == 0) ? BarnacleLocation.Cam3 : (pick == 1) ? BarnacleLocation.Cam5 : BarnacleLocation.AtDoor;

        Debug.Log("[Barnacle] Cam4 branch roll (weighted) -> " + destination + " (" + chosenPercent.ToString("F0") + "% chance).");
        MoveTo(destination);
    }

    /// <summary>
    /// Shared weighted pick used by both branch rooms. Returns 0, 1 or 2 -
    /// which of the three weights was picked - and outputs that option's
    /// chance as a percentage (for the Console log).
    ///
    /// How it works: imagine the three weights laid end to end on a ruler
    /// of length "total". We pick a random point on that ruler, and
    /// whichever option's stretch it lands in is the one picked - so a
    /// bigger weight means a bigger stretch, and a higher chance.
    /// </summary>
    private int PickWeighted(float weightA, float weightB, float weightC, string roomName, out float chosenPercent)
    {
        float total = weightA + weightB + weightC;

        // All three weights set to 0 would leave nothing to pick from -
        // warn and fall back to an even 1-in-3 pick instead of breaking.
        if (total <= 0f)
        {
            Debug.LogWarning("[Barnacle] All three " + roomName + " branch weights are 0 - falling back to an even 1-in-3 pick. Set at least one weight above 0 in the Inspector.");
            chosenPercent = 100f / 3f;
            return Random.Range(0, 3); // 0, 1, or 2 - each equally likely.
        }

        float roll = Random.value * total;

        int pick;
        float chosenWeight;
        if (roll < weightA)
        {
            pick = 0;
            chosenWeight = weightA;
        }
        else if (roll < weightA + weightB)
        {
            pick = 1;
            chosenWeight = weightB;
        }
        else
        {
            pick = 2;
            chosenWeight = weightC;
        }

        chosenPercent = chosenWeight / total * 100f;
        return pick;
    }

    /// <summary>The forward chance for every non-branch room (Cam1, Cam3, Cam5, Cam6, Cam7), read from the Forward Chances Inspector fields. Do not call this for Cam2/Cam4.</summary>
    private float GetForwardChance(BarnacleLocation location)
    {
        switch (location)
        {
            case BarnacleLocation.Cam1: return cam1ForwardChance;
            case BarnacleLocation.Cam3: return cam3ForwardChance;
            case BarnacleLocation.Cam5: return cam5ForwardChance;
            case BarnacleLocation.Cam6: return cam6ForwardChance;
            case BarnacleLocation.Cam7: return cam7ForwardChance;
            default:
                Debug.LogWarning("[Barnacle] GetForwardChance called with an unsupported location: " + location);
                return 0.5f;
        }
    }

    /// <summary>The single forward neighbor for every non-branch room (Cam1, Cam3, Cam5, Cam6, Cam7). Do not call this for Cam2/Cam4 - they have their own resolver methods above.</summary>
    private BarnacleLocation GetForwardNeighbor(BarnacleLocation location)
    {
        switch (location)
        {
            case BarnacleLocation.Cam1: return BarnacleLocation.AtWindow;
            case BarnacleLocation.Cam3: return BarnacleLocation.Cam4;
            case BarnacleLocation.Cam5: return BarnacleLocation.Cam4;
            case BarnacleLocation.Cam6: return BarnacleLocation.Cam5;
            case BarnacleLocation.Cam7: return BarnacleLocation.Cam2;
            default:
                Debug.LogWarning("[Barnacle] GetForwardNeighbor called with an unsupported location: " + location);
                return location;
        }
    }

    /// <summary>The single backward neighbor for every non-branch room (Cam1, Cam3, Cam5, Cam6, Cam7). Do not call this for Cam2/Cam4 - they have their own resolver methods above.</summary>
    private BarnacleLocation GetBackwardNeighbor(BarnacleLocation location)
    {
        switch (location)
        {
            case BarnacleLocation.Cam1: return BarnacleLocation.Cam2;
            case BarnacleLocation.Cam3: return BarnacleLocation.Cam2;
            case BarnacleLocation.Cam5: return BarnacleLocation.Cam6;
            case BarnacleLocation.Cam6: return BarnacleLocation.Cam7;
            case BarnacleLocation.Cam7: return BarnacleLocation.Cam6;
            default:
                Debug.LogWarning("[Barnacle] GetBackwardNeighbor called with an unsupported location: " + location);
                return location;
        }
    }


    // -------------------------------------------------------------------
    // REGION: SONAR PING COUNTERPLAY
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires once a Sonar Ping's illumination flash finishes and its
    /// pushback roll has already been decided by GameMaster (GameMaster
    /// handles the transient "occupied + lit" sprite entirely on its own,
    /// since it already knows who occupies which camera - see
    /// SetCaptainBarnacleCameraIndex() and SonarPingRoutine() there). All
    /// this handler does is decide whether Barnacle actually retreats.
    /// </summary>
    private void HandleSonarPing(int cameraIndex, bool isVentCamera, bool pushbackRollSucceeded)
    {
        if (isVentCamera || !TryGetCameraIndex(currentLocation, out int myCameraIndex))
        {
            return;
        }

        if (myCameraIndex != cameraIndex)
        {
            return;
        }

        if (!pushbackRollSucceeded)
        {
            Debug.Log("[Barnacle] Sonar Ping hit him at Cam" + (myCameraIndex + 1) + ", but the pushback roll FAILED (RNG Fail State) - he holds his ground.");
            return;
        }

        BarnacleLocation retreatTarget = GetSonarRetreatTarget(currentLocation);
        Debug.Log("[Barnacle] Sonar Ping hit him at Cam" + (myCameraIndex + 1) + " and the pushback roll SUCCEEDED - retreating to " + retreatTarget + ".");
        MoveTo(retreatTarget);
    }

    /// <summary>
    /// The FIXED chain Sonar Ping pushback uses: Cam1->Cam2->Cam3->Cam4->
    /// Cam5->Cam6->Cam7->Cam2 (wrapping via the existing Cam7-Cam2 edge).
    /// This is deliberately separate from GetForwardNeighbor/
    /// GetBackwardNeighbor above - the game bible describes Sonar pushback
    /// as a simple "push to the next-numbered camera" rule, which is NOT
    /// always the same as his roaming AI's "backward" direction (pushing
    /// him from Cam3 to Cam4 is explicitly called out as dangerous for the
    /// player, since Cam4 is the door room).
    /// </summary>
    private BarnacleLocation GetSonarRetreatTarget(BarnacleLocation location)
    {
        switch (location)
        {
            case BarnacleLocation.Cam1: return BarnacleLocation.Cam2;
            case BarnacleLocation.Cam2: return BarnacleLocation.Cam3;
            case BarnacleLocation.Cam3: return BarnacleLocation.Cam4;
            case BarnacleLocation.Cam4: return BarnacleLocation.Cam5;
            case BarnacleLocation.Cam5: return BarnacleLocation.Cam6;
            case BarnacleLocation.Cam6: return BarnacleLocation.Cam7;
            case BarnacleLocation.Cam7: return BarnacleLocation.Cam2;
            default:
                Debug.LogWarning("[Barnacle] GetSonarRetreatTarget called with an unsupported location: " + location);
                return location;
        }
    }


    // -------------------------------------------------------------------
    // REGION: RELEASE PRESSURE COUNTERPLAY
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires the instant the player starts holding Release Pressure. Only
    /// matters if Barnacle is currently waiting At Window or At Door - it
    /// has zero effect on him anywhere else (Sonar Ping is the tool for
    /// Cam1-Cam7, per the game bible).
    /// </summary>
    private void HandleReleasePressureStarted()
    {
        if (currentLocation != BarnacleLocation.AtWindow && currentLocation != BarnacleLocation.AtDoor)
        {
            return;
        }

        float requiredHoldSeconds = Random.Range(pushbackHoldMinSeconds, pushbackHoldMaxSeconds);
        Debug.Log("[Barnacle] Release Pressure engaged while he waits " + currentLocation + " - needs to be held for " + requiredHoldSeconds.ToString("F2") + "s to push him back.");

        pushbackHoldCoroutine = StartCoroutine(PushbackHoldRoutine(requiredHoldSeconds));
    }

    /// <summary>
    /// Fires the instant the player lets go of Release Pressure. If a
    /// pushback timer was running, cancel it - letting go early means no
    /// pushback happens, and Barnacle's own movement timer simply keeps
    /// running independently in the background.
    /// </summary>
    private void HandleReleasePressureStopped()
    {
        if (pushbackHoldCoroutine == null)
        {
            return;
        }

        StopCoroutine(pushbackHoldCoroutine);
        pushbackHoldCoroutine = null;
        Debug.Log("[Barnacle] Release Pressure let go before the required hold time - no pushback.");
    }

    /// <summary>
    /// Waits out the rolled hold duration, then - since this coroutine only
    /// ever reaches this point if the player kept holding the whole time
    /// (HandleReleasePressureStopped() above cancels it otherwise) - pushes
    /// Barnacle back to a random valid target for whichever special state
    /// he was waiting in.
    /// </summary>
    private IEnumerator PushbackHoldRoutine(float requiredHoldSeconds)
    {
        yield return new WaitForSeconds(requiredHoldSeconds);

        BarnacleLocation pushbackTarget;
        if (currentLocation == BarnacleLocation.AtWindow)
        {
            pushbackTarget = (Random.value < 0.5f) ? BarnacleLocation.Cam1 : BarnacleLocation.Cam2;
        }
        else
        {
            // At the Door - pick uniformly among the four rooms the game
            // bible lists (Cam3, Cam5, Cam6, Cam7).
            int pick = Random.Range(0, 4);
            switch (pick)
            {
                case 0: pushbackTarget = BarnacleLocation.Cam3; break;
                case 1: pushbackTarget = BarnacleLocation.Cam5; break;
                case 2: pushbackTarget = BarnacleLocation.Cam6; break;
                default: pushbackTarget = BarnacleLocation.Cam7; break;
            }
        }

        Debug.Log("[Barnacle] Held Release Pressure long enough! Pushed back from " + currentLocation + " to " + pushbackTarget + ".");
        MoveTo(pushbackTarget);
        pushbackHoldCoroutine = null;
    }


    // -------------------------------------------------------------------
    // REGION: LUMINA RAGE
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires the instant Lumina jumpscares the player. Starts (or restarts
    /// from full) his rage window - see the Lumina Rage Settings above and
    /// Update() for how it speeds up his movement checks.
    /// </summary>
    private void HandleLuminaJumpscare()
    {
        luminaRageTimeRemaining = luminaRageDurationSeconds;
        Debug.Log("[Barnacle] Lumina jumpscared the player - he is ENRAGED (movement checks x" + luminaRageSpeedMultiplier.ToString("F1") + " for " + luminaRageDurationSeconds.ToString("F0") + "s).");
    }


    // -------------------------------------------------------------------
    // REGION: SHARED MOVEMENT / VISUALS HELPERS
    // -------------------------------------------------------------------

    /// <summary>
    /// The single place that actually changes currentLocation. Reports the
    /// change to GameMaster (which owns every camera sprite and decides
    /// what a location change actually looks like) and handles the Window/
    /// Door visuals that are local to this script. Every movement path
    /// above (roaming, Sonar pushback, Release Pressure pushback) funnels
    /// through here so GameMaster's occupancy state can never fall out of
    /// sync with currentLocation.
    /// </summary>
    private void MoveTo(BarnacleLocation next)
    {
        BarnacleLocation previous = currentLocation;

        // Hide the window visual if we're leaving that state - this stays
        // local to this script since it's a plain scene GameObject toggle,
        // not a camera-feed sprite GameMaster owns.
        if (previous == BarnacleLocation.AtWindow && windowVisualObject != null)
        {
            windowVisualObject.SetActive(false);
        }

        currentLocation = next;

        // Tell GameMaster which regular camera (if any) he now occupies -
        // it will refresh both the camera he left and the one he arrived
        // at, reverting either one to empty if it maps to -1 (i.e. isn't
        // one of Cam1-Cam7).
        TryGetCameraIndex(next, out int nextCameraIndex);
        gameMaster.SetCaptainBarnacleCameraIndex(nextCameraIndex);

        if (next == BarnacleLocation.AtWindow && windowVisualObject != null)
        {
            windowVisualObject.SetActive(true);
        }
        else if (next == BarnacleLocation.AtDoor)
        {
            // Game bible: no animation yet for the Door state, just a
            // one-time log the instant he enters it.
            Debug.Log("[Barnacle] ENTERED THE DOOR. He is now waiting right outside the office - his next successful movement will end the night unless Release Pressure pushes him back first.");
        }
    }

    /// <summary>Converts a camera NUMBER (1-7, matching the game bible's Cam01-Cam07 labels) into its BarnacleLocation. Public and static so BarnacleHallucination can reuse it.</summary>
    public static BarnacleLocation CameraNumberToLocation(int cameraNumber)
    {
        // Cam1 is enum value 0, Cam2 is 1, and so on - camera NUMBER minus
        // one lines up exactly with the enum's declaration order above.
        return (BarnacleLocation)(cameraNumber - 1);
    }

    /// <summary>
    /// Converts a BarnacleLocation into its 0-based camera index, matching
    /// GameMaster's own cameraFeedViews/cameraIndex numbering exactly (that
    /// numbering is what SetCaptainBarnacleCameraIndex() and the Sonar Ping
    /// event both use). Returns false for AtWindow and AtDoor, since
    /// neither of those is one of the 7 regular pingable/viewable cameras.
    /// Public and static (it uses no per-Barnacle state) so
    /// BarnacleHallucination can reuse the same mapping.
    /// </summary>
    public static bool TryGetCameraIndex(BarnacleLocation location, out int index)
    {
        switch (location)
        {
            case BarnacleLocation.Cam1: index = 0; return true;
            case BarnacleLocation.Cam2: index = 1; return true;
            case BarnacleLocation.Cam3: index = 2; return true;
            case BarnacleLocation.Cam4: index = 3; return true;
            case BarnacleLocation.Cam5: index = 4; return true;
            case BarnacleLocation.Cam6: index = 5; return true;
            case BarnacleLocation.Cam7: index = 6; return true;
            default:
                index = -1;
                return false;
        }
    }
}
