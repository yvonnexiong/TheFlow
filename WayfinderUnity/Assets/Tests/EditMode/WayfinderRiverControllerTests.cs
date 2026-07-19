using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Wayfinder.Tests
{
    public sealed class WayfinderRiverControllerTests
    {
        private const string ScenePath = "Assets/Wayfinder/Scenes/WayfinderRiver.unity";
        private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

        private readonly List<GameObject> createdObjects = new List<GameObject>();

        private static Type ControllerType
        {
            get
            {
                Type type = AppDomain.CurrentDomain.GetAssemblies()
                    .Select(assembly => assembly.GetType("WayfinderRiverController", false))
                    .FirstOrDefault(candidate => candidate != null);

                Assert.That(type, Is.Not.Null, "WayfinderRiverController must compile into a loaded assembly.");
                return type;
            }
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject createdObject in createdObjects.Where(item => item != null))
            {
                UnityEngine.Object.DestroyImmediate(createdObject);
            }

            createdObjects.Clear();
        }

        [Test]
        public void FreshController_StartsIdle_WithNoProgress_AndToleratesMissingOptionalReferences()
        {
            Component controller = CreateController(0, 3, false);

            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.Zero);
            Assert.That(GetProperty<int>(controller, "RequiredPushes"), Is.EqualTo(3));
            Assert.That(GetProperty<object>(controller, "State").ToString(), Is.EqualTo("Idle"));

            Assert.DoesNotThrow(() => Invoke(controller, "UpdateRiverVisuals", false));
            Assert.DoesNotThrow(() => Invoke(controller, "UpdateParticles", false));
            Assert.DoesNotThrow(() => Invoke(controller, "UpdateGate"));
            Assert.DoesNotThrow(() => Invoke(controller, "UpdateStones"));
            Assert.DoesNotThrow(() => Invoke(controller, "UpdateTrail", Vector3.zero, false));
        }

        [Test]
        public void PatientPushes_AdvanceExactlyOneStep_ClampAtRequiredCount_AndComplete()
        {
            Component controller = CreateController(3, 3, true);
            TextMesh guide = (TextMesh)GetField(controller, "guideTextMesh");

            Invoke(controller, "RegisterPatientPush");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(1));
            Assert.That(GetProperty<object>(controller, "State").ToString(), Is.EqualTo("Patience"));

            Invoke(controller, "RegisterPatientPush");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(2));

            Invoke(controller, "RegisterPatientPush");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(3));
            Assert.That(GetProperty<object>(controller, "State").ToString(), Is.EqualTo("Completion"));
            Assert.That(guide.text, Is.EqualTo("Patience opens the path."));

            Invoke(controller, "RegisterPatientPush");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(3));
        }

        [Test]
        public void Rush_RemovesOnlyOneEarnedStep_PerCooldownWindow()
        {
            Component controller = CreateController(3, 3, true);
            Invoke(controller, "RegisterPatientPush");
            Invoke(controller, "RegisterPatientPush");

            Invoke(controller, "EnterRush");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(1));
            Assert.That(GetProperty<object>(controller, "State").ToString(), Is.EqualTo("Rush"));
            Assert.That(GetField<float>(controller, "rushCooldown"), Is.GreaterThanOrEqualTo(1f));

            Invoke(controller, "EnterRush");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(1),
                "Continuous fast input must not remove multiple stones in one cooldown window.");
        }

        [Test]
        public void CircularCompletion_RaisesOneStone_AndCircularResistanceDoesNotDeleteIt()
        {
            Component controller = CreateController(3, 3, true);
            InvokePublic(controller, "RegisterCircularStone");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(1));

            InvokePublic(controller, "ShowCircularResistance");
            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.EqualTo(1));
            Assert.That(GetProperty<object>(controller, "State").ToString(), Is.EqualTo("Rush"));
        }

        [Test]
        public void ResetExperience_RestoresProgress_State_Copy_AndStoneTransforms()
        {
            Component controller = CreateController(2, 2, true);
            List<Transform> stones = GetField<List<Transform>>(controller, "stones");
            List<float> riseProgress = GetField<List<float>>(controller, "stoneRiseProgress");
            Dictionary<Transform, Vector3> starts = GetField<Dictionary<Transform, Vector3>>(controller, "stoneStarts");

            Invoke(controller, "RegisterPatientPush");
            Invoke(controller, "RegisterPatientPush");
            for (int index = 0; index < stones.Count; index++)
            {
                riseProgress[index] = 1f;
                stones[index].position += Vector3.up * 4f;
                stones[index].localScale = Vector3.one * 3f;
            }

            Invoke(controller, "ResetExperience");

            Assert.That(GetProperty<int>(controller, "SuccessfulPushes"), Is.Zero);
            Assert.That(GetProperty<object>(controller, "State").ToString(), Is.EqualTo("Idle"));
            Assert.That(GetField<bool>(controller, "completed"), Is.False);
            Assert.That(GetField<float>(controller, "rushCooldown"), Is.Zero);
            Assert.That(GetField<bool>(controller, "awaitingRelease"), Is.False);
            Assert.That(((TextMesh)GetField(controller, "guideTextMesh")).text,
                Is.EqualTo("Slow down. Watch the river.\nHold the mouse and push gently to the right."));

            for (int index = 0; index < stones.Count; index++)
            {
                Assert.That(riseProgress[index], Is.Zero);
                Assert.That(Vector3.Distance(stones[index].position, starts[stones[index]]), Is.LessThan(0.0001f));
                Assert.That(Vector3.Distance(stones[index].localScale, new Vector3(1.2f, 0.22f, 0.9f)),
                    Is.LessThan(0.0001f));
            }
        }

        [Test]
        public void InvalidRequiredPushCount_IsClampedToOne_AndToAvailableStones()
        {
            Component noStoneController = CreateController(0, 0, false);
            Assert.That(GetProperty<int>(noStoneController, "RequiredPushes"), Is.EqualTo(1));

            Component threeStoneController = CreateController(3, 99, false);
            Assert.That(GetProperty<int>(threeStoneController, "RequiredPushes"), Is.EqualTo(3));
        }

        [Test]
        public void InvalidZeroTimingAndDistance_DoNotCreateNaNOrInfinity()
        {
            Component controller = CreateController(0, 0, false);
            SetField(controller, "requiredPushSeconds", 0f);
            SetField(controller, "requiredPushDistance", 0f);
            SetField(controller, "stoneRiseSeconds", 0f);

            float flow = (float)Invoke(controller, "Flow01");

            Assert.That(float.IsNaN(flow), Is.False);
            Assert.That(float.IsInfinity(flow), Is.False);
            Assert.That(flow, Is.InRange(0f, 1f));
            Assert.DoesNotThrow(() => Invoke(controller, "UpdateStones"));
        }

        [Test]
        public void MainScene_HasRequiredReferences_ThreeAscendingSteps_AndSafeTuning()
        {
            var scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            Component controller = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Component>(true))
                .FirstOrDefault(component => component != null && component.GetType() == ControllerType);

            Assert.That(controller, Is.Not.Null, "Main scene must contain one WayfinderRiverController.");
            var serialized = new SerializedObject(controller);
            string[] requiredReferences =
            {
                "riverRenderer", "gateLight", "gateLeft", "gateRight", "guideTextMesh",
                "calmParticles", "turbulenceParticles", "gestureTrail"
            };

            foreach (string propertyName in requiredReferences)
            {
                SerializedProperty property = serialized.FindProperty(propertyName);
                Assert.That(property, Is.Not.Null, $"Missing serialized field: {propertyName}");
                Assert.That(property.objectReferenceValue, Is.Not.Null, $"Unassigned scene reference: {propertyName}");
            }

            SerializedProperty stones = serialized.FindProperty("stones");
            SerializedProperty flowLines = serialized.FindProperty("flowLines");
            Assert.That(stones.arraySize, Is.EqualTo(3));
            Assert.That(flowLines.arraySize, Is.GreaterThanOrEqualTo(4));
            Assert.That(serialized.FindProperty("requiredPushes").intValue, Is.EqualTo(stones.arraySize));

            float minimum = serialized.FindProperty("flowSpeedMin").floatValue;
            float maximum = serialized.FindProperty("flowSpeedMax").floatValue;
            float force = serialized.FindProperty("forceSpeedThreshold").floatValue;
            Assert.That(minimum, Is.GreaterThan(0f));
            Assert.That(maximum, Is.GreaterThan(minimum));
            Assert.That(force, Is.GreaterThan(maximum));
            Assert.That(serialized.FindProperty("requiredPushSeconds").floatValue, Is.GreaterThan(0f));
            Assert.That(serialized.FindProperty("requiredPushDistance").floatValue, Is.GreaterThan(0f));
            Assert.That(serialized.FindProperty("releaseSeconds").floatValue, Is.GreaterThan(0f));
            Assert.That(serialized.FindProperty("rushCooldownSeconds").floatValue, Is.GreaterThanOrEqualTo(1f));
            Assert.That(serialized.FindProperty("stoneRiseSeconds").floatValue, Is.GreaterThan(0f));
        }

        [Test]
        public void BuildSettings_EnableOnlyTheMainScene_AndDesktopInputFallbackIsConfigured()
        {
            EditorBuildSettingsScene[] enabledScenes = EditorBuildSettings.scenes.Where(scene => scene.enabled).ToArray();
            Assert.That(enabledScenes.Length, Is.EqualTo(1));
            Assert.That(enabledScenes[0].path, Is.EqualTo(ScenePath));
            Assert.That(AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath), Is.Not.Null);

            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string projectSettings = File.ReadAllText(Path.Combine(projectRoot, "ProjectSettings/ProjectSettings.asset"));
            Assert.That(
                projectSettings.Contains("activeInputHandler: 0") || projectSettings.Contains("activeInputHandler: 2"),
                Is.True,
                "Legacy input must be active because the desktop fallback uses UnityEngine.Input.");
        }

        [Test]
        public void OptionalPicoPackage_WhenPresent_HasItsRequiredCompileDependencies()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName;
            string manifest = File.ReadAllText(Path.Combine(projectRoot, "Packages/manifest.json"));
            if (!manifest.Contains("com.unity.xr.openxr.picoxr"))
            {
                Assert.Pass("PICO package is not installed; desktop fallback remains the release path.");
            }

            Assert.That(manifest, Does.Contain("com.unity.xr.openxr"),
                "The PICO OpenXR SDK requires Unity OpenXR.");
            Assert.That(manifest, Does.Contain("com.unity.ugui"),
                "PICO Spatial Mesh code references UnityEngine.UI and requires the uGUI package.");
            Assert.That(manifest, Does.Contain("\"com.unity.xr.hands\": \"1.4.0\""),
                "The selected XRHandSubsystem provider path is XR Hands 1.4.0.");
        }

        [Test]
        public void PicoOpenXrBuild_IsStaticallyConfiguredForHandsOnly()
        {
            string picoSettings = File.ReadAllText(
                Path.Combine(Application.dataPath, "Resources/PICOProjectSetting.asset"));
            string builder = File.ReadAllText(
                Path.Combine(Application.dataPath, "Editor/WayfinderPicoBuilder.cs"));

            Assert.That(picoSettings, Does.Contain("isHandTracking: 1"));
            Assert.That(picoSettings, Does.Contain("highFrequencyHand: 1"));
            Assert.That(picoSettings, Does.Contain("handTrackingSupportType: 1"),
                "Installed PICO OpenXR 1.4.0 defines HandsOnly as enum value 1.");
            Assert.That(builder, Does.Contain("feature is HandTracking"));
            Assert.That(builder, Does.Contain("feature is HandInteractionProfile"));
            Assert.That(builder, Does.Contain("feature.enabled = isPicoCore"));
            Assert.That(builder, Does.Not.Contain("No PICO OpenXR controller interaction profile was found"));
            Assert.That(builder, Does.Contain("Hands Only manifest must not contain controller metadata"));
        }

        [Test]
        public void DesktopFallback_HasNoApiKey_OrEnvironmentVariableDependency()
        {
            string runtimePath = Path.Combine(Application.dataPath, "Wayfinder/Scripts");
            string runtimeSource = string.Join("\n", Directory.GetFiles(runtimePath, "*.cs", SearchOption.AllDirectories)
                .Select(File.ReadAllText));

            Assert.That(runtimeSource, Does.Contain("Input.GetMouseButton(0)"),
                "A mouse-button fallback must remain available without XR hardware.");
            Assert.That(runtimeSource, Does.Contain("Input.mousePosition"));
            Assert.That(runtimeSource, Does.Not.Contain("GetEnvironmentVariable"));
            Assert.That(runtimeSource, Does.Not.Contain("API_KEY"));
            Assert.That(runtimeSource, Does.Not.Contain("SECRET_KEY"));
            Assert.That(runtimeSource, Does.Not.Contain("api.openai.com"));
            Assert.That(runtimeSource, Does.Not.Contain("api.worldlabs.ai"));
        }

        [Test]
        public void OptionalIntegrationDefaults_AreDisabled_Local_AndHaveNoSecretFields()
        {
            Type configType = FindType("WayfinderIntegrationConfig");
            object config = Activator.CreateInstance(configType);

            Assert.That(GetPublicField<bool>(config, "enableWorldLabs"), Is.False);
            Assert.That(GetPublicField<bool>(config, "enableReactor"), Is.False);
            Assert.That(GetPublicField<string>(config, "gatewayBaseUrl"), Does.StartWith("http://127.0.0.1"));
            Assert.That(GetPublicField<string>(config, "worldLabsWorldId"), Is.Empty);

            string[] fieldNames = configType.GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => field.Name.ToLowerInvariant())
                .ToArray();
            Assert.That(fieldNames.Any(name => name.Contains("key") || name.Contains("secret") || name.Contains("token")),
                Is.False,
                "Unity-side integration config must not accept or store sponsor API secrets.");
        }

        [Test]
        public void HybridInput_WithGenericXRDisabled_ReturnsDesktopFallbackFrame()
        {
            Type hybridType = FindType("WayfinderHybridInputSource");
            object hybrid = Activator.CreateInstance(hybridType, false, 850f);
            MethodInfo sample = hybridType.GetMethod("Sample", BindingFlags.Instance | BindingFlags.Public);
            Assert.That(sample, Is.Not.Null);

            object frame = sample.Invoke(hybrid, new object[] { 1f / 60f });
            Assert.That(GetPublicField<bool>(frame, "IsXR"), Is.False,
                "Disabling generic XR must deterministically retain the mouse demo path.");
        }

        [Test]
        public void InvalidIntegrationConfig_NormalizesBounds_AndFallsBackWithoutNetwork()
        {
            Type configType = FindType("WayfinderIntegrationConfig");
            object config = Activator.CreateInstance(configType);
            SetPublicField(config, "gatewayBaseUrl", "not a valid gateway///");
            SetPublicField(config, "worldLabsWorldId", null);
            SetPublicField(config, "requestTimeoutSeconds", -40);
            SetPublicField(config, "healthRetrySeconds", 5000);
            InvokePublic(config, "Normalize");

            Assert.That(GetPublicField<string>(config, "gatewayBaseUrl"), Is.EqualTo("not a valid gateway"));
            Assert.That(GetPublicField<string>(config, "worldLabsWorldId"), Is.Empty);
            Assert.That(GetPublicField<int>(config, "requestTimeoutSeconds"), Is.EqualTo(1));
            Assert.That(GetPublicField<int>(config, "healthRetrySeconds"), Is.EqualTo(120));
            Assert.That(GetPublicProperty<bool>(config, "HasGateway"), Is.False);

            var managerRoot = new GameObject("Integration manager offline test root");
            managerRoot.SetActive(false);
            createdObjects.Add(managerRoot);
            Component manager = managerRoot.AddComponent(FindType("WayfinderIntegrationManager"));
            var statusRoot = new GameObject("Integration status test text");
            statusRoot.transform.SetParent(managerRoot.transform);
            TextMesh status = statusRoot.AddComponent<TextMesh>();
            SetField(manager, "config", config);
            SetField(manager, "statusText", status);

            var check = (IEnumerator)Invoke(manager, "CheckGateway");
            Assert.That(check.MoveNext(), Is.False,
                "An invalid or missing gateway must exit before yielding a network request.");
            Assert.That(GetProperty<bool>(manager, "GatewayOnline"), Is.False);
            Assert.That(status.text, Does.Contain("OFFLINE SAFE"));
        }

        private Component CreateController(int stoneCount, int requiredPushes, bool includeGuide)
        {
            var root = new GameObject("Wayfinder controller test root");
            root.SetActive(false);
            createdObjects.Add(root);

            Component controller = root.AddComponent(ControllerType);
            var stones = new List<Transform>();
            for (int index = 0; index < stoneCount; index++)
            {
                var stone = new GameObject($"Test stone {index + 1}");
                stone.transform.SetParent(root.transform);
                stone.transform.position = new Vector3(index, -0.72f, index);
                stone.transform.localScale = new Vector3(1.2f, 0.22f, 0.9f);
                stones.Add(stone.transform);
            }

            SetField(controller, "stones", stones);
            SetField(controller, "flowLines", new List<LineRenderer>());
            SetField(controller, "requiredPushes", requiredPushes);

            if (includeGuide)
            {
                var guideObject = new GameObject("Test guide");
                guideObject.transform.SetParent(root.transform);
                TextMesh guide = guideObject.AddComponent<TextMesh>();
                SetField(controller, "guideText", guideObject.transform);
                SetField(controller, "guideTextMesh", guide);
            }

            Invoke(controller, "Awake");
            return controller;
        }

        private static object Invoke(Component target, string methodName, params object[] arguments)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, PrivateInstance);
            Assert.That(method, Is.Not.Null, $"Controller method changed or disappeared: {methodName}");

            try
            {
                return method.Invoke(target, arguments);
            }
            catch (TargetInvocationException exception)
            {
                throw exception.InnerException ?? exception;
            }
        }

        private static void SetField(Component target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Controller field changed or disappeared: {fieldName}");
            field.SetValue(target, value);
        }

        private static object GetField(Component target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, PrivateInstance);
            Assert.That(field, Is.Not.Null, $"Controller field changed or disappeared: {fieldName}");
            return field.GetValue(target);
        }

        private static T GetField<T>(Component target, string fieldName)
        {
            return (T)GetField(target, fieldName);
        }

        private static T GetProperty<T>(Component target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, $"Controller property changed or disappeared: {propertyName}");
            return (T)property.GetValue(target);
        }

        private static Type FindType(string typeName)
        {
            Type type = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType(typeName, false))
                .FirstOrDefault(candidate => candidate != null);
            Assert.That(type, Is.Not.Null, $"Required runtime type must compile: {typeName}");
            return type;
        }

        private static void InvokePublic(object target, string methodName)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, $"Public method changed or disappeared: {methodName}");
            method.Invoke(target, null);
        }

        private static void SetPublicField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, $"Public config field changed or disappeared: {fieldName}");
            field.SetValue(target, value);
        }

        private static T GetPublicField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(field, Is.Not.Null, $"Public config field changed or disappeared: {fieldName}");
            return (T)field.GetValue(target);
        }

        private static T GetPublicProperty<T>(object target, string propertyName)
        {
            PropertyInfo property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            Assert.That(property, Is.Not.Null, $"Public config property changed or disappeared: {propertyName}");
            return (T)property.GetValue(target);
        }
    }
}
