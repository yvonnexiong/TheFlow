using System.Collections.Generic;
using UnityEngine;

public class WayfinderRiverController : MonoBehaviour
{
    public enum RiverState
    {
        Idle,
        Patience,
        Rush,
        Completion
    }

    [Header("Scene References")]
    [SerializeField] private Renderer riverRenderer;
    [SerializeField] private Light gateLight;
    [SerializeField] private Transform gateLeft;
    [SerializeField] private Transform gateRight;
    [SerializeField] private Transform guideText;
    [SerializeField] private TextMesh guideTextMesh;
    [SerializeField] private ParticleSystem calmParticles;
    [SerializeField] private ParticleSystem turbulenceParticles;
    [SerializeField] private LineRenderer gestureTrail;
    [SerializeField] private List<Transform> stones = new List<Transform>();
    [SerializeField] private List<LineRenderer> flowLines = new List<LineRenderer>();

    [Header("Tuning")]
    [SerializeField] private float forceSpeedThreshold = 480f;
    [SerializeField] private float flowSpeedMin = 80f;
    [SerializeField] private float flowSpeedMax = 360f;
    [SerializeField, Min(1)] private int requiredPushes = 3;
    [SerializeField] private float requiredPushSeconds = 0.62f;
    [SerializeField] private float requiredPushDistance = 55f;
    [SerializeField] private float releaseSeconds = 0.18f;
    [SerializeField] private float rushCooldownSeconds = 1.35f;
    [SerializeField] private float stoneRiseSeconds = 0.7f;
    [SerializeField] private float stoneRiseHeight = 0.82f;
    [SerializeField] private float stoneRiseHeightStep = 0.26f;

    [Header("Input")]
    [SerializeField] private bool enableGenericXRInput = true;
    [SerializeField] private bool useCircularGestureProgression = true;
    [SerializeField] private float xrMetersToPixels = 850f;
    [SerializeField] private WayfinderHandMotionConfig handMotionConfig = new WayfinderHandMotionConfig();
    [SerializeField] private bool showHandDiagnostics = true;

    private readonly Queue<Vector3> trailPoints = new Queue<Vector3>();
    private readonly Dictionary<Transform, Vector3> stoneStarts = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, Vector3> stoneTargets = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, Vector3> stoneBaseScales = new Dictionary<Transform, Vector3>();
    private readonly Dictionary<Transform, Vector3> stoneRaisedScales = new Dictionary<Transform, Vector3>();
    private readonly List<float> stoneRiseProgress = new List<float>();

    private Camera sceneCamera;
    private IWayfinderInputSource inputSource;
    private RiverState state;
    private int successfulPushes;
    private float pushSeconds;
    private float pushHorizontalDisplacement;
    private float stillSeconds;
    private float rushCooldown;
    private float forcePulse;
    private bool awaitingRelease;
    private bool completed;
    private float completedAt;

    public RiverState State => state;
    public int SuccessfulPushes => successfulPushes;
    public int RequiredPushes => Mathf.Max(1, Mathf.Min(requiredPushes, stones.Count > 0 ? stones.Count : requiredPushes));
    public bool IsUsingXRInput { get; private set; }
    public WayfinderHandMotionMetrics CurrentHandMetrics { get; private set; }

    private Color calmRiver = new Color(0.12f, 0.44f, 0.47f, 0.78f);
    private Color forceRiver = new Color(0.18f, 0.31f, 0.40f, 0.88f);
    private Color flowLineDim = new Color(0.55f, 0.95f, 0.82f, 0.20f);
    private Color flowLineBright = new Color(0.86f, 1.0f, 0.93f, 0.95f);

    private void Awake()
    {
        sceneCamera = Camera.main;
        if (inputSource == null)
        {
            inputSource = new WayfinderHybridInputSource(enableGenericXRInput, xrMetersToPixels, handMotionConfig);
        }

        for (int index = 0; index < stones.Count; index++)
        {
            Transform stone = stones[index];
            stoneStarts[stone] = stone.position;
            stoneTargets[stone] = stone.position + Vector3.up *
                (stoneRiseHeight + index * Mathf.Max(0f, stoneRiseHeightStep));
            stoneBaseScales[stone] = stone.localScale;
            stoneRaisedScales[stone] = new Vector3(
                stone.localScale.x * 1.08f,
                stone.localScale.y * 1.72f,
                stone.localScale.z * 1.08f);
            stoneRiseProgress.Add(0f);
        }

        if (gestureTrail != null)
        {
            gestureTrail.positionCount = 0;
        }

        state = RiverState.Idle;
        SetGuide("Slow down. Watch the river.\nHold the mouse and push gently to the right.");
    }

    private void Update()
    {
        WayfinderInputFrame input = inputSource.Sample(Time.unscaledDeltaTime);
        IsUsingXRInput = input.IsXR;
        CurrentHandMetrics = input.HandMetrics;

        if (input.ResetPressed)
        {
            ResetExperience();
            return;
        }

        float speed = input.MotionSpeed;
        bool moving = speed > 12f;
        bool forceful = speed > forceSpeedThreshold;
        bool inputHeld = input.GestureHeld;
        bool flowing = inputHeld && moving && speed >= flowSpeedMin && speed <= flowSpeedMax;

        if (!completed)
        {
            if (useCircularGestureProgression)
            {
                if (rushCooldown > 0f)
                {
                    rushCooldown = Mathf.Max(0f, rushCooldown - Time.deltaTime);
                    if (rushCooldown <= 0f) state = RiverState.Idle;
                }
            }
            else if (forceful)
            {
                EnterRush();
            }
            else if (rushCooldown > 0f)
            {
                rushCooldown = Mathf.Max(0f, rushCooldown - Time.deltaTime);
                if (rushCooldown <= 0f)
                {
                    state = RiverState.Idle;
                    SetGuide("Let the water settle.\nThen begin one slow push.");
                }
            }
            else
            {
                UpdatePatienceGesture(speed, input.HorizontalDelta, inputHeld, moving, flowing);
            }
        }

        forcePulse = Mathf.MoveTowards(forcePulse, 0f, Time.deltaTime * 1.8f);
        UpdateTrail(input.ScreenPosition, forceful);
        UpdateRiverVisuals(forceful);
        UpdateStones();
        UpdateGate();
        UpdateParticles(forceful);

        if (showHandDiagnostics && input.IsXR && !completed)
        {
            UpdateHandDiagnosticGuide(input.HandMetrics);
        }
    }

    private void UpdateHandDiagnosticGuide(WayfinderHandMotionMetrics metrics)
    {
        string hands = metrics.LeftTracked && metrics.RightTracked
            ? "L+R"
            : metrics.LeftTracked ? "LEFT" : metrics.RightTracked ? "RIGHT" : "NONE";

        if (!metrics.TrackingValid)
        {
            SetGuide("PICO HANDS: NOT TRACKED\nRaise one open hand in front of the headset.");
            return;
        }

        if (!metrics.InInteractionVolume)
        {
            SetGuide($"PICO HAND: {hands}  {metrics.CombinedSpeedMps:0.00} m/s\nMove it to chest height, in front of you.");
            return;
        }

        if (state == RiverState.Rush)
        {
            SetGuide($"TOO FAST  {metrics.CombinedSpeedMps:0.00} m/s\nHold still and let the water settle.");
            return;
        }

        if (awaitingRelease)
        {
            SetGuide($"STONE {successfulPushes}/{RequiredPushes} RAISED\nHold still briefly, then push right again.");
            return;
        }

        float pushMeters = pushHorizontalDisplacement / Mathf.Max(1f, xrMetersToPixels);
        if (metrics.Classification == WayfinderHandMotionClass.Desired)
        {
            SetGuide($"GOOD  {metrics.CombinedSpeedMps:0.00} m/s  {hands}\n" +
                     $"Hold {pushSeconds:0.00}/{requiredPushSeconds:0.00}s  " +
                     $"Right {pushMeters:0.00}/{requiredPushDistance / Mathf.Max(1f, xrMetersToPixels):0.00}m");
            return;
        }

        if (!metrics.IsIntendedDirection && metrics.CombinedSpeedMps > 0.04f)
        {
            SetGuide($"HAND OK  {metrics.CombinedSpeedMps:0.00} m/s\nPush slowly from LEFT to RIGHT.");
            return;
        }

        string instruction = metrics.Classification == WayfinderHandMotionClass.TooFast
            ? "Slightly slower"
            : metrics.Classification == WayfinderHandMotionClass.TooSlow
                ? "A little faster"
                : "Begin a slow push to the right";
        SetGuide($"PICO HAND: {hands}  {metrics.CombinedSpeedMps:0.00} m/s\n{instruction} (target 0.10-0.35 m/s).");
    }

    private void UpdatePatienceGesture(float speed, float horizontalDelta, bool inputHeld, bool moving, bool flowing)
    {
        if (awaitingRelease)
        {
            if (!inputHeld || speed < 28f)
            {
                stillSeconds += Time.deltaTime;
                if (stillSeconds >= releaseSeconds)
                {
                    awaitingRelease = false;
                    stillSeconds = 0f;
                    state = RiverState.Idle;
                    SetGuide("Again. One slow push with the same rhythm.");
                }
            }
            else
            {
                stillSeconds = 0f;
            }
            return;
        }

        if (flowing)
        {
            state = RiverState.Patience;
            stillSeconds = 0f;
            pushSeconds += Time.deltaTime;
            pushHorizontalDisplacement = Mathf.Max(0f, pushHorizontalDisplacement + horizontalDelta);
            SetGuide("Good. Hold the rhythm.");

            if (pushSeconds >= requiredPushSeconds && pushHorizontalDisplacement >= requiredPushDistance)
            {
                RegisterPatientPush();
            }
        }
        else if (!inputHeld || !moving || speed < flowSpeedMin)
        {
            // A short pause is forgiving, but a full release starts a fresh push.
            stillSeconds += Time.deltaTime;
            if (stillSeconds >= releaseSeconds)
            {
                ResetCurrentPush();
                state = RiverState.Idle;
            }
        }
        else
        {
            // Movement above the patience window is not a success, even when it is
            // just below the force threshold.
            ResetCurrentPush();
            state = RiverState.Idle;
            SetGuide("Soften the movement.\nSteady is stronger than fast.");
        }
    }

    private void RegisterPatientPush()
    {
        successfulPushes = Mathf.Min(RequiredPushes, successfulPushes + 1);
        ResetCurrentPush();
        awaitingRelease = true;

        if (successfulPushes >= RequiredPushes)
        {
            CompleteCrossing();
            return;
        }

        state = RiverState.Patience;
        SetGuide("Good. Hold the rhythm.\nThe path remembers patience.");
    }

    private void EnterRush()
    {
        if (rushCooldown <= 0f)
        {
            successfulPushes = Mathf.Max(0, successfulPushes - 1);
        }

        state = RiverState.Rush;
        rushCooldown = rushCooldownSeconds;
        forcePulse = 1f;
        awaitingRelease = false;
        ResetCurrentPush();
        SetGuide("Too fast. The path closes when forced.");
    }

    private void ResetCurrentPush()
    {
        pushSeconds = 0f;
        pushHorizontalDisplacement = 0f;
        stillSeconds = 0f;
    }

    private void ResetExperience()
    {
        completed = false;
        state = RiverState.Idle;
        successfulPushes = 0;
        rushCooldown = 0f;
        forcePulse = 0f;
        awaitingRelease = false;
        completedAt = 0f;
        ResetCurrentPush();

        trailPoints.Clear();
        if (gestureTrail != null)
        {
            gestureTrail.positionCount = 0;
        }

        for (int i = 0; i < stones.Count; i++)
        {
            stoneRiseProgress[i] = 0f;
            stones[i].position = stoneStarts[stones[i]];
            stones[i].localScale = stoneBaseScales[stones[i]];
        }

        UpdateGate();
        SetGuide("Slow down. Watch the river.\nHold the mouse and push gently to the right.");
    }

    /// <summary>Allows deterministic input injection in PlayMode tests or alternate builds.</summary>
    public void SetInputSourceForTests(IWayfinderInputSource source)
    {
        if (source != null)
        {
            inputSource = source;
        }
    }

    /// <summary>Controller-free demo reset entry point for the presentation layer.</summary>
    public void DemoResetExperience()
    {
        ResetExperience();
    }

    /// <summary>Raises exactly one existing stone after a validated palm circle.</summary>
    public void RegisterCircularStone()
    {
        if (completed || !useCircularGestureProgression) return;
        awaitingRelease = false;
        RegisterPatientPush();
    }

    /// <summary>Shows river resistance without deleting an earned stone or resetting the circle.</summary>
    public void ShowCircularResistance()
    {
        if (completed || !useCircularGestureProgression) return;
        state = RiverState.Rush;
        rushCooldown = Mathf.Max(rushCooldown, rushCooldownSeconds);
        forcePulse = 1f;
    }

    private void UpdateTrail(Vector3 mousePosition, bool forceful)
    {
        if (gestureTrail == null || sceneCamera == null)
        {
            return;
        }

        Vector3 worldPoint = sceneCamera.ScreenToWorldPoint(new Vector3(mousePosition.x, mousePosition.y, 7.5f));
        trailPoints.Enqueue(worldPoint);
        while (trailPoints.Count > 42)
        {
            trailPoints.Dequeue();
        }

        gestureTrail.positionCount = trailPoints.Count;
        int index = 0;
        foreach (Vector3 point in trailPoints)
        {
            gestureTrail.SetPosition(index++, point);
        }

        Color trailColor = forceful
            ? new Color(1f, 0.48f, 0.36f, 0.75f)
            : Color.Lerp(new Color(0.70f, 0.95f, 1f, 0.35f), new Color(0.95f, 0.82f, 0.42f, 0.95f), Flow01());
        gestureTrail.startColor = trailColor;
        gestureTrail.endColor = new Color(trailColor.r, trailColor.g, trailColor.b, 0f);
    }

    private void UpdateRiverVisuals(bool forceful)
    {
        float flow = Flow01();
        float turbulence = forceful ? 1f : forcePulse;

        if (riverRenderer != null)
        {
            Color river = Color.Lerp(calmRiver, forceRiver, turbulence);
            river = Color.Lerp(river, new Color(0.10f, 0.52f, 0.55f, 0.65f), flow * 0.65f);
            riverRenderer.material.color = river;
        }

        foreach (LineRenderer line in flowLines)
        {
            Color lineColor = Color.Lerp(flowLineDim, flowLineBright, flow);
            line.startColor = lineColor;
            line.endColor = new Color(lineColor.r, lineColor.g, lineColor.b, lineColor.a * 0.1f);
            line.widthMultiplier = Mathf.Lerp(0.035f, 0.095f, flow);
        }
    }

    private void UpdateStones()
    {
        for (int i = 0; i < stones.Count; i++)
        {
            Transform stone = stones[i];
            float target = i < successfulPushes ? 1f : 0f;
            stoneRiseProgress[i] = Mathf.MoveTowards(stoneRiseProgress[i], target, Time.deltaTime / Mathf.Max(0.01f, stoneRiseSeconds));
            float easedProgress = Smooth(stoneRiseProgress[i]);
            stone.position = Vector3.Lerp(stoneStarts[stone], stoneTargets[stone], easedProgress);
            stone.localScale = Vector3.Lerp(
                stoneBaseScales[stone], stoneRaisedScales[stone], easedProgress);
        }
    }

    private void UpdateGate()
    {
        float open = completed ? Smooth(Mathf.Clamp01((Time.time - completedAt - 1.3f) / 1.2f)) : 0f;

        if (gateLight != null)
        {
            gateLight.intensity = Mathf.Lerp(0.25f, 9.5f, open);
            gateLight.range = Mathf.Lerp(3.2f, 13.0f, open);
        }

        if (gateLeft != null)
        {
            gateLeft.localRotation = Quaternion.Euler(0f, -38f * open, 0f);
        }

        if (gateRight != null)
        {
            gateRight.localRotation = Quaternion.Euler(0f, 38f * open, 0f);
        }
    }

    private void UpdateParticles(bool forceful)
    {
        if (calmParticles != null)
        {
            ParticleSystem.EmissionModule calmEmission = calmParticles.emission;
            calmEmission.rateOverTime = Mathf.Lerp(8f, 36f, Flow01());
        }

        if (turbulenceParticles != null)
        {
            ParticleSystem.EmissionModule forceEmission = turbulenceParticles.emission;
            forceEmission.rateOverTime = forceful ? 95f : Mathf.Lerp(0f, 25f, forcePulse);
        }
    }

    private void CompleteCrossing()
    {
        completed = true;
        state = RiverState.Completion;
        completedAt = Time.time;
        SetGuide("Patience opens the path.");
    }

    private void SetGuide(string message)
    {
        if (guideTextMesh != null && guideTextMesh.text != message)
        {
            guideTextMesh.text = message;
        }
    }

    private float Flow01()
    {
        float partialPush = Mathf.Min(
            pushSeconds / Mathf.Max(0.01f, requiredPushSeconds),
            pushHorizontalDisplacement / Mathf.Max(0.01f, requiredPushDistance));
        return Mathf.Clamp01((successfulPushes + partialPush) / RequiredPushes);
    }

    private static float Smooth(float value)
    {
        return value * value * (3f - 2f * value);
    }
}
