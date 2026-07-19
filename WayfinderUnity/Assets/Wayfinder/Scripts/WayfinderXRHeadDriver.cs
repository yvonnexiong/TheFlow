using UnityEngine;
using UnityEngine.XR;

public sealed class WayfinderXRHeadDriver : MonoBehaviour
{
    [SerializeField] private Vector3 xrOriginPosition = new Vector3(0f, 0f, -3.45f);
    [SerializeField] private Vector3 fallbackHeadPosition = new Vector3(0f, 1.65f, 0f);

    private Vector3 desktopPosition;
    private Quaternion desktopRotation;
    private bool xrWasActive;

    private void Awake()
    {
        desktopPosition = transform.position;
        desktopRotation = transform.rotation;
    }

    private void LateUpdate()
    {
        InputDevice head = InputDevices.GetDeviceAtXRNode(XRNode.Head);
        bool xrActive = head.isValid;
        if (!xrActive)
        {
            if (xrWasActive)
            {
                transform.SetPositionAndRotation(desktopPosition, desktopRotation);
            }
            xrWasActive = false;
            return;
        }

        Vector3 headPosition = fallbackHeadPosition;
        Quaternion headRotation = Quaternion.identity;
        head.TryGetFeatureValue(CommonUsages.devicePosition, out headPosition);
        head.TryGetFeatureValue(CommonUsages.deviceRotation, out headRotation);

        transform.SetPositionAndRotation(xrOriginPosition + headPosition, headRotation);
        xrWasActive = true;
    }
}
