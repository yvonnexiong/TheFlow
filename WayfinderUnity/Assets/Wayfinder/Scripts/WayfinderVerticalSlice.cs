using System.Collections.Generic;
using UnityEngine;

public interface IWayfinderPalmPoseSource
{
    bool TryGetLeftPalm(out Pose worldPose);
    bool TryGetRightPalm(out Pose worldPose);
}

public enum WayfinderDwellTargetKind
{
    MemorySeed,
    PatienceStone,
    StillWaterLantern,
    Options,
    ToggleJudgeHud,
    DemoReset
}

public sealed class WayfinderPalmDwellTracker
{
    private readonly float dwellSeconds;
    private int candidate = -1;
    private float heldSeconds;
    private bool latched;

    public int Candidate => candidate;
    public float Progress01 => Mathf.Clamp01(heldSeconds / dwellSeconds);

    public WayfinderPalmDwellTracker(float dwellSeconds)
    {
        this.dwellSeconds = Mathf.Max(0.05f, dwellSeconds);
    }

    public int Update(
        bool leftTracked, Vector3 leftPalm,
        bool rightTracked, Vector3 rightPalm,
        IReadOnlyList<Vector3> targets, float radiusMeters, float deltaTime)
    {
        int nearest = FindNearest(leftTracked, leftPalm, rightTracked, rightPalm, targets,
            Mathf.Max(0.01f, radiusMeters));
        if (nearest < 0)
        {
            candidate = -1;
            heldSeconds = 0f;
            latched = false;
            return -1;
        }
        if (nearest != candidate)
        {
            candidate = nearest;
            heldSeconds = 0f;
            latched = false;
        }
        if (!latched && IsFinitePositive(deltaTime))
        {
            heldSeconds += deltaTime;
            if (heldSeconds >= dwellSeconds)
            {
                latched = true;
                return candidate;
            }
        }
        return -1;
    }

    public void Reset()
    {
        candidate = -1;
        heldSeconds = 0f;
        latched = false;
    }

    private static int FindNearest(
        bool leftTracked, Vector3 leftPalm, bool rightTracked, Vector3 rightPalm,
        IReadOnlyList<Vector3> targets, float radius)
    {
        int nearest = -1;
        float nearestSquared = radius * radius;
        for (int index = 0; index < targets.Count; index++)
        {
            if (leftTracked)
            {
                float squared = (leftPalm - targets[index]).sqrMagnitude;
                if (squared <= nearestSquared)
                {
                    nearestSquared = squared;
                    nearest = index;
                }
            }
            if (rightTracked)
            {
                float squared = (rightPalm - targets[index]).sqrMagnitude;
                if (squared <= nearestSquared)
                {
                    nearestSquared = squared;
                    nearest = index;
                }
            }
        }
        return nearest;
    }

    private static bool IsFinitePositive(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
    }
}

public sealed class WayfinderVerticalSliceSession
{
    private readonly WayfinderMemoryKeeper keeper;

    public WayfinderMemoryDecision LastDecision { get; private set; }
    public WayfinderMemoryKeeperState State => keeper.State;
    public float Progress01 => keeper.Progress01;
    public WayfinderMemoryRecord LastRecord => keeper.LastRecord;

    public WayfinderVerticalSliceSession(
        IWayfinderWorldSlot worldSlot,
        IWayfinderRewardSlot rewardSlot,
        IWayfinderMemoryRepository repository,
        WayfinderMemoryKeeperConfig config)
    {
        keeper = new WayfinderMemoryKeeper(worldSlot, rewardSlot, repository, config);
        LastDecision = new WayfinderMemoryDecision(
            WayfinderMemoryKeeperState.AwaitingHands,
            WayfinderMemoryAction.RequestRecenter,
            WayfinderMemoryReasonCode.TrackingValidityLow,
            0f);
    }

    public WayfinderMemoryDecision Tick(WayfinderHandMotionMetrics metrics, float deltaTime)
    {
        return Tick(metrics, metrics.IsIntendedDirection, deltaTime);
    }

    public WayfinderMemoryDecision Tick(
        WayfinderHandMotionMetrics metrics, bool gestureIntentValid, float deltaTime)
    {
        WayfinderHandObservation observation = WayfinderHandObservation.FromMetrics(metrics);
        observation.IsIntendedDirection = gestureIntentValid;
        LastDecision = keeper.Step(observation, deltaTime);
        return LastDecision;
    }

    public WayfinderMemoryDecision ChooseReflection(WayfinderReflectionChoice choice)
    {
        LastDecision = keeper.ChooseReflection(choice);
        return LastDecision;
    }

    public WayfinderMemoryDecision Reset()
    {
        LastDecision = keeper.DemoReset();
        return LastDecision;
    }
}
