using System;
using System.IO;
using System.Reflection;
using System.Xml;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEditor.XR.Management;
using UnityEditor.XR.Management.Metadata;
using UnityEditor.XR.OpenXR.Features;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR.Management;
using UnityEngine.XR.Hands.OpenXR;
using UnityEngine.XR.OpenXR;
using UnityEngine.XR.OpenXR.Features;
using UnityEngine.XR.OpenXR.Features.Interactions;
using Unity.XR.PXR;
using Unity.XR.OpenXR.Features.PICOSupport;

public static class WayfinderPicoBuilder
{
    private const string ScenePath = "Assets/Wayfinder/Scenes/WayfinderRiver.unity";
    private const string ApkPath = "Builds/PICO/Wayfinder-PICO.apk";
    private const string ApplicationId = "com.wayfinder.patience";
    private const bool HighFrequencyHands = true;

    [MenuItem("Wayfinder/Configure PICO OpenXR")]
    public static void ConfigurePicoOpenXR()
    {
        if (!BuildPipeline.IsBuildTargetSupported(BuildTargetGroup.Android, BuildTarget.Android))
        {
            throw new InvalidOperationException("Android Build Support is not installed for this Unity editor.");
        }

        PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, ApplicationId);
        PlayerSettings.companyName = "Peacemakers";
        PlayerSettings.productName = "THE FLOW";
        PlayerSettings.SetScriptingBackend(NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel29;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.Android.forceInternetPermission = true;
        // The local sponsor gateway is reached through `adb reverse` during the hackathon demo.
        // It carries no secrets from the APK; production deployments should use HTTPS instead.
        PlayerSettings.insecureHttpOption = InsecureHttpOption.DevelopmentOnly;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan });

        XRGeneralSettings general = EnsureXrSettings();

        bool assigned = XRPackageMetadataStore.AssignLoader(
            general.Manager,
            "UnityEngine.XR.OpenXR.OpenXRLoader",
            BuildTargetGroup.Android);
        if (!assigned && !HasOpenXrLoader(general.Manager))
        {
            throw new InvalidOperationException("Could not assign the Unity OpenXR loader for Android.");
        }

        // Package installation can introduce new feature types after the OpenXR settings asset was
        // first created. Use OpenXR's public editor helper to materialize those feature sub-assets.
        FeatureHelpers.RefreshFeatures(BuildTargetGroup.Android);
        OpenXRSettings openXr = OpenXRSettings.GetSettingsForBuildTargetGroup(BuildTargetGroup.Android);
        if (openXr == null)
        {
            throw new InvalidOperationException("Android OpenXR settings were not created.");
        }

        int picoFeatures = 0;
        int handFeatures = 0;
        int enabledInteractionProfiles = 0;
        foreach (OpenXRFeature feature in openXr.GetFeatures<OpenXRFeature>())
        {
            string identity = (feature.GetType().FullName + " " + feature.name).ToLowerInvariant();
            bool isPicoCore = feature.GetType().Name == "PICOFeature";
            bool isHandTrackingSubsystem = feature is HandTracking;
            bool isHandInteractionProfile = feature is HandInteractionProfile;

            // Hands Only needs PICO's core build hook, XR Hands' XRHandSubsystem provider, and
            // a hand interaction profile. Controller profiles and optional PICO capabilities stay off.
            if (identity.Contains("pico"))
            {
                feature.enabled = isPicoCore;
                if (feature.enabled)
                {
                    picoFeatures++;
                }
            }
            else if (feature is OpenXRInteractionFeature)
            {
                feature.enabled = isHandInteractionProfile;
            }
            else if (isHandTrackingSubsystem)
            {
                feature.enabled = true;
            }

            if (feature.enabled && (isHandTrackingSubsystem || isHandInteractionProfile))
            {
                handFeatures++;
            }

            if (feature is OpenXRInteractionFeature && feature.enabled)
            {
                enabledInteractionProfiles++;
            }
        }

        if (picoFeatures == 0)
        {
            throw new InvalidOperationException("The required PICO Support OpenXR feature was not found.");
        }
        if (handFeatures != 2)
        {
            throw new InvalidOperationException(
                "Hands Only requires both XR Hands' Hand Tracking Subsystem and the Hand Interaction Profile.");
        }

        ConfigurePicoHandSettings();

        // The optional PICO Platform SDK preprocessor calls Trim() on this value. Normalize the
        // unset value so builds without a PICO platform app ID remain offline-safe and exception-free.
        PXR_PlatformSetting platform = PXR_PlatformSetting.Instance;
        platform.appID ??= string.Empty;

        general.InitManagerOnStart = true;
        EditorUtility.SetDirty(general);
        EditorUtility.SetDirty(general.Manager);
        EditorUtility.SetDirty(openXr);
        EditorUtility.SetDirty(platform);
        AssetDatabase.SaveAssets();

        Debug.Log("WAYFINDER PICO CONFIG PASSED: OpenXR + Vulkan + IL2CPP + ARM64; " +
                  $"PICO core features={picoFeatures}, hand features={handFeatures}, " +
                  $"controller profiles=0, interaction profiles={enabledInteractionProfiles}, " +
                  $"Hands Only, high-frequency hands={(HighFrequencyHands ? 1 : 0)}");
    }

    [MenuItem("Wayfinder/Build PICO APK")]
    public static void BuildPicoApk()
    {
        ConfigurePicoOpenXR();
        WayfinderSceneValidator.ValidateRiverScene();
        Directory.CreateDirectory(Path.GetDirectoryName(ApkPath));

        BuildReport report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = ApkPath,
            target = BuildTarget.Android,
            options = BuildOptions.Development
        });

        if (report.summary.result != BuildResult.Succeeded)
        {
            throw new InvalidOperationException(
                $"PICO APK build failed: {report.summary.result}, " +
                $"{report.summary.totalErrors} errors, {report.summary.totalWarnings} warnings.");
        }

        Debug.Log($"WAYFINDER PICO APK PASSED: {ApkPath} ({report.summary.totalSize / (1024f * 1024f):0.0} MB)");
    }

    private static bool HasOpenXrLoader(XRManagerSettings manager)
    {
        foreach (XRLoader loader in manager.activeLoaders)
        {
            if (loader != null && loader.GetType().FullName == "UnityEngine.XR.OpenXR.OpenXRLoader")
            {
                return true;
            }
        }
        return false;
    }

    private static void ConfigurePicoHandSettings()
    {
        PICOProjectSetting settings = PICOProjectSetting.GetProjectConfig();
        if (settings == null)
        {
            throw new InvalidOperationException("PICOProjectSetting could not be loaded or created.");
        }

        var serialized = new SerializedObject(settings);
        SetBoolean(serialized, "isHandTracking", true);
        SetBoolean(serialized, "highFrequencyHand", HighFrequencyHands);

        SerializedProperty support = serialized.FindProperty("handTrackingSupportType");
        if (support == null)
        {
            throw new InvalidOperationException("PICOProjectSetting.handTrackingSupportType is unavailable.");
        }
        support.enumValueIndex = (int)HandTrackingSupport.HandsOnly;
        serialized.ApplyModifiedPropertiesWithoutUndo();
        EditorUtility.SetDirty(settings);
    }

    private static void SetBoolean(SerializedObject serialized, string propertyName, bool value)
    {
        SerializedProperty property = serialized.FindProperty(propertyName);
        if (property == null)
        {
            throw new InvalidOperationException($"PICOProjectSetting.{propertyName} is unavailable.");
        }
        property.boolValue = value;
    }

    private static XRGeneralSettings EnsureXrSettings()
    {
        XRGeneralSettings general =
            XRGeneralSettingsPerBuildTarget.XRGeneralSettingsForBuildTarget(BuildTargetGroup.Android);
        if (general != null && general.Manager != null)
        {
            return general;
        }

        // XR Management exposes creation through its settings object, while the accessor for
        // that object is internal. Using the package's own GetOrCreate path keeps the generated
        // assets identical to those made by Project Settings and avoids checking in brittle YAML.
        MethodInfo getOrCreate = typeof(XRGeneralSettingsPerBuildTarget).GetMethod(
            "GetOrCreate",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (getOrCreate == null)
        {
            throw new InvalidOperationException("This XR Management version cannot create Android settings.");
        }

        XRGeneralSettingsPerBuildTarget perTarget =
            getOrCreate.Invoke(null, null) as XRGeneralSettingsPerBuildTarget;
        if (perTarget == null)
        {
            throw new InvalidOperationException("Failed to create XR Plug-in Management settings.");
        }

        if (!perTarget.HasManagerSettingsForBuildTarget(BuildTargetGroup.Android))
        {
            perTarget.CreateDefaultManagerSettingsForBuildTarget(BuildTargetGroup.Android);
            AssetDatabase.SaveAssets();
        }

        general = perTarget.SettingsForBuildTarget(BuildTargetGroup.Android);
        if (general == null || general.Manager == null)
        {
            throw new InvalidOperationException("XR Plug-in Management settings are unavailable for Android.");
        }

        return general;
    }
}

public sealed class WayfinderPicoManifestValidator : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 1000;

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src/main/AndroidManifest.xml");
        ValidateManifest(manifestPath);
        Debug.Log($"WAYFINDER PICO MANIFEST PASSED: {manifestPath}; hand tracking permission + Hands Only + 60Hz");
    }

    internal static void ValidateManifest(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new BuildFailedException($"Generated PICO Android manifest was not found: {manifestPath}");
        }

        var document = new XmlDocument();
        document.Load(manifestPath);
        var namespaces = new XmlNamespaceManager(document.NameTable);
        namespaces.AddNamespace("android", "http://schemas.android.com/apk/res/android");

        RequireNode(document, namespaces,
            "/manifest/uses-permission[@android:name='com.picovr.permission.HAND_TRACKING']",
            "com.picovr.permission.HAND_TRACKING permission");
        RequireNode(document, namespaces,
            "/manifest/application/meta-data[@android:name='handtracking' and @android:value='1']",
            "handtracking=1 metadata");
        RequireNode(document, namespaces,
            "/manifest/application/meta-data[@android:name='Hand_Tracking_HighFrequency' and @android:value='1']",
            "Hand_Tracking_HighFrequency=1 metadata");

        XmlNode controller = document.SelectSingleNode(
            "/manifest/application/meta-data[@android:name='controller']", namespaces);
        if (controller != null)
        {
            throw new BuildFailedException("Hands Only manifest must not contain controller metadata.");
        }
    }

    private static void RequireNode(
        XmlDocument document, XmlNamespaceManager namespaces, string xpath, string description)
    {
        if (document.SelectSingleNode(xpath, namespaces) == null)
        {
            throw new BuildFailedException($"Generated PICO Android manifest is missing {description}.");
        }
    }
}
