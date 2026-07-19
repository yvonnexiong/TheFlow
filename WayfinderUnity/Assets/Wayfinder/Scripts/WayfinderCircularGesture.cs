using System;
using UnityEngine;

public enum WayfinderCircularTutorialStage
{
    PlaceYourCircle,
    CircleHere,
    FollowLight,
    Relax
}

[Serializable]
public sealed class WayfinderCircularGestureConfig
{
    [Header("User-positioned placement")]
    [Min(0.05f)] public float MinimumPlacementDistanceMeters = 0.35f;
    [Min(0.10f)] public float MaximumPlacementDistanceMeters = 0.80f;
    [Min(0.05f)] public float MinimumForwardDistanceMeters = 0.25f;
    [Min(0.10f)] public float MaximumHorizontalOffsetMeters = 0.65f;
    [Min(0.10f)] public float MaximumVerticalOffsetMeters = 0.55f;
    [Min(0.02f)] public float PalmAttachSteadySeconds = 0.18f;
    [Min(0.001f)] public float PalmAttachSpeedMetersPerSecond = 0.14f;
    [Min(0.10f)] public float PlacementHoldSeconds = 0.80f;
    [Min(0.001f)] public float PlacementStillSpeedMetersPerSecond = 0.12f;
    [Min(0.001f)] public float PlacementStillRadiusMeters = 0.045f;
    [Min(0.05f)] public float CircleGrowSeconds = 0.55f;

    [Header("Forgiving practice circle")]
    [Min(0.01f)] public float GuideRadiusMeters = 0.30f;
    [Min(0.01f)] public float RibbonWidthMeters = 0.05f;
    [Min(0.01f)] public float MinimumRadiusMeters = 0.18f;
    [Min(0.01f)] public float MaximumRadiusMeters = 0.42f;
    [Min(0.01f)] public float GuidePlaneDepthToleranceMeters = 0.24f;
    [Min(0.1f)] public float TargetDurationSeconds = 4.5f;
    [Range(90f, 720f)] public float MinimumAngularCoverageDegrees = 270f;
    [Range(0f, 1f)] public float MinimumDirectionConsistency01 = 0.50f;
    [Range(1f, 45f)] public float DirectionCommitDegrees = 8f;
    [Min(0.02f)] public float DirectionCommitSeconds = 0.12f;
    [Range(5f, 180f)] public float MaximumSampleAngleDegrees = 55f;
    [Min(0f)] public float InterruptionGraceSeconds = 0.60f;
    [Min(0f)] public float SustainedRushSeconds = 0.45f;
    [Min(0f)] public float RushedRecedePerSecond = 0.06f;
    [Min(0.05f)] public float RearmRestSeconds = 0.55f;
}

public struct WayfinderCircularGestureResult
{
    public WayfinderCircularTutorialStage Stage;
    public bool PearlAttached;
    public bool PlacementLocked;
    public bool AttemptActive;
    public bool ValidFlow;
    public bool LooseFragments;
    public bool Rushing;
    public bool PausedWithGrace;
    public bool ReturnToLight;
    public bool RiverResistance;
    public bool CompletedThisFrame;
    public bool CompletionLatched;
    public float PlacementHold01;
    public float CircleGrowth01;
    public float Progress01;
    public float AngularCoverageDegrees;
    public float SignedAngularDegrees;
    public float MeanRadiusMeters;
    public float RadiusConsistency01;
    public float ClosureDistanceMeters;
    public float ContinuousFlowSeconds;
    public float InterruptionSeconds;
    public float PacingAngleDegrees;
    public float TraceStartAngleDegrees;
    public int PacingDirection;
    public Pose CirclePose;
    public Vector3 PearlWorldPosition;
    public Vector3 PalmWorldPosition;
    public Vector3 PacingLightWorldPosition;
}

/// <summary>
/// SDK-neutral, world-space circle placement and tolerant orbit recognizer.
/// It consumes only a palm pose, head pose, and the existing physical speed class.
/// </summary>
public sealed class WayfinderCircularGestureTracker
{
    private readonly WayfinderCircularGestureConfig config;
    private WayfinderCircularTutorialStage stage;
    private Pose circlePose;
    private Vector3 pearlWorld;
    private Vector3 previousPalm;
    private Vector3 holdAnchor;
    private bool hasPreviousPalm;
    private float attachSteadySeconds;
    private float placementHoldSeconds;
    private float circleGrowSeconds;
    private float previousAngleDegrees;
    private float traceStartAngleDegrees;
    private float pacingAngleDegrees;
    private float pendingSignedDegrees;
    private float pendingDirectionSeconds;
    private int pendingDirection;
    private int pacingDirection;
    private float angularCoverage;
    private float signedAngular;
    private float tracingSeconds;
    private float interruptionSeconds;
    private float rushedSeconds;
    private float penalty01;
    private float radiusSum;
    private float radiusSquaredSum;
    private int radiusSamples;
    private Vector3 firstTracePalm;
    private Vector3 lastTracePalm;
    private bool hasTracePalm;

    public WayfinderCircularGestureTracker(WayfinderCircularGestureConfig config = null)
    {
        this.config = config ?? new WayfinderCircularGestureConfig();
        Reset();
    }

    public WayfinderCircularGestureResult Update(
        bool trackingValid, Pose palmWorldPose, Pose headWorldPose,
        WayfinderHandMotionClass speedClassification, float deltaTime)
    {
        float dt = FinitePositive(deltaTime);
        bool finite = IsFinite(palmWorldPose.position) && IsFinite(headWorldPose.position);
        Vector3 palm = palmWorldPose.position;
        float palmSpeed = hasPreviousPalm && dt > 0f
            ? Vector3.Distance(previousPalm, palm) / dt
            : 0f;

        if (stage == WayfinderCircularTutorialStage.Relax)
        {
            WayfinderCircularGestureResult complete = BuildResult(palm, false, false, false, false);
            complete.Progress01 = 1f;
            return complete;
        }

        if (stage == WayfinderCircularTutorialStage.PlaceYourCircle)
        {
            if (trackingValid && finite)
            {
                bool steadyEnough = !hasPreviousPalm || palmSpeed <=
                    Mathf.Max(0.001f, config.PalmAttachSpeedMetersPerSecond);
                attachSteadySeconds = steadyEnough ? attachSteadySeconds + dt : 0f;
                if (attachSteadySeconds >= Mathf.Max(0.02f, config.PalmAttachSteadySeconds))
                {
                    pearlWorld = ClampPlacement(palm, headWorldPose);
                    if (placementHoldSeconds <= 0f) holdAnchor = pearlWorld;
                    float drift = Vector3.Distance(pearlWorld, holdAnchor);
                    bool holding = palmSpeed <= Mathf.Max(0.001f,
                        config.PlacementStillSpeedMetersPerSecond) &&
                        drift <= Mathf.Max(0.001f, config.PlacementStillRadiusMeters);
                    if (holding)
                    {
                        placementHoldSeconds += dt;
                    }
                    else
                    {
                        placementHoldSeconds = 0f;
                        holdAnchor = pearlWorld;
                    }

                    if (placementHoldSeconds >= Mathf.Max(0.10f, config.PlacementHoldSeconds))
                    {
                        circlePose = CreateCirclePose(pearlWorld, headWorldPose);
                        stage = WayfinderCircularTutorialStage.CircleHere;
                        circleGrowSeconds = 0f;
                    }
                }
                previousPalm = palm;
                hasPreviousPalm = true;
            }
            else
            {
                attachSteadySeconds = 0f;
                placementHoldSeconds = 0f;
                hasPreviousPalm = false;
            }
            return BuildResult(palm, false, false, false, false);
        }

        circleGrowSeconds += dt;
        if (stage == WayfinderCircularTutorialStage.CircleHere)
        {
            if (trackingValid && finite && TryCircleCoordinates(palm, out Vector3 local, out float radius))
            {
                if (InTracingBand(local.z, radius))
                {
                    stage = WayfinderCircularTutorialStage.FollowLight;
                    previousAngleDegrees = AngleDegrees(local);
                    traceStartAngleDegrees = previousAngleDegrees;
                    pacingAngleDegrees = previousAngleDegrees;
                    firstTracePalm = palm;
                    lastTracePalm = palm;
                    hasTracePalm = true;
                    AddRadius(radius);
                }
            }
            previousPalm = palm;
            hasPreviousPalm = trackingValid && finite;
            return BuildResult(palm, false, false, false, false);
        }

        bool validFlow = false;
        bool looseFragments = false;
        bool rushing = false;
        bool geometricallyValid = false;
        if (trackingValid && finite && TryCircleCoordinates(palm, out Vector3 traceLocal, out float traceRadius))
        {
            geometricallyValid = InTracingBand(traceLocal.z, traceRadius);
            if (geometricallyValid)
            {
                float angle = AngleDegrees(traceLocal);
                float delta = Mathf.DeltaAngle(previousAngleDegrees, angle);
                if (Mathf.Abs(delta) <= Mathf.Max(5f, config.MaximumSampleAngleDegrees))
                {
                    if (pacingDirection == 0)
                    {
                        int direction = Mathf.Abs(delta) >= 0.25f ? (delta >= 0f ? 1 : -1) : 0;
                        if (direction != 0 && direction != pendingDirection)
                        {
                            pendingDirection = direction;
                            pendingSignedDegrees = delta;
                            pendingDirectionSeconds = dt;
                        }
                        else if (direction != 0)
                        {
                            pendingSignedDegrees += delta;
                            pendingDirectionSeconds += dt;
                        }
                        if (Mathf.Abs(pendingSignedDegrees) >= Mathf.Max(1f, config.DirectionCommitDegrees) &&
                            pendingDirectionSeconds >= Mathf.Max(0.02f, config.DirectionCommitSeconds))
                        {
                            pacingDirection = pendingSignedDegrees >= 0f ? 1 : -1;
                            angularCoverage += Mathf.Abs(pendingSignedDegrees);
                            signedAngular += pendingSignedDegrees;
                            pendingSignedDegrees = 0f;
                        }
                    }
                    else if (Mathf.Sign(delta) == pacingDirection || Mathf.Abs(delta) < 1.5f)
                    {
                        angularCoverage += Mathf.Abs(delta);
                        signedAngular += delta;
                    }
                }

                previousAngleDegrees = angle;
                lastTracePalm = palm;
                hasTracePalm = true;
                AddRadius(traceRadius);
                tracingSeconds += dt;
                validFlow = pacingDirection != 0 &&
                    speedClassification == WayfinderHandMotionClass.Desired;
                looseFragments = speedClassification == WayfinderHandMotionClass.TooFast;
                rushing = speedClassification == WayfinderHandMotionClass.Rushed;
                if (validFlow || looseFragments)
                {
                    interruptionSeconds = 0f;
                }
            }
        }

        if (pacingDirection != 0 && geometricallyValid && !rushing)
        {
            pacingAngleDegrees += pacingDirection * 360f /
                Mathf.Max(0.1f, config.TargetDurationSeconds) * dt;
        }

        if (!validFlow)
        {
            interruptionSeconds += dt;
            if (rushing)
            {
                rushedSeconds += dt;
                if (rushedSeconds > Mathf.Max(0f, config.SustainedRushSeconds))
                    penalty01 += Mathf.Max(0f, config.RushedRecedePerSecond) * dt;
            }
            else rushedSeconds = 0f;
        }
        else rushedSeconds = 0f;

        previousPalm = palm;
        hasPreviousPalm = trackingValid && finite;
        WayfinderCircularGestureResult result = BuildResult(
            palm, validFlow, looseFragments, rushing,
            !validFlow && interruptionSeconds <= Mathf.Max(0f, config.InterruptionGraceSeconds));
        if (MeetsCompletion(result))
        {
            stage = WayfinderCircularTutorialStage.Relax;
            result.Stage = stage;
            result.CompletedThisFrame = true;
            result.CompletionLatched = true;
            result.Progress01 = 1f;
        }
        return result;
    }

    public void Reset()
    {
        stage = WayfinderCircularTutorialStage.PlaceYourCircle;
        circlePose = default;
        pearlWorld = Vector3.zero;
        previousPalm = Vector3.zero;
        holdAnchor = Vector3.zero;
        hasPreviousPalm = false;
        attachSteadySeconds = placementHoldSeconds = circleGrowSeconds = 0f;
        previousAngleDegrees = traceStartAngleDegrees = pacingAngleDegrees = pendingSignedDegrees = 0f;
        pendingDirectionSeconds = 0f;
        pendingDirection = 0;
        pacingDirection = 0;
        angularCoverage = signedAngular = tracingSeconds = interruptionSeconds = 0f;
        rushedSeconds = penalty01 = 0f;
        radiusSum = radiusSquaredSum = 0f;
        radiusSamples = 0;
        firstTracePalm = lastTracePalm = Vector3.zero;
        hasTracePalm = false;
    }

    /// <summary>
    /// Starts another orbit on the already placed circle. This intentionally keeps the
    /// player's chosen world-space pose so completing a stone never recenters the pearl.
    /// </summary>
    public void BeginNextOrbit()
    {
        if (stage != WayfinderCircularTutorialStage.Relax) return;
        stage = WayfinderCircularTutorialStage.CircleHere;
        ResetOrbitProgress();
        circleGrowSeconds = Mathf.Max(0.05f, config.CircleGrowSeconds);
    }

    private void ResetOrbitProgress()
    {
        previousAngleDegrees = traceStartAngleDegrees = pacingAngleDegrees = pendingSignedDegrees = 0f;
        pendingDirectionSeconds = 0f;
        pendingDirection = 0;
        pacingDirection = 0;
        angularCoverage = signedAngular = tracingSeconds = interruptionSeconds = 0f;
        rushedSeconds = penalty01 = 0f;
        radiusSum = radiusSquaredSum = 0f;
        radiusSamples = 0;
        firstTracePalm = lastTracePalm = Vector3.zero;
        hasTracePalm = false;
    }

    public Vector3 ClampPlacement(Vector3 palmWorld, Pose headWorldPose)
    {
        Vector3 local = Quaternion.Inverse(headWorldPose.rotation) * (palmWorld - headWorldPose.position);
        local.x = Mathf.Clamp(local.x, -Mathf.Max(0.1f, config.MaximumHorizontalOffsetMeters),
            Mathf.Max(0.1f, config.MaximumHorizontalOffsetMeters));
        local.y = Mathf.Clamp(local.y, -Mathf.Max(0.1f, config.MaximumVerticalOffsetMeters),
            Mathf.Max(0.1f, config.MaximumVerticalOffsetMeters));
        local.z = Mathf.Max(Mathf.Max(0.05f, config.MinimumForwardDistanceMeters), local.z);
        float distance = local.magnitude;
        float minimum = Mathf.Max(0.05f, config.MinimumPlacementDistanceMeters);
        float maximum = Mathf.Max(minimum, config.MaximumPlacementDistanceMeters);
        if (distance < minimum) local = local.normalized * minimum;
        if (local.sqrMagnitude < 0.000001f) local = Vector3.forward * minimum;
        if (distance > maximum) local = local.normalized * maximum;
        return headWorldPose.position + headWorldPose.rotation * local;
    }

    public static Pose CreateCirclePose(Vector3 centerWorld, Pose headWorldPose)
    {
        Vector3 towardHead = headWorldPose.position - centerWorld;
        if (towardHead.sqrMagnitude < 0.000001f)
            towardHead = -(headWorldPose.rotation * Vector3.forward);
        Vector3 up = Vector3.ProjectOnPlane(
            headWorldPose.rotation * Vector3.up, towardHead).normalized;
        if (up.sqrMagnitude < 0.000001f) up = Vector3.up;
        return new Pose(centerWorld, Quaternion.LookRotation(towardHead.normalized, up));
    }

    private bool TryCircleCoordinates(Vector3 palmWorld, out Vector3 local, out float radius)
    {
        local = Quaternion.Inverse(circlePose.rotation) * (palmWorld - circlePose.position);
        radius = new Vector2(local.x, local.y).magnitude;
        return IsFinite(local);
    }

    private bool InTracingBand(float localDepth, float radius)
    {
        return Mathf.Abs(localDepth) <= Mathf.Max(0.01f, config.GuidePlaneDepthToleranceMeters) &&
               radius >= Mathf.Max(0.01f, config.MinimumRadiusMeters) &&
               radius <= Mathf.Max(config.MinimumRadiusMeters, config.MaximumRadiusMeters);
    }

    private WayfinderCircularGestureResult BuildResult(
        Vector3 palm, bool validFlow, bool looseFragments, bool rushing, bool grace)
    {
        float mean = radiusSamples > 0 ? radiusSum / radiusSamples : 0f;
        float variance = radiusSamples > 0
            ? Mathf.Max(0f, radiusSquaredSum / radiusSamples - mean * mean) : 0f;
        float variation = mean > 0.0001f ? Mathf.Sqrt(variance) / mean : 1f;
        Vector3 pace = circlePose.position;
        if (stage >= WayfinderCircularTutorialStage.CircleHere)
        {
            float radians = pacingAngleDegrees * Mathf.Deg2Rad;
            pace += circlePose.rotation * new Vector3(
                Mathf.Cos(radians) * config.GuideRadiusMeters,
                Mathf.Sin(radians) * config.GuideRadiusMeters, 0f);
        }
        return new WayfinderCircularGestureResult
        {
            Stage = stage,
            PearlAttached = attachSteadySeconds >= Mathf.Max(0.02f, config.PalmAttachSteadySeconds),
            PlacementLocked = stage != WayfinderCircularTutorialStage.PlaceYourCircle,
            AttemptActive = stage == WayfinderCircularTutorialStage.FollowLight,
            ValidFlow = validFlow,
            LooseFragments = looseFragments,
            Rushing = rushing,
            PausedWithGrace = grace,
            ReturnToLight = stage == WayfinderCircularTutorialStage.FollowLight && !validFlow,
            RiverResistance = rushing && rushedSeconds > Mathf.Max(0f, config.SustainedRushSeconds),
            CompletionLatched = stage == WayfinderCircularTutorialStage.Relax,
            PlacementHold01 = Mathf.Clamp01(placementHoldSeconds /
                Mathf.Max(0.1f, config.PlacementHoldSeconds)),
            CircleGrowth01 = Mathf.Clamp01(circleGrowSeconds /
                Mathf.Max(0.05f, config.CircleGrowSeconds)),
            Progress01 = Mathf.Clamp01(angularCoverage /
                Mathf.Max(1f, config.MinimumAngularCoverageDegrees) - penalty01),
            AngularCoverageDegrees = angularCoverage,
            SignedAngularDegrees = signedAngular,
            MeanRadiusMeters = mean,
            RadiusConsistency01 = Mathf.Clamp01(1f - variation),
            ClosureDistanceMeters = hasTracePalm ? Vector3.Distance(lastTracePalm, firstTracePalm) : 0f,
            ContinuousFlowSeconds = tracingSeconds,
            InterruptionSeconds = interruptionSeconds,
            PacingAngleDegrees = pacingAngleDegrees,
            TraceStartAngleDegrees = traceStartAngleDegrees,
            PacingDirection = pacingDirection,
            CirclePose = circlePose,
            PearlWorldPosition = stage == WayfinderCircularTutorialStage.PlaceYourCircle
                ? pearlWorld : circlePose.position,
            PalmWorldPosition = palm,
            PacingLightWorldPosition = pace
        };
    }

    private bool MeetsCompletion(WayfinderCircularGestureResult result)
    {
        float consistency = angularCoverage > 0.001f
            ? Mathf.Abs(signedAngular) / angularCoverage : 0f;
        return pacingDirection != 0 &&
               result.Progress01 >= 1f &&
               tracingSeconds >= Mathf.Max(0.5f, config.TargetDurationSeconds * 0.60f) &&
               consistency >= Mathf.Clamp01(config.MinimumDirectionConsistency01);
    }

    private void AddRadius(float radius)
    {
        radiusSum += radius;
        radiusSquaredSum += radius * radius;
        radiusSamples++;
    }

    private static float AngleDegrees(Vector3 local)
    {
        return Mathf.Atan2(local.y, local.x) * Mathf.Rad2Deg;
    }

    private static float FinitePositive(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) || value <= 0f ? 0f : value;
    }

    private static bool IsFinite(Vector3 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
               !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
               !float.IsNaN(value.z) && !float.IsInfinity(value.z);
    }
}
