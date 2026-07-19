using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;
using UnityEngine.XR.Hands;

/// <summary>
/// Physical provider for the installed XR Hands/OpenXR path. All XR Hands SDK
/// types are confined to this class; consumers receive SDK-neutral poses.
/// </summary>
public sealed class WayfinderXRHandsProvider : IWayfinderHandProvider
{
    private readonly List<XRHandSubsystem> subsystems = new List<XRHandSubsystem>();
    private XRHandSubsystem subsystem;

    public WayfinderHandProviderFrame Sample()
    {
        RefreshSubsystem();
        WayfinderHandProviderFrame frame = new WayfinderHandProviderFrame
        {
            Left = ReadHand(subsystem != null ? subsystem.leftHand : default),
            Right = ReadHand(subsystem != null ? subsystem.rightHand : default)
        };

        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        Vector3 headPosition = Vector3.zero;
        Quaternion headRotation = Quaternion.identity;
        bool hasPosition = head.isValid && head.TryGetFeatureValue(CommonUsages.devicePosition, out headPosition);
        bool hasRotation = head.isValid && head.TryGetFeatureValue(CommonUsages.deviceRotation, out headRotation);
        frame.IsHeadTracked = hasPosition && hasRotation;
        frame.HeadTrackingOriginPose = frame.IsHeadTracked
            ? new Pose(headPosition, headRotation)
            : Pose.identity;
        return frame;
    }

    private void RefreshSubsystem()
    {
        if (subsystem != null && subsystem.running)
        {
            return;
        }

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

    private static WayfinderTrackedHandPose ReadHand(XRHand hand)
    {
        if (!hand.isTracked)
        {
            return default;
        }

        Pose pose;
        if (hand.GetJoint(XRHandJointID.Palm).TryGetPose(out pose) ||
            hand.GetJoint(XRHandJointID.Wrist).TryGetPose(out pose))
        {
            return new WayfinderTrackedHandPose(true, pose);
        }
        return default;
    }
}
