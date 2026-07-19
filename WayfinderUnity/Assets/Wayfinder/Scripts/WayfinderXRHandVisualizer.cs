using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

/// <summary>
/// Presentation-only XR Hands visualization. Joint data stays inside this class
/// and is never supplied to motion scoring or the Memory Keeper.
/// </summary>
[DefaultExecutionOrder(100)]
public sealed class WayfinderXRHandVisualizer : MonoBehaviour, IWayfinderPalmPoseSource
{
    private static readonly XRHandJointID[] JointIds =
    {
        XRHandJointID.Wrist, XRHandJointID.Palm,
        XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal,
        XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip,
        XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
        XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip,
        XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
        XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip,
        XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
        XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip,
        XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
        XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip
    };

    private static readonly XRHandJointID[][] Chains =
    {
        new[] { XRHandJointID.Wrist, XRHandJointID.Palm },
        new[] { XRHandJointID.Wrist, XRHandJointID.ThumbMetacarpal, XRHandJointID.ThumbProximal,
            XRHandJointID.ThumbDistal, XRHandJointID.ThumbTip },
        new[] { XRHandJointID.Palm, XRHandJointID.IndexMetacarpal, XRHandJointID.IndexProximal,
            XRHandJointID.IndexIntermediate, XRHandJointID.IndexDistal, XRHandJointID.IndexTip },
        new[] { XRHandJointID.Palm, XRHandJointID.MiddleMetacarpal, XRHandJointID.MiddleProximal,
            XRHandJointID.MiddleIntermediate, XRHandJointID.MiddleDistal, XRHandJointID.MiddleTip },
        new[] { XRHandJointID.Palm, XRHandJointID.RingMetacarpal, XRHandJointID.RingProximal,
            XRHandJointID.RingIntermediate, XRHandJointID.RingDistal, XRHandJointID.RingTip },
        new[] { XRHandJointID.Palm, XRHandJointID.LittleMetacarpal, XRHandJointID.LittleProximal,
            XRHandJointID.LittleIntermediate, XRHandJointID.LittleDistal, XRHandJointID.LittleTip }
    };

    private sealed class HandVisual
    {
        public readonly Dictionary<XRHandJointID, Transform> Joints =
            new Dictionary<XRHandJointID, Transform>();
        public readonly Dictionary<XRHandJointID, Pose> Poses =
            new Dictionary<XRHandJointID, Pose>();
        public readonly List<LineRenderer> Lines = new List<LineRenderer>();
        public Material JointMaterial;
        public Material PalmMaterial;
        public Transform PalmOrb;
        public TrailRenderer PalmTrail;
        public float Visibility;
        public bool Tracked;
        public Pose PalmPose;
    }

    [SerializeField, Min(0.002f)] private float jointDiameterMeters = 0.012f;
    [SerializeField, Min(0.003f)] private float palmDiameterMeters = 0.052f;
    [SerializeField, Min(0.001f)] private float connectionWidthMeters = 0.006f;
    [SerializeField, Min(0.01f)] private float trackingLossFadeSeconds = 0.22f;
    [SerializeField, Min(0.01f)] private float palmTrailSeconds = 0.18f;
    [SerializeField] private Color leftColor = new Color(0.28f, 0.92f, 0.84f, 0.85f);
    [SerializeField] private Color rightColor = new Color(0.42f, 0.72f, 1f, 0.85f);
    [SerializeField] private Color palmColor = new Color(1f, 0.86f, 0.34f, 1f);

    private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
    private readonly HandVisual left = new HandVisual();
    private readonly HandVisual right = new HandVisual();
    private XRHandSubsystem subsystem;
    private Transform visualRoot;

    private void Awake()
    {
        visualRoot = new GameObject("XR Hands Procedural Visuals (Scoring Independent)").transform;
        visualRoot.SetParent(transform, false);
        CreateHand(left, "Left Hand Glow", leftColor);
        CreateHand(right, "Right Hand Glow", rightColor);
    }

    private void Update()
    {
        RefreshSubsystem();
        XRHand leftHand = subsystem != null ? subsystem.leftHand : default;
        XRHand rightHand = subsystem != null ? subsystem.rightHand : default;
        UpdateHand(left, leftHand, Time.unscaledDeltaTime);
        UpdateHand(right, rightHand, Time.unscaledDeltaTime);
    }

    public bool TryGetLeftPalm(out Pose worldPose)
    {
        worldPose = left.PalmPose;
        return left.Tracked;
    }

    public bool TryGetRightPalm(out Pose worldPose)
    {
        worldPose = right.PalmPose;
        return right.Tracked;
    }

    private void RefreshSubsystem()
    {
        if (subsystem != null && subsystem.running) return;
        subsystems.Clear();
        SubsystemManager.GetSubsystems(subsystems);
        subsystem = null;
        foreach (XRHandSubsystem candidate in subsystems)
        {
            if (candidate != null && candidate.running)
            {
                subsystem = candidate;
                break;
            }
        }
    }

    private void CreateHand(HandVisual visual, string name, Color color)
    {
        Transform handRoot = new GameObject(name).transform;
        handRoot.SetParent(visualRoot, false);
        visual.JointMaterial = CreateGlowMaterial(name + " Joint Material", color);
        visual.PalmMaterial = CreateGlowMaterial(name + " Palm Material", palmColor);

        foreach (XRHandJointID jointId in JointIds)
        {
            GameObject point = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            point.name = jointId + " Glow Point";
            point.transform.SetParent(handRoot, false);
            point.transform.localScale = Vector3.one * jointDiameterMeters;
            Collider collider = point.GetComponent<Collider>();
            if (collider != null) Destroy(collider);
            point.GetComponent<Renderer>().sharedMaterial = visual.JointMaterial;
            visual.Joints[jointId] = point.transform;
        }

        GameObject palm = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        palm.name = "Bright Palm Interaction Orb";
        palm.transform.SetParent(handRoot, false);
        palm.transform.localScale = Vector3.one * palmDiameterMeters;
        Collider palmCollider = palm.GetComponent<Collider>();
        if (palmCollider != null) Destroy(palmCollider);
        palm.GetComponent<Renderer>().sharedMaterial = visual.PalmMaterial;
        visual.PalmOrb = palm.transform;

        TrailRenderer trail = palm.AddComponent<TrailRenderer>();
        trail.sharedMaterial = visual.PalmMaterial;
        trail.time = palmTrailSeconds;
        trail.startWidth = connectionWidthMeters * 1.8f;
        trail.endWidth = 0f;
        trail.minVertexDistance = 0.008f;
        trail.emitting = false;
        visual.PalmTrail = trail;

        for (int index = 0; index < Chains.Length; index++)
        {
            GameObject lineObject = new GameObject("Palm/Finger Connection " + (index + 1));
            lineObject.transform.SetParent(handRoot, false);
            LineRenderer line = lineObject.AddComponent<LineRenderer>();
            line.sharedMaterial = visual.JointMaterial;
            line.useWorldSpace = true;
            line.widthMultiplier = connectionWidthMeters;
            line.numCapVertices = 3;
            line.positionCount = 0;
            visual.Lines.Add(line);
        }
    }

    private void UpdateHand(HandVisual visual, XRHand hand, float deltaTime)
    {
        visual.Tracked = hand.isTracked;
        float fadeStep = deltaTime / Mathf.Max(0.01f, trackingLossFadeSeconds);
        visual.Visibility = Mathf.MoveTowards(visual.Visibility, visual.Tracked ? 1f : 0f, fadeStep);
        visual.Poses.Clear();

        if (visual.Tracked)
        {
            foreach (XRHandJointID jointId in JointIds)
            {
                Pose trackingPose;
                if (hand.GetJoint(jointId).TryGetPose(out trackingPose))
                {
                    Pose worldPose = TrackingPoseToWorld(trackingPose);
                    visual.Poses[jointId] = worldPose;
                    Transform point = visual.Joints[jointId];
                    point.SetPositionAndRotation(worldPose.position, worldPose.rotation);
                }
            }

            Pose palmPose;
            if (visual.Poses.TryGetValue(XRHandJointID.Palm, out palmPose) ||
                visual.Poses.TryGetValue(XRHandJointID.Wrist, out palmPose))
            {
                visual.PalmPose = palmPose;
                visual.PalmOrb.SetPositionAndRotation(palmPose.position, palmPose.rotation);
            }
            else
            {
                visual.Tracked = false;
            }
        }

        float visibleScale = Mathf.Max(0.001f, visual.Visibility);
        foreach (KeyValuePair<XRHandJointID, Transform> pair in visual.Joints)
        {
            pair.Value.localScale = Vector3.one * jointDiameterMeters * visibleScale;
            pair.Value.gameObject.SetActive(visual.Visibility > 0.001f);
        }
        visual.PalmOrb.localScale = Vector3.one * palmDiameterMeters * visibleScale;
        visual.PalmOrb.gameObject.SetActive(visual.Visibility > 0.001f);
        visual.PalmTrail.emitting = visual.Tracked;
        SetMaterialAlpha(visual.JointMaterial, visual.Visibility * 0.85f);
        SetMaterialAlpha(visual.PalmMaterial, visual.Visibility);
        UpdateLines(visual);
    }

    private void UpdateLines(HandVisual visual)
    {
        for (int chainIndex = 0; chainIndex < Chains.Length; chainIndex++)
        {
            XRHandJointID[] chain = Chains[chainIndex];
            LineRenderer line = visual.Lines[chainIndex];
            bool complete = visual.Tracked;
            for (int index = 0; index < chain.Length; index++)
            {
                if (!visual.Poses.ContainsKey(chain[index]))
                {
                    complete = false;
                    break;
                }
            }
            if (!complete || visual.Visibility <= 0.001f)
            {
                line.positionCount = 0;
                continue;
            }
            line.positionCount = chain.Length;
            for (int index = 0; index < chain.Length; index++)
            {
                line.SetPosition(index, visual.Poses[chain[index]].position);
            }
            Color color = visual.JointMaterial.color;
            line.startColor = color;
            line.endColor = new Color(color.r, color.g, color.b, color.a * 0.55f);
        }
    }

    private static Pose TrackingPoseToWorld(Pose trackingPose)
    {
        Camera camera = Camera.main;
        if (camera == null) return trackingPose;

        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        Vector3 headPosition;
        Quaternion headRotation;
        if (!head.isValid ||
            !head.TryGetFeatureValue(CommonUsages.devicePosition, out headPosition) ||
            !head.TryGetFeatureValue(CommonUsages.deviceRotation, out headRotation))
        {
            return new Pose(camera.transform.TransformPoint(trackingPose.position),
                camera.transform.rotation * trackingPose.rotation);
        }

        Quaternion trackingToWorld = camera.transform.rotation * Quaternion.Inverse(headRotation);
        Vector3 worldPosition = camera.transform.position + trackingToWorld * (trackingPose.position - headPosition);
        return new Pose(worldPosition, trackingToWorld * trackingPose.rotation);
    }

    private static Material CreateGlowMaterial(string name, Color color)
    {
        Shader shader = Shader.Find("Sprites/Default");
        Material material = new Material(shader) { name = name, color = color };
        material.renderQueue = 3100;
        return material;
    }

    private static void SetMaterialAlpha(Material material, float alpha)
    {
        Color color = material.color;
        color.a = Mathf.Clamp01(alpha);
        material.color = color;
    }
}
