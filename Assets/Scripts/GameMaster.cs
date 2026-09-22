using System.Collections;  // Gives us IEnumerator, needed for the Sonar Ping's timed coroutine.
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

    [Tooltip("The SpriteRenderer showing the office image. Its sprite is swapped between the normal ('lit') and Office Dim Sprite below whenever the lights are toggled.")]
    [SerializeField] private SpriteRenderer officeSpriteRenderer;

    [Tooltip("The sprite to show while the lights are OFF. Leave unassigned until you have this art - the office will simply keep showing its normal sprite instead of going blank. The 'lit' sprite is captured automatically from whatever officeSpriteRenderer is showing at the start of the night, so you don't need to assign that one separately.")]
    [SerializeField] private Sprite officeDimSprite;

    // The office's normal sprite, captured once in Start() from whatever
    // officeSpriteRenderer already shows - mirrors how officeViewCenterPosition
    // below is captured, so the user doesn't have to redundantly re-assign
    // a sprite that's already sitting right there in the scene.
    private Sprite officeLitSprite;

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

        // The Camera button's visibility has its own rules (light-gated, with
        // a playtest bypass) - see RefreshCameraButtonVisibility() in the
        // Camera Monitor region for the single place that decides this.
        RefreshCameraButtonVisibility();
    }

    /// <summary>
    /// Swaps the office's sprite to match the current light state. Called
    /// from ToggleLights() whenever the lights are switched. Safe
    /// to call even before officeDimSprite has any art assigned yet - it
    /// simply leaves the lit sprite showing instead of going blank.
    /// </summary>
    private void UpdateOfficeSpriteForLights()
    {
        if (officeSpriteRenderer == null)
        {
            return;
        }

        if (lightsOn)
        {
            officeSpriteRenderer.sprite = officeLitSprite;
        }
        else if (officeDimSprite != null)
        {
            officeSpriteRenderer.sprite = officeDimSprite;
        }
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

    [Tooltip("The Maintenance Panel itself (Release Pressure + Lights button). Should start inactive.")]
    [SerializeField] private GameObject maintenancePanel;

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
    /// Flips the office lights on/off. Hook this up to the Lights button's
    /// OnClick in the Inspector - one button both turns the lights off and
    /// back on, the same click-to-toggle pattern already used by the
    /// Maintenance/Camera edge buttons (see ToggleMaintenancePanel() and
    /// ToggleCameraMonitor()).
    ///
    /// This is deliberately a PARAMETERLESS method rather than one taking a
    /// bool: a plain Button's OnClick has no live value to pass, so wiring
    /// it to a method with a bool parameter forces you to set a FIXED value
    /// in the Inspector - meaning every click would send that same fixed
    /// value forever, which is exactly the "lights never turn back on" bug
    /// this replaced. By flipping our own lightsOn field internally, a
    /// plain no-argument Button click is all that's needed.
    /// </summary>
    public void ToggleLights()
    {
        lightsOn = !lightsOn;

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
        // without lights (unless hideCameraButtonWhileLightsAreDim has been
        // turned off for playtesting, in which case this restriction doesn't
        // apply at all).
        bool cameraRestrictedRightNow = !lightsOn && hideCameraButtonWhileLightsAreDim;
        if (cameraRestrictedRightNow && isCameraPanelOpen)
        {
            CloseCameraMonitor();
        }

        // Match the office's sprite to the new light state.
        UpdateOfficeSpriteForLights();

        // Re-evaluate the Camera button's visibility right now, rather than
        // waiting for UpdateLookAndEdgeButtons() to eventually get around to
        // it - this is what guarantees the button reliably comes back the
        // instant the lights are turned back on.
        RefreshCameraButtonVisibility();
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

    [Header("Camera Monitor - Map / Vent Layer References")]
    // The Camera Monitor has two parallel "layers", exactly like FNAF3's
    // Cams/Vents split: the normal Facility Floorplan (rooms) and the Vent
    // Network. Only one layer is visible at a time - ToggleMapView() below
    // swaps between them. Each layer is made of two pieces: a "selector"
    // root (the map graphic + the buttons you click to pick a camera) and a
    // "feed" root (the actual camera views, one of which SelectCamera/
    // SelectVentCamera shows at a time).

    [Tooltip("The existing 'CameraSelector' object: the Facility Floorplan map graphic plus its camera-select buttons. Shown while isViewingVents is false.")]
    [SerializeField] private GameObject regularCameraSelectorRoot;

    [Tooltip("The existing 'CameraViews' object: the parent of cameraFeedViews. Shown while isViewingVents is false.")]
    [SerializeField] private GameObject regularCameraFeedRoot;

    [Tooltip("A new object you build: the Vent Network map graphic plus its vent-select buttons. Shown while isViewingVents is true.")]
    [SerializeField] private GameObject ventCameraSelectorRoot;

    [Tooltip("A new object you build: the parent of ventFeedViews. Shown while isViewingVents is true.")]
    [SerializeField] private GameObject ventCameraFeedRoot;

    [Tooltip("Placeholder views for each vent camera feed, in the same order as the vent buttons you will build. Works exactly like cameraFeedViews, just for the Vent Network layer.")]
    [SerializeField] private GameObject[] ventFeedViews;

    [Tooltip("The Map button that swaps between the Facility Floorplan and Vent Network layers. Hook its OnClick to ToggleMapView().")]
    [SerializeField] private Button mapToggleButton;

    [Tooltip("OPTIONAL: every regular camera-select Button, used only to visibly grey them out while a Sonar Ping has the monitor locked. Safe to leave empty - the ping lock still functionally blocks camera switching without this.")]
    [SerializeField] private Button[] regularCameraSelectButtons;

    [Tooltip("OPTIONAL: every vent camera-select Button - same purpose as regularCameraSelectButtons, but for the Vent Network layer.")]
    [SerializeField] private Button[] ventCameraSelectButtons;

    [Header("Camera Monitor - Settings")]

    [Tooltip("When checked (default, matches the game bible), the Camera Monitor button is hidden and cannot be opened while the lights are off. Uncheck during playtesting if you want to reach the camera screen regardless of light state.")]
    [SerializeField] private bool hideCameraButtonWhileLightsAreDim = true;

    [Tooltip("Which camera (its index in Camera Feed Views) is shown the first time the Camera Monitor is opened each night. Every time after that, the Monitor remembers whichever camera was last selected.")]
    [SerializeField] private int defaultCameraIndex = 0;

    [Tooltip("Which vent camera (its index in Vent Feed Views) is shown the first time the player switches to the Vent Network layer each night. Every time after that, the Monitor remembers whichever vent camera was last selected - mirrors defaultCameraIndex above, just for the vent layer.")]
    [SerializeField] private int defaultVentCameraIndex = 0;

    // True while the Camera Monitor is open and covering the view.
    private bool isCameraPanelOpen;

    // Which camera feed is currently selected, or -1 if none has been
    // picked yet this session.
    private int currentCameraIndex = -1;

    // Which vent camera feed is currently selected, or -1 if none has been
    // picked yet this session. Mirrors currentCameraIndex, just for vents.
    private int currentVentCameraIndex = -1;

    // False = viewing the Facility Floorplan (regular cameras, the default).
    // True = viewing the Vent Network. Flipped by ToggleMapView().
    private bool isViewingVents;

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
        // Game bible: firing a Sonar Ping locks the monitor - "you cannot
        // lower the cameras while the ping animation plays." Block the
        // player-facing toggle entirely while a ping is in progress. Note
        // this does NOT affect CloseCameraMonitor() being called directly
        // by ToggleLights()/HideAllPanelsAndButtons() elsewhere - lights-off
        // and game-over must always be able to force the monitor shut.
        if (isPingLocked)
        {
            return;
        }

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
        // Blocked while lights are off, UNLESS hideCameraButtonWhileLightsAreDim
        // has been turned off for playtesting.
        if (!lightsOn && hideCameraButtonWhileLightsAreDim)
        {
            return;
        }

        isCameraPanelOpen = true;

        if (cameraPanel != null)
        {
            cameraPanel.SetActive(true);
        }

        // Only the very first open this night has no prior selection
        // (currentCameraIndex is still -1) - show the configured default.
        // Every open after that leaves currentCameraIndex/cameraFeedViews
        // exactly as SelectCamera() last set them, so the Monitor remembers
        // the last-viewed camera automatically.
        if (currentCameraIndex == -1)
        {
            SelectCamera(defaultCameraIndex);
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
    /// Single source of truth for whether the Camera Monitor's edge button
    /// should currently be visible. Called every frame from
    /// UpdateLookAndEdgeButtons() while free-looking, AND immediately from
    /// ToggleLights() - calling it from both places is what
    /// guarantees the button reliably reappears the instant the lights come
    /// back on, rather than depending on the pan state happening to line up
    /// again on its own.
    /// </summary>
    private void RefreshCameraButtonVisibility()
    {
        // Don't touch it while a panel covers the screen. If the Camera
        // Monitor itself is open, this button doubles as its close button
        // and must stay visible/untouched here. If the Maintenance Panel is
        // open, UpdateLookAndEdgeButtons() isn't running pan logic at all,
        // so there's no meaningful "current position" to check yet - it'll
        // be correctly resolved the moment that panel closes.
        if (isMaintenancePanelOpen || isCameraPanelOpen)
        {
            return;
        }

        bool reachedFarRight = currentPanOffset <= -maxPanDistance + edgeArrivalTolerance;

        // Per the game bible, the Camera Monitor is normally unavailable
        // while the lights are off - hideCameraButtonWhileLightsAreDim lets
        // you bypass that restriction during playtesting.
        bool cameraCurrentlyAllowed = lightsOn || !hideCameraButtonWhileLightsAreDim;

        SetButtonVisible(cameraOpenButton, reachedFarRight && cameraCurrentlyAllowed);
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
        // Blocked while a Sonar Ping has the monitor locked - see the Sonar
        // Ping region for why.
        if (isPingLocked)
        {
            return;
        }

        // Guard against an out-of-range index (e.g. an Inspector typo)
        // rather than throwing an exception mid-game.
        if (cameraFeedViews == null || cameraIndex < 0 || cameraIndex >= cameraFeedViews.Length)
        {
            Debug.LogWarning("GameMaster.SelectCamera: index " + cameraIndex + " is out of range.");
            return;
        }

        currentCameraIndex = cameraIndex;
        ActivateOnlyIndex(cameraFeedViews, currentCameraIndex);
        RefreshAllCameraSelectButtonHighlights();
    }

    /// <summary>
    /// Switches the Vent Network layer to show a specific vent camera's
    /// feed. Hook one of these up (with the matching index) to each vent
    /// button you build inside ventCameraSelectorRoot. Works exactly like
    /// SelectCamera() above, just targeting the vent arrays/state instead of
    /// the regular camera ones - the two layers are otherwise identical.
    /// </summary>
    public void SelectVentCamera(int ventCameraIndex)
    {
        // Blocked while a Sonar Ping has the monitor locked - see the Sonar
        // Ping region for why.
        if (isPingLocked)
        {
            return;
        }

        // Guard against an out-of-range index (e.g. an Inspector typo)
        // rather than throwing an exception mid-game.
        if (ventFeedViews == null || ventCameraIndex < 0 || ventCameraIndex >= ventFeedViews.Length)
        {
            Debug.LogWarning("GameMaster.SelectVentCamera: index " + ventCameraIndex + " is out of range.");
            return;
        }

        currentVentCameraIndex = ventCameraIndex;
        ActivateOnlyIndex(ventFeedViews, currentVentCameraIndex);
        RefreshAllCameraSelectButtonHighlights();
    }

    /// <summary>
    /// Shared helper used by both SelectCamera() and SelectVentCamera():
    /// activates only the view at indexToShow within the given array and
    /// deactivates every other one, so only a single feed is ever visible
    /// at a time within a layer.
    /// </summary>
    private void ActivateOnlyIndex(GameObject[] views, int indexToShow)
    {
        for (int i = 0; i < views.Length; i++)
        {
            if (views[i] != null)
            {
                views[i].SetActive(i == indexToShow);
            }
        }
    }

    /// <summary>
    /// Swaps the Camera Monitor between the Facility Floorplan (regular
    /// cameras) and the Vent Network - the "Map" button from the game
    /// bible's Section 3C. Hook this up to mapToggleButton's OnClick.
    /// Blocked while a Sonar Ping has the monitor locked, same as switching
    /// cameras - the bible's "cannot lower the cameras" restriction covers
    /// any monitor navigation, not just camera selection.
    /// </summary>
    public void ToggleMapView()
    {
        if (isPingLocked)
        {
            return;
        }

        isViewingVents = !isViewingVents;

        // Show whichever pair of roots (selector + feed) matches the layer
        // we just switched to, and hide the other pair.
        SetActiveIfAssigned(regularCameraSelectorRoot, !isViewingVents);
        SetActiveIfAssigned(regularCameraFeedRoot, !isViewingVents);
        SetActiveIfAssigned(ventCameraSelectorRoot, isViewingVents);
        SetActiveIfAssigned(ventCameraFeedRoot, isViewingVents);

        // The very first time we switch INTO the vent layer this session,
        // there is no previously-selected vent camera yet - show the
        // configured default, exactly like OpenCameraMonitor() does for the
        // regular layer's first-ever open.
        if (isViewingVents && currentVentCameraIndex == -1)
        {
            SelectVentCamera(defaultVentCameraIndex);
        }
    }

    /// <summary>Small helper to safely SetActive() a GameObject that might not be assigned yet.</summary>
    private void SetActiveIfAssigned(GameObject targetObject, bool active)
    {
        if (targetObject != null)
        {
            targetObject.SetActive(active);
        }
    }

    /// <summary>
    /// Which camera index is currently on-screen, regardless of which layer
    /// (regular or vent) is active. Used by the Sonar Ping to know which
    /// camera it is pinging.
    /// </summary>
    private int GetActiveCameraIndex()
    {
        return isViewingVents ? currentVentCameraIndex : currentCameraIndex;
    }

    /// <summary>
    /// Greys out (disables) the button at selectedIndex within the given
    /// array and makes sure every OTHER button in that same array is
    /// interactable. This is what gives the player a clear "you are already
    /// looking at this one" indicator on the camera-select buttons. Safe to
    /// call with a null/empty array (e.g. if you haven't wired up
    /// regularCameraSelectButtons/ventCameraSelectButtons yet).
    /// </summary>
    private void HighlightSelectedButton(Button[] buttons, int selectedIndex)
    {
        if (buttons == null)
        {
            return;
        }

        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] != null)
            {
                buttons[i].interactable = (i != selectedIndex);
            }
        }
    }

    /// <summary>
    /// Re-applies the "currently selected camera is disabled" highlight to
    /// BOTH layers at once (regular and vent), each using its own
    /// remembered currentCameraIndex/currentVentCameraIndex. Called
    /// whenever a camera is selected, and again once a Sonar Ping's lock
    /// ends (see SetSonarLockedButtonsInteractable()) so the previously
    /// selected buttons come back disabled instead of every button coming
    /// back enabled. Refreshing both layers together - not just whichever
    /// one is currently visible - means switching the Map to the other
    /// layer always shows the correct highlight immediately, with no extra
    /// refresh call needed in ToggleMapView().
    /// </summary>
    private void RefreshAllCameraSelectButtonHighlights()
    {
        HighlightSelectedButton(regularCameraSelectButtons, currentCameraIndex);
        HighlightSelectedButton(ventCameraSelectButtons, currentVentCameraIndex);
    }


    // -------------------------------------------------------------------
    // REGION: SONAR PING
    // -------------------------------------------------------------------
    [Header("Sonar Ping - References")]

    [Tooltip("The Sonar Ping button, shown inside the Camera Monitor panel. Hook its OnClick to FireSonarPing().")]
    [SerializeField] private Button sonarPingButton;

    [Tooltip("A green-tinted UI Image (with a CanvasGroup component) covering the camera feed display area. Code fades its alpha 0-1-0 during a ping to simulate the sonar's illumination flash. Leave its GameObject inactive by default in the editor.")]
    [SerializeField] private CanvasGroup sonarFlashOverlay;

    [Tooltip("OPTIONAL: an AudioSource to play the ping sound effect through. Leave unassigned to skip audio for now.")]
    [SerializeField] private AudioSource sonarPingAudioSource;

    [Tooltip("OPTIONAL: the sound effect played the instant a ping fires. Only plays if both this and sonarPingAudioSource are assigned.")]
    [SerializeField] private AudioClip sonarPingClip;

    [Header("Sonar Ping - Settings")]

    [Tooltip("How long (in real seconds) the monitor stays locked while a ping's illumination plays out. Game bible: 1.5 seconds.")]
    [SerializeField] private float pingLockDurationSeconds = 1.5f;

    [Tooltip("Chance (0-1) that a ping's pushback attempt succeeds against Captain Barnacle, once he exists - the game bible's 'RNG Fail State'. Not yet consumed by any animatronic; kept here so the roll is ready the moment Barnacle is implemented.")]
    [Range(0f, 1f)]
    [SerializeField] private float pushbackSuccessChance = 0.7f;

    // True for the whole duration of a ping's illumination sequence. While
    // true, the monitor cannot be closed and cameras/the map cannot be
    // switched - see the isPingLocked checks throughout the Camera Monitor
    // region above.
    private bool isPingLocked;

    // Reference to the currently-running ping sequence, if any, so
    // HideAllPanelsAndButtons() can cleanly cancel it if the night ends
    // mid-ping (e.g. Suffocation while a ping is in progress).
    private Coroutine activePingCoroutine;

    /// <summary>
    /// Raised once a Sonar Ping's illumination sequence finishes playing.
    /// This is the extension point future animatronic scripts hook into:
    /// Captain Barnacle (once implemented) subscribes in his own OnEnable
    /// and checks "was I the occupant of cameraIndex/isVentCamera that just
    /// got pinged?" - if so, and pushbackRollSucceeded is true, he retreats
    /// one camera per the game bible's Pushback/RNG Fail State rule. The
    /// Diver ignores pushbackRollSucceeded entirely (pings only ever slow
    /// him, per the bible) but can still listen for when a ping happens.
    ///
    /// This is a STATIC event (rather than an instance one) because there
    /// is exactly one GameMaster in the scene - any future animatronic
    /// script can subscribe to GameMaster.OnSonarPing without needing a
    /// scene reference to find this object first.
    ///
    /// Nothing subscribes to this yet, since no animatronics exist - firing
    /// a ping today still does all of its heat/lock/visual/audio work, it
    /// just raises this event into the void afterwards.
    /// </summary>
    public static event System.Action<int, bool, bool> OnSonarPing;
    // Event parameters, in order:
    //   int  cameraIndex           - GetActiveCameraIndex() at the moment the ping fired.
    //   bool isVentCamera          - true if that camera was on the Vent Network layer.
    //   bool pushbackRollSucceeded - the result of this ping's pushbackSuccessChance roll.

    /// <summary>
    /// Fires a Sonar Ping on whichever camera is currently on-screen. Hook
    /// this up to sonarPingButton's OnClick. Does nothing if the monitor
    /// isn't open, a ping is already playing, or the Sonar is overheated.
    /// </summary>
    public void FireSonarPing()
    {
        if (!isCameraPanelOpen || isPingLocked || isSonarOverheated)
        {
            return;
        }

        activePingCoroutine = StartCoroutine(SonarPingRoutine());
    }

    /// <summary>
    /// Runs the full timed sequence for a single Sonar Ping: locks the
    /// monitor, fades the green illumination overlay in and back out over
    /// pingLockDurationSeconds, plays the ping sound effect, registers the
    /// ping against the Sonar Overheat counter, rolls the pushback RNG, and
    /// finally raises OnSonarPing before unlocking the monitor again.
    /// </summary>
    private IEnumerator SonarPingRoutine()
    {
        isPingLocked = true;
        SetSonarLockedButtonsInteractable(false);

        // The remaining-charge meter is only ever on-screen while a ping is
        // actually playing - show it now, hide it again once this routine
        // finishes below.
        if (sonarHeatMeter != null)
        {
            sonarHeatMeter.gameObject.SetActive(true);
        }

        // Remember which camera we are pinging BEFORE anything below has a
        // chance to change it (it can't while locked, but this keeps the
        // intent explicit rather than reading GetActiveCameraIndex() again
        // after the wait below).
        int pingedCameraIndex = GetActiveCameraIndex();
        bool pingedIsVentCamera = isViewingVents;

        // Play the ping sound effect immediately, if one is assigned.
        if (sonarPingAudioSource != null && sonarPingClip != null)
        {
            sonarPingAudioSource.PlayOneShot(sonarPingClip);
        }

        // A ping always costs against the Overheat counter, whether or not
        // it ends up overheating the Sonar this time.
        RegisterPingForOverheat();

        // Roll now whether the (future) pushback attempt succeeds. Rolling
        // it here, once per ping, means the result is ready and consistent
        // by the time OnSonarPing is raised below.
        bool pushbackRollSucceeded = Random.value <= pushbackSuccessChance;

        // Fade the green illumination overlay in for the first half of the
        // lock duration, then back out for the second half - a simple
        // triangle fade that is easy to follow and needs no extra library.
        float halfDuration = pingLockDurationSeconds / 2f;

        if (sonarFlashOverlay != null)
        {
            sonarFlashOverlay.gameObject.SetActive(true);
            sonarFlashOverlay.alpha = 0f;
        }

        float elapsed = 0f;
        while (elapsed < halfDuration)
        {
            elapsed += Time.deltaTime;
            if (sonarFlashOverlay != null)
            {
                sonarFlashOverlay.alpha = Mathf.Clamp01(elapsed / halfDuration);
            }
            yield return null;
        }

        elapsed = 0f;
        while (elapsed < halfDuration)
        {
            elapsed += Time.deltaTime;
            if (sonarFlashOverlay != null)
            {
                sonarFlashOverlay.alpha = 1f - Mathf.Clamp01(elapsed / halfDuration);
            }
            yield return null;
        }

        if (sonarFlashOverlay != null)
        {
            sonarFlashOverlay.alpha = 0f;
            sonarFlashOverlay.gameObject.SetActive(false);
        }

        // Tell anything listening (future animatronics) that a ping just
        // resolved against pingedCameraIndex/pingedIsVentCamera.
        OnSonarPing?.Invoke(pingedCameraIndex, pingedIsVentCamera, pushbackRollSucceeded);

        isPingLocked = false;
        activePingCoroutine = null;
        SetSonarLockedButtonsInteractable(true);

        if (sonarHeatMeter != null)
        {
            sonarHeatMeter.gameObject.SetActive(false);
        }
    }

    /// <summary>
    /// Enables/disables every button that the game bible's "UI Lock" rule
    /// says should be unusable while a ping is playing: the Map toggle and
    /// every camera-select button on both layers (if their optional arrays
    /// were assigned). The Sonar Ping button itself is handled separately
    /// by the Overheat system below, since it can also be disabled for a
    /// completely different reason (overheating).
    /// </summary>
    private void SetSonarLockedButtonsInteractable(bool interactable)
    {
        // The Ping button also greys itself out for the duration of its own
        // lock window, so it visibly reflects "busy" rather than looking
        // clickable while clicks are actually being ignored.
        if (sonarPingButton != null)
        {
            sonarPingButton.interactable = interactable;
        }

        if (mapToggleButton != null)
        {
            mapToggleButton.interactable = interactable;
        }

        if (interactable)
        {
            // Re-enabling after a ping: restore each layer's own "currently
            // selected camera" highlight rather than blanket-enabling every
            // button - otherwise the camera we're actually looking at would
            // wrongly become clickable again.
            RefreshAllCameraSelectButtonHighlights();
        }
        else
        {
            // Locking for a ping: every camera button goes fully
            // non-interactable regardless of selection - nothing can be
            // clicked at all while the monitor is locked.
            SetButtonArrayInteractable(regularCameraSelectButtons, false);
            SetButtonArrayInteractable(ventCameraSelectButtons, false);
        }
    }

    /// <summary>Small helper that safely sets .interactable on every Button in an (optionally null/empty) array.</summary>
    private void SetButtonArrayInteractable(Button[] buttons, bool interactable)
    {
        if (buttons == null)
        {
            return;
        }

        for (int i = 0; i < buttons.Length; i++)
        {
            if (buttons[i] != null)
            {
                buttons[i].interactable = interactable;
            }
        }
    }


    // -------------------------------------------------------------------
    // REGION: SONAR OVERHEAT
    // -------------------------------------------------------------------
    [Header("Sonar Overheat - References")]

    [Tooltip("OPTIONAL: the 'SONAR REBOOTING' text shown in place of the Sonar Ping button while overheated. Should start inactive - code activates it the instant an overheat begins and deactivates it the instant the reboot finishes.")]
    [SerializeField] private TMP_Text sonarRebootingText;

    [Tooltip("OPTIONAL: a Slider (0-1, same pattern as the Oxygen slider) visualizing the Sonar's REMAINING charge - full at 1 right after a reboot, draining toward 0 as pings are fired. Only shown while a ping is actively playing; hidden the rest of the time. Leave unassigned to skip this visual for now.")]
    [SerializeField] private Slider sonarHeatMeter;

    [Header("Sonar Overheat - Settings")]

    [Tooltip("The lowest possible number of pings the Sonar can fire before overheating. Each reboot re-rolls a new random value between this and sonarOverheatCapacityMaxPings, matching the game bible's 'random maximum usage limit'.")]
    [SerializeField] private int sonarOverheatCapacityMinPings = 4;

    [Tooltip("The highest possible number of pings the Sonar can fire before overheating.")]
    [SerializeField] private int sonarOverheatCapacityMaxPings = 7;

    [Tooltip("How long (in real seconds) the Sonar stays offline after overheating before it automatically reboots. Runs even while the Camera Monitor is closed, matching the game bible's 'reboots automatically over time'.")]
    [SerializeField] private float sonarRebootSeconds = 8f;

    // How many pings this random cap allows before the Sonar overheats.
    // Re-rolled in Start() and again every time the Sonar finishes
    // rebooting - see RollNewOverheatCapacity().
    private int currentOverheatCapacityPings;

    // How many pings have been fired since the Sonar last came online.
    // Resets to 0 whenever a new capacity is rolled.
    private int pingsFiredSinceLastReboot;

    // True while the Sonar is offline recovering from an overheat. The
    // Sonar Ping button is hidden and sonarRebootingText is shown for the
    // whole time this is true.
    private bool isSonarOverheated;

    // Counts down from sonarRebootSeconds while isSonarOverheated is true.
    // Once it reaches zero, the Sonar comes back online automatically.
    private float overheatRebootTimer;

    /// <summary>
    /// Rolls a fresh random overheat capacity between
    /// sonarOverheatCapacityMinPings and sonarOverheatCapacityMaxPings
    /// (inclusive) and resets the ping counter back to zero. Called once at
    /// the start of the night and again every time the Sonar finishes
    /// rebooting, so every "life" of the Sonar gets its own random limit.
    /// </summary>
    private void RollNewOverheatCapacity()
    {
        // Random.Range(int, int) treats the upper bound as EXCLUSIVE, so we
        // add 1 to make sonarOverheatCapacityMaxPings itself reachable.
        currentOverheatCapacityPings = Random.Range(sonarOverheatCapacityMinPings, sonarOverheatCapacityMaxPings + 1);
        pingsFiredSinceLastReboot = 0;
        UpdateSonarHeatMeterUI();
    }

    /// <summary>
    /// Counts one ping against the Overheat capacity and triggers an
    /// overheat if the cap has just been reached. Called once per fired
    /// ping from SonarPingRoutine(), regardless of whether that ping ends
    /// up overheating the Sonar.
    /// </summary>
    private void RegisterPingForOverheat()
    {
        pingsFiredSinceLastReboot++;

        if (pingsFiredSinceLastReboot >= currentOverheatCapacityPings)
        {
            TriggerSonarOverheat();
        }

        UpdateSonarHeatMeterUI();
    }

    /// <summary>
    /// Puts the Sonar offline: hides the Ping button, shows the "SONAR
    /// REBOOTING" text in its place, and starts the reboot countdown.
    /// </summary>
    private void TriggerSonarOverheat()
    {
        isSonarOverheated = true;
        overheatRebootTimer = sonarRebootSeconds;

        SetButtonVisible(sonarPingButton != null ? sonarPingButton.gameObject : null, false);
        if (sonarRebootingText != null)
        {
            sonarRebootingText.gameObject.SetActive(true);
        }
    }

    /// <summary>
    /// Counts down the reboot timer while the Sonar is overheated. Called
    /// every frame from Update() regardless of whether the Camera Monitor
    /// is even open, so the Sonar keeps recovering in the background - the
    /// game bible specifies it "reboots automatically over time". Once the
    /// timer reaches zero, the Sonar comes back online with a freshly
    /// rolled overheat capacity and the Ping button reappears in place of
    /// the "SONAR REBOOTING" text.
    /// </summary>
    private void UpdateSonarOverheatCooldown(float deltaTime)
    {
        if (!isSonarOverheated)
        {
            return;
        }

        overheatRebootTimer -= deltaTime;
        if (overheatRebootTimer <= 0f)
        {
            isSonarOverheated = false;
            RollNewOverheatCapacity();

            if (sonarRebootingText != null)
            {
                sonarRebootingText.gameObject.SetActive(false);
            }
            SetButtonVisible(sonarPingButton != null ? sonarPingButton.gameObject : null, true);
        }
    }

    /// <summary>
    /// Refreshes the optional Sonar Heat slider, if one is assigned. Reads as
    /// REMAINING charge rather than heat built up: 1 (full) right after a
    /// reboot, counting down to 0 as pings are fired, reaching 0 exactly on
    /// the ping that overheats the Sonar.
    /// </summary>
    private void UpdateSonarHeatMeterUI()
    {
        if (sonarHeatMeter != null && currentOverheatCapacityPings > 0)
        {
            sonarHeatMeter.value = 1f - (float)pingsFiredSinceLastReboot / currentOverheatCapacityPings;
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

        // If a Sonar Ping happened to be mid-flight when the night ended
        // (e.g. Suffocation while the green flash was still playing), cancel
        // it so its overlay doesn't stay stuck visible under the end screen.
        if (activePingCoroutine != null)
        {
            StopCoroutine(activePingCoroutine);
            activePingCoroutine = null;
        }
        isPingLocked = false;
        if (sonarFlashOverlay != null)
        {
            sonarFlashOverlay.alpha = 0f;
            sonarFlashOverlay.gameObject.SetActive(false);
        }
        if (sonarHeatMeter != null)
        {
            sonarHeatMeter.gameObject.SetActive(false);
        }
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

        // Remember whatever sprite is already showing as the "lit" sprite,
        // so UpdateOfficeSpriteForLights() can swap back to it later without
        // needing it separately assigned in the Inspector.
        if (officeSpriteRenderer != null)
        {
            officeLitSprite = officeSpriteRenderer.sprite;
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

        // Every night starts on the Facility Floorplan layer, with neither
        // layer having a remembered camera selection yet.
        isViewingVents = false;
        currentCameraIndex = -1;
        currentVentCameraIndex = -1;
        SetActiveIfAssigned(regularCameraSelectorRoot, true);
        SetActiveIfAssigned(regularCameraFeedRoot, true);
        SetActiveIfAssigned(ventCameraSelectorRoot, false);
        SetActiveIfAssigned(ventCameraFeedRoot, false);

        // The Sonar starts cold (no pings fired yet) and online, with a
        // fresh random overheat capacity for the night.
        isSonarOverheated = false;
        RollNewOverheatCapacity();
        SetButtonVisible(sonarPingButton != null ? sonarPingButton.gameObject : null, true);
        if (sonarRebootingText != null) sonarRebootingText.gameObject.SetActive(false);
        if (sonarHeatMeter != null) sonarHeatMeter.gameObject.SetActive(false);

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

        // The Sonar's reboot timer keeps counting down even while the
        // Camera Monitor is closed - see UpdateSonarOverheatCooldown()'s
        // comment for why.
        UpdateSonarOverheatCooldown(Time.deltaTime);

        // You can only look around the office while no panel is covering
        // the view - the Maintenance Panel and Camera Monitor both take
        // over full control of the screen while open.
        if (!isMaintenancePanelOpen && !isCameraPanelOpen)
        {
            UpdateLookAndEdgeButtons(Time.deltaTime);
        }
    }
}
