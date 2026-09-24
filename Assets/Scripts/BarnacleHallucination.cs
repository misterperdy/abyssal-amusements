using UnityEngine;

/// <summary>
/// Hypoxia Hallucinations - a fake Captain Barnacle. After Lumina
/// jumpscares the player (which can only happen while they are Hypoxic),
/// the player's oxygen-starved brain starts seeing Barnacle where he isn't,
/// for a short "hallucination window" (hallucinationDurationSeconds).
///
/// HOW THE FAKE WORKS:
///   - Every time a regular camera comes on screen during the window, there
///     is a chance a fake Barnacle is sitting on it. Every few seconds there
///     is also a chance he shows up at the office Window instead.
///   - Only ONE fake exists at a time. He never spawns where the real
///     Barnacle is, and never goes to the Door or jumpscares anyone.
///   - He STAYS PUT for a random few seconds (fakeLifetimeMin/MaxSeconds),
///     like the real one waiting for his next movement opportunity - switch
///     away and back and he is still there. Then he silently "moves on".
///   - He looks exactly like the real one: on camera, GameMaster draws him
///     with Barnacle's own sprites (see SetFakeBarnacleCameraIndex()); at
///     the Window, fakeWindowVisualObject is a copy of Barnacle's sprite.
///   - He reacts to counterplay like the real one: a successful Sonar Ping
///     on his camera, or holding Release Pressure long enough while he's at
///     the Window, makes him vanish. That's the real cost of a
///     hallucination - the sonar charge or oxygen the player wasted on him.
///   - When the window ends (or the real Barnacle walks into his spot), the
///     fake disappears and only the real Barnacle is left.
///
/// Like every animatronic script, this only reads GameMaster/CaptainBarnacle
/// and listens to GameMaster's events - it doesn't change how either works.
/// </summary>
public class BarnacleHallucination : MonoBehaviour
{
    // -------------------------------------------------------------------
    // REGION: REFERENCES
    // -------------------------------------------------------------------
    [Header("References")]

    [Tooltip("Drag the scene's GameMaster object here. Used to read the Hypoxia and camera state, and to draw the fake on the camera feeds.")]
    [SerializeField] private GameMaster gameMaster;

    [Tooltip("Drag the scene's (real) CaptainBarnacle here. Used so the fake never appears on top of the real one.")]
    [SerializeField] private CaptainBarnacle captainBarnacle;


    // -------------------------------------------------------------------
    // REGION: SETTINGS
    // -------------------------------------------------------------------
    [Header("Hallucination Window")]

    [Tooltip("How long (in real seconds) the player can hallucinate after a Lumina jumpscare. Another Lumina jumpscare restarts this from full (it does not stack).")]
    [Min(0f)]
    [SerializeField] private float hallucinationDurationSeconds = 15f;

    [Header("Camera Hallucination")]

    [Tooltip("Chance (0-1) that a fake Barnacle is on the camera the player just opened/switched to, during the hallucination window.")]
    [Range(0f, 1f)]
    [SerializeField] private float cameraAppearChance = 0.35f;

    [Header("Window Hallucination")]

    [Tooltip("How often (in real seconds) the fake rolls to appear at the office Window, during the hallucination window.")]
    [Min(0.1f)]
    [SerializeField] private float windowCheckIntervalSeconds = 4f;

    [Tooltip("Chance (0-1) that each window roll puts a fake Barnacle at the Window.")]
    [Range(0f, 1f)]
    [SerializeField] private float windowAppearChance = 0.25f;

    [Header("Fake Lifetime")]

    [Tooltip("The shortest time (in real seconds) a fake stays in place before he 'moves on'. A fresh value between this and the Max is rolled for every fake.")]
    [Min(0f)]
    [SerializeField] private float fakeLifetimeMinSeconds = 5f;

    [Tooltip("The longest time (in real seconds) a fake stays in place before he 'moves on'.")]
    [Min(0f)]
    [SerializeField] private float fakeLifetimeMaxSeconds = 10f;

    [Header("Fake Counterplay (Window)")]

    [Tooltip("The shortest time (in real seconds) the player must hold Release Pressure to 'push back' a fake at the Window. Same idea as the real Barnacle's pushback hold.")]
    [Min(0f)]
    [SerializeField] private float pushbackHoldMinSeconds = 1f;

    [Tooltip("The longest time (in real seconds) the player might need to hold Release Pressure to 'push back' a fake at the Window.")]
    [Min(0f)]
    [SerializeField] private float pushbackHoldMaxSeconds = 3f;


    // -------------------------------------------------------------------
    // REGION: VISUALS
    // -------------------------------------------------------------------
    [Header("Visuals")]

    [Tooltip("A copy of Barnacle's window GameObject (same sprite, same position), placed in the office scene. Should start INACTIVE - this script turns it on only while a fake is at the Window.")]
    [SerializeField] private GameObject fakeWindowVisualObject;


    // -------------------------------------------------------------------
    // REGION: RUNTIME STATE
    // -------------------------------------------------------------------

    // How many seconds of hallucination are left. Above 0 means the player
    // is currently hallucinating and fakes may appear.
    private float hallucinationTimeRemaining;

    // True while a fake Barnacle exists. Only ever changed by SpawnFake()
    // and RemoveFake() below.
    private bool hasFake;

    // Where the fake is (only Cam1-Cam7 or AtWindow). Only meaningful
    // while hasFake is true.
    private CaptainBarnacle.BarnacleLocation fakeLocation;

    // How long the current fake has left before he "moves on".
    private float fakeLifetimeRemaining;

    // Counts up to windowCheckIntervalSeconds between window rolls.
    private float windowCheckTimer;

    // True while the player is holding Release Pressure. Tracked all the
    // time (not just while a fake is at the Window), so a fake that appears
    // mid-hold can still be pushed back by that same hold.
    private bool isHoldingPressure;

    // How long Release Pressure has been held against the current Window
    // fake, and how long it needs to be held for him to vanish.
    private float pushbackHoldTimer;
    private float requiredHoldSeconds;


    // -------------------------------------------------------------------
    // REGION: UNITY LIFECYCLE
    // -------------------------------------------------------------------

    /// <summary>Subscribes to every GameMaster event this script cares about (static events, so OnEnable/OnDisable - same pattern as CaptainBarnacle).</summary>
    private void OnEnable()
    {
        GameMaster.OnLuminaJumpscare += HandleLuminaJumpscare;
        GameMaster.OnCameraViewChanged += HandleCameraViewChanged;
        GameMaster.OnSonarPing += HandleSonarPing;
        GameMaster.OnReleasePressureStarted += HandleReleasePressureStarted;
        GameMaster.OnReleasePressureStopped += HandleReleasePressureStopped;
    }

    /// <summary>Unsubscribes from every event we subscribed to in OnEnable().</summary>
    private void OnDisable()
    {
        GameMaster.OnLuminaJumpscare -= HandleLuminaJumpscare;
        GameMaster.OnCameraViewChanged -= HandleCameraViewChanged;
        GameMaster.OnSonarPing -= HandleSonarPing;
        GameMaster.OnReleasePressureStarted -= HandleReleasePressureStarted;
        GameMaster.OnReleasePressureStopped -= HandleReleasePressureStopped;
    }

    /// <summary>Starts the night with no hallucination and the fake window visual hidden.</summary>
    private void Start()
    {
        SetVisualActive(fakeWindowVisualObject, false);

        hallucinationTimeRemaining = 0f;
        hasFake = false;
        isHoldingPressure = false;
    }

    /// <summary>Counts down the hallucination window and the current fake, for as long as the night is in progress.</summary>
    private void Update()
    {
        // Don't act at all once the night is no longer being played, exactly
        // like the animatronics.
        if (gameMaster == null || captainBarnacle == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        if (hallucinationTimeRemaining <= 0f)
        {
            return;
        }

        // The real Barnacle just walked into the fake's spot - quietly merge
        // the two into one, so the player never sees him "split".
        if (hasFake && captainBarnacle.CurrentLocation == fakeLocation)
        {
            RemoveFake("the real Barnacle arrived at " + fakeLocation + " - merged into one");
        }

        hallucinationTimeRemaining -= Time.deltaTime;
        if (hallucinationTimeRemaining <= 0f)
        {
            Debug.Log("[Hallucination] The hallucination fades - only the real Barnacle remains.");
            if (hasFake)
            {
                RemoveFake("the hallucination ended");
            }
            return;
        }

        if (hasFake)
        {
            UpdateFake();
        }
        else
        {
            UpdateWindowRoll();
        }
    }


    // -------------------------------------------------------------------
    // REGION: STARTING A HALLUCINATION
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires the instant Lumina jumpscares the player. Starts (or restarts
    /// from full) the hallucination window. Lumina only exists while the
    /// player is Hypoxic, but we double-check here to keep the rule explicit.
    /// </summary>
    private void HandleLuminaJumpscare()
    {
        if (gameMaster == null || !gameMaster.IsHypoxic)
        {
            return;
        }

        hallucinationTimeRemaining = hallucinationDurationSeconds;
        Debug.Log("[Hallucination] Lumina's jumpscare left the player hallucinating for " + hallucinationDurationSeconds.ToString("F0") + "s.");
    }


    // -------------------------------------------------------------------
    // REGION: FAKE APPEARANCES
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires every time the player puts a new feed on the monitor. While
    /// hallucinating (and with no fake already out), rolls to put a fake
    /// Barnacle on the regular camera the player is now looking at - unless
    /// the real one is already there. Vent cameras are skipped (Barnacle
    /// never goes into the vents).
    /// </summary>
    private void HandleCameraViewChanged()
    {
        if (gameMaster == null || captainBarnacle == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        if (hallucinationTimeRemaining <= 0f || hasFake)
        {
            return;
        }

        int watchedCameraIndex = gameMaster.WatchedRegularCameraIndex;
        if (watchedCameraIndex == -1)
        {
            return;
        }

        // Camera index 0 is Cam1, so the camera NUMBER is index + 1.
        CaptainBarnacle.BarnacleLocation watchedLocation = CaptainBarnacle.CameraNumberToLocation(watchedCameraIndex + 1);
        if (captainBarnacle.CurrentLocation == watchedLocation)
        {
            return;
        }

        float roll = Random.value;
        bool appears = roll <= cameraAppearChance;
        Debug.Log("[Hallucination] Camera check on " + watchedLocation + ": rolled " + roll.ToString("F2") + " vs " + cameraAppearChance.ToString("F2") + " -> " + (appears ? "the player sees a FAKE Barnacle!" : "nothing."));

        if (appears)
        {
            SpawnFake(watchedLocation);
        }
    }

    /// <summary>While hallucinating with no fake out, rolls every windowCheckIntervalSeconds to put a fake at the Window (unless the real one is there).</summary>
    private void UpdateWindowRoll()
    {
        windowCheckTimer += Time.deltaTime;
        if (windowCheckTimer < windowCheckIntervalSeconds)
        {
            return;
        }
        windowCheckTimer = 0f;

        if (captainBarnacle.CurrentLocation == CaptainBarnacle.BarnacleLocation.AtWindow)
        {
            return;
        }

        float roll = Random.value;
        bool appears = roll <= windowAppearChance;
        Debug.Log("[Hallucination] Window check: rolled " + roll.ToString("F2") + " vs " + windowAppearChance.ToString("F2") + " -> " + (appears ? "the player sees a FAKE Barnacle at the Window!" : "nothing."));

        if (appears)
        {
            SpawnFake(CaptainBarnacle.BarnacleLocation.AtWindow);
        }
    }

    /// <summary>
    /// Runs every frame while a fake is out: counts down his lifetime, and
    /// while he's at the Window, times how long Release Pressure is held
    /// against him.
    /// </summary>
    private void UpdateFake()
    {
        fakeLifetimeRemaining -= Time.deltaTime;
        if (fakeLifetimeRemaining <= 0f)
        {
            RemoveFake("his time was up - he 'moved on'");
            return;
        }

        if (fakeLocation == CaptainBarnacle.BarnacleLocation.AtWindow && isHoldingPressure)
        {
            pushbackHoldTimer += Time.deltaTime;
            if (pushbackHoldTimer >= requiredHoldSeconds)
            {
                RemoveFake("Release Pressure 'pushed him back' from the Window (oxygen wasted on a hallucination)");
            }
        }
    }


    // -------------------------------------------------------------------
    // REGION: FAKE COUNTERPLAY
    // -------------------------------------------------------------------

    /// <summary>
    /// Fires once a Sonar Ping resolves. If it hit the fake's camera, he
    /// reacts exactly like the real Barnacle would: a successful pushback
    /// roll makes him vanish (as if pushed to another room), a failed one
    /// leaves him standing there.
    /// </summary>
    private void HandleSonarPing(int cameraIndex, bool isVentCamera, bool pushbackRollSucceeded)
    {
        if (!hasFake || isVentCamera || !CaptainBarnacle.TryGetCameraIndex(fakeLocation, out int fakeCameraIndex))
        {
            return;
        }

        if (fakeCameraIndex != cameraIndex)
        {
            return;
        }

        if (!pushbackRollSucceeded)
        {
            Debug.Log("[Hallucination] Sonar Ping hit the FAKE at " + fakeLocation + ", but the pushback roll failed - he 'holds his ground'.");
            return;
        }

        RemoveFake("a Sonar Ping 'pushed him back' (sonar charge wasted on a hallucination)");
    }

    /// <summary>The player started holding Release Pressure - roll how long they need to hold it to push back a Window fake.</summary>
    private void HandleReleasePressureStarted()
    {
        isHoldingPressure = true;
        pushbackHoldTimer = 0f;
        requiredHoldSeconds = Random.Range(pushbackHoldMinSeconds, pushbackHoldMaxSeconds);
    }

    /// <summary>The player let go of Release Pressure - any hold in progress is cancelled.</summary>
    private void HandleReleasePressureStopped()
    {
        isHoldingPressure = false;
        pushbackHoldTimer = 0f;
    }


    // -------------------------------------------------------------------
    // REGION: SPAWN / REMOVE HELPERS
    // -------------------------------------------------------------------

    /// <summary>
    /// The single place a fake appears. Rolls his lifetime and shows him in
    /// the right place: on a camera via GameMaster (using Barnacle's own
    /// sprites), or at the Window via fakeWindowVisualObject.
    /// </summary>
    private void SpawnFake(CaptainBarnacle.BarnacleLocation location)
    {
        hasFake = true;
        fakeLocation = location;
        fakeLifetimeRemaining = Random.Range(fakeLifetimeMinSeconds, fakeLifetimeMaxSeconds);

        // A fresh hold requirement, in case the player is ALREADY holding
        // Release Pressure the moment he appears at the Window.
        pushbackHoldTimer = 0f;
        requiredHoldSeconds = Random.Range(pushbackHoldMinSeconds, pushbackHoldMaxSeconds);

        if (CaptainBarnacle.TryGetCameraIndex(location, out int cameraIndex))
        {
            gameMaster.SetFakeBarnacleCameraIndex(cameraIndex);
        }
        else
        {
            SetVisualActive(fakeWindowVisualObject, true);
        }

        Debug.Log("[Hallucination] FAKE Barnacle appeared at " + location + " for " + fakeLifetimeRemaining.ToString("F1") + "s.");
    }

    /// <summary>The single place a fake disappears. Clears him from the cameras and the Window, and resets the window roll so a new fake can't pop up instantly.</summary>
    private void RemoveFake(string reason)
    {
        hasFake = false;
        gameMaster.SetFakeBarnacleCameraIndex(-1);
        SetVisualActive(fakeWindowVisualObject, false);

        pushbackHoldTimer = 0f;
        windowCheckTimer = 0f;

        Debug.Log("[Hallucination] FAKE Barnacle at " + fakeLocation + " vanished: " + reason + ".");
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
