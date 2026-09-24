using UnityEngine;

/// <summary>
/// Lumina - the "Hypoxia Phantom". She is a FNAF3-style phantom: she can
/// jumpscare the player as many times as she likes, but her jumpscares
/// NEVER end the night. Her job is to distract the player from fending off
/// Captain Barnacle and The Diver - and every jumpscare she lands also
/// makes Barnacle more aggressive for a while (see CaptainBarnacle's Lumina
/// Rage Settings). Like the other animatronics, this script is completely
/// separate from GameMaster: it only reads GameMaster's state, listens to
/// its camera events, and asks it to play her jumpscare.
///
/// WHEN SHE EXISTS:
///   - Only while the player is Hypoxic (oxygen below GameMaster's
///     hypoxiaThresholdPercent, 50% by default). Above that she is Absent
///     and does nothing at all.
///   - After spawning, and after every appearance ends, she waits
///     appearanceCooldownSeconds before she can appear again. She is only
///     ever in one place at a time.
///
/// HOW SHE APPEARS - two ways:
///   1. ON CAMERA: every time the player opens the monitor, switches camera
///      or flips the Map, she rolls cameraAppearChance to show up over that
///      feed (one shared overlay sprite, whichever camera it is). Switching
///      away or closing the monitor makes her vanish. Stare at her for
///      cameraStareSeconds and she slams the monitor shut and jumpscares.
///   2. AT THE WINDOW: every windowCheckIntervalSeconds she rolls
///      windowAppearChance to appear behind the office glass. Every second
///      the player spends with NO panel open counts as "looking at her" -
///      reach windowLookSeconds in total and she jumpscares. That look time
///      adds up across opening/closing panels. She leaves on her own after
///      windowLeaveSeconds at the window, no matter what.
///
/// HOW THE PLAYER FIGHTS BACK:
///   - On camera: switch camera or close the monitor quickly.
///   - At the window: hide behind a panel (cameras or maintenance) until
///     she leaves.
///   - Lights, Sonar Ping and Release Pressure do nothing to her.
/// </summary>
public class Lumina : MonoBehaviour
{
    // -------------------------------------------------------------------
    // STATES
    // -------------------------------------------------------------------
    public enum LuminaState
    {
        Absent,     // Oxygen is above the Hypoxia threshold - she isn't in the map at all.
        Waiting,    // Hypoxic - invisible, waiting out her cooldown and rolling to appear.
        OnCamera,   // Overlaid on the camera feed the player is looking at.
        AtWindow    // Standing behind the office window.
    }


    // -------------------------------------------------------------------
    // REGION: REFERENCES
    // -------------------------------------------------------------------
    [Header("References")]

    [Tooltip("Drag the scene's GameMaster object here. Used to read the Hypoxia and panel state, to force-close the Camera Monitor, and to trigger her jumpscare.")]
    [SerializeField] private GameMaster gameMaster;


    // -------------------------------------------------------------------
    // REGION: APPEARANCE SETTINGS
    // -------------------------------------------------------------------
    [Header("Appearance Settings")]

    [Tooltip("How long (in real seconds) she waits before she is allowed to appear again - counted from when she first spawns (Hypoxia starts) and from the end of every appearance (vanished, left, or jumpscared).")]
    [Min(0f)]
    [SerializeField] private float appearanceCooldownSeconds = 10f;

    [Header("Camera Appearance")]

    [Tooltip("Chance (0-1) that she appears on the camera feed every time the player opens the Camera Monitor, switches camera, or flips the Map. Keep this LOW.")]
    [Range(0f, 1f)]
    [SerializeField] private float cameraAppearChance = 0.1f;

    [Tooltip("How long (in real seconds) the player can keep looking at her on camera before she force-closes the monitor and jumpscares them.")]
    [Min(0f)]
    [SerializeField] private float cameraStareSeconds = 2f;

    [Header("Window Appearance")]

    [Tooltip("How often (in real seconds) she rolls to appear at the office window, once her cooldown is over.")]
    [Min(0.1f)]
    [SerializeField] private float windowCheckIntervalSeconds = 5f;

    [Tooltip("Chance (0-1) that each window roll makes her appear behind the office window.")]
    [Range(0f, 1f)]
    [SerializeField] private float windowAppearChance = 0.15f;

    [Tooltip("How long (in real seconds, in TOTAL) the player can look at her - i.e. have no panel open - before she jumpscares. Time spent looking adds up even if the player opens and closes panels in between.")]
    [Min(0f)]
    [SerializeField] private float windowLookSeconds = 5f;

    [Tooltip("How long (in real seconds) she stays at the window before leaving on her own without a jumpscare. Counts the whole time she is there, whether or not the player is looking.")]
    [Min(0f)]
    [SerializeField] private float windowLeaveSeconds = 10f;


    // -------------------------------------------------------------------
    // REGION: VISUALS
    // -------------------------------------------------------------------
    [Header("Visuals")]

    [Tooltip("Her single camera overlay sprite - a UI Image inside the Camera Monitor that covers the feed area (for both the regular and vent layers). Should start INACTIVE - this script turns it on only while she is On Camera.")]
    [SerializeField] private GameObject cameraOverlayObject;

    [Tooltip("The 'Lumina behind the office glass' GameObject, placed in the office scene (a child of the office sprite, so it pans with it). Should start INACTIVE - this script turns it on only while she is At Window.")]
    [SerializeField] private GameObject windowVisualObject;


    // -------------------------------------------------------------------
    // REGION: RUNTIME STATE
    // -------------------------------------------------------------------

    // Which state Lumina is in right now. Only ever changed by EnterState()
    // below, so there is exactly one place that changes it.
    private LuminaState currentState;

    // While Waiting: how long she has been waiting. She can only appear once
    // this reaches appearanceCooldownSeconds (see IsReadyToAppear()).
    private float cooldownTimer;

    // While Waiting (and ready): counts up to windowCheckIntervalSeconds,
    // at which point she rolls to appear at the window.
    private float windowCheckTimer;

    // While On Camera: how long the player has been looking at her.
    private float cameraStareTimer;

    // While At Window: total time the player has spent looking at her (no
    // panel open). Never resets while she is there, so it adds up.
    private float windowLookTimer;

    // While At Window: total time she has been there, looked at or not.
    private float windowLeaveTimer;


    // -------------------------------------------------------------------
    // REGION: UNITY LIFECYCLE
    // -------------------------------------------------------------------

    /// <summary>Subscribes to GameMaster's camera events (static events, so OnEnable/OnDisable - same pattern as CaptainBarnacle).</summary>
    private void OnEnable()
    {
        GameMaster.OnCameraViewChanged += HandleCameraViewChanged;
        GameMaster.OnCameraMonitorClosed += HandleCameraMonitorClosed;
    }

    /// <summary>Unsubscribes from every event we subscribed to in OnEnable().</summary>
    private void OnDisable()
    {
        GameMaster.OnCameraViewChanged -= HandleCameraViewChanged;
        GameMaster.OnCameraMonitorClosed -= HandleCameraMonitorClosed;
    }

    /// <summary>Starts the night with Lumina Absent and both visuals hidden.</summary>
    private void Start()
    {
        EnterState(LuminaState.Absent);
    }

    /// <summary>Spawns/despawns her with Hypoxia, then runs whichever logic matches her current state.</summary>
    private void Update()
    {
        // Don't act at all once the night is no longer being played, exactly
        // like the other animatronics. Note that her OWN jumpscare does not
        // stop the night, so she keeps running through it.
        if (gameMaster == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        // She only exists while the player is Hypoxic.
        if (!gameMaster.IsHypoxic)
        {
            if (currentState != LuminaState.Absent)
            {
                Debug.Log("[Lumina] Oxygen is back above the Hypoxia threshold - she fades away.");
                EnterState(LuminaState.Absent);
            }
            return;
        }

        if (currentState == LuminaState.Absent)
        {
            Debug.Log("[Lumina] The player is Hypoxic - Lumina has spawned.");
            EnterState(LuminaState.Waiting);
            return;
        }

        switch (currentState)
        {
            case LuminaState.Waiting:
                UpdateWaiting();
                break;

            case LuminaState.OnCamera:
                UpdateOnCamera();
                break;

            case LuminaState.AtWindow:
                UpdateAtWindow();
                break;
        }
    }


    // -------------------------------------------------------------------
    // REGION: WAITING
    // -------------------------------------------------------------------

    /// <summary>
    /// Waiting: counts down her cooldown, then rolls windowAppearChance
    /// every windowCheckIntervalSeconds. (Her camera rolls don't happen here
    /// - they happen in HandleCameraViewChanged(), whenever the player
    /// changes what's on the monitor.)
    /// </summary>
    private void UpdateWaiting()
    {
        cooldownTimer += Time.deltaTime;
        if (!IsReadyToAppear())
        {
            return;
        }

        windowCheckTimer += Time.deltaTime;
        if (windowCheckTimer < windowCheckIntervalSeconds)
        {
            return;
        }
        windowCheckTimer = 0f;

        float roll = Random.value;
        bool appears = roll <= windowAppearChance;
        Debug.Log("[Lumina] Window check: rolled " + roll.ToString("F2") + " vs Window Appear Chance " + windowAppearChance.ToString("F2") + " -> " + (appears ? "appears at the window!" : "stays hidden."));

        if (appears)
        {
            EnterState(LuminaState.AtWindow);
        }
    }

    /// <summary>True once she has waited out her full cooldown since her last appearance (or since spawning).</summary>
    private bool IsReadyToAppear()
    {
        return cooldownTimer >= appearanceCooldownSeconds;
    }


    // -------------------------------------------------------------------
    // REGION: CAMERA APPEARANCE
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires every time the player puts a new feed on the monitor (opening
    /// it, switching camera, or flipping the Map). If she was already on
    /// camera, the player looked away in time and she vanishes. Otherwise,
    /// if she is ready, she rolls to appear on the new feed.
    /// </summary>
    private void HandleCameraViewChanged()
    {
        if (gameMaster == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        if (currentState == LuminaState.OnCamera)
        {
            Debug.Log("[Lumina] The player switched camera in time - she vanishes.");
            EnterState(LuminaState.Waiting);
            return;
        }

        if (currentState != LuminaState.Waiting || !IsReadyToAppear())
        {
            return;
        }

        float roll = Random.value;
        bool appears = roll <= cameraAppearChance;
        Debug.Log("[Lumina] Camera check: rolled " + roll.ToString("F2") + " vs Camera Appear Chance " + cameraAppearChance.ToString("F2") + " -> " + (appears ? "appears on the camera!" : "stays hidden."));

        if (appears)
        {
            EnterState(LuminaState.OnCamera);
        }
    }

    /// <summary>Fires whenever the Camera Monitor closes. Closing it while she's on camera makes her vanish.</summary>
    private void HandleCameraMonitorClosed()
    {
        if (currentState != LuminaState.OnCamera)
        {
            return;
        }

        Debug.Log("[Lumina] The player closed the monitor in time - she vanishes.");
        EnterState(LuminaState.Waiting);
    }

    /// <summary>
    /// On Camera: times how long the player keeps looking at her. Once it
    /// reaches cameraStareSeconds she force-closes the monitor and
    /// jumpscares. (Switching or closing in time is handled by the two
    /// event handlers above.) Note that firing a Sonar Ping while she is on
    /// screen locks the monitor for its duration - that's the player's
    /// mistake to make, so her timer deliberately keeps running.
    /// </summary>
    private void UpdateOnCamera()
    {
        cameraStareTimer += Time.deltaTime;
        if (cameraStareTimer < cameraStareSeconds)
        {
            return;
        }

        Debug.Log("[Lumina] The player stared at her on camera for too long - she slams the monitor shut!");

        // Leave the OnCamera state FIRST, so the "monitor closed" event
        // raised by the force-close below is ignored instead of being
        // treated as the player escaping her in time.
        EnterState(LuminaState.Waiting);
        gameMaster.ForceCloseCameraMonitor();
        gameMaster.TriggerLuminaJumpscare();
    }


    // -------------------------------------------------------------------
    // REGION: WINDOW APPEARANCE
    // -------------------------------------------------------------------

    /// <summary>
    /// At Window: her leave timer always counts, but her look timer only
    /// counts while the player has no panel open (i.e. is looking at the
    /// office). Whichever runs out first decides how the visit ends.
    /// </summary>
    private void UpdateAtWindow()
    {
        windowLeaveTimer += Time.deltaTime;

        if (!gameMaster.IsAnyPanelOpen)
        {
            windowLookTimer += Time.deltaTime;
        }

        if (windowLookTimer >= windowLookSeconds)
        {
            Debug.Log("[Lumina] The player looked at her through the window for too long - jumpscare!");

            // Hide her window sprite first (via EnterState) - the jumpscare
            // overlay IS her, so leaving it on would show two of her at once.
            EnterState(LuminaState.Waiting);
            gameMaster.TriggerLuminaJumpscare();
            return;
        }

        if (windowLeaveTimer >= windowLeaveSeconds)
        {
            Debug.Log("[Lumina] She drifts away from the window (looked at for " + windowLookTimer.ToString("F1") + "s of " + windowLookSeconds.ToString("F1") + "s).");
            EnterState(LuminaState.Waiting);
        }
    }


    // -------------------------------------------------------------------
    // REGION: STATE CHANGES / VISUALS HELPERS
    // -------------------------------------------------------------------

    /// <summary>
    /// The single place that actually changes currentState. Every
    /// transition above funnels through here, so her visuals can never fall
    /// out of sync with where she really is. It:
    ///   1. shows only the visual that matches the new state, and
    ///   2. resets every timer, so each state (and each cooldown) starts fresh.
    /// </summary>
    private void EnterState(LuminaState next)
    {
        currentState = next;

        SetVisualActive(cameraOverlayObject, next == LuminaState.OnCamera);
        SetVisualActive(windowVisualObject, next == LuminaState.AtWindow);

        cooldownTimer = 0f;
        windowCheckTimer = 0f;
        cameraStareTimer = 0f;
        windowLookTimer = 0f;
        windowLeaveTimer = 0f;

        Debug.Log("[Lumina] Entered state: " + next + ".");
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
