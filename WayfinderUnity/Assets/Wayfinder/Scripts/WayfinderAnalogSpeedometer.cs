using UnityEngine;

public sealed class WayfinderAnalogSpeedometer : MonoBehaviour
{
    [SerializeField] private Transform needle;
    [SerializeField, Min(0.01f)] private float dampingSeconds = 0.30f;

    private float displayedValue;
    private float dampingVelocity;

    public float DisplayedValue => displayedValue;

    public void SetSpeed(
        float speedMps, float desiredMinimumMps, float desiredMaximumMps,
        float rushedMps, float deltaTime)
    {
        float flowSpeed = (Mathf.Max(0f, desiredMinimumMps) +
                           Mathf.Max(desiredMinimumMps, desiredMaximumMps)) * 0.5f;
        float rushSpeed = Mathf.Max(flowSpeed + 0.001f, rushedMps);
        float target = speedMps <= flowSpeed
            ? Mathf.InverseLerp(0f, flowSpeed, Mathf.Max(0f, speedMps))
            : 1f + Mathf.InverseLerp(flowSpeed, rushSpeed, speedMps);
        float dt = float.IsNaN(deltaTime) || float.IsInfinity(deltaTime) || deltaTime <= 0f
            ? 0f
            : deltaTime;
        displayedValue = Mathf.SmoothDamp(
            displayedValue, Mathf.Clamp(target, 0f, 2f), ref dampingVelocity,
            Mathf.Max(0.01f, dampingSeconds), Mathf.Infinity, dt);
        if (needle != null)
        {
            needle.localRotation = Quaternion.Euler(0f, 0f, Mathf.Lerp(180f, 0f, displayedValue * 0.5f));
        }
    }

    public void ResetDisplay()
    {
        displayedValue = 0f;
        dampingVelocity = 0f;
        if (needle != null) needle.localRotation = Quaternion.Euler(0f, 0f, 180f);
    }

    public void Configure(Transform needleTransform)
    {
        needle = needleTransform;
    }
}
