using System.Collections.Generic;
using UnityEngine;

/// <summary>Presentation-only placed circle, pearl, and persistent energy calligraphy.</summary>
public sealed class WayfinderCircularPalmGuide : MonoBehaviour
{
    [SerializeField] private LineRenderer faintCircle;
    [SerializeField] private LineRenderer validFlowFill;
    [SerializeField] private LineRenderer holdProgressHalo;
    [SerializeField] private LineRenderer tealEnergyRibbon;
    [SerializeField] private LineRenderer goldEnergyRibbon;
    [SerializeField] private Transform placementPearl;
    [SerializeField] private Transform pacingLight;
    [SerializeField] private ParticleSystem gaussianFragments;
    [SerializeField] private ParticleSystem softSparks;
    [SerializeField] private ParticleSystem waterRipples;
    [SerializeField, Range(32, 160)] private int segments = 96;
    [SerializeField, Min(0.002f)] private float trailSampleDistanceMeters = 0.006f;

    private readonly List<Vector3> tealPoints = new List<Vector3>();
    private readonly List<Vector3> goldPoints = new List<Vector3>();
    private Vector3 lastTrailPoint;
    private bool hasTrailPoint;
    private float rippleCooldown;

    public void Render(
        Camera headCamera, WayfinderCircularGestureResult result,
        float radiusMeters, float ribbonWidthMeters, bool visible)
    {
        gameObject.SetActive(visible);
        if (!visible || headCamera == null) return;

        bool placing = result.Stage == WayfinderCircularTutorialStage.PlaceYourCircle;
        bool circleVisible = result.PlacementLocked;
        if (placementPearl != null)
        {
            placementPearl.gameObject.SetActive(result.PearlAttached && placing);
            placementPearl.position = result.PearlWorldPosition;
            float pulse = 1f + Mathf.Sin(Time.unscaledTime * 4f) * 0.12f;
            placementPearl.localScale = Vector3.one * (0.045f * pulse);
        }

        RenderHoldHalo(headCamera, result, placing && result.PearlAttached);
        RenderCircle(result, radiusMeters, ribbonWidthMeters, circleVisible);
        RenderPacingLight(result);
        RenderEnergy(result);
    }

    public void ResetGuide()
    {
        tealPoints.Clear();
        goldPoints.Clear();
        hasTrailPoint = false;
        rippleCooldown = 0f;
        ClearLine(validFlowFill);
        ClearLine(tealEnergyRibbon);
        ClearLine(goldEnergyRibbon);
        if (gaussianFragments != null) gaussianFragments.Clear(true);
        if (softSparks != null) softSparks.Clear(true);
        if (waterRipples != null) waterRipples.Clear(true);
        gameObject.SetActive(false);
    }

    public void Configure(
        LineRenderer circle, LineRenderer fill, LineRenderer halo,
        LineRenderer tealRibbon, LineRenderer goldRibbon,
        Transform pearl, Transform light,
        ParticleSystem fragments, ParticleSystem sparks, ParticleSystem ripples)
    {
        faintCircle = circle;
        validFlowFill = fill;
        holdProgressHalo = halo;
        tealEnergyRibbon = tealRibbon;
        goldEnergyRibbon = goldRibbon;
        placementPearl = pearl;
        pacingLight = light;
        gaussianFragments = fragments;
        softSparks = sparks;
        waterRipples = ripples;
    }

    private void RenderHoldHalo(
        Camera camera, WayfinderCircularGestureResult result, bool visible)
    {
        if (holdProgressHalo == null) return;
        if (!visible)
        {
            holdProgressHalo.positionCount = 0;
            return;
        }
        int count = Mathf.Max(2, Mathf.CeilToInt(result.PlacementHold01 * 48f) + 1);
        holdProgressHalo.positionCount = count;
        Quaternion rotation = Quaternion.LookRotation(
            camera.transform.position - result.PearlWorldPosition, camera.transform.up);
        for (int i = 0; i < count; i++)
        {
            float angle = Mathf.PI * 2f * i / 48f;
            holdProgressHalo.SetPosition(i, result.PearlWorldPosition + rotation *
                new Vector3(Mathf.Cos(angle), Mathf.Sin(angle), 0f) * 0.065f);
        }
    }

    private void RenderCircle(
        WayfinderCircularGestureResult result, float radius, float ribbonWidth, bool visible)
    {
        if (faintCircle == null || validFlowFill == null) return;
        if (!visible)
        {
            faintCircle.positionCount = 0;
            validFlowFill.positionCount = 0;
            return;
        }

        faintCircle.widthMultiplier = Mathf.Clamp(ribbonWidth, 0.04f, 0.08f);
        int count = Mathf.Max(32, segments);
        float grownRadius = radius * Mathf.SmoothStep(0f, 1f, result.CircleGrowth01);
        faintCircle.positionCount = count + 1;
        for (int i = 0; i <= count; i++)
            faintCircle.SetPosition(i, CirclePoint(result.CirclePose, grownRadius,
                i / (float)count * 360f));

        int fillCount = result.AttemptActive || result.CompletionLatched
            ? Mathf.Clamp(Mathf.CeilToInt(result.Progress01 * count) + 1, 1, count + 1) : 0;
        validFlowFill.positionCount = fillCount;
        int direction = result.PacingDirection == 0 ? 1 : result.PacingDirection;
        for (int i = 0; i < fillCount; i++)
        {
            float degrees = result.TraceStartAngleDegrees +
                direction * i / (float)count * 360f;
            validFlowFill.SetPosition(i, CirclePoint(result.CirclePose, grownRadius, degrees));
        }
    }

    private void RenderPacingLight(WayfinderCircularGestureResult result)
    {
        if (pacingLight == null) return;
        pacingLight.gameObject.SetActive(result.Stage == WayfinderCircularTutorialStage.FollowLight);
        pacingLight.position = result.PacingLightWorldPosition;
        float pulse = 1f + Mathf.Sin(Time.unscaledTime * 5f) * 0.10f;
        pacingLight.localScale = Vector3.one * (0.034f * pulse);
    }

    private void RenderEnergy(WayfinderCircularGestureResult result)
    {
        rippleCooldown = Mathf.Max(0f, rippleCooldown - Time.unscaledDeltaTime);
        if (!result.AttemptActive) return;
        Vector3 palm = result.PalmWorldPosition;
        if (!hasTrailPoint || Vector3.Distance(lastTrailPoint, palm) >= trailSampleDistanceMeters)
        {
            if (result.ValidFlow)
            {
                tealPoints.Add(palm);
                goldPoints.Add(palm + result.CirclePose.rotation * Vector3.forward * 0.004f);
                ApplyPoints(tealEnergyRibbon, tealPoints);
                ApplyPoints(goldEnergyRibbon, goldPoints);
                Emit(softSparks, palm, 1, new Color(0.95f, 0.76f, 0.32f, 0.72f), 0.014f);
                if (rippleCooldown <= 0f)
                {
                    Emit(waterRipples, palm, 1, new Color(0.20f, 0.90f, 0.80f, 0.34f), 0.05f);
                    rippleCooldown = 0.14f;
                }
            }
            else if (result.LooseFragments)
            {
                Emit(gaussianFragments, palm, 3, new Color(0.34f, 0.90f, 0.79f, 0.54f), 0.026f);
            }
            else if (result.Rushing)
            {
                Emit(gaussianFragments, palm, 7, new Color(0.95f, 0.68f, 0.25f, 0.46f), 0.035f);
                Emit(softSparks, palm, 4, new Color(1f, 0.76f, 0.34f, 0.60f), 0.018f);
            }
            lastTrailPoint = palm;
            hasTrailPoint = true;
        }
    }

    private static Vector3 CirclePoint(Pose pose, float radius, float degrees)
    {
        float radians = degrees * Mathf.Deg2Rad;
        return pose.position + pose.rotation * new Vector3(
            Mathf.Cos(radians) * radius, Mathf.Sin(radians) * radius, 0f);
    }

    private static void ApplyPoints(LineRenderer line, List<Vector3> points)
    {
        if (line == null) return;
        line.positionCount = points.Count;
        line.SetPositions(points.ToArray());
    }

    private static void Emit(
        ParticleSystem system, Vector3 position, int count, Color color, float size)
    {
        if (system == null) return;
        var parameters = new ParticleSystem.EmitParams
        {
            position = position,
            startColor = color,
            startSize = size,
            velocity = Random.insideUnitSphere * 0.035f
        };
        system.Emit(parameters, count);
    }

    private static void ClearLine(LineRenderer line)
    {
        if (line != null) line.positionCount = 0;
    }
}
