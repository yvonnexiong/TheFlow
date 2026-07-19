using UnityEngine;

public struct WayfinderInputFrame
{
    public Vector2 ScreenPosition;
    public float MotionSpeed;
    public float HorizontalDelta;
    public bool GestureHeld;
    public bool ResetPressed;
    public bool IsXR;
    public WayfinderHandMotionMetrics HandMetrics;
}

public interface IWayfinderInputSource
{
    WayfinderInputFrame Sample(float deltaTime);
}

/// <summary>
/// Keeps the desktop fallback live while opportunistically accepting physical
/// hand motion from the supported XR Hands provider.
/// </summary>
public sealed class WayfinderHybridInputSource : IWayfinderInputSource
{
    private const float XRTakeoverSpeed = 120f;
    private const float XRTakeoverSeconds = 0.35f;

    private readonly WayfinderMouseInputSource mouse = new WayfinderMouseInputSource();
    private readonly WayfinderXRInputSource xr;
    private readonly bool xrEnabled;
    private float xrTakeoverRemaining;

    public WayfinderHybridInputSource(bool xrEnabled, float xrMetersToPixels)
        : this(xrEnabled, xrMetersToPixels, null)
    {
    }

    public WayfinderHybridInputSource(
        bool xrEnabled, float xrMetersToPixels, WayfinderHandMotionConfig handMotionConfig)
    {
        this.xrEnabled = xrEnabled;
        xr = new WayfinderXRInputSource(xrMetersToPixels, null, handMotionConfig);
    }

    public WayfinderInputFrame Sample(float deltaTime)
    {
        WayfinderInputFrame mouseFrame = mouse.Sample(deltaTime);
        if (!xrEnabled)
        {
            return mouseFrame;
        }

        WayfinderInputFrame xrFrame = xr.Sample(deltaTime);
        bool handTracked = xrFrame.HandMetrics.LeftTracked || xrFrame.HandMetrics.RightTracked;
        if (handTracked || xrFrame.GestureHeld || xrFrame.ResetPressed || xrFrame.MotionSpeed >= XRTakeoverSpeed)
        {
            xrTakeoverRemaining = XRTakeoverSeconds;
        }
        else
        {
            xrTakeoverRemaining = Mathf.Max(0f, xrTakeoverRemaining - deltaTime);
        }

        // An actively held mouse always remains a deterministic demo fallback.
        if (mouseFrame.GestureHeld)
        {
            xrTakeoverRemaining = 0f;
            return mouseFrame;
        }

#if UNITY_ANDROID
        // The PICO hands-only build must keep returning XR telemetry even while
        // hands are outside the interaction volume or temporarily untracked.
        return xrFrame;
#else
        return xrTakeoverRemaining > 0f ? xrFrame : mouseFrame;
#endif
    }
}

public sealed class WayfinderMouseInputSource : IWayfinderInputSource
{
    private Vector3 lastPosition = Input.mousePosition;

    public WayfinderInputFrame Sample(float deltaTime)
    {
        Vector3 position = Input.mousePosition;
        Vector3 delta = position - lastPosition;
        lastPosition = position;

        return new WayfinderInputFrame
        {
            ScreenPosition = position,
            MotionSpeed = delta.magnitude / Mathf.Max(0.001f, deltaTime),
            HorizontalDelta = delta.x,
            GestureHeld = Input.GetMouseButton(0),
            ResetPressed = Input.GetKeyDown(KeyCode.R),
            IsXR = false
        };
    }
}

public sealed class WayfinderXRInputSource : IWayfinderInputSource
{
    private readonly float metersToPixels;
    private readonly IWayfinderHandProvider handProvider;
    private readonly WayfinderHandMotionEstimator estimator;
    private Vector2 virtualScreenPosition;

    public WayfinderXRInputSource(
        float metersToPixels,
        IWayfinderHandProvider handProvider = null,
        WayfinderHandMotionConfig motionConfig = null)
    {
        this.metersToPixels = Mathf.Max(1f, metersToPixels);
        this.handProvider = handProvider ?? new WayfinderXRHandsProvider();
        estimator = new WayfinderHandMotionEstimator(motionConfig);
        virtualScreenPosition = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
    }

    public WayfinderInputFrame Sample(float deltaTime)
    {
        WayfinderHandProviderFrame providerFrame = handProvider.Sample();
        WayfinderHandMotionMetrics metrics = estimator.Update(providerFrame, deltaTime);
        Vector2 screenDelta = new Vector2(metrics.HeadRelativeDelta.x, metrics.HeadRelativeDelta.y) * metersToPixels;
        virtualScreenPosition += screenDelta;
        virtualScreenPosition.x = Mathf.Clamp(virtualScreenPosition.x, 0f, Mathf.Max(1f, Screen.width));
        virtualScreenPosition.y = Mathf.Clamp(virtualScreenPosition.y, 0f, Mathf.Max(1f, Screen.height));

        return new WayfinderInputFrame
        {
            ScreenPosition = virtualScreenPosition,
            MotionSpeed = metrics.CombinedSpeedMps * metersToPixels,
            HorizontalDelta = metrics.IntendedDirectionDeltaMeters * metersToPixels,
            GestureHeld = metrics.TrackingValid && metrics.InInteractionVolume,
            ResetPressed = false,
            IsXR = true,
            HandMetrics = metrics
        };
    }
}
