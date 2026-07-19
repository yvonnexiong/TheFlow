using System;
using System.Collections.Generic;
using UnityEngine;

public enum WayfinderHandMotionClass
{
    Untracked,
    Still,
    TooSlow,
    Desired,
    TooFast,
    Rushed
}

[Serializable]
public sealed class WayfinderHandMotionConfig
{
    [Tooltip("Allow either tracked hand to keep the measurement valid.")]
    public bool AllowOneHand = true;
    [Tooltip("Exponential velocity smoothing time constant in seconds.")]
    public float VelocitySmoothingSeconds = 0.055f;
    [Tooltip("Speeds below this value are treated as tracking noise, in m/s.")]
    public float StationaryNoiseFloorMps = 0.025f;
    [Tooltip("Reject implausible sample-to-sample velocities above this value, in m/s.")]
    public float MaximumAcceptedSpeedMps = 3f;
    [Tooltip("Lower edge of the initial desired-speed window, in m/s.")]
    public float DesiredSpeedMinMps = 0.10f;
    [Tooltip("Upper edge of the initial desired-speed window, in m/s.")]
    public float DesiredSpeedMaxMps = 0.35f;
    [Tooltip("Initial rushed-motion boundary, in m/s.")]
    public float RushedSpeedMps = 0.55f;
    [Tooltip("Minimum positive head-relative directional speed, in m/s.")]
    public float IntendedDirectionMinMps = 0.04f;
    [Tooltip("Head-local direction used to recognize an intended push.")]
    public Vector3 IntendedHeadLocalDirection = Vector3.right;
    [Tooltip("Minimum head-local interaction-volume corner, in meters.")]
    public Vector3 InteractionVolumeMin = new Vector3(-0.8f, -0.65f, 0.15f);
    [Tooltip("Maximum head-local interaction-volume corner, in meters.")]
    public Vector3 InteractionVolumeMax = new Vector3(0.8f, 0.45f, 1.05f);
    [Tooltip("Acceleration scale used to normalize the optional smoothness score.")]
    public float SmoothnessAccelerationScale = 2f;
}

public struct WayfinderTrackedHandPose
{
    public bool IsTracked;
    public Pose TrackingOriginPose;

    public WayfinderTrackedHandPose(bool isTracked, Pose trackingOriginPose)
    {
        IsTracked = isTracked;
        TrackingOriginPose = trackingOriginPose;
    }
}

public struct WayfinderHandProviderFrame
{
    public WayfinderTrackedHandPose Left;
    public WayfinderTrackedHandPose Right;
    public bool IsHeadTracked;
    public Pose HeadTrackingOriginPose;
}

public interface IWayfinderHandProvider
{
    WayfinderHandProviderFrame Sample();
}

public struct WayfinderHandMotionMetrics
{
    public bool TrackingValid;
    public bool LeftTracked;
    public bool RightTracked;
    public float LeftSpeedMps;
    public float RightSpeedMps;
    public float CombinedSpeedMps;
    public float PeakSpeedMps;
    public float Smoothness01;
    public float Continuity01;
    public float DistanceMeters;
    public float DurationSeconds;
    public float PathEfficiency01;
    public bool HasSymmetry;
    public float Symmetry01;
    public bool InInteractionVolume;
    public bool IsIntendedDirection;
    public float IntendedDirectionDeltaMeters;
    public Vector3 HeadRelativeCentroid;
    public Vector3 HeadRelativeDelta;
    public WayfinderHandMotionClass Classification;
}

/// <summary>
/// Aggregate-only input for offline experience logic. This intentionally omits
/// joint poses, trajectories, meshes, room data, and participant identifiers.
/// </summary>
public struct WayfinderHandObservation
{
    public bool TrackingValid;
    public float TrackingValidity01;
    public float CombinedSpeedMps;
    public float PeakSpeedMps;
    public float Smoothness01;
    public float Continuity01;
    public float DistanceMeters;
    public float DurationSeconds;
    public float PathEfficiency01;
    public bool HasSymmetry;
    public float Symmetry01;
    public bool InInteractionVolume;
    public bool IsIntendedDirection;
    public WayfinderHandMotionClass Classification;

    public static WayfinderHandObservation FromMetrics(WayfinderHandMotionMetrics metrics)
    {
        return new WayfinderHandObservation
        {
            TrackingValid = metrics.TrackingValid,
            TrackingValidity01 = metrics.TrackingValid ? 1f : 0f,
            CombinedSpeedMps = metrics.CombinedSpeedMps,
            PeakSpeedMps = metrics.PeakSpeedMps,
            Smoothness01 = metrics.Smoothness01,
            Continuity01 = metrics.Continuity01,
            DistanceMeters = metrics.DistanceMeters,
            DurationSeconds = metrics.DurationSeconds,
            PathEfficiency01 = metrics.PathEfficiency01,
            HasSymmetry = metrics.HasSymmetry,
            Symmetry01 = metrics.Symmetry01,
            InInteractionVolume = metrics.InInteractionVolume,
            IsIntendedDirection = metrics.IsIntendedDirection,
            Classification = metrics.Classification
        };
    }
}

/// <summary>
/// SDK-neutral physical motion estimator. Positions and distances remain in meters;
/// callers supply elapsed time in seconds.
/// </summary>
public sealed class WayfinderHandMotionEstimator
{
    private sealed class HandState
    {
        public bool HasPrevious;
        public bool HasVelocity;
        public Vector3 PreviousOrigin;
        public Vector3 PreviousHeadLocal;
        public Vector3 FilteredVelocity;
        public Vector3 SegmentStart;
        public float PathMeters;
        public float CompletedDirectMeters;
        public float AccelerationEnergy;
        public float AccelerationSeconds;
    }

    private readonly WayfinderHandMotionConfig config;
    private readonly HandState left = new HandState();
    private readonly HandState right = new HandState();
    private bool measurementStarted;
    private float elapsedSeconds;
    private float validSeconds;
    private float distanceMeters;
    private float peakSpeedMps;

    public WayfinderHandMotionEstimator(WayfinderHandMotionConfig config = null)
    {
        this.config = config ?? new WayfinderHandMotionConfig();
    }

    public void Reset()
    {
        ResetHand(left);
        ResetHand(right);
        measurementStarted = false;
        elapsedSeconds = 0f;
        validSeconds = 0f;
        distanceMeters = 0f;
        peakSpeedMps = 0f;
    }

    public WayfinderHandMotionMetrics Update(WayfinderHandProviderFrame frame, float deltaTime)
    {
        bool validDeltaTime = IsFinite(deltaTime) && deltaTime > 0f;
        float dt = validDeltaTime ? deltaTime : 0f;
        bool headValid = frame.IsHeadTracked && IsFinite(frame.HeadTrackingOriginPose.position) &&
                         IsFinite(frame.HeadTrackingOriginPose.rotation);

        HandResult leftResult = ProcessHand(left, frame.Left, frame.HeadTrackingOriginPose, headValid, dt);
        HandResult rightResult = ProcessHand(right, frame.Right, frame.HeadTrackingOriginPose, headValid, dt);

        bool anyTracked = leftResult.Tracked || rightResult.Tracked;
        bool trackingValid = config.AllowOneHand
            ? anyTracked
            : leftResult.Tracked && rightResult.Tracked;

        if (anyTracked)
        {
            measurementStarted = true;
        }
        if (measurementStarted && validDeltaTime)
        {
            elapsedSeconds += dt;
            if (trackingValid)
            {
                validSeconds += dt;
            }
        }

        int speedCount = 0;
        float squaredSpeed = 0f;
        if (leftResult.Tracked)
        {
            squaredSpeed += leftResult.SpeedMps * leftResult.SpeedMps;
            speedCount++;
        }
        if (rightResult.Tracked)
        {
            squaredSpeed += rightResult.SpeedMps * rightResult.SpeedMps;
            speedCount++;
        }
        float combinedSpeed = speedCount > 0 ? Mathf.Sqrt(squaredSpeed / speedCount) : 0f;
        combinedSpeed = FiniteOrZero(combinedSpeed);

        if (trackingValid && validDeltaTime)
        {
            distanceMeters += combinedSpeed * dt;
            peakSpeedMps = Mathf.Max(peakSpeedMps, combinedSpeed);
        }

        float directionDeltaSquared = 0f;
        int directionCount = 0;
        if (leftResult.Tracked && leftResult.DirectionDeltaMeters > 0f)
        {
            directionDeltaSquared += leftResult.DirectionDeltaMeters * leftResult.DirectionDeltaMeters;
            directionCount++;
        }
        if (rightResult.Tracked && rightResult.DirectionDeltaMeters > 0f)
        {
            directionDeltaSquared += rightResult.DirectionDeltaMeters * rightResult.DirectionDeltaMeters;
            directionCount++;
        }
        float intendedDelta = directionCount > 0 ? Mathf.Sqrt(directionDeltaSquared / directionCount) : 0f;
        bool intended = validDeltaTime && intendedDelta / dt >= SafePositive(config.IntendedDirectionMinMps, 0.04f);

        bool inVolume = config.AllowOneHand
            ? (leftResult.Tracked && leftResult.InVolume) || (rightResult.Tracked && rightResult.InVolume)
            : leftResult.Tracked && rightResult.Tracked && leftResult.InVolume && rightResult.InVolume;

        Vector3 centroid = Vector3.zero;
        Vector3 centroidDelta = Vector3.zero;
        int centroidCount = 0;
        if (leftResult.Tracked)
        {
            centroid += leftResult.HeadLocalPosition;
            centroidDelta += leftResult.HeadLocalDelta;
            centroidCount++;
        }
        if (rightResult.Tracked)
        {
            centroid += rightResult.HeadLocalPosition;
            centroidDelta += rightResult.HeadLocalDelta;
            centroidCount++;
        }
        if (centroidCount > 0)
        {
            centroid /= centroidCount;
            centroidDelta /= centroidCount;
        }

        bool symmetryAvailable = leftResult.Tracked && rightResult.Tracked &&
                                 Mathf.Max(leftResult.SpeedMps, rightResult.SpeedMps) >
                                 SafeNonNegative(config.StationaryNoiseFloorMps);
        float symmetry = 0f;
        if (symmetryAvailable)
        {
            symmetry = 1f - Mathf.Abs(leftResult.SpeedMps - rightResult.SpeedMps) /
                       Mathf.Max(0.0001f, Mathf.Max(leftResult.SpeedMps, rightResult.SpeedMps));
        }

        return new WayfinderHandMotionMetrics
        {
            TrackingValid = trackingValid,
            LeftTracked = leftResult.Tracked,
            RightTracked = rightResult.Tracked,
            LeftSpeedMps = FiniteOrZero(leftResult.SpeedMps),
            RightSpeedMps = FiniteOrZero(rightResult.SpeedMps),
            CombinedSpeedMps = combinedSpeed,
            PeakSpeedMps = FiniteOrZero(peakSpeedMps),
            Smoothness01 = CalculateSmoothness(),
            Continuity01 = elapsedSeconds > 0f ? Mathf.Clamp01(validSeconds / elapsedSeconds) : 0f,
            DistanceMeters = FiniteOrZero(distanceMeters),
            DurationSeconds = FiniteOrZero(validSeconds),
            PathEfficiency01 = CalculatePathEfficiency(),
            HasSymmetry = symmetryAvailable,
            Symmetry01 = Mathf.Clamp01(FiniteOrZero(symmetry)),
            InInteractionVolume = trackingValid && headValid && inVolume,
            IsIntendedDirection = trackingValid && headValid && intended,
            IntendedDirectionDeltaMeters = FiniteOrZero(intendedDelta),
            HeadRelativeCentroid = IsFinite(centroid) ? centroid : Vector3.zero,
            HeadRelativeDelta = IsFinite(centroidDelta) ? centroidDelta : Vector3.zero,
            Classification = Classify(trackingValid, combinedSpeed)
        };
    }

    private struct HandResult
    {
        public bool Tracked;
        public float SpeedMps;
        public bool InVolume;
        public float DirectionDeltaMeters;
        public Vector3 HeadLocalPosition;
        public Vector3 HeadLocalDelta;
    }

    private HandResult ProcessHand(
        HandState state, WayfinderTrackedHandPose sample, Pose headPose, bool headValid, float dt)
    {
        bool tracked = sample.IsTracked && IsFinite(sample.TrackingOriginPose.position) &&
                       IsFinite(sample.TrackingOriginPose.rotation);
        if (!tracked)
        {
            FinalizeSegment(state);
            state.HasPrevious = false;
            state.HasVelocity = false;
            state.FilteredVelocity = Vector3.zero;
            return default;
        }

        Vector3 origin = sample.TrackingOriginPose.position;
        Vector3 headLocal = headValid
            ? Quaternion.Inverse(headPose.rotation) * (origin - headPose.position)
            : Vector3.zero;
        bool inVolume = headValid && IsInsideVolume(headLocal);

        if (!state.HasPrevious || dt <= 0f)
        {
            state.HasPrevious = true;
            state.HasVelocity = false;
            state.PreviousOrigin = origin;
            state.PreviousHeadLocal = headLocal;
            state.SegmentStart = origin;
            state.FilteredVelocity = Vector3.zero;
            return new HandResult
            {
                Tracked = true,
                InVolume = inVolume,
                HeadLocalPosition = headLocal
            };
        }

        Vector3 originDelta = origin - state.PreviousOrigin;
        Vector3 headLocalDelta = headValid ? headLocal - state.PreviousHeadLocal : Vector3.zero;
        Vector3 rawVelocity = originDelta / dt;
        float maximumSpeed = SafePositive(config.MaximumAcceptedSpeedMps, 3f);
        if (!IsFinite(rawVelocity) || rawVelocity.magnitude > maximumSpeed)
        {
            // Drop the implausible interval but move the baseline forward, preventing a delayed spike.
            rawVelocity = Vector3.zero;
            originDelta = Vector3.zero;
            headLocalDelta = Vector3.zero;
        }

        Vector3 previousFiltered = state.FilteredVelocity;
        if (!state.HasVelocity)
        {
            state.FilteredVelocity = rawVelocity;
            state.HasVelocity = true;
        }
        else
        {
            float smoothing = SafePositive(config.VelocitySmoothingSeconds, 0.055f);
            float alpha = 1f - Mathf.Exp(-dt / smoothing);
            state.FilteredVelocity = Vector3.Lerp(previousFiltered, rawVelocity, Mathf.Clamp01(alpha));
            Vector3 acceleration = (state.FilteredVelocity - previousFiltered) / dt;
            if (IsFinite(acceleration))
            {
                state.AccelerationEnergy += acceleration.sqrMagnitude * dt;
                state.AccelerationSeconds += dt;
            }
        }

        float speed = state.FilteredVelocity.magnitude;
        if (speed <= SafeNonNegative(config.StationaryNoiseFloorMps))
        {
            state.FilteredVelocity = Vector3.zero;
            speed = 0f;
        }

        state.PathMeters += originDelta.magnitude;
        state.PreviousOrigin = origin;
        state.PreviousHeadLocal = headLocal;

        Vector3 intendedDirection = config.IntendedHeadLocalDirection;
        if (!IsFinite(intendedDirection) || intendedDirection.sqrMagnitude < 0.000001f)
        {
            intendedDirection = Vector3.right;
        }
        float directionDelta = headValid
            ? Mathf.Max(0f, Vector3.Dot(headLocalDelta, intendedDirection.normalized))
            : 0f;

        return new HandResult
        {
            Tracked = true,
            SpeedMps = speed,
            InVolume = inVolume,
            DirectionDeltaMeters = directionDelta,
            HeadLocalPosition = headLocal,
            HeadLocalDelta = headLocalDelta
        };
    }

    private WayfinderHandMotionClass Classify(bool tracked, float speed)
    {
        if (!tracked)
        {
            return WayfinderHandMotionClass.Untracked;
        }

        float desiredMin = SafePositive(config.DesiredSpeedMinMps, 0.10f);
        float desiredMax = Mathf.Max(desiredMin, SafePositive(config.DesiredSpeedMaxMps, 0.35f));
        float rushed = Mathf.Max(desiredMax, SafePositive(config.RushedSpeedMps, 0.55f));
        if (speed <= SafeNonNegative(config.StationaryNoiseFloorMps)) return WayfinderHandMotionClass.Still;
        if (speed < desiredMin) return WayfinderHandMotionClass.TooSlow;
        if (speed <= desiredMax) return WayfinderHandMotionClass.Desired;
        if (speed > rushed) return WayfinderHandMotionClass.Rushed;
        return WayfinderHandMotionClass.TooFast;
    }

    private bool IsInsideVolume(Vector3 point)
    {
        Vector3 min = Vector3.Min(config.InteractionVolumeMin, config.InteractionVolumeMax);
        Vector3 max = Vector3.Max(config.InteractionVolumeMin, config.InteractionVolumeMax);
        return point.x >= min.x && point.x <= max.x &&
               point.y >= min.y && point.y <= max.y &&
               point.z >= min.z && point.z <= max.z;
    }

    private float CalculateSmoothness()
    {
        float energy = left.AccelerationEnergy + right.AccelerationEnergy;
        float seconds = left.AccelerationSeconds + right.AccelerationSeconds;
        if (seconds <= 0f)
        {
            return measurementStarted ? 1f : 0f;
        }
        float rmsAcceleration = Mathf.Sqrt(Mathf.Max(0f, energy / seconds));
        float scale = SafePositive(config.SmoothnessAccelerationScale, 2f);
        return Mathf.Clamp01(1f / (1f + rmsAcceleration / scale));
    }

    private float CalculatePathEfficiency()
    {
        float path = left.PathMeters + right.PathMeters;
        if (path <= 0.000001f)
        {
            return measurementStarted ? 1f : 0f;
        }
        float direct = DirectMeters(left) + DirectMeters(right);
        return Mathf.Clamp01(FiniteOrZero(direct / path));
    }

    private static float DirectMeters(HandState state)
    {
        return state.CompletedDirectMeters +
               (state.HasPrevious ? Vector3.Distance(state.SegmentStart, state.PreviousOrigin) : 0f);
    }

    private static void FinalizeSegment(HandState state)
    {
        if (state.HasPrevious)
        {
            state.CompletedDirectMeters += Vector3.Distance(state.SegmentStart, state.PreviousOrigin);
        }
    }

    private static void ResetHand(HandState state)
    {
        state.HasPrevious = false;
        state.HasVelocity = false;
        state.PreviousOrigin = Vector3.zero;
        state.PreviousHeadLocal = Vector3.zero;
        state.FilteredVelocity = Vector3.zero;
        state.SegmentStart = Vector3.zero;
        state.PathMeters = 0f;
        state.CompletedDirectMeters = 0f;
        state.AccelerationEnergy = 0f;
        state.AccelerationSeconds = 0f;
    }

    private static float SafePositive(float value, float fallback)
    {
        return IsFinite(value) && value > 0f ? value : fallback;
    }

    private static float SafeNonNegative(float value)
    {
        return IsFinite(value) && value >= 0f ? value : 0f;
    }

    private static float FiniteOrZero(float value)
    {
        return IsFinite(value) ? value : 0f;
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(Quaternion value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z) && IsFinite(value.w);
    }
}

/// <summary>Deterministic provider for tests, recordings, and offline replays.</summary>
public sealed class WayfinderScriptedHandProvider : IWayfinderHandProvider
{
    private readonly Queue<WayfinderHandProviderFrame> frames = new Queue<WayfinderHandProviderFrame>();
    private WayfinderHandProviderFrame lastFrame;

    public int RemainingFrames => frames.Count;

    public WayfinderScriptedHandProvider(IEnumerable<WayfinderHandProviderFrame> frames = null)
    {
        if (frames == null) return;
        foreach (WayfinderHandProviderFrame frame in frames)
        {
            this.frames.Enqueue(frame);
        }
    }

    public void Enqueue(WayfinderHandProviderFrame frame)
    {
        frames.Enqueue(frame);
    }

    public WayfinderHandProviderFrame Sample()
    {
        if (frames.Count > 0)
        {
            lastFrame = frames.Dequeue();
        }
        return lastFrame;
    }
}

#if UNITY_EDITOR
/// <summary>
/// Optional editor-only hand simulator. Hold the mouse for the right hand and
/// Shift+mouse for a mirrored left hand; output remains in tracking-origin meters.
/// </summary>
public sealed class WayfinderEditorHandSimulatorProvider : IWayfinderHandProvider
{
    public WayfinderHandProviderFrame Sample()
    {
        float width = Mathf.Max(1f, Screen.width);
        float height = Mathf.Max(1f, Screen.height);
        Vector3 mouse = Input.mousePosition;
        Vector3 rightLocal = new Vector3(
            (mouse.x / width - 0.5f) * 1.1f,
            (mouse.y / height - 0.5f) * 0.8f - 0.1f,
            0.55f);
        bool rightTracked = Input.GetMouseButton(0);
        bool leftTracked = rightTracked && (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift));
        Vector3 leftLocal = new Vector3(-rightLocal.x, rightLocal.y, rightLocal.z);
        return new WayfinderHandProviderFrame
        {
            Left = new WayfinderTrackedHandPose(leftTracked, new Pose(leftLocal, Quaternion.identity)),
            Right = new WayfinderTrackedHandPose(rightTracked, new Pose(rightLocal, Quaternion.identity)),
            IsHeadTracked = true,
            HeadTrackingOriginPose = Pose.identity
        };
    }
}
#endif
