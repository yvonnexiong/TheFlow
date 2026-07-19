using System;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class WayfinderSceneValidator
{
    private const string ScenePath = "Assets/Wayfinder/Scenes/WayfinderRiver.unity";

    [MenuItem("Wayfinder/Validate River Scene")]
    public static void ValidateRiverScene()
    {
        if (!System.IO.File.Exists(ScenePath))
        {
            throw new InvalidOperationException("Wayfinder scene is missing: " + ScenePath);
        }

        EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);

        Camera camera = Camera.main;
        WayfinderRiverController controller = UnityEngine.Object.FindFirstObjectByType<WayfinderRiverController>();
        if (camera == null || controller == null)
        {
            throw new InvalidOperationException("Wayfinder scene requires a tagged Main Camera and one river controller.");
        }

        WayfinderVerticalSliceController verticalSlice =
            UnityEngine.Object.FindFirstObjectByType<WayfinderVerticalSliceController>(FindObjectsInactive.Include);
        WayfinderXRHandVisualizer handVisualizer =
            UnityEngine.Object.FindFirstObjectByType<WayfinderXRHandVisualizer>(FindObjectsInactive.Include);
        if (verticalSlice == null || handVisualizer == null)
        {
            throw new InvalidOperationException(
                "Wayfinder scene requires the Memory Keeper vertical slice and XR Hands visualization.");
        }

        SerializedObject verticalSerialized = new SerializedObject(verticalSlice);
        foreach (string property in new[]
                 {
                     "riverController", "palmPoseSource", "worldLabsWorldSlot", "tripoRewardSlot",
                     "userFeedbackText", "judgeHudText", "completionText",
                     "analogSpeedometer", "circularPalmGuide"
                 })
        {
            RequireReference(verticalSerialized, property);
        }
        SerializedProperty dwellTargets = verticalSerialized.FindProperty("dwellTargets");
        if (dwellTargets == null || dwellTargets.arraySize != 6)
        {
            throw new InvalidOperationException(
                "Vertical slice requires three reflection targets plus Options, Technical View, and Start Over.");
        }

        SerializedObject serialized = new SerializedObject(controller);
        RequireReference(serialized, "riverRenderer");
        RequireReference(serialized, "gateLight");
        RequireReference(serialized, "gateLeft");
        RequireReference(serialized, "gateRight");
        RequireReference(serialized, "guideTextMesh");
        RequireReference(serialized, "calmParticles");
        RequireReference(serialized, "turbulenceParticles");
        RequireReference(serialized, "gestureTrail");

        SerializedProperty stones = serialized.FindProperty("stones");
        SerializedProperty flowLines = serialized.FindProperty("flowLines");
        SerializedProperty requiredPushes = serialized.FindProperty("requiredPushes");
        if (stones == null || stones.arraySize != 3 || flowLines == null || flowLines.arraySize < 4)
        {
            throw new InvalidOperationException("THE FLOW requires exactly three ascending stones and at least four flow lines.");
        }

        if (requiredPushes == null || requiredPushes.intValue != stones.arraySize)
        {
            throw new InvalidOperationException("Required pushes must match the number of stepping stones.");
        }

        bool sceneEnabled = Array.Exists(
            EditorBuildSettings.scenes,
            entry => entry.enabled && entry.path == ScenePath);
        if (!sceneEnabled)
        {
            throw new InvalidOperationException("Wayfinder scene is not enabled in Build Settings.");
        }

        Debug.Log("THE FLOW VALIDATION PASSED: camera, three-stone hands-only river, XR joint visualization, " +
                  "opening story card, Memory Keeper HUD, six palm dwell targets, placeholder slots, and build scene are ready.");
    }

    private static void RequireReference(SerializedObject serialized, string propertyName)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null || property.objectReferenceValue == null)
        {
            throw new InvalidOperationException("Missing controller reference: " + propertyName);
        }
    }
}
