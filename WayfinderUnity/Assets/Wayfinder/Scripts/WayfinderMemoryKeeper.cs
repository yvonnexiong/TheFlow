using System;
using System.IO;
using UnityEngine;

public enum WayfinderMemoryKeeperState
{
    AwaitingHands,
    Calibrating,
    Observing,
    Coaching,
    Unlocking,
    Reflecting,
    Rewarding,
    MemorySaved
}

public enum WayfinderMemoryAction
{
    None,
    RequestRecenter,
    BeginCalibration,
    BeginObservation,
    ResumeObservation,
    ContinueMovement,
    CueSlower,
    CueSoftenAndSmooth,
    OpenMemoryWorld,
    RequestReflection,
    PrepareReward,
    GrantReward,
    SaveMemory,
    DemoReset
}

public enum WayfinderMemoryReasonCode
{
    None,
    TrackingValidityLow,
    TrackingStable,
    CalibrationComplete,
    MovementInRange,
    MovementRushed,
    MovementUneven,
    MovementOutsideGoal,
    SustainedSuccess,
    WorldSlotOpened,
    ReflectionSelected,
    SuccessfulParticipant,
    AggregateRecordSaved,
    DemoResetRequested
}

public enum WayfinderReflectionChoice
{
    MemorySeed,
    PatienceStone,
    StillWaterLantern
}

public enum WayfinderRewardVariant
{
    Grounded,
    Steady,
    Luminous
}

[Serializable]
public sealed class WayfinderMemoryKeeperConfig
{
    [Min(0f)] public float CalibrationSeconds = 0.75f;
    [Range(0f, 1f)] public float MinimumTrackingValidity01 = 0.65f;
    [Min(0f)] public float DesiredSpeedMinMps = 0.10f;
    [Min(0f)] public float DesiredSpeedMaxMps = 0.35f;
    [Min(0f)] public float RushedSpeedMps = 0.55f;
    [Range(0f, 1f)] public float MinimumSmoothness01 = 0.70f;
    [Range(0f, 1f)] public float MinimumContinuity01 = 0.75f;
    [Min(0.01f)] public float SuccessfulMovementSeconds = 3f;
    [Min(0f)] public float RushedProgressReductionPerSecond = 0.08f;
    [Range(0f, 1f)] public float SteadyRewardScoreBoundary = 0.60f;
    [Range(0f, 1f)] public float LuminousRewardScoreBoundary = 0.82f;
}

public struct WayfinderMemoryDecision
{
    public WayfinderMemoryKeeperState State;
    public WayfinderMemoryAction Action;
    public WayfinderMemoryReasonCode ReasonCode;
    public float Progress01;

    public WayfinderMemoryDecision(
        WayfinderMemoryKeeperState state,
        WayfinderMemoryAction action,
        WayfinderMemoryReasonCode reasonCode,
        float progress01)
    {
        State = state;
        Action = action;
        ReasonCode = reasonCode;
        Progress01 = progress01;
    }
}

public interface IWayfinderWorldSlot
{
    bool IsOpen { get; }
    void OpenPlaceholder();
    void ResetPlaceholder();
}

public interface IWayfinderRewardSlot
{
    void GrantPlaceholder(string artifactId, WayfinderRewardVariant variant);
    void ResetPlaceholder();
}

public interface IWayfinderMemoryRepository
{
    void Save(WayfinderMemoryRecord record);
}

public sealed class WayfinderPlaceholderWorldSlot : IWayfinderWorldSlot
{
    public bool IsOpen { get; private set; }
    public int OpenCount { get; private set; }

    public void OpenPlaceholder()
    {
        IsOpen = true;
        OpenCount++;
    }

    public void ResetPlaceholder()
    {
        IsOpen = false;
        OpenCount = 0;
    }
}

public sealed class WayfinderPlaceholderRewardSlot : IWayfinderRewardSlot
{
    public int GrantCount { get; private set; }
    public string LastArtifactId { get; private set; }
    public WayfinderRewardVariant LastVariant { get; private set; }

    public void GrantPlaceholder(string artifactId, WayfinderRewardVariant variant)
    {
        LastArtifactId = artifactId;
        LastVariant = variant;
        GrantCount++;
    }

    public void ResetPlaceholder()
    {
        GrantCount = 0;
        LastArtifactId = null;
        LastVariant = WayfinderRewardVariant.Grounded;
    }
}

[Serializable]
public sealed class WayfinderMemoryRecord
{
    public int schemaVersion = 1;
    public string reflectionArtifact;
    public string rewardVariant;
    public float score01;
    public float averageSpeedMps;
    public float peakSpeedMps;
    public float smoothness01;
    public float continuity01;
    public float distanceMeters;
    public float durationSeconds;
    public float pathEfficiency01;
    public bool symmetryAvailable;
    public float symmetry01;
}

public static class WayfinderMemoryRecordJson
{
    public static string Serialize(WayfinderMemoryRecord record, bool prettyPrint = false)
    {
        if (record == null)
        {
            throw new ArgumentNullException(nameof(record));
        }
        return JsonUtility.ToJson(record, prettyPrint);
    }

    public static WayfinderMemoryRecord Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new ArgumentException("Memory JSON must not be empty.", nameof(json));
        }
        return JsonUtility.FromJson<WayfinderMemoryRecord>(json);
    }
}

/// <summary>
/// Bounded local repository: the latest aggregate record replaces the previous
/// record. It never stores samples, poses, trajectories, or external identifiers.
/// </summary>
public sealed class WayfinderLocalJsonMemoryRepository : IWayfinderMemoryRepository
{
    public const string DefaultFileName = "wayfinder-memory.json";
    public string FilePath { get; }

    public WayfinderLocalJsonMemoryRepository(string directoryPath = null, string fileName = DefaultFileName)
    {
        string directory = string.IsNullOrWhiteSpace(directoryPath)
            ? Application.persistentDataPath
            : directoryPath;
        if (string.IsNullOrWhiteSpace(fileName) || Path.GetFileName(fileName) != fileName)
        {
            throw new ArgumentException("A plain local file name is required.", nameof(fileName));
        }
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, fileName);
    }

    public void Save(WayfinderMemoryRecord record)
    {
        string temporaryPath = FilePath + ".tmp";
        File.WriteAllText(temporaryPath, WayfinderMemoryRecordJson.Serialize(record, true));
        File.Copy(temporaryPath, FilePath, true);
        File.Delete(temporaryPath);
    }
}

/// <summary>
/// Deterministic offline state machine. Its only live input is the aggregate-only
/// WayfinderHandObservation produced by the motion layer.
/// </summary>
public sealed class WayfinderMemoryKeeper
{
    private readonly WayfinderMemoryKeeperConfig config;
    private readonly IWayfinderWorldSlot worldSlot;
    private readonly IWayfinderRewardSlot rewardSlot;
    private readonly IWayfinderMemoryRepository repository;

    private float calibrationSeconds;
    private float progress01;
    private float successfulSeconds;
    private float weightedSpeed;
    private float weightedSmoothness;
    private float weightedContinuity;
    private float weightedPathEfficiency;
    private float peakSpeed;
    private float latestDistance;
    private float latestDuration;
    private float weightedSymmetry;
    private float symmetrySeconds;
    private string reflectionArtifact;
    private WayfinderRewardVariant rewardVariant;
    private bool rewardGranted;

    public WayfinderMemoryKeeperState State { get; private set; } = WayfinderMemoryKeeperState.AwaitingHands;
    public float Progress01 => progress01;
    public WayfinderMemoryRecord LastRecord { get; private set; }

    public WayfinderMemoryKeeper(
        IWayfinderWorldSlot worldSlot,
        IWayfinderRewardSlot rewardSlot,
        IWayfinderMemoryRepository repository,
        WayfinderMemoryKeeperConfig config = null)
    {
        this.worldSlot = worldSlot ?? throw new ArgumentNullException(nameof(worldSlot));
        this.rewardSlot = rewardSlot ?? throw new ArgumentNullException(nameof(rewardSlot));
        this.repository = repository ?? throw new ArgumentNullException(nameof(repository));
        this.config = config ?? new WayfinderMemoryKeeperConfig();
    }

    public WayfinderMemoryDecision Step(WayfinderHandObservation observation, float deltaTime)
    {
        float dt = FiniteNonNegative(deltaTime);
        observation = Sanitize(observation);

        switch (State)
        {
            case WayfinderMemoryKeeperState.AwaitingHands:
                if (HasLowTracking(observation))
                {
                    return Decision(WayfinderMemoryAction.RequestRecenter,
                        WayfinderMemoryReasonCode.TrackingValidityLow);
                }
                State = WayfinderMemoryKeeperState.Calibrating;
                calibrationSeconds = 0f;
                return Decision(WayfinderMemoryAction.BeginCalibration,
                    WayfinderMemoryReasonCode.TrackingStable);

            case WayfinderMemoryKeeperState.Calibrating:
                if (HasLowTracking(observation))
                {
                    calibrationSeconds = 0f;
                    State = WayfinderMemoryKeeperState.AwaitingHands;
                    return Decision(WayfinderMemoryAction.RequestRecenter,
                        WayfinderMemoryReasonCode.TrackingValidityLow);
                }
                calibrationSeconds += dt;
                if (calibrationSeconds >= Mathf.Max(0f, config.CalibrationSeconds))
                {
                    State = WayfinderMemoryKeeperState.Observing;
                    return Decision(WayfinderMemoryAction.BeginObservation,
                        WayfinderMemoryReasonCode.CalibrationComplete);
                }
                return Decision(WayfinderMemoryAction.None, WayfinderMemoryReasonCode.TrackingStable);

            case WayfinderMemoryKeeperState.Observing:
                return Observe(observation, dt);

            case WayfinderMemoryKeeperState.Coaching:
                if (HasLowTracking(observation))
                {
                    return Decision(WayfinderMemoryAction.RequestRecenter,
                        WayfinderMemoryReasonCode.TrackingValidityLow);
                }
                State = WayfinderMemoryKeeperState.Observing;
                return Decision(WayfinderMemoryAction.ResumeObservation,
                    WayfinderMemoryReasonCode.TrackingStable);

            case WayfinderMemoryKeeperState.Unlocking:
                State = WayfinderMemoryKeeperState.Reflecting;
                return Decision(WayfinderMemoryAction.RequestReflection,
                    WayfinderMemoryReasonCode.WorldSlotOpened);

            case WayfinderMemoryKeeperState.Reflecting:
                return Decision(WayfinderMemoryAction.RequestReflection,
                    WayfinderMemoryReasonCode.WorldSlotOpened);

            case WayfinderMemoryKeeperState.Rewarding:
                if (!rewardGranted)
                {
                    rewardVariant = GetRewardVariant(CalculateScore01(), config);
                    rewardSlot.GrantPlaceholder(reflectionArtifact, rewardVariant);
                    rewardGranted = true;
                    return Decision(WayfinderMemoryAction.GrantReward,
                        WayfinderMemoryReasonCode.SuccessfulParticipant);
                }
                LastRecord = CreateRecord();
                repository.Save(LastRecord);
                State = WayfinderMemoryKeeperState.MemorySaved;
                return Decision(WayfinderMemoryAction.SaveMemory,
                    WayfinderMemoryReasonCode.AggregateRecordSaved);

            default:
                return Decision(WayfinderMemoryAction.None, WayfinderMemoryReasonCode.None);
        }
    }

    public WayfinderMemoryDecision ChooseReflection(WayfinderReflectionChoice choice)
    {
        if (State != WayfinderMemoryKeeperState.Reflecting)
        {
            return Decision(WayfinderMemoryAction.None, WayfinderMemoryReasonCode.None);
        }

        reflectionArtifact = ReflectionArtifactFor(choice);
        State = WayfinderMemoryKeeperState.Rewarding;
        rewardGranted = false;
        return Decision(WayfinderMemoryAction.PrepareReward,
            WayfinderMemoryReasonCode.ReflectionSelected);
    }

    public WayfinderMemoryDecision DemoReset()
    {
        State = WayfinderMemoryKeeperState.AwaitingHands;
        calibrationSeconds = 0f;
        progress01 = 0f;
        successfulSeconds = 0f;
        weightedSpeed = 0f;
        weightedSmoothness = 0f;
        weightedContinuity = 0f;
        weightedPathEfficiency = 0f;
        peakSpeed = 0f;
        latestDistance = 0f;
        latestDuration = 0f;
        weightedSymmetry = 0f;
        symmetrySeconds = 0f;
        reflectionArtifact = null;
        rewardVariant = WayfinderRewardVariant.Grounded;
        rewardGranted = false;
        LastRecord = null;
        worldSlot.ResetPlaceholder();
        rewardSlot.ResetPlaceholder();
        return Decision(WayfinderMemoryAction.DemoReset,
            WayfinderMemoryReasonCode.DemoResetRequested);
    }

    public static string ReflectionArtifactFor(WayfinderReflectionChoice choice)
    {
        switch (choice)
        {
            case WayfinderReflectionChoice.MemorySeed: return "memory_seed";
            case WayfinderReflectionChoice.PatienceStone: return "patience_stone";
            case WayfinderReflectionChoice.StillWaterLantern: return "still_water_lantern";
            default: throw new ArgumentOutOfRangeException(nameof(choice), choice, "Unknown reflection choice.");
        }
    }

    public static WayfinderRewardVariant GetRewardVariant(
        float score01, WayfinderMemoryKeeperConfig config = null)
    {
        WayfinderMemoryKeeperConfig resolved = config ?? new WayfinderMemoryKeeperConfig();
        float score = Mathf.Clamp01(FiniteNonNegative(score01));
        if (score >= Mathf.Clamp01(resolved.LuminousRewardScoreBoundary))
        {
            return WayfinderRewardVariant.Luminous;
        }
        if (score >= Mathf.Clamp01(resolved.SteadyRewardScoreBoundary))
        {
            return WayfinderRewardVariant.Steady;
        }
        return WayfinderRewardVariant.Grounded;
    }

    private WayfinderMemoryDecision Observe(WayfinderHandObservation observation, float dt)
    {
        if (HasLowTracking(observation))
        {
            State = WayfinderMemoryKeeperState.Coaching;
            return Decision(WayfinderMemoryAction.RequestRecenter,
                WayfinderMemoryReasonCode.TrackingValidityLow);
        }

        if (observation.CombinedSpeedMps > Mathf.Max(0f, config.RushedSpeedMps))
        {
            progress01 = Mathf.Clamp01(progress01 -
                Mathf.Max(0f, config.RushedProgressReductionPerSecond) * dt);
            State = WayfinderMemoryKeeperState.Coaching;
            return Decision(WayfinderMemoryAction.CueSlower,
                WayfinderMemoryReasonCode.MovementRushed);
        }

        bool speedInRange = observation.CombinedSpeedMps >= Mathf.Max(0f, config.DesiredSpeedMinMps) &&
                            observation.CombinedSpeedMps <= Mathf.Max(
                                config.DesiredSpeedMinMps, config.DesiredSpeedMaxMps);
        if (speedInRange && observation.Smoothness01 < Mathf.Clamp01(config.MinimumSmoothness01))
        {
            State = WayfinderMemoryKeeperState.Coaching;
            return Decision(WayfinderMemoryAction.CueSoftenAndSmooth,
                WayfinderMemoryReasonCode.MovementUneven);
        }

        bool successful = speedInRange &&
                          observation.Smoothness01 >= Mathf.Clamp01(config.MinimumSmoothness01) &&
                          observation.Continuity01 >= Mathf.Clamp01(config.MinimumContinuity01) &&
                          observation.InInteractionVolume && observation.IsIntendedDirection;
        if (!successful || dt <= 0f)
        {
            return Decision(WayfinderMemoryAction.ContinueMovement,
                successful ? WayfinderMemoryReasonCode.MovementInRange :
                    WayfinderMemoryReasonCode.MovementOutsideGoal);
        }

        float requiredSeconds = Mathf.Max(0.01f, config.SuccessfulMovementSeconds);
        progress01 = Mathf.Clamp01(progress01 + dt / requiredSeconds);
        Accumulate(observation, dt);
        if (progress01 >= 1f)
        {
            worldSlot.OpenPlaceholder();
            State = WayfinderMemoryKeeperState.Unlocking;
            return Decision(WayfinderMemoryAction.OpenMemoryWorld,
                WayfinderMemoryReasonCode.SustainedSuccess);
        }
        return Decision(WayfinderMemoryAction.ContinueMovement,
            WayfinderMemoryReasonCode.MovementInRange);
    }

    private void Accumulate(WayfinderHandObservation observation, float dt)
    {
        successfulSeconds += dt;
        weightedSpeed += observation.CombinedSpeedMps * dt;
        weightedSmoothness += observation.Smoothness01 * dt;
        weightedContinuity += observation.Continuity01 * dt;
        weightedPathEfficiency += observation.PathEfficiency01 * dt;
        peakSpeed = Mathf.Max(peakSpeed, observation.PeakSpeedMps, observation.CombinedSpeedMps);
        latestDistance = Mathf.Max(latestDistance, observation.DistanceMeters);
        latestDuration = Mathf.Max(latestDuration, observation.DurationSeconds);
        if (observation.HasSymmetry)
        {
            weightedSymmetry += observation.Symmetry01 * dt;
            symmetrySeconds += dt;
        }
    }

    private float CalculateScore01()
    {
        if (successfulSeconds <= 0f)
        {
            return 0f;
        }
        float smoothness = weightedSmoothness / successfulSeconds;
        float continuity = weightedContinuity / successfulSeconds;
        float efficiency = weightedPathEfficiency / successfulSeconds;
        return Mathf.Clamp01((smoothness + continuity + efficiency) / 3f);
    }

    private WayfinderMemoryRecord CreateRecord()
    {
        float divisor = Mathf.Max(0.0001f, successfulSeconds);
        return new WayfinderMemoryRecord
        {
            reflectionArtifact = reflectionArtifact,
            rewardVariant = rewardVariant.ToString(),
            score01 = CalculateScore01(),
            averageSpeedMps = weightedSpeed / divisor,
            peakSpeedMps = peakSpeed,
            smoothness01 = weightedSmoothness / divisor,
            continuity01 = weightedContinuity / divisor,
            distanceMeters = latestDistance,
            durationSeconds = latestDuration,
            pathEfficiency01 = weightedPathEfficiency / divisor,
            symmetryAvailable = symmetrySeconds > 0f,
            symmetry01 = symmetrySeconds > 0f ? weightedSymmetry / symmetrySeconds : 0f
        };
    }

    private bool HasLowTracking(WayfinderHandObservation observation)
    {
        return !observation.TrackingValid ||
               observation.TrackingValidity01 < Mathf.Clamp01(config.MinimumTrackingValidity01);
    }

    private WayfinderMemoryDecision Decision(
        WayfinderMemoryAction action, WayfinderMemoryReasonCode reason)
    {
        return new WayfinderMemoryDecision(State, action, reason, progress01);
    }

    private static WayfinderHandObservation Sanitize(WayfinderHandObservation observation)
    {
        observation.TrackingValidity01 = Mathf.Clamp01(FiniteNonNegative(observation.TrackingValidity01));
        observation.CombinedSpeedMps = FiniteNonNegative(observation.CombinedSpeedMps);
        observation.PeakSpeedMps = FiniteNonNegative(observation.PeakSpeedMps);
        observation.Smoothness01 = Mathf.Clamp01(FiniteNonNegative(observation.Smoothness01));
        observation.Continuity01 = Mathf.Clamp01(FiniteNonNegative(observation.Continuity01));
        observation.DistanceMeters = FiniteNonNegative(observation.DistanceMeters);
        observation.DurationSeconds = FiniteNonNegative(observation.DurationSeconds);
        observation.PathEfficiency01 = Mathf.Clamp01(FiniteNonNegative(observation.PathEfficiency01));
        observation.Symmetry01 = Mathf.Clamp01(FiniteNonNegative(observation.Symmetry01));
        return observation;
    }

    private static float FiniteNonNegative(float value)
    {
        return float.IsNaN(value) || float.IsInfinity(value) ? 0f : Mathf.Max(0f, value);
    }
}
