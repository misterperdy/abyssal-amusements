using UnityEngine;
using UnityEngine.UI;   // Gives us Slider, Button, Toggle (the standard, non-TextMeshPro UI widgets).
using TMPro;            // Gives us TMP_Text, used for all on-screen text in this project.

/// <summary>
/// GameMaster is the single orchestrator for the entire night: it owns the clock,
/// the oxygen meter, the office look/pan, the Maintenance Panel, and the Camera
/// Monitor shell. There are deliberately no separate manager scripts - every
/// system a FNAF3-style night needs (before animatronics are added) lives here
/// so there is exactly one place to look when tracing how the game behaves.
///
/// Nothing in this script knows about Captain Barnacle, The Diver, The Depth
/// Stalker, or Lumina yet - those will hook into the systems below (oxygen,
/// lights, cameras) once they exist, but this file is intentionally free of
/// any animatronic logic for this first playable slice.
///
/// This GameObject is a plain, isolated root object - it does not need to be
/// parented under the Canvas and does not carry any UI Graphic component.
/// See the Office Look/Pan region for how it still reads the cursor's
/// position reliably despite that.
/// </summary>
public class GameMaster : MonoBehaviour
{
    // -------------------------------------------------------------------
    // GAME STATE
    // -------------------------------------------------------------------
    // The overall state the night is in right now. Every per-frame system
    // below (clock, oxygen, look/pan) only runs while we are "Playing" -
    // once we hit Victory or GameOver, everything freezes in place.
    public enum GameState
    {
        Playing,
        Victory,
        GameOver
    }

    // Not serialized - we always want the night to start in Playing state,
    // so there is no reason to expose this in the Inspector.
    private GameState currentState;


    // -------------------------------------------------------------------
    // REGION: CLOCK & NIGHT
    // -------------------------------------------------------------------
    [Header("Clock & Night - UI References")]

    [Tooltip("Drag the TMP_Text object that should display 'Night 1', 'Night 2', etc.")]
    [SerializeField] private TMP_Text nightNumberText;

    [Tooltip("Drag the TMP_Text object that should display the in-game time, e.g. '12 AM'.")]
    [SerializeField] private TMP_Text timeText;

    [Header("Clock & Night - Settings")]

    [Tooltip("Which night this is. Purely cosmetic for now (shown in the UI) - night selection/progression is future work.")]
    [SerializeField] private int currentNight = 1;

    // A night always represents 12 AM through 6 AM, i.e. 6 in-game hours.
    // This never changes, so it is a compile-time constant rather than a field.
    private const float NIGHT_LENGTH_HOURS = 6f;

    [Tooltip("How many REAL seconds a full night takes. The game bible specifies 6 minutes (360 seconds).")]
    [SerializeField] private float nightLengthInRealSeconds = 360f;

    // How far into the night we are, in in-game hours. Starts at 0 (12 AM)
    // and counts up to 6 (6 AM), at which point the night is over.
    private float currentHourOfNight;

    /// <summary>
    /// Advances the in-game clock and refreshes the time text. Called every
    /// frame from Update() while the night is in progress.
    /// </summary>
    /// <param name="deltaTime">Time.deltaTime - real seconds since the last frame.</param>
    private void UpdateClock(float deltaTime)
    {
        // Work out how many in-game hours pass per real second, then advance
        // the clock by that rate scaled by how much real time just passed.
        float hoursPerRealSecond = NIGHT_LENGTH_HOURS / nightLengthInRealSeconds;
        currentHourOfNight += hoursPerRealSecond * deltaTime;

        // Once we reach (or pass) 6 AM, the night is complete - stop advancing
        // the clock any further and hand off to the victory flow.
        if (currentHourOfNight >= NIGHT_LENGTH_HOURS)
        {
            currentHourOfNight = NIGHT_LENGTH_HOURS;
            TriggerVictory();
        }

        // Push the current hour into the UI text every frame so it always
        // matches currentHourOfNight, even though it only visually changes
        // once per in-game hour.
        if (timeText != null)
        {
            timeText.text = FormatHour(currentHourOfNight);
        }
    }

    /// <summary>
    /// Converts an in-game hour-of-night float (0-6) into a FNAF-style label
    /// like "12 AM", "1 AM", ... "5 AM".
    /// </summary>
    private string FormatHour(float hour)
    {
        // Floor to the whole hour - we only ever display whole hours, never
        // fractional ones (e.g. 2.75 still reads as "2 AM").
        int hourOfNight = Mathf.FloorToInt(hour);

        // Hour 0 is midnight, which FNAF (and everyday clocks) label "12 AM"
        // rather than "0 AM".
        int displayHour = (hourOfNight == 0) ? 12 : hourOfNight;

        return displayHour + " AM";
    }


    // -------------------------------------------------------------------
    // REGION: OXYGEN
    // -------------------------------------------------------------------
    [Header("Oxygen - UI References")]

    [Tooltip("Drag the Slider that visually represents the oxygen meter (0 = empty, 1 = full).")]
    [SerializeField] private Slider oxygenSlider;

    [Tooltip("Optional: a full-screen overlay object (e.g. a blue-tinted Image) shown while Oxygen is below the Hypoxia threshold. Leave empty to skip this effect for now.")]
    [SerializeField] private GameObject hypoxiaOverlay;

    [Header("Oxygen - Settings")]

    [Tooltip("The oxygen value that represents a full tank.")]
    [SerializeField] private float maxOxygen = 100f;

    [Tooltip("How much oxygen drains per second just from existing, even if you never touch Release Pressure. Keep this small - a perfect night should barely dent the tank.")]
    [SerializeField] private float oxygenDecayPerSecond = 0.5f;

    [Tooltip("How much EXTRA oxygen drains per second while the Release Pressure button is held down.")]
    [SerializeField] private float releasePressureDrainPerSecond = 8f;

    [Tooltip("Oxygen percentage (0-100) below which the office enters the Hypoxia state.")]
    [SerializeField] private float hypoxiaThresholdPercent = 50f;

    // The tank's current value, from 0 to maxOxygen. Not serialized because
    // Start() always resets it to a full tank.
    private float currentOxygen;

    // True once currentOxygen has dropped below hypoxiaThresholdPercent.
    // Tracked separately from the raw float so we only toggle the overlay
    // on the exact frame the threshold is crossed, not every frame.
    private bool isHypoxic;

    /// <summary>
    /// Drains oxygen (naturally, plus extra if Release Pressure is held),
    /// updates the UI, and checks for the Hypoxia and Suffocation thresholds.
    /// Called every frame from Update() while the night is in progress.
    /// </summary>
    private void UpdateOxygen(float deltaTime)
    {
        // Natural, slow drain that always applies.
        float drainThisFrame = oxygenDecayPerSecond * deltaTime;

        // Release Pressure adds a much heavier drain on top of the natural one.
        if (isReleasingPressure)
        {
            drainThisFrame += releasePressureDrainPerSecond * deltaTime;
        }

        currentOxygen -= drainThisFrame;
        currentOxygen = Mathf.Clamp(currentOxygen, 0f, maxOxygen);

        // Keep the slider in sync. Sliders expect a 0-1 range, so we convert
        // our 0-maxOxygen value down to that range.
        if (oxygenSlider != null)
        {
            oxygenSlider.value = currentOxygen / maxOxygen;
        }

        // Work out the current percentage and compare it against the Hypoxia
        // threshold, only reacting on the frame the state actually changes.
        float oxygenPercent = (currentOxygen / maxOxygen) * 100f;
        bool shouldBeHypoxic = oxygenPercent < hypoxiaThresholdPercent;

        if (shouldBeHypoxic != isHypoxic)
        {
            isHypoxic = shouldBeHypoxic;

            if (hypoxiaOverlay != null)
            {
                hypoxiaOverlay.SetActive(isHypoxic);
            }
        }

        // An empty tank ends the night immediately, regardless of what else
        // is happening (game bible: "Suffocation - Instant Game Over").
        if (currentOxygen <= 0f)
        {
            TriggerGameOver();
        }
    }


    // -------------------------------------------------------------------
    // REGION: OFFICE LOOK / PAN
    // -------------------------------------------------------------------
    [Header("Office Look/Pan - References")]

    [Tooltip("The Transform that holds the office SpriteRenderer(s). This is the object that physically moves left/right as the player looks around.")]
    [SerializeField] private Transform officeViewRoot;

    [Tooltip("Drag the scene's main Canvas here. We use its RectTransform to resolve the cursor's screen position reliably (the same way Unity's UI already hit-tests buttons correctly), instead of dividing Input.mousePosition by Screen.width directly, which can drift out of sync with the Canvas's real pixel size after the Game View is resized/docked or under Editor DPI scaling.")]
    [SerializeField] private RectTransform canvasRectTransform;

    [Header("Office Look/Pan - Settings")]

    [Tooltip("How far (in world units) the office view can shift left or right of its starting position.")]
    [SerializeField] private float maxPanDistance = 5f;

    [Tooltip("Normalized distance from each screen edge (0-0.5) that counts as a 'scroll zone'. The middle of the screen (everything between the two zones) is neutral padding where the cursor does not move the view - like FNAF3, you have to push the cursor past this point to commit to scrolling.")]
    [SerializeField] private float panZoneThreshold = 0.3f;

    [Tooltip("How fast (in world units per second) the office view scrolls toward its destination once the cursor is past the padding. This is what makes the pan feel like a committed scroll rather than an instant snap to the mouse position.")]
    [SerializeField] private float panSpeed = 4f;

    [Tooltip("How close (in world units) the view must get to the full pan distance before we count it as 'arrived' and show the Maintenance/Camera button. A small buffer so the button appears right as the scroll finishes, not before.")]
    [SerializeField] private float edgeArrivalTolerance = 0.05f;

    // Where the office view should sit when the player is looking dead
    // center, captured once at Start() so panning is always relative to
    // wherever the object was originally placed in the scene.
    private Vector3 officeViewCenterPosition;

    // The view's current offset from center. Unlike a direct mouse-to-position
    // mapping, this now persists across frames and eases toward its target
    // over time, which is what produces the FNAF3-style scrolling motion.
    private float currentPanOffset;

    /// <summary>
    /// Reads the latest cursor zone (left scroll zone, right scroll zone, or
    /// the neutral padding in the middle) and eases the office view toward
    /// that zone's destination at a constant speed (a scroll, not a snap).
    /// Shows/hides the Maintenance/Camera open buttons only once the view
    /// has fully finished scrolling to that side. Only called when no panel
    /// is currently open (you cannot pan while a panel covers the view).
    /// </summary>
    private void UpdateLookAndEdgeButtons(float deltaTime)
    {
        // Default to dead-center (the padding zone) in case canvasRectTransform
        // hasn't been assigned yet - a safe fallback rather than a crash.
        float normalizedMouseX = 0.5f;

        if (canvasRectTransform != null)
        {
            // Resolve the cursor's screen position against the Canvas's own
            // RectTransform. This goes through the same math Unity's UI uses
            // to correctly hit-test buttons, and stays accurate even when
            // Screen.width would not (see this field's tooltip for why we
            // don't just divide Input.mousePosition.x by Screen.width).
            Vector2 localPoint;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRectTransform, Input.mousePosition, null, out localPoint);

            Rect rect = canvasRectTransform.rect;
            normalizedMouseX = Mathf.Clamp01(Mathf.InverseLerp(rect.xMin, rect.xMax, localPoint.x));
        }

        // Decide where the view WANTS to end up based on which zone the
        // cursor is currently in. Default to holding whatever position we
        // are already at - the padding zone is inert, it does NOT pull the
        // view back to center. Only the two outer zones actively set a
        // destination, matching FNAF3: the view only moves while you're
        // looking toward an edge, and simply stays put everywhere else.
        float targetOffset = currentPanOffset;
        if (normalizedMouseX <= panZoneThreshold)
        {
            targetOffset = maxPanDistance;         // committed to scrolling fully left
        }
        else if (normalizedMouseX >= 1f - panZoneThreshold)
        {
            targetOffset = -maxPanDistance;        // committed to scrolling fully right
        }

        // Move the current offset toward the destination at a fixed speed,
        // rather than jumping straight to it. MoveTowards will not overshoot,
        // so this naturally comes to rest exactly at the destination.
        currentPanOffset = Mathf.MoveTowards(currentPanOffset, targetOffset, panSpeed * deltaTime);

        if (officeViewRoot != null)
        {
            officeViewRoot.localPosition = officeViewCenterPosition + new Vector3(currentPanOffset, 0f, 0f);
        }

        // The Maintenance button only appears once the view has actually
        // finished scrolling all the way to the left - not just because the
        // cursor moved there - matching FNAF3's feel. It's always available
        // regardless of light state (you need it to turn the lights back on).
        bool reachedFarLeft = currentPanOffset >= maxPanDistance - edgeArrivalTolerance;
        SetButtonVisible(maintenanceOpenButton, reachedFarLeft);

        // Same idea for the Camera button on the right, AND only while the
        // lights are on (game bible: the Camera Monitor is disabled while
        // the lights are off).
        bool reachedFarRight = currentPanOffset <= -maxPanDistance + edgeArrivalTolerance;
        SetButtonVisible(cameraOpenButton, reachedFarRight && lightsOn);
    }

    /// <summary>Small helper to safely show/hide a button GameObject that might not be assigned yet.</summary>
    private void SetButtonVisible(GameObject buttonObject, bool visible)
    {
        if (buttonObject != null)
        {
            buttonObject.SetActive(visible);
        }
    }


    // -------------------------------------------------------------------
    // REGION: MAINTENANCE PANEL (LEFT)
    // -------------------------------------------------------------------
    [Header("Maintenance Panel - References")]

    [Tooltip("The single button that appears at the far-left edge. It doubles as both the open AND close button for the Maintenance Panel, and stays visible the whole time the panel is open.")]
    [SerializeField] private GameObject maintenanceOpenButton;

    [Tooltip("The Maintenance Panel itself (Release Pressure + Lights toggle). Should start inactive.")]
    [SerializeField] private GameObject maintenancePanel;

    [Tooltip("The Toggle switch inside the Maintenance Panel that controls the office lights.")]
    [SerializeField] private Toggle lightsToggle;

    [Tooltip("The Release Pressure button inside the Maintenance Panel. Its Interactable state is controlled by the lights (disabled while lights are off).")]
    [SerializeField] private Button releasePressureButton;

    // True while the Maintenance Panel is open and covering the view.
    private bool isMaintenancePanelOpen;

    // True while the office lights are on. Starts true because a night
    // always begins with the lights on.
    private bool lightsOn = true;

    // True for as long as the player is physically holding down the
    // Release Pressure button.
    private bool isReleasingPressure;

    /// <summary>
    /// Opens or closes the Maintenance Panel depending on its current state.
    /// Hook this up to the single edge button's OnClick in the Inspector -
    /// the same button is used to both open and close the panel, and it
    /// stays on screen the whole time the panel is open.
    /// </summary>
    public void ToggleMaintenancePanel()
    {
        if (isMaintenancePanelOpen)
        {
            CloseMaintenancePanel();
        }
        else
        {
            OpenMaintenancePanel();
        }
    }

    /// <summary>Internal helper that actually opens the panel. Use ToggleMaintenancePanel() from the Inspector instead of this directly.</summary>
    private void OpenMaintenancePanel()
    {
        isMaintenancePanelOpen = true;

        if (maintenancePanel != null)
        {
            maintenancePanel.SetActive(true);
        }

        // Note: unlike before, we deliberately do NOT hide maintenanceOpenButton
        // here - it needs to stay visible so the player can click it again to close.
    }

    /// <summary>Internal helper that actually closes the panel. Use ToggleMaintenancePanel() from the Inspector instead of this directly.</summary>
    private void CloseMaintenancePanel()
    {
        isMaintenancePanelOpen = false;

        if (maintenancePanel != null)
        {
            maintenancePanel.SetActive(false);
        }

        // Also make sure Release Pressure stops draining oxygen if the
        // player closes the panel while still holding the button down.
        isReleasingPressure = false;
    }

    /// <summary>
    /// Called whenever the lights Toggle changes value. Hook this up to the
    /// Toggle's OnValueChanged(bool) event in the Inspector.
    /// </summary>
    public void OnLightsToggleChanged(bool isOn)
    {
        lightsOn = isOn;

        // Game bible: "While lights are off, the Camera Monitor and Release
        // Pressure buttons are disabled." Enforce that here in one place.
        if (releasePressureButton != null)
        {
            releasePressureButton.interactable = lightsOn;
        }

        // If the lights were just turned off while holding Release Pressure,
        // stop the drain immediately rather than waiting for pointer-up.
        if (!lightsOn)
        {
            isReleasingPressure = false;
        }

        // If the lights were just turned off while the Camera Monitor
        // happens to be open, force it closed - it isn't allowed to be open
        // without lights. We also hide its button here: normally the button
        // stays visible as long as the panel is open (since it doubles as
        // the close button), but this is a forced close the player didn't
        // click, so nothing else will hide it for us. Once the player looks
        // away from and back to the right edge, UpdateLookAndEdgeButtons()
        // will correctly manage its visibility again.
        if (!lightsOn && isCameraPanelOpen)
        {
            CloseCameraMonitor();
            SetButtonVisible(cameraOpenButton, false);
        }
    }

    /// <summary>
    /// Starts draining oxygen for Release Pressure. Hook this to an
    /// EventTrigger's PointerDown event on the button (a plain OnClick
    /// cannot represent "press and hold").
    /// </summary>
    public void OnReleasePressureButtonDown()
    {
        // Guard against the lights being off - the button should already be
        // non-interactable in that case, but this keeps the logic safe even
        // if something else calls it directly.
        if (!lightsOn)
        {
            return;
        }

        isReleasingPressure = true;
    }

    /// <summary>
    /// Stops draining oxygen for Release Pressure. Hook this to the same
    /// EventTrigger's PointerUp event on the button.
    /// </summary>
    public void OnReleasePressureButtonUp()
    {
        isReleasingPressure = false;
    }


    // -------------------------------------------------------------------
    // REGION: CAMERA MONITOR (RIGHT)
    // -------------------------------------------------------------------
    [Header("Camera Monitor - References")]

    [Tooltip("The single button that appears at the far-right edge. It doubles as both the open AND close button for the Camera Monitor, and stays visible the whole time the panel is open.")]
    [SerializeField] private GameObject cameraOpenButton;

    [Tooltip("The Camera Monitor panel itself. Should start inactive.")]
    [SerializeField] private GameObject cameraPanel;

    [Tooltip("Placeholder views for each camera feed, in the same order as the camera buttons you will build. Only one is shown at a time. Leave empty until you add camera art.")]
    [SerializeField] private GameObject[] cameraFeedViews;

    // True while the Camera Monitor is open and covering the view.
    private bool isCameraPanelOpen;

    // Which camera feed is currently selected, or -1 if none has been
    // picked yet this session.
    private int currentCameraIndex = -1;

    /// <summary>
    /// Opens or closes the Camera Monitor depending on its current state.
    /// Hook this up to the single edge button's OnClick in the Inspector -
    /// the same button is used to both open and close the panel, and it
    /// stays on screen the whole time the panel is open. Does nothing if
    /// the lights are off and the panel is currently closed, matching the
    /// game bible's restriction.
    /// </summary>
    public void ToggleCameraMonitor()
    {
        if (isCameraPanelOpen)
        {
            CloseCameraMonitor();
        }
        else
        {
            OpenCameraMonitor();
        }
    }

    /// <summary>Internal helper that actually opens the panel. Use ToggleCameraMonitor() from the Inspector instead of this directly.</summary>
    private void OpenCameraMonitor()
    {
        if (!lightsOn)
        {
            return;
        }

        isCameraPanelOpen = true;

        if (cameraPanel != null)
        {
            cameraPanel.SetActive(true);
        }

        // Note: unlike before, we deliberately do NOT hide cameraOpenButton
        // here - it needs to stay visible so the player can click it again to close.
    }

    /// <summary>Internal helper that actually closes the panel. Use ToggleCameraMonitor() from the Inspector instead of this directly.</summary>
    private void CloseCameraMonitor()
    {
        isCameraPanelOpen = false;

        if (cameraPanel != null)
        {
            cameraPanel.SetActive(false);
        }
    }

    /// <summary>
    /// Switches the Camera Monitor to show a specific camera's feed. Hook
    /// one of these up (with the matching index) to each camera button you
    /// build inside the Camera Monitor panel. Currently this only swaps
    /// which placeholder view is visible - no animatronic tracking exists
    /// yet, so selecting a camera has no gameplay effect beyond the view.
    /// </summary>
    public void SelectCamera(int cameraIndex)
    {
        // Guard against an out-of-range index (e.g. an Inspector typo)
        // rather than throwing an exception mid-game.
        if (cameraFeedViews == null || cameraIndex < 0 || cameraIndex >= cameraFeedViews.Length)
        {
            Debug.LogWarning("GameMaster.SelectCamera: index " + cameraIndex + " is out of range.");
            return;
        }

        currentCameraIndex = cameraIndex;

        // Show only the selected feed, hide every other one.
        for (int i = 0; i < cameraFeedViews.Length; i++)
        {
            if (cameraFeedViews[i] != null)
            {
                cameraFeedViews[i].SetActive(i == currentCameraIndex);
            }
        }
    }


    // -------------------------------------------------------------------
    // REGION: END STATES (VICTORY / GAME OVER)
    // -------------------------------------------------------------------
    [Header("End States")]

    [Tooltip("Shown when the player survives until 6 AM. Should start inactive.")]
    [SerializeField] private GameObject victoryScreen;

    [Tooltip("Shown when Oxygen reaches 0 (suffocation). Should start inactive.")]
    [SerializeField] private GameObject gameOverScreen;

    /// <summary>Ends the night successfully. Called automatically once the clock reaches 6 AM.</summary>
    private void TriggerVictory()
    {
        currentState = GameState.Victory;
        HideAllPanelsAndButtons();

        if (victoryScreen != null)
        {
            victoryScreen.SetActive(true);
        }
    }

    /// <summary>Ends the night in failure. Called automatically once Oxygen reaches 0.</summary>
    private void TriggerGameOver()
    {
        currentState = GameState.GameOver;
        HideAllPanelsAndButtons();

        if (gameOverScreen != null)
        {
            gameOverScreen.SetActive(true);
        }
    }

    /// <summary>Closes any open panel/button so the end-state screen isn't cluttered by leftover HUD elements.</summary>
    private void HideAllPanelsAndButtons()
    {
        SetButtonVisible(maintenanceOpenButton, false);
        SetButtonVisible(cameraOpenButton, false);

        if (maintenancePanel != null) maintenancePanel.SetActive(false);
        if (cameraPanel != null) cameraPanel.SetActive(false);

        isMaintenancePanelOpen = false;
        isCameraPanelOpen = false;
        isReleasingPressure = false;
    }


    // -------------------------------------------------------------------
    // UNITY LIFECYCLE
    // -------------------------------------------------------------------

    /// <summary>Called once when the scene starts. Resets every system to its "beginning of the night" state.</summary>
    private void Start()
    {
        // Remember wherever the office view was placed in the editor as our
        // "looking straight ahead" position, so panning is relative to it.
        if (officeViewRoot != null)
        {
            officeViewCenterPosition = officeViewRoot.localPosition;
        }

        // Start looking straight ahead, with no scroll destination committed yet.
        currentPanOffset = 0f;

        // canvasRectTransform is required for the office look/pan to work at
        // all - warn clearly in the Console if it hasn't been assigned,
        // rather than leaving the office silently stuck looking straight ahead.
        if (canvasRectTransform == null)
        {
            Debug.LogWarning("GameMaster's Canvas Rect Transform field is not assigned, so the office view will not pan. Drag the scene's Canvas into that field in the Inspector.");
        }

        // Reset the clock to 12 AM.
        currentHourOfNight = 0f;

        // Fill the oxygen tank.
        currentOxygen = maxOxygen;
        isHypoxic = false;

        // Lights start on, nothing is being held or panned yet.
        lightsOn = true;
        isReleasingPressure = false;

        // The night is now live.
        currentState = GameState.Playing;

        // Make sure every panel/button/end-screen starts in the correct
        // hidden/shown state, regardless of how they were left in the editor.
        HideAllPanelsAndButtons();
        if (hypoxiaOverlay != null) hypoxiaOverlay.SetActive(false);
        if (victoryScreen != null) victoryScreen.SetActive(false);
        if (gameOverScreen != null) gameOverScreen.SetActive(false);

        // Draw the very first frame of UI text immediately, so the HUD
        // isn't blank for the first frame before Update() runs.
        if (nightNumberText != null)
        {
            nightNumberText.text = "Night " + currentNight;
        }
        if (timeText != null)
        {
            timeText.text = FormatHour(currentHourOfNight);
        }
        if (oxygenSlider != null)
        {
            oxygenSlider.value = 1f;
        }
    }

    /// <summary>Called once per frame. Drives every system while the night is in progress.</summary>
    private void Update()
    {
        // Once the night has ended (Victory or GameOver), freeze every
        // system in place - nothing below this point should keep running.
        if (currentState != GameState.Playing)
        {
            return;
        }

        UpdateClock(Time.deltaTime);
        UpdateOxygen(Time.deltaTime);

        // You can only look around the office while no panel is covering
        // the view - the Maintenance Panel and Camera Monitor both take
        // over full control of the screen while open.
        if (!isMaintenancePanelOpen && !isCameraPanelOpen)
        {
            UpdateLookAndEdgeButtons(Time.deltaTime);
        }
    }
}
