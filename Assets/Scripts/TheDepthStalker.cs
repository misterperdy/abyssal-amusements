using UnityEngine;

/// <summary>
/// The Depth Stalker - the game's "anti-camper". His one and only job is to
/// stop the player from sitting in the dark forever to stay safe from The
/// Diver. Like CaptainBarnacle and TheDiver, this script is completely
/// separate from GameMaster: GameMaster owns the lights and the
/// jumpscare/Game Over flow, and this script only READS the lights and asks
/// GameMaster to play his jumpscare when the time comes.
///
/// HOW HE WORKS - one "darkness meter" (darknessTimer) drives everything:
///
///   - Lights OFF: the meter fills up by 1 every real second.
///   - Lights ON:  the meter drains by 1 x recoveryRateMultiplier every
///                 real second, never going below 0.
///
///   0s ............ appearDelaySeconds ............ appearDelay + jumpscareDelay
///   |---- safe ----|---- he is visible in the office ----| JUMPSCARE
///
///   - Once the meter reaches appearDelaySeconds (5s by default), his
///     office sprite appears. It stays visible for as long as the meter is
///     at or above that point - EVEN with the lights back on - so the player
///     can see he is still "close" and that turning the lights off again
///     right now is dangerous.
///   - Once the meter reaches appearDelaySeconds + jumpscareDelaySeconds
///     (10s by default), he jumpscares the player and the night is over.
///     The meter can only go UP while the lights are off, so this can only
///     ever happen in the dark.
///
/// Using ONE meter instead of two separate timers is what makes the
/// "timers decrease at the same rate they increased" rule work for free:
/// draining the meter naturally undoes the second phase first, then the
/// first phase, with no extra bookkeeping.
///
/// HOW THE PLAYER FIGHTS BACK:
///   - Simply turn the lights back on and keep them on for a while. Cameras,
///     Sonar Ping, and Release Pressure do nothing to him, so this script
///     doesn't listen to any of GameMaster's events.
/// </summary>
public class TheDepthStalker : MonoBehaviour
{
    // -------------------------------------------------------------------
    // REGION: REFERENCES
    // -------------------------------------------------------------------
    [Header("References")]

    [Tooltip("Drag the scene's GameMaster object here. Used to read whether the lights are on, whether the night is still in progress, and to trigger his jumpscare.")]
    [SerializeField] private GameMaster gameMaster;


    // -------------------------------------------------------------------
    // REGION: DARKNESS SETTINGS
    // -------------------------------------------------------------------
    [Header("Darkness Settings")]

    [Tooltip("How many seconds of darkness it takes for The Depth Stalker to appear in the office.")]
    [Min(0f)]
    [SerializeField] private float appearDelaySeconds = 5f;

    [Tooltip("How many MORE seconds of darkness, after he has appeared, before he jumpscares the player. The total darkness allowed is Appear Delay + this.")]
    [Min(0f)]
    [SerializeField] private float jumpscareDelaySeconds = 5f;

    [Tooltip("How fast the darkness meter drains while the lights are ON, compared to how fast it fills while they are OFF. 1 = same speed (10s of darkness takes 10s of light to fully forget). 2 = twice as fast, 0.5 = half as fast. 0 = he never forgets.")]
    [Min(0f)]
    [SerializeField] private float recoveryRateMultiplier = 1f;


    // -------------------------------------------------------------------
    // REGION: VISUALS
    // -------------------------------------------------------------------
    [Header("Visuals (Office)")]

    [Tooltip("The 'Depth Stalker inside the office' GameObject, placed in the office scene (a child of the office sprite, so it pans with it). Should start INACTIVE - this script turns it on only while the darkness meter is at or above Appear Delay.")]
    [SerializeField] private GameObject officeVisualObject;


    // -------------------------------------------------------------------
    // REGION: RUNTIME STATE
    // -------------------------------------------------------------------

    // The darkness meter, in seconds. Goes up while the lights are off and
    // down while they are on. See the class comment above for how the two
    // thresholds (appear and jumpscare) sit along it.
    private float darknessTimer;

    // True while his office visual is showing. Tracked separately from the
    // meter so we only toggle the visual (and log) on the exact frame it
    // changes, not every single frame.
    private bool isVisible;


    // -------------------------------------------------------------------
    // REGION: UNITY LIFECYCLE
    // -------------------------------------------------------------------

    /// <summary>Starts the night with an empty darkness meter and his office visual hidden.</summary>
    private void Start()
    {
        // Hide the visual no matter how it was left in the editor. We set
        // it directly here (instead of going through SetVisible()) because
        // SetVisible() only acts when the state CHANGES, and isVisible
        // already starts out false.
        SetVisualActive(officeVisualObject, false);

        darknessTimer = 0f;
        isVisible = false;
    }

    /// <summary>Fills/drains the darkness meter and reacts to it, for as long as the night is still in progress.</summary>
    private void Update()
    {
        // Don't act at all once the night is no longer being played:
        // Victory, Game Over, or ANY animatronic's jumpscare (including this
        // one's) freezes him in place, exactly like Barnacle and The Diver.
        if (gameMaster == null || gameMaster.CurrentState != GameMaster.GameState.Playing)
        {
            return;
        }

        UpdateDarknessTimer();
        RefreshVisibility();

        if (darknessTimer >= GetJumpscareThreshold())
        {
            // GameMaster ignores this if another animatronic's jumpscare (or
            // any other end of the night) has already started, so two
            // jumpscares can never play at once. Once it succeeds, the game
            // state is no longer Playing, so the guard at the top of this
            // method stops us from ever calling it a second time.
            Debug.Log("[DepthStalker] The lights stayed off for too long - jumpscare!");

            // Hide his in-office sprite first - the jumpscare overlay IS him, so
            // leaving this on would show two of him at once. Done directly (not via
            // SetVisible()) so we don't log the misleading "faded back" message.
            isVisible = false;
            SetVisualActive(officeVisualObject, false);

            gameMaster.TriggerDepthStalkerJumpscare();
        }
    }


    // -------------------------------------------------------------------
    // REGION: DARKNESS METER
    // -------------------------------------------------------------------

    /// <summary>
    /// Fills the darkness meter while the lights are off, and drains it
    /// (scaled by recoveryRateMultiplier) while they are on. The result is
    /// always kept between 0 and the jumpscare threshold.
    /// </summary>
    private void UpdateDarknessTimer()
    {
        if (gameMaster.LightsOn)
        {
            darknessTimer -= Time.deltaTime * recoveryRateMultiplier;
        }
        else
        {
            darknessTimer += Time.deltaTime;
        }

        darknessTimer = Mathf.Clamp(darknessTimer, 0f, GetJumpscareThreshold());
    }

    /// <summary>The total seconds of darkness allowed before the jumpscare: the appear delay plus the jumpscare delay.</summary>
    private float GetJumpscareThreshold()
    {
        return appearDelaySeconds + jumpscareDelaySeconds;
    }


    // -------------------------------------------------------------------
    // REGION: VISUALS HELPERS
    // -------------------------------------------------------------------

    /// <summary>
    /// He is visible whenever the darkness meter is at or above
    /// appearDelaySeconds - regardless of whether the lights are currently
    /// on or off.
    /// </summary>
    private void RefreshVisibility()
    {
        SetVisible(darknessTimer >= appearDelaySeconds);
    }

    /// <summary>
    /// Shows/hides his office visual, but only on the frame the state
    /// actually changes - so the visual isn't re-toggled and the Console
    /// isn't spammed every frame.
    /// </summary>
    private void SetVisible(bool visible)
    {
        if (visible == isVisible)
        {
            return;
        }

        isVisible = visible;
        SetVisualActive(officeVisualObject, isVisible);

        if (isVisible)
        {
            Debug.Log("[DepthStalker] Materialized in the office after " + appearDelaySeconds.ToString("F1") + "s of darkness - " + jumpscareDelaySeconds.ToString("F1") + "s more and he strikes!");
        }
        else
        {
            Debug.Log("[DepthStalker] Faded back into the dark - the player kept the lights on long enough.");
        }
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
