using System.Collections.Generic;
using UnityEngine;

[DefaultExecutionOrder(200)]
public sealed class WayfinderVerticalSliceController : MonoBehaviour
{
    [Header("Existing motion source")]
    [SerializeField] private WayfinderRiverController riverController;
    [SerializeField] private MonoBehaviour palmPoseSource;

    [Header("Named future integration slots")]
    [SerializeField] private WayfinderWorldRevealSlot worldLabsWorldSlot;
    [SerializeField] private WayfinderRewardMaterializationSlot tripoRewardSlot;
    [SerializeField] private WayfinderScriptedReflectionController scriptedReflection;

    [Header("Headset-legible feedback")]
    [SerializeField] private TextMesh userFeedbackText;
    [SerializeField] private TextMesh judgeHudText;
    [SerializeField] private GameObject judgeHudRoot;
    [SerializeField] private TextMesh completionText;
    [SerializeField] private LineRenderer progressRing;
    [SerializeField] private WayfinderAnalogSpeedometer analogSpeedometer;
    [SerializeField] private WayfinderCircularPalmGuide circularPalmGuide;
    [SerializeField] private WayfinderPalmDwellTarget[] dwellTargets;

    [Header("First-time player onboarding")]
    [SerializeField] private GameObject introCardRoot;
    [SerializeField] private TextMesh introCardText;
    [SerializeField] private GameObject gameplayHudRoot;
    [SerializeField] private Transform introStartButton;
    [SerializeField, Min(0.05f)] private float introButtonTouchRadiusMeters = 0.16f;

    [Header("Story and peace-state presentation")]
    [SerializeField] private GameObject stoneStoryRoot;
    [SerializeField] private TextMesh stoneStoryText;
    [SerializeField] private GameObject peaceStateRoot;
    [SerializeField] private TextMesh peaceStateText;
    [SerializeField, Min(1f)] private float stoneStorySeconds = 3.2f;

    [Header("Public policy configuration")]
    [SerializeField] private WayfinderMemoryKeeperConfig memoryKeeperConfig = new WayfinderMemoryKeeperConfig();
    [SerializeField] private WayfinderCircularGestureConfig circularGestureConfig = new WayfinderCircularGestureConfig();
    [SerializeField, Min(0.1f)] private float palmDwellSeconds = 1.1f;
    [SerializeField, Min(0.02f)] private float palmDwellRadiusMeters = 0.13f;
    [SerializeField] private bool judgeHudVisible;

    private readonly List<WayfinderPalmDwellTarget> activeTargets = new List<WayfinderPalmDwellTarget>();
    private readonly List<Vector3> activePositions = new List<Vector3>();
    private IWayfinderPalmPoseSource palms;
    private WayfinderPalmDwellTracker dwellTracker;
    private WayfinderCircularGestureTracker circularTracker;
    private WayfinderCircularGestureResult circleResult;
    private WayfinderVerticalSliceSession session;
    private float stoneAwakenedSeconds;
    private bool optionsOpen;
    private int activeCircleHand;
    private bool introComplete;
    private Vector3 introButtonBaseScale;
    private int lastStoryStone;

    public WayfinderVerticalSliceSession Session => session;
    public bool JudgeHudVisible => judgeHudVisible;

    private void Awake()
    {
        palms = palmPoseSource as IWayfinderPalmPoseSource;
        dwellTracker = new WayfinderPalmDwellTracker(palmDwellSeconds);
        circularTracker = new WayfinderCircularGestureTracker(circularGestureConfig);
        session = new WayfinderVerticalSliceSession(
            worldLabsWorldSlot,
            tripoRewardSlot,
            new WayfinderLocalJsonMemoryRepository(),
            memoryKeeperConfig);
        ApplyJudgeHud();
        UpdateProgressRing(0f);
        if (completionText != null) completionText.gameObject.SetActive(false);
        if (introStartButton != null) introButtonBaseScale = introStartButton.localScale;
        if (stoneStoryRoot != null) stoneStoryRoot.SetActive(false);
        if (peaceStateRoot != null) peaceStateRoot.SetActive(false);
        ApplyIntroState();
    }

    private void Update()
    {
        if (riverController == null || session == null) return;
        WayfinderHandMotionMetrics metrics = riverController.CurrentHandMetrics;
        if (!introComplete)
        {
            UpdateIntro();
            return;
        }
        circleResult = UpdateCircle(metrics, Time.unscaledDeltaTime);
        if (worldLabsWorldSlot != null && riverController.SuccessfulPushes >= 2)
            worldLabsWorldSlot.Prepare();
        bool finalOrbit = riverController.SuccessfulPushes >= riverController.RequiredPushes - 1;
        WayfinderMemoryDecision decision = session.Tick(
            metrics, circleResult.ValidFlow && finalOrbit, Time.unscaledDeltaTime);
        if (scriptedReflection != null &&
            scriptedReflection.TryConsumeCompletion(out WayfinderReflectionChoice reflectionChoice))
            session.ChooseReflection(reflectionChoice);
        UpdateFeedback(metrics, decision);
        UpdateDwellTargets();
        if (Input.GetKeyDown(KeyCode.J)) ToggleJudgeHud();
        if (Input.GetKeyDown(KeyCode.R)) ResetDemo();
    }

    public void ResetDemo()
    {
        session.Reset();
        riverController.DemoResetExperience();
        dwellTracker.Reset();
        circularTracker.Reset();
        circleResult = default;
        stoneAwakenedSeconds = 0f;
        optionsOpen = false;
        activeCircleHand = 0;
        judgeHudVisible = false;
        introComplete = false;
        lastStoryStone = 0;
        ApplyJudgeHud();
        ApplyIntroState();
        if (circularPalmGuide != null) circularPalmGuide.ResetGuide();
        if (analogSpeedometer != null) analogSpeedometer.ResetDisplay();
        if (completionText != null) completionText.gameObject.SetActive(false);
        if (stoneStoryRoot != null) stoneStoryRoot.SetActive(false);
        if (peaceStateRoot != null) peaceStateRoot.SetActive(false);
        if (scriptedReflection != null) scriptedReflection.ResetReflection();
        UpdateProgressRing(0f);
    }

    public static string NormalFeedback(
        WayfinderHandMotionMetrics metrics, WayfinderMemoryDecision decision)
    {
        if (!metrics.TrackingValid) return "READY";
        if (decision.Action == WayfinderMemoryAction.CueSlower ||
            metrics.CombinedSpeedMps > 0.55f) return "SLOW DOWN";
        if (metrics.CombinedSpeedMps > 0.35f) return "A LITTLE SLOWER";
        if (decision.ReasonCode == WayfinderMemoryReasonCode.MovementInRange) return "PERFECT FLOW";
        return "READY";
    }

    public static float MovementFocus01(WayfinderHandMotionMetrics metrics)
    {
        if (!metrics.TrackingValid) return 0f;
        float third = metrics.HasSymmetry ? metrics.Symmetry01 : metrics.PathEfficiency01;
        return Mathf.Clamp01((metrics.Smoothness01 + metrics.Continuity01 + third) / 3f);
    }

    private void UpdateFeedback(WayfinderHandMotionMetrics metrics, WayfinderMemoryDecision decision)
    {
        stoneAwakenedSeconds = Mathf.Max(0f, stoneAwakenedSeconds - Time.unscaledDeltaTime);
        string instruction;
        if (circleResult.Stage == WayfinderCircularTutorialStage.Relax)
            instruction = riverController.SuccessfulPushes >= riverController.RequiredPushes
                ? "PEACE STATE OPENING"
                : $"STONE {riverController.SuccessfulPushes}/{riverController.RequiredPushes} AWAKENED — BEGIN AGAIN";
        else if (circleResult.Stage == WayfinderCircularTutorialStage.PlaceYourCircle)
            instruction = "MOVE THE LIGHT WHERE YOUR CIRCLE FEELS COMFORTABLE";
        else if (circleResult.Stage == WayfinderCircularTutorialStage.CircleHere)
            instruction = "YOUR CIRCLE IS HERE";
        else
            instruction = "FOLLOW THE LIGHT — PAINT ONE SLOW CIRCLE";

        if (userFeedbackText != null)
        {
            userFeedbackText.text = instruction;
        }
        bool showStory = stoneAwakenedSeconds > 0f && lastStoryStone > 0;
        if (stoneStoryRoot != null) stoneStoryRoot.SetActive(showStory);
        if (stoneStoryText != null && showStory)
            stoneStoryText.text = StoneStory(lastStoryStone);

        bool crossingComplete = riverController.State == WayfinderRiverController.RiverState.Completion;
        bool showPeace = crossingComplete && !showStory &&
                         (scriptedReflection == null || !scriptedReflection.IsVisible);
        if (peaceStateRoot != null) peaceStateRoot.SetActive(showPeace);
        if (peaceStateText != null && showPeace)
        {
            peaceStateText.text = decision.State == WayfinderMemoryKeeperState.MemorySaved
                ? "PEACE STATE\n\nYOUR MEMORY IS SAVED\nYOUR REWARD IS READY"
                : decision.State == WayfinderMemoryKeeperState.Reflecting
                    ? "PEACE STATE REACHED\n\nTHE GATE IS OPEN\nCHOOSE WHAT YOU WILL CARRY FORWARD"
                    : "PEACE STATE REACHED\n\nTHE GATE IS OPEN\nYOUR MEMORY WORLD IS FORMING";
        }
        if (judgeHudText != null)
        {
            string symmetry = metrics.HasSymmetry ? metrics.Symmetry01.ToString("0.00") : "ONE-HAND";
            float remaining = Mathf.Max(0f,
                memoryKeeperConfig.SuccessfulMovementSeconds * (1f - session.Progress01));
            judgeHudText.text =
                $"TRACKING {(metrics.TrackingValid ? "VALID" : "LOST")}  " +
                $"L {(metrics.LeftTracked ? "ON" : "--")}  R {(metrics.RightTracked ? "ON" : "--")}\n" +
                $"SPEED {metrics.CombinedSpeedMps:0.000} m/s  TARGET " +
                $"{memoryKeeperConfig.DesiredSpeedMinMps:0.00}–{memoryKeeperConfig.DesiredSpeedMaxMps:0.00}\n" +
                $"SMOOTH {metrics.Smoothness01:0.00}  CONT {metrics.Continuity01:0.00}  " +
                $"SYMM {symmetry}\n" +
                $"FOCUS {MovementFocus01(metrics):0.00}  REMAIN {remaining:0.0}s\n" +
                $"CIRCLE {circleResult.AngularCoverageDegrees:0}°  R {circleResult.MeanRadiusMeters:0.00}m  " +
                $"CLOSE {circleResult.ClosureDistanceMeters:0.00}m\n" +
                $"CIRCLE FLOW {circleResult.ContinuousFlowSeconds:0.0}s  " +
                $"CONSIST {circleResult.RadiusConsistency01:0.00}  " +
                $"PLACE {circleResult.PlacementHold01:0.00}\n" +
                $"STATE {decision.State}\nACTION {decision.Action}\nREASON {decision.ReasonCode}";
        }
        UpdateProgressRing(session.Progress01);
        if (analogSpeedometer != null)
        {
            analogSpeedometer.SetSpeed(
                metrics.CombinedSpeedMps,
                memoryKeeperConfig.DesiredSpeedMinMps,
                memoryKeeperConfig.DesiredSpeedMaxMps,
                memoryKeeperConfig.RushedSpeedMps,
                Time.unscaledDeltaTime);
        }
        if (completionText != null)
        {
            bool saved = decision.State == WayfinderMemoryKeeperState.MemorySaved &&
                         scriptedReflection == null;
            completionText.gameObject.SetActive(saved);
            if (saved) completionText.text = "MEMORY SAVED\nYOUR REWARD IS READY";
        }
    }

    private void UpdateDwellTargets()
    {
        if (dwellTargets == null || dwellTracker == null) return;
        bool scriptedEndingOwnsInput = scriptedReflection != null &&
                                        worldLabsWorldSlot != null &&
                                        worldLabsWorldSlot.IsOpen;
        bool reflecting = session.State == WayfinderMemoryKeeperState.Reflecting &&
                          !scriptedEndingOwnsInput;
        activeTargets.Clear();
        activePositions.Clear();
        foreach (WayfinderPalmDwellTarget target in dwellTargets)
        {
            if (target == null) continue;
            bool completed = circleResult.Stage == WayfinderCircularTutorialStage.Relax ||
                             session.State == WayfinderMemoryKeeperState.MemorySaved;
            bool available = !scriptedEndingOwnsInput &&
                             IsTargetAvailable(target.Kind, reflecting, optionsOpen, completed);
            target.SetProgress(0f, available);
            if (available)
            {
                activeTargets.Add(target);
                activePositions.Add(target.Position);
            }
        }

        Pose leftPose = default;
        Pose rightPose = default;
        bool left = palms != null && palms.TryGetLeftPalm(out leftPose);
        bool right = palms != null && palms.TryGetRightPalm(out rightPose);
        int selected = dwellTracker.Update(
            left, leftPose.position, right, rightPose.position,
            activePositions, palmDwellRadiusMeters, Time.unscaledDeltaTime);
        if (dwellTracker.Candidate >= 0 && dwellTracker.Candidate < activeTargets.Count)
        {
            activeTargets[dwellTracker.Candidate].SetProgress(dwellTracker.Progress01, true);
        }
        if (selected >= 0 && selected < activeTargets.Count) Activate(activeTargets[selected].Kind);
    }

    private void Activate(WayfinderDwellTargetKind kind)
    {
        switch (kind)
        {
            case WayfinderDwellTargetKind.MemorySeed:
                session.ChooseReflection(WayfinderReflectionChoice.MemorySeed);
                break;
            case WayfinderDwellTargetKind.PatienceStone:
                session.ChooseReflection(WayfinderReflectionChoice.PatienceStone);
                break;
            case WayfinderDwellTargetKind.StillWaterLantern:
                session.ChooseReflection(WayfinderReflectionChoice.StillWaterLantern);
                break;
            case WayfinderDwellTargetKind.Options:
                optionsOpen = !optionsOpen;
                if (!optionsOpen)
                {
                    judgeHudVisible = false;
                    ApplyJudgeHud();
                }
                dwellTracker.Reset();
                break;
            case WayfinderDwellTargetKind.ToggleJudgeHud:
                ToggleJudgeHud();
                break;
            case WayfinderDwellTargetKind.DemoReset:
                ResetDemo();
                break;
        }
    }

    private void ToggleJudgeHud()
    {
        judgeHudVisible = !judgeHudVisible;
        ApplyJudgeHud();
    }

    private void ApplyJudgeHud()
    {
        if (judgeHudRoot != null) judgeHudRoot.SetActive(judgeHudVisible);
    }

    private void UpdateProgressRing(float progress01)
    {
        if (progressRing == null || progressRing.positionCount < 2) return;
        int count = progressRing.positionCount;
        float visible = Mathf.Clamp01(progress01);
        for (int index = 0; index < count; index++)
        {
            float normalized = index / (float)(count - 1);
            float angle = Mathf.PI * 2f * Mathf.Min(normalized, visible) - Mathf.PI * 0.5f;
            progressRing.SetPosition(index, new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.22f);
        }
    }

    private WayfinderCircularGestureResult UpdateCircle(
        WayfinderHandMotionMetrics metrics, float deltaTime)
    {
        Pose leftPose = default;
        Pose rightPose = default;
        bool left = palms != null && palms.TryGetLeftPalm(out leftPose) && metrics.LeftTracked;
        bool right = palms != null && palms.TryGetRightPalm(out rightPose) && metrics.RightTracked;
        if (activeCircleHand == 0)
        {
            if (right) activeCircleHand = 2;
            else if (left) activeCircleHand = 1;
        }
        bool selectedTracked = activeCircleHand == 2 ? right : activeCircleHand == 1 && left;
        bool tracked = metrics.TrackingValid && selectedTracked;
        Pose active = activeCircleHand == 2 ? rightPose : leftPose;
        Camera camera = Camera.main;
        Pose headPose = camera != null
            ? new Pose(camera.transform.position, camera.transform.rotation)
            : default;
        WayfinderCircularGestureResult result = circularTracker.Update(
            tracked, active, headPose,
            tracked ? metrics.Classification : WayfinderHandMotionClass.Untracked,
            deltaTime);

        if (result.RiverResistance) riverController.ShowCircularResistance();
        if (result.CompletedThisFrame)
        {
            riverController.RegisterCircularStone();
            lastStoryStone = riverController.SuccessfulPushes;
            stoneAwakenedSeconds = Mathf.Max(1f, stoneStorySeconds);
            if (riverController.SuccessfulPushes < riverController.RequiredPushes)
                circularTracker.BeginNextOrbit();
        }

        if (circularPalmGuide != null)
        {
            circularPalmGuide.Render(
                camera, result, circularGestureConfig.GuideRadiusMeters,
                circularGestureConfig.RibbonWidthMeters,
                camera != null);
        }
        return result;
    }

    private void UpdateIntro()
    {
        Pose leftPose = default;
        Pose rightPose = default;
        bool left = palms != null && palms.TryGetLeftPalm(out leftPose);
        bool right = palms != null && palms.TryGetRightPalm(out rightPose);
        Vector3 buttonPosition = introStartButton != null
            ? introStartButton.position : Vector3.positiveInfinity;
        bool touched = IsIntroButtonTouched(
            left, leftPose.position, right, rightPose.position,
            buttonPosition, introButtonTouchRadiusMeters);

        if (introStartButton != null)
        {
            float nearest = Mathf.Min(
                left ? Vector3.Distance(leftPose.position, buttonPosition) : float.PositiveInfinity,
                right ? Vector3.Distance(rightPose.position, buttonPosition) : float.PositiveInfinity);
            float hover = Mathf.Clamp01(1f - nearest /
                Mathf.Max(0.05f, introButtonTouchRadiusMeters * 2.2f));
            introStartButton.localScale = introButtonBaseScale * Mathf.Lerp(1f, 1.10f, hover);
        }
        if (!touched) return;

        introComplete = true;
        ApplyIntroState();
        circularTracker.Reset();
        activeCircleHand = 0;
    }

    private void ApplyIntroState()
    {
        if (introCardRoot != null) introCardRoot.SetActive(!introComplete);
        if (gameplayHudRoot != null) gameplayHudRoot.SetActive(introComplete);
        if (!introComplete && circularPalmGuide != null) circularPalmGuide.ResetGuide();
    }

    public static bool IsIntroButtonTouched(
        bool leftTracked, Vector3 leftPalm,
        bool rightTracked, Vector3 rightPalm,
        Vector3 buttonPosition, float radiusMeters)
    {
        float radius = Mathf.Max(0.05f, radiusMeters);
        return (leftTracked && Vector3.Distance(leftPalm, buttonPosition) <= radius) ||
               (rightTracked && Vector3.Distance(rightPalm, buttonPosition) <= radius);
    }

    public static string IntroCopy(float progress01, bool palmReady)
    {
        return "THE FLOW\n" +
               "BY PEACEMAKERS\n\n" +
               "TRAIN PATIENCE THROUGH THREE SLOW CIRCLES.\n" +
               "PLACE YOUR CIRCLE WHERE YOUR BODY FEELS COMFORTABLE.\n" +
               "EACH CALM ORBIT AWAKENS ONE STONE.\n\n" +
               "TRAIN  •  PRACTICE  •  ENTER PEACE\n" +
               "AFTER THREE STONES, A MEMORY WORLD OPENS AROUND YOU.\n\n" +
               "TOUCH OPEN THE GAME WITH YOUR HAND";
    }

    public static string StoneStory(int stoneNumber)
    {
        switch (stoneNumber)
        {
            case 1:
                return "STONE I  •  LISTEN\n\nTHE RIVER NOTICES\nWHEN YOU CHOOSE TO PAY ATTENTION.";
            case 2:
                return "STONE II  •  PATIENCE\n\nWHAT YOU DO NOT FORCE\nBEGINS TO RISE ON ITS OWN.";
            default:
                return "STONE III  •  PEACE\n\nYOU DID NOT CONQUER THE PATH.\nYOU BECAME QUIET ENOUGH TO SEE IT.";
        }
    }

    private static bool IsReflection(WayfinderDwellTargetKind kind)
    {
        return kind == WayfinderDwellTargetKind.MemorySeed ||
               kind == WayfinderDwellTargetKind.PatienceStone ||
               kind == WayfinderDwellTargetKind.StillWaterLantern;
    }

    public static bool IsTargetAvailable(
        WayfinderDwellTargetKind kind, bool reflecting, bool optionsOpen, bool completed)
    {
        if (IsReflection(kind)) return reflecting;
        if (kind == WayfinderDwellTargetKind.Options) return completed && !reflecting;
        if (kind == WayfinderDwellTargetKind.ToggleJudgeHud) return optionsOpen || completed;
        if (kind == WayfinderDwellTargetKind.DemoReset) return optionsOpen || completed;
        return false;
    }

    private static string FriendlyReason(WayfinderMemoryDecision decision)
    {
        switch (decision.ReasonCode)
        {
            case WayfinderMemoryReasonCode.TrackingValidityLow: return "RECENTER HANDS";
            case WayfinderMemoryReasonCode.MovementRushed: return "MOVE SLOWER";
            case WayfinderMemoryReasonCode.MovementUneven: return "SOFTEN + SMOOTH";
            default: return "TRY AGAIN";
        }
    }
}
