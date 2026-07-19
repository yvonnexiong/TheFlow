using System;
using System.IO;
using System.Reflection;
using GaussianSplatting.Editor;
using GaussianSplatting.Runtime;
using UnityEditor;
using UnityEngine;

public static class WayfinderSplatImporter
{
    public const string OutputFolder = "Assets/Wayfinder/Splats";
    public const string AssetPath = OutputFolder + "/theflow-bamboo-pico-300k.asset";
    public const string CelestialAssetPath = OutputFolder + "/theflow-celestial-pico-250k.asset";

    public static void ImportPicoReward()
    {
        string input = CommandLineValue("-wayfinderSplatInput");
        if (string.IsNullOrWhiteSpace(input))
            input = "/private/tmp/theflow-bamboo-pico-300k.ply";
        input = Path.GetFullPath(input);
        if (!File.Exists(input))
            throw new FileNotFoundException("PICO reward splat source was not found", input);

        Directory.CreateDirectory(OutputFolder);
        GaussianSplatAssetCreator creator = ScriptableObject.CreateInstance<GaussianSplatAssetCreator>();
        try
        {
            Type type = typeof(GaussianSplatAssetCreator);
            Set(type, creator, "m_InputFile", input);
            Set(type, creator, "m_ImportCameras", false);
            Set(type, creator, "m_OutputFolder", OutputFolder);

            FieldInfo qualityField = type.GetField("m_Quality", BindingFlags.Instance | BindingFlags.NonPublic);
            if (qualityField == null) throw new MissingFieldException(type.FullName, "m_Quality");
            // Medium uses normalized color data and avoids the SH clustering
            // passes used by Low/Very Low. This source has no SH bands.
            qualityField.SetValue(creator, Enum.ToObject(qualityField.FieldType, 2));

            Invoke(type, creator, "ApplyQualityLevel");
            Invoke(type, creator, "CreateAsset");
            string error = Get<string>(type, creator, "m_ErrorMessage");
            if (!string.IsNullOrWhiteSpace(error))
                throw new InvalidOperationException("Gaussian splat import failed: " + error);
        }
        finally
        {
            UnityEngine.Object.DestroyImmediate(creator);
            EditorUtility.ClearProgressBar();
        }

        string generatedAssetPath = OutputFolder + "/" + Path.GetFileNameWithoutExtension(input) + ".asset";
        int expectedCount = 300000;
        string countArgument = CommandLineValue("-wayfinderSplatCount");
        if (!string.IsNullOrWhiteSpace(countArgument) && !int.TryParse(countArgument, out expectedCount))
            throw new ArgumentException("-wayfinderSplatCount must be an integer.");
        GaussianSplatAsset asset = AssetDatabase.LoadAssetAtPath<GaussianSplatAsset>(generatedAssetPath);
        if (asset == null || asset.splatCount != expectedCount)
            throw new InvalidOperationException(
                $"Expected a {expectedCount:N0}-splat reward asset at {generatedAssetPath}");
        Debug.Log($"THE FLOW: imported PICO reward splat ({asset.splatCount:N0} splats) at {generatedAssetPath}");
    }

    private static string CommandLineValue(string key)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int index = 0; index + 1 < args.Length; index++)
            if (string.Equals(args[index], key, StringComparison.Ordinal)) return args[index + 1];
        return null;
    }

    private static void Set(Type type, object target, string name, object value)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        field.SetValue(target, value);
    }

    private static T Get<T>(Type type, object target, string name)
    {
        FieldInfo field = type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (field == null) throw new MissingFieldException(type.FullName, name);
        return (T)field.GetValue(target);
    }

    private static void Invoke(Type type, object target, string name)
    {
        MethodInfo method = type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null) throw new MissingMethodException(type.FullName, name);
        method.Invoke(target, null);
    }
}
