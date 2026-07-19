using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Wayfinder.Tests
{
    public sealed class WayfinderVerticalSliceTests
    {
        private sealed class RecordingRepository : IWayfinderMemoryRepository
        {
            public int SaveCount;
            public void Save(WayfinderMemoryRecord record) => SaveCount++;
        }

        [Test]
        public void PalmDwell_FiresOnceUntilPalmLeavesTarget()
        {
            var tracker = new WayfinderPalmDwellTracker(0.5f);
            var targets = new[] { Vector3.zero };

            Assert.That(tracker.Update(true, Vector3.zero, false, Vector3.zero, targets, 0.1f, 0.25f), Is.EqualTo(-1));
            Assert.That(tracker.Update(true, Vector3.zero, false, Vector3.zero, targets, 0.1f, 0.25f), Is.EqualTo(0));
            Assert.That(tracker.Update(true, Vector3.zero, false, Vector3.zero, targets, 0.1f, 2f), Is.EqualTo(-1),
                "A held palm must not select the same target twice.");

            tracker.Update(true, Vector3.one, false, Vector3.zero, targets, 0.1f, 0.1f);
            tracker.Update(true, Vector3.zero, false, Vector3.zero, targets, 0.1f, 0.25f);
            Assert.That(tracker.Update(true, Vector3.zero, false, Vector3.zero, targets, 0.1f, 0.25f), Is.EqualTo(0));
        }

        [Test]
        public void PalmDwell_ChoosesOnlyNearestTargetWhenBothHandsOverlapChoices()
        {
            var tracker = new WayfinderPalmDwellTracker(0.1f);
            var targets = new[] { Vector3.zero, Vector3.right };
            int selected = tracker.Update(
                true, new Vector3(0.01f, 0f, 0f),
                true, new Vector3(1.04f, 0f, 0f),
                targets, 0.1f, 0.1f);

            Assert.That(selected, Is.EqualTo(0), "A frame can emit at most one deterministic selection.");
        }

        [Test]
        public void IntegratedSession_UntrackedHandsNeverAdvanceProgress()
        {
            var world = new WayfinderPlaceholderWorldSlot();
            var reward = new WayfinderPlaceholderRewardSlot();
            var repository = new RecordingRepository();
            var session = new WayfinderVerticalSliceSession(
                world, reward, repository, FastConfig());

            WayfinderMemoryDecision decision = default;
            for (int index = 0; index < 20; index++)
            {
                decision = session.Tick(default, 1f);
            }

            Assert.That(session.State, Is.EqualTo(WayfinderMemoryKeeperState.AwaitingHands));
            Assert.That(session.Progress01, Is.Zero);
            Assert.That(decision.Action, Is.EqualTo(WayfinderMemoryAction.RequestRecenter));
            Assert.That(world.IsOpen, Is.False);
            Assert.That(reward.GrantCount, Is.Zero);
            Assert.That(repository.SaveCount, Is.Zero);
        }

        [Test]
        public void IntegratedSession_ResetIsCleanAfterRevealRewardAndSave()
        {
            var world = new WayfinderPlaceholderWorldSlot();
            var reward = new WayfinderPlaceholderRewardSlot();
            var repository = new RecordingRepository();
            var session = new WayfinderVerticalSliceSession(world, reward, repository, FastConfig());
            WayfinderHandMotionMetrics good = GoodMetrics();

            session.Tick(good, 0f);
            session.Tick(good, 0.1f);
            session.Tick(good, 0.1f);
            session.Tick(good, 0.1f);
            session.Tick(default, 0f);
            session.ChooseReflection(WayfinderReflectionChoice.StillWaterLantern);
            session.Tick(default, 0f);
            session.Tick(default, 0f);

            Assert.That(world.IsOpen, Is.True);
            Assert.That(reward.GrantCount, Is.EqualTo(1));
            Assert.That(repository.SaveCount, Is.EqualTo(1));
            Assert.That(session.State, Is.EqualTo(WayfinderMemoryKeeperState.MemorySaved));

            WayfinderMemoryDecision reset = session.Reset();
            Assert.That(reset.Action, Is.EqualTo(WayfinderMemoryAction.DemoReset));
            Assert.That(session.State, Is.EqualTo(WayfinderMemoryKeeperState.AwaitingHands));
            Assert.That(session.Progress01, Is.Zero);
            Assert.That(world.IsOpen, Is.False);
            Assert.That(reward.GrantCount, Is.Zero);
            Assert.That(repository.SaveCount, Is.EqualTo(1), "Reset must not erase the saved record.");
        }

        [Test]
        public void NormalFeedback_UsesRequiredPlainLanguageBands()
        {
            WayfinderHandMotionMetrics metrics = GoodMetrics();
            WayfinderMemoryDecision accepted = new WayfinderMemoryDecision(
                WayfinderMemoryKeeperState.Observing,
                WayfinderMemoryAction.ContinueMovement,
                WayfinderMemoryReasonCode.MovementInRange,
                0.5f);
            Assert.That(WayfinderVerticalSliceController.NormalFeedback(metrics, accepted),
                Is.EqualTo("PERFECT FLOW"));

            metrics.CombinedSpeedMps = 0.4f;
            accepted.ReasonCode = WayfinderMemoryReasonCode.MovementOutsideGoal;
            Assert.That(WayfinderVerticalSliceController.NormalFeedback(metrics, accepted),
                Is.EqualTo("A LITTLE SLOWER"));

            metrics.CombinedSpeedMps = 0.6f;
            Assert.That(WayfinderVerticalSliceController.NormalFeedback(metrics, accepted),
                Is.EqualTo("SLOW DOWN"));

            metrics.TrackingValid = false;
            Assert.That(WayfinderVerticalSliceController.NormalFeedback(metrics, accepted),
                Is.EqualTo("READY"));
        }

        [Test]
        public void DeveloperControls_AreHiddenUntilOptionsOrCompletion()
        {
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.Options, false, false, false), Is.False);
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.ToggleJudgeHud, false, false, false), Is.False);
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.DemoReset, false, false, false), Is.False);
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.ToggleJudgeHud, false, true, false), Is.True);
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.DemoReset, false, true, false), Is.True);
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.DemoReset, false, false, true), Is.True);
            Assert.That(WayfinderVerticalSliceController.IsTargetAvailable(
                WayfinderDwellTargetKind.Options, false, false, true), Is.True);
        }

        [Test]
        public void MainScene_HasLegibleVerticalSliceAndNamedFutureSlots()
        {
            var scene = EditorSceneManager.OpenScene(
                "Assets/Wayfinder/Scenes/WayfinderRiver.unity", OpenSceneMode.Single);
            WayfinderVerticalSliceController controller = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<WayfinderVerticalSliceController>(true))
                .Single();
            SerializedObject serialized = new SerializedObject(controller);
            foreach (string field in new[]
                     {
                         "riverController", "palmPoseSource", "worldLabsWorldSlot", "tripoRewardSlot",
                         "userFeedbackText", "judgeHudText", "judgeHudRoot", "completionText",
                         "analogSpeedometer", "circularPalmGuide", "introCardRoot",
                         "introCardText", "gameplayHudRoot", "introStartButton",
                         "stoneStoryRoot", "stoneStoryText", "peaceStateRoot", "peaceStateText"
                     })
            {
                Assert.That(serialized.FindProperty(field).objectReferenceValue, Is.Not.Null, field);
            }

            SerializedProperty targets = serialized.FindProperty("dwellTargets");
            Assert.That(targets.arraySize, Is.EqualTo(6));
            var kinds = new List<WayfinderDwellTargetKind>();
            for (int index = 0; index < targets.arraySize; index++)
            {
                var target = (WayfinderPalmDwellTarget)targets.GetArrayElementAtIndex(index).objectReferenceValue;
                kinds.Add(target.Kind);
            }
            Assert.That(kinds.Count(kind => kind == WayfinderDwellTargetKind.MemorySeed ||
                                           kind == WayfinderDwellTargetKind.PatienceStone ||
                                           kind == WayfinderDwellTargetKind.StillWaterLantern), Is.EqualTo(3));
            CollectionAssert.Contains(kinds, WayfinderDwellTargetKind.ToggleJudgeHud);
            CollectionAssert.Contains(kinds, WayfinderDwellTargetKind.DemoReset);
            CollectionAssert.Contains(kinds, WayfinderDwellTargetKind.Options);

            TextMesh userHud = (TextMesh)serialized.FindProperty("userFeedbackText").objectReferenceValue;
            TextMesh judgeHud = (TextMesh)serialized.FindProperty("judgeHudText").objectReferenceValue;
            Assert.That(userHud.fontSize, Is.GreaterThanOrEqualTo(60));
            Assert.That(userHud.characterSize, Is.GreaterThanOrEqualTo(0.007f));
            Assert.That(userHud.transform.IsChildOf(Camera.main.transform), Is.True,
                "Normal feedback must remain in the headset comfort view as the head turns.");
            Assert.That(judgeHud.fontSize, Is.GreaterThanOrEqualTo(50));
            Assert.That(judgeHud.characterSize, Is.GreaterThanOrEqualTo(0.016f));
            Assert.That(userHud.text, Does.Not.Contain("m/s"),
                "Normal mode must use the analog gauge instead of numeric speed telemetry.");
            Assert.That(judgeHud.text, Does.Contain("SPEED 0.000 m/s"));

            SerializedProperty circleConfig = serialized.FindProperty("circularGestureConfig");
            Assert.That(circleConfig.FindPropertyRelative("TargetDurationSeconds").floatValue,
                Is.EqualTo(4.5f).Within(0.001f));
            Assert.That(circleConfig.FindPropertyRelative("MinimumRadiusMeters").floatValue,
                Is.EqualTo(0.18f).Within(0.001f));
            Assert.That(circleConfig.FindPropertyRelative("MaximumRadiusMeters").floatValue,
                Is.EqualTo(0.42f).Within(0.001f));
            Assert.That(circleConfig.FindPropertyRelative("MinimumAngularCoverageDegrees").floatValue,
                Is.EqualTo(270f).Within(0.001f));

            string[] names = scene.GetRootGameObjects()
                .SelectMany(root => root.GetComponentsInChildren<Transform>(true))
                .Select(item => item.name).ToArray();
            CollectionAssert.Contains(names, "WorldLabs_Original_World_Slot_PLACEHOLDER");
            CollectionAssert.Contains(names, "Enchanted Bamboo Forest Sanctuary • PICO 300K");
            CollectionAssert.Contains(names, "Tripo_Reward_Slot_PLACEHOLDER");
            CollectionAssert.Contains(names, "Analog Speedometer • 0 REST 1 FLOW 2 RUSH");
            CollectionAssert.Contains(names, "Palm Circle Guide • Calm Analog Clock");
            CollectionAssert.Contains(names, "Palm-Attached Luminous Placement Pearl");
            CollectionAssert.Contains(names, "Persistent Teal Water-Ink Ribbon");
            CollectionAssert.Contains(names, "Persistent Warm-Gold Calligraphy Ribbon");
            CollectionAssert.Contains(names, "Wayfinder Opening Story Card");
            CollectionAssert.Contains(names, "Generated THE FLOW Three-Stone Logo");
            CollectionAssert.Contains(names, "OPEN THE GAME • Hand Touch Button");
            CollectionAssert.Contains(names, "Three Stone Story Chapter Card");
            CollectionAssert.Contains(names, "PEACE STATE • Unmistakable Completion Reveal");
            CollectionAssert.DoesNotContain(names, "Target Speed Indicator");
            CollectionAssert.DoesNotContain(names, "Calm Continuous Motion Progress Ring");
            CollectionAssert.DoesNotContain(names, "Gesture Rhythm Circle");
        }

        [Test]
        public void IntroCard_UsesCorrectProjectAndTeamNames_AndExplainsThreeCircles()
        {
            string copy = WayfinderVerticalSliceController.IntroCopy(0f, false);
            Assert.That(copy, Does.StartWith("THE FLOW"));
            Assert.That(copy, Does.Contain("BY PEACEMAKERS"));
            Assert.That(copy, Does.Contain("THREE SLOW CIRCLES"));
            Assert.That(copy, Does.Contain("EACH CALM ORBIT AWAKENS ONE STONE"));
            Assert.That(copy, Does.Contain("ENTER PEACE"));
            Assert.That(copy, Does.Contain("MEMORY WORLD"));
            Assert.That(copy, Does.Contain("TOUCH OPEN THE GAME"));
            Assert.That(copy, Does.Not.Contain("World Labs"));
            Assert.That(copy, Does.Not.Contain("API"));
        }

        [Test]
        public void OpeningButton_StartsOnTrackedHandTouch_WithoutDwellOrController()
        {
            Vector3 button = new Vector3(0f, 1f, 0.7f);
            Assert.That(WayfinderVerticalSliceController.IsIntroButtonTouched(
                true, button + Vector3.right * 0.08f,
                false, Vector3.zero, button, 0.16f), Is.True);
            Assert.That(WayfinderVerticalSliceController.IsIntroButtonTouched(
                true, button + Vector3.right * 0.25f,
                false, Vector3.zero, button, 0.16f), Is.False);
        }

        [Test]
        public void ThreeStoneStory_HasAnOrderedChapterForEveryStone()
        {
            Assert.That(WayfinderVerticalSliceController.StoneStory(1), Does.Contain("STONE I  •  LISTEN"));
            Assert.That(WayfinderVerticalSliceController.StoneStory(2), Does.Contain("STONE II  •  PATIENCE"));
            Assert.That(WayfinderVerticalSliceController.StoneStory(3), Does.Contain("STONE III  •  PEACE"));
        }

        [Test]
        public void ScriptedReflection_IsOfflineDeterministicAndMapsToRewardArtifacts()
        {
            Assert.That(WayfinderScriptedReflectionController.OpeningPrompt,
                Does.Contain("WHAT FEELS MOST PRESENT"));
            Assert.That(WayfinderScriptedReflectionController.SecondPrompt(0),
                Does.Contain("PATIENCE"));
            Assert.That(WayfinderScriptedReflectionController.ChoiceForSecondAnswer(0),
                Is.EqualTo(WayfinderReflectionChoice.PatienceStone));
            Assert.That(WayfinderScriptedReflectionController.ChoiceForSecondAnswer(1),
                Is.EqualTo(WayfinderReflectionChoice.MemorySeed));
            Assert.That(WayfinderScriptedReflectionController.ChoiceForSecondAnswer(2),
                Is.EqualTo(WayfinderReflectionChoice.StillWaterLantern));
        }

        [Test]
        public void AnalogSpeedometer_UsesUiOnlyDampingAndPhysicalSpeedBands()
        {
            var root = new GameObject("speedometer test");
            var needle = new GameObject("needle");
            needle.transform.SetParent(root.transform);
            WayfinderAnalogSpeedometer speedometer = root.AddComponent<WayfinderAnalogSpeedometer>();
            speedometer.Configure(needle.transform);
            try
            {
                speedometer.SetSpeed(0.55f, 0.10f, 0.35f, 0.55f, 0.05f);
                Assert.That(speedometer.DisplayedValue, Is.GreaterThan(0f).And.LessThan(2f),
                    "Approximately 0.3 seconds of UI damping must prevent an instant jump to RUSH.");
                for (int index = 0; index < 30; index++)
                {
                    speedometer.SetSpeed(0.55f, 0.10f, 0.35f, 0.55f, 0.05f);
                }
                Assert.That(speedometer.DisplayedValue, Is.EqualTo(2f).Within(0.02f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void HandVisualization_IsPresentationOnlyAndUsesInstalledXRHands()
        {
            string source = File.ReadAllText(
                Path.Combine(Application.dataPath, "Wayfinder/Scripts/WayfinderXRHandVisualizer.cs"));
            Assert.That(source, Does.Contain("UnityEngine.XR.Hands"));
            Assert.That(source, Does.Contain("XRHandJointID.Palm"));
            Assert.That(source, Does.Contain("Palm Interaction Orb"));
            Assert.That(source, Does.Contain("TrailRenderer"));
            Assert.That(source, Does.Not.Contain("WayfinderHandMotionEstimator"));
            Assert.That(source, Does.Not.Contain("WayfinderMemoryKeeperConfig"));
            Assert.That(source, Does.Not.Contain("Unity.XR.PXR"));
        }

        private static WayfinderMemoryKeeperConfig FastConfig()
        {
            return new WayfinderMemoryKeeperConfig
            {
                CalibrationSeconds = 0.1f,
                SuccessfulMovementSeconds = 0.2f
            };
        }

        private static WayfinderHandMotionMetrics GoodMetrics()
        {
            return new WayfinderHandMotionMetrics
            {
                TrackingValid = true,
                RightTracked = true,
                CombinedSpeedMps = 0.2f,
                PeakSpeedMps = 0.22f,
                Smoothness01 = 0.9f,
                Continuity01 = 0.9f,
                DistanceMeters = 0.4f,
                DurationSeconds = 2f,
                PathEfficiency01 = 0.9f,
                InInteractionVolume = true,
                IsIntendedDirection = true,
                Classification = WayfinderHandMotionClass.Desired
            };
        }
    }
}
