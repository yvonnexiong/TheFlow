using System.IO;
using UnityEngine;

/// <summary>
/// Development-build-only deterministic trailer performance. It never starts
/// for a participant; placing trailer.flag in persistentDataPath starts one take.
/// The actual scene, world reveal, story cards, and reflection flow remain live.
/// </summary>
public sealed class WayfinderTrailerDirector : MonoBehaviour
{
    private sealed class EnergyHand
    {
        private readonly Transform root;
        private readonly Transform palm;
        private readonly Transform[] tips = new Transform[5];
        private readonly LineRenderer[] fingers = new LineRenderer[5];
        private readonly TrailRenderer palmTrail;
        private readonly float mirror;

        public EnergyHand(string name, Color color, float handedness)
        {
            mirror = handedness;
            root = new GameObject(name).transform;
            Material material = GlowMaterial(name + " Material", color);
            palm = Sphere(root, "Luminous Palm", 0.052f, material);
            palmTrail = palm.gameObject.AddComponent<TrailRenderer>();
            palmTrail.sharedMaterial = material;
            palmTrail.time = 1.4f;
            palmTrail.startWidth = 0.034f;
            palmTrail.endWidth = 0.002f;
            palmTrail.minVertexDistance = 0.008f;
            palmTrail.emitting = true;
            for (int index = 0; index < tips.Length; index++)
            {
                tips[index] = Sphere(root, "Fingertip " + (index + 1), 0.018f, material);
                TrailRenderer trail = tips[index].gameObject.AddComponent<TrailRenderer>();
                trail.sharedMaterial = material;
                trail.time = 0.42f;
                trail.startWidth = 0.011f;
                trail.endWidth = 0f;
                trail.minVertexDistance = 0.006f;
                trail.emitting = true;
                GameObject lineObject = new GameObject("Energy Finger " + (index + 1));
                lineObject.transform.SetParent(root, false);
                LineRenderer line = lineObject.AddComponent<LineRenderer>();
                line.sharedMaterial = material;
                line.useWorldSpace = true;
                line.positionCount = 3;
                line.widthMultiplier = 0.009f;
                line.numCapVertices = 5;
                fingers[index] = line;
            }
        }

        public void Pose(Camera camera, Vector3 localPalm, float openness, float roll)
        {
            Vector3 palmWorld = camera.transform.TransformPoint(localPalm);
            palm.position = palmWorld;
            Vector3 right = camera.transform.right;
            Vector3 up = camera.transform.up;
            Vector3 forward = camera.transform.forward;
            for (int index = 0; index < tips.Length; index++)
            {
                float spread = (index - 2f) * 0.032f * Mathf.Lerp(0.55f, 1.45f, openness) * mirror;
                float length = index == 0 ? 0.075f : 0.105f + (2f - Mathf.Abs(index - 2f)) * 0.012f;
                Vector3 knuckle = palmWorld + right * spread * 0.55f + up * 0.028f + forward * 0.005f;
                Vector3 tip = palmWorld + right * spread + up * length +
                              forward * (Mathf.Sin(roll + index * 0.6f) * 0.012f);
                tips[index].position = tip;
                fingers[index].SetPosition(0, palmWorld);
                fingers[index].SetPosition(1, knuckle);
                fingers[index].SetPosition(2, tip);
            }
        }

        public void SetVisible(bool visible) => root.gameObject.SetActive(visible);

        private static Transform Sphere(Transform parent, string name, float diameter, Material material)
        {
            GameObject sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            sphere.name = name;
            sphere.transform.SetParent(parent, false);
            sphere.transform.localScale = Vector3.one * diameter;
            Object.Destroy(sphere.GetComponent<Collider>());
            sphere.GetComponent<Renderer>().sharedMaterial = material;
            return sphere.transform;
        }

        private static Material GlowMaterial(string name, Color color)
        {
            Shader shader = Shader.Find("Standard");
            var material = new Material(shader) { name = name, color = color };
            material.EnableKeyword("_EMISSION");
            material.SetColor("_EmissionColor", color * 2.2f);
            material.SetFloat("_Glossiness", 0.8f);
            return material;
        }
    }

    [SerializeField] private WayfinderVerticalSliceController flow;
    [SerializeField] private WayfinderScriptedReflectionController reflection;
    [SerializeField] private Transform openButton;
    [SerializeField] private Camera captureCamera;

    private EnergyHand leftHand;
    private EnergyHand rightHand;
    private float takeSeconds;
    private float triggerPollSeconds;
    private bool taking;
    private bool started;
    private int completedOrbits;
    private bool openingAnswered;
    private bool secondAnswered;

    public void Configure(
        WayfinderVerticalSliceController controller,
        WayfinderScriptedReflectionController reflectionController,
        Transform button,
        Camera camera)
    {
        flow = controller;
        reflection = reflectionController;
        openButton = button;
        captureCamera = camera;
    }

    private void Update()
    {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
        if (!taking)
        {
            triggerPollSeconds -= Time.unscaledDeltaTime;
            if (triggerPollSeconds > 0f && !Input.GetKeyDown(KeyCode.T)) return;
            triggerPollSeconds = 0.5f;
            string flag = Path.Combine(Application.persistentDataPath, "trailer.flag");
            if (!Input.GetKeyDown(KeyCode.T) && !File.Exists(flag)) return;
            if (File.Exists(flag)) File.Delete(flag);
            BeginTake();
        }
        RunTake(Time.unscaledDeltaTime);
#endif
    }

    private void BeginTake()
    {
        if (flow == null || captureCamera == null) return;
        flow.ResetDemo();
        leftHand = new EnergyHand("Trailer Left Hand • Teal", new Color(0.20f, 1f, 0.83f, 1f), -1f);
        rightHand = new EnergyHand("Trailer Right Hand • Gold", new Color(1f, 0.69f, 0.20f, 1f), 1f);
        takeSeconds = 0f;
        completedOrbits = 0;
        started = openingAnswered = secondAnswered = false;
        taking = true;
        Debug.Log("THE FLOW TRAILER TAKE STARTED");
    }

    private void RunTake(float deltaTime)
    {
        if (!taking || captureCamera == null) return;
        takeSeconds += deltaTime;

        if (takeSeconds < 3.6f)
        {
            float reach = Smooth01((takeSeconds - 0.6f) / 2.3f);
            Vector3 target = openButton != null
                ? captureCamera.transform.InverseTransformPoint(openButton.position)
                : new Vector3(0f, -0.25f, 0.72f);
            Vector3 right = Vector3.Lerp(new Vector3(0.48f, -0.46f, 0.42f), target, reach);
            rightHand.Pose(captureCamera, right, reach, takeSeconds);
            leftHand.Pose(captureCamera, new Vector3(-0.40f, -0.42f, 0.50f), 0.75f, takeSeconds);
            if (!started && takeSeconds >= 3.05f)
            {
                started = true;
                flow.DemoStartExperience();
            }
            return;
        }

        int orbit = takeSeconds < 7.2f ? 1 : takeSeconds < 13.6f ? 2 : takeSeconds < 20f ? 3 : 0;
        if (orbit > 0)
        {
            float start = orbit == 1 ? 3.6f : orbit == 2 ? 10f : 16.4f;
            float phase = Mathf.Clamp01((takeSeconds - start) / 3.05f);
            float angle = phase * Mathf.PI * 2f - Mathf.PI * 0.5f;
            Vector3 center = new Vector3(0f, -0.04f, 0.78f);
            Vector3 right = center + new Vector3(Mathf.Cos(angle) * 0.29f, Mathf.Sin(angle) * 0.29f, 0f);
            Vector3 left = center + new Vector3(Mathf.Cos(angle + Mathf.PI) * 0.22f,
                Mathf.Sin(angle + Mathf.PI) * 0.22f, 0.035f);
            rightHand.Pose(captureCamera, right, 0.72f, angle);
            leftHand.Pose(captureCamera, left, 0.90f, -angle);
        }
        else
        {
            float breath = Mathf.Sin(takeSeconds * 1.5f) * 0.025f;
            leftHand.Pose(captureCamera, new Vector3(-0.34f, -0.34f + breath, 0.57f), 1f, takeSeconds);
            rightHand.Pose(captureCamera, new Vector3(0.34f, -0.34f - breath, 0.57f), 1f, -takeSeconds);
        }

        if (completedOrbits < 1 && takeSeconds >= 6.72f) CompleteOrbit();
        if (completedOrbits < 2 && takeSeconds >= 13.12f) CompleteOrbit();
        if (completedOrbits < 3 && takeSeconds >= 19.52f) CompleteOrbit();

        if (!openingAnswered && takeSeconds >= 24.2f && reflection != null)
        {
            reflection.DemoSelectOpeningAnswer(1);
            openingAnswered = true;
        }
        if (!secondAnswered && takeSeconds >= 27.1f && reflection != null)
        {
            reflection.DemoSelectSecondAnswer(1);
            secondAnswered = true;
        }

        if (takeSeconds < 33.5f) return;
        leftHand.SetVisible(false);
        rightHand.SetVisible(false);
        taking = false;
        Debug.Log("THE FLOW TRAILER TAKE COMPLETE");
    }

    private void CompleteOrbit()
    {
        flow.DemoCompleteOrbit();
        completedOrbits++;
    }

    private static float Smooth01(float value)
    {
        value = Mathf.Clamp01(value);
        return value * value * (3f - 2f * value);
    }
}
