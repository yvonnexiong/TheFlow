using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Wayfinder.Tests
{
    public sealed class WayfinderMemoryKeeperTests
    {
        private sealed class RecordingRepository : IWayfinderMemoryRepository
        {
            public readonly List<WayfinderMemoryRecord> Records = new List<WayfinderMemoryRecord>();
            public void Save(WayfinderMemoryRecord record) => Records.Add(record);
        }

        private sealed class Fixture
        {
            public readonly WayfinderPlaceholderWorldSlot World = new WayfinderPlaceholderWorldSlot();
            public readonly WayfinderPlaceholderRewardSlot Reward = new WayfinderPlaceholderRewardSlot();
            public readonly RecordingRepository Repository = new RecordingRepository();
            public readonly WayfinderMemoryKeeperConfig Config;
            public readonly WayfinderMemoryKeeper Keeper;

            public Fixture()
            {
                Config = new WayfinderMemoryKeeperConfig
                {
                    CalibrationSeconds = 0.1f,
                    MinimumTrackingValidity01 = 0.65f,
                    DesiredSpeedMinMps = 0.10f,
                    DesiredSpeedMaxMps = 0.35f,
                    RushedSpeedMps = 0.55f,
                    MinimumSmoothness01 = 0.70f,
                    MinimumContinuity01 = 0.75f,
                    SuccessfulMovementSeconds = 0.2f,
                    RushedProgressReductionPerSecond = 0.20f,
                    SteadyRewardScoreBoundary = 0.60f,
                    LuminousRewardScoreBoundary = 0.82f
                };
                Keeper = new WayfinderMemoryKeeper(World, Reward, Repository, Config);
            }

            public void ReachObserving()
            {
                AssertDecision(Keeper.Step(Good(), 0f), WayfinderMemoryKeeperState.Calibrating,
                    WayfinderMemoryAction.BeginCalibration, WayfinderMemoryReasonCode.TrackingStable);
                AssertDecision(Keeper.Step(Good(), Config.CalibrationSeconds),
                    WayfinderMemoryKeeperState.Observing, WayfinderMemoryAction.BeginObservation,
                    WayfinderMemoryReasonCode.CalibrationComplete);
            }

            public void ReachReflecting(float targetScore = 0.9f)
            {
                ReachObserving();
                float smoothness = targetScore < 0.75f ? 0.70f : targetScore;
                float continuity = targetScore < 0.75f ? 0.75f : targetScore;
                float efficiency = 3f * targetScore - smoothness - continuity;
                WayfinderHandObservation observation = Good(
                    smoothness: smoothness, continuity: continuity, efficiency: efficiency);
                Keeper.Step(observation, 0.1f);
                AssertDecision(Keeper.Step(observation, 0.1f), WayfinderMemoryKeeperState.Unlocking,
                    WayfinderMemoryAction.OpenMemoryWorld, WayfinderMemoryReasonCode.SustainedSuccess);
                Assert.That(World.IsOpen, Is.True);
                Assert.That(World.OpenCount, Is.EqualTo(1));
                AssertDecision(Keeper.Step(default, 0f), WayfinderMemoryKeeperState.Reflecting,
                    WayfinderMemoryAction.RequestReflection, WayfinderMemoryReasonCode.WorldSlotOpened);
            }
        }

        [Test]
        public void HappyPath_VisitsEveryRequiredStateAndSeparatesRewardFromSave()
        {
            var f = new Fixture();
            var states = new List<WayfinderMemoryKeeperState> { f.Keeper.State };

            states.Add(f.Keeper.Step(Good(), 0f).State);
            states.Add(f.Keeper.Step(Good(), 0.1f).State);
            f.Keeper.Step(Rushed(), 0.01f);
            states.Add(f.Keeper.State);
            f.Keeper.Step(Good(), 0f);
            f.Keeper.Step(Good(), 0.1f);
            states.Add(f.Keeper.Step(Good(), 0.1f).State);
            WayfinderMemoryDecision reflecting = f.Keeper.Step(default, 0f);
            states.Add(reflecting.State);
            states.Add(f.Keeper.ChooseReflection(WayfinderReflectionChoice.MemorySeed).State);

            WayfinderMemoryDecision grant = f.Keeper.Step(default, 0f);
            AssertDecision(grant, WayfinderMemoryKeeperState.Rewarding,
                WayfinderMemoryAction.GrantReward, WayfinderMemoryReasonCode.SuccessfulParticipant);
            Assert.That(f.Reward.GrantCount, Is.EqualTo(1));
            Assert.That(f.Repository.Records, Is.Empty);

            WayfinderMemoryDecision save = f.Keeper.Step(default, 0f);
            states.Add(save.State);
            AssertDecision(save, WayfinderMemoryKeeperState.MemorySaved,
                WayfinderMemoryAction.SaveMemory, WayfinderMemoryReasonCode.AggregateRecordSaved);
            Assert.That(f.Repository.Records, Has.Count.EqualTo(1));
            CollectionAssert.AreEquivalent(Enum.GetValues(typeof(WayfinderMemoryKeeperState)), states);
        }

        [Test]
        public void LowTracking_AlwaysRequestsRecenteringAndReacquiresThroughCalibration()
        {
            var f = new Fixture();
            AssertDecision(f.Keeper.Step(Good(validity: 0.6499f), 0.1f),
                WayfinderMemoryKeeperState.AwaitingHands, WayfinderMemoryAction.RequestRecenter,
                WayfinderMemoryReasonCode.TrackingValidityLow);

            f.Keeper.Step(Good(validity: 0.65f), 0f);
            Assert.That(f.Keeper.State, Is.EqualTo(WayfinderMemoryKeeperState.Calibrating),
                "The tracking-validity boundary is inclusive.");
            AssertDecision(f.Keeper.Step(Good(tracked: false), 0.1f),
                WayfinderMemoryKeeperState.AwaitingHands, WayfinderMemoryAction.RequestRecenter,
                WayfinderMemoryReasonCode.TrackingValidityLow);

            f.ReachObserving();
            AssertDecision(f.Keeper.Step(Good(validity: 0.1f), 0.1f),
                WayfinderMemoryKeeperState.Coaching, WayfinderMemoryAction.RequestRecenter,
                WayfinderMemoryReasonCode.TrackingValidityLow);
            AssertDecision(f.Keeper.Step(Good(), 0f), WayfinderMemoryKeeperState.Observing,
                WayfinderMemoryAction.ResumeObservation, WayfinderMemoryReasonCode.TrackingStable);
        }

        [Test]
        public void RushedMovement_CuesSlowerAndGentlyReducesBoundedProgress()
        {
            var f = new Fixture();
            f.ReachObserving();
            f.Keeper.Step(Good(), 0.1f);
            Assert.That(f.Keeper.Progress01, Is.EqualTo(0.5f).Within(0.0001f));

            WayfinderMemoryDecision decision = f.Keeper.Step(Rushed(), 0.25f);
            AssertDecision(decision, WayfinderMemoryKeeperState.Coaching,
                WayfinderMemoryAction.CueSlower, WayfinderMemoryReasonCode.MovementRushed);
            Assert.That(f.Keeper.Progress01, Is.EqualTo(0.45f).Within(0.0001f));

            f.Keeper.Step(Good(), 0f);
            f.Keeper.Step(Rushed(), 100f);
            Assert.That(f.Keeper.Progress01, Is.Zero);
        }

        [Test]
        public void DesiredSpeedButUnevenMovement_CuesSoftenWithoutProgress()
        {
            var f = new Fixture();
            f.ReachObserving();
            WayfinderMemoryDecision decision = f.Keeper.Step(Good(smoothness: 0.6999f), 0.1f);
            AssertDecision(decision, WayfinderMemoryKeeperState.Coaching,
                WayfinderMemoryAction.CueSoftenAndSmooth, WayfinderMemoryReasonCode.MovementUneven);
            Assert.That(f.Keeper.Progress01, Is.Zero);
        }

        [TestCase(0.10f)]
        [TestCase(0.35f)]
        public void DesiredSpeedBoundaries_AreInclusive(float speed)
        {
            var f = new Fixture();
            f.ReachObserving();
            WayfinderMemoryDecision decision = f.Keeper.Step(Good(speed: speed), 0.1f);
            Assert.That(decision.ReasonCode, Is.EqualTo(WayfinderMemoryReasonCode.MovementInRange));
            Assert.That(f.Keeper.Progress01, Is.EqualTo(0.5f).Within(0.0001f));
        }

        [Test]
        public void SmoothnessAndContinuityBoundaries_AreInclusive()
        {
            var f = new Fixture();
            f.ReachObserving();
            WayfinderMemoryDecision atBoundary = f.Keeper.Step(
                Good(smoothness: 0.70f, continuity: 0.75f), 0.1f);
            Assert.That(atBoundary.ReasonCode, Is.EqualTo(WayfinderMemoryReasonCode.MovementInRange));
            Assert.That(f.Keeper.Progress01, Is.EqualTo(0.5f).Within(0.0001f));

            var belowContinuity = new Fixture();
            belowContinuity.ReachObserving();
            WayfinderMemoryDecision below = belowContinuity.Keeper.Step(
                Good(smoothness: 0.70f, continuity: 0.7499f), 0.1f);
            Assert.That(below.ReasonCode, Is.EqualTo(WayfinderMemoryReasonCode.MovementOutsideGoal));
            Assert.That(belowContinuity.Keeper.Progress01, Is.Zero);
        }

        [Test]
        public void RushedBoundary_IsStrictlyGreaterThanConfiguredThreshold()
        {
            var f = new Fixture();
            f.ReachObserving();
            WayfinderMemoryDecision atBoundary = f.Keeper.Step(Good(speed: 0.55f), 0.1f);
            Assert.That(atBoundary.Action, Is.EqualTo(WayfinderMemoryAction.ContinueMovement));
            Assert.That(atBoundary.ReasonCode, Is.EqualTo(WayfinderMemoryReasonCode.MovementOutsideGoal));

            WayfinderMemoryDecision aboveBoundary = f.Keeper.Step(Good(speed: 0.5501f), 0.1f);
            Assert.That(aboveBoundary.Action, Is.EqualTo(WayfinderMemoryAction.CueSlower));
        }

        [TestCase(WayfinderReflectionChoice.MemorySeed, "memory_seed")]
        [TestCase(WayfinderReflectionChoice.PatienceStone, "patience_stone")]
        [TestCase(WayfinderReflectionChoice.StillWaterLantern, "still_water_lantern")]
        public void ReflectionChoices_MapToWhitelistedArtifactIds(
            WayfinderReflectionChoice choice, string expectedArtifact)
        {
            Assert.That(WayfinderMemoryKeeper.ReflectionArtifactFor(choice), Is.EqualTo(expectedArtifact));

            var f = new Fixture();
            f.ReachReflecting();
            AssertDecision(f.Keeper.ChooseReflection(choice), WayfinderMemoryKeeperState.Rewarding,
                WayfinderMemoryAction.PrepareReward, WayfinderMemoryReasonCode.ReflectionSelected);
            f.Keeper.Step(default, 0f);
            Assert.That(f.Reward.LastArtifactId, Is.EqualTo(expectedArtifact));
        }

        [Test]
        public void UnknownReflectionChoice_IsRejectedWithoutArbitraryAction()
        {
            var f = new Fixture();
            f.ReachReflecting();
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                f.Keeper.ChooseReflection((WayfinderReflectionChoice)999));
            Assert.That(f.Keeper.State, Is.EqualTo(WayfinderMemoryKeeperState.Reflecting));
        }

        [TestCase(0f, WayfinderRewardVariant.Grounded)]
        [TestCase(0.5999f, WayfinderRewardVariant.Grounded)]
        [TestCase(0.60f, WayfinderRewardVariant.Steady)]
        [TestCase(0.8199f, WayfinderRewardVariant.Steady)]
        [TestCase(0.82f, WayfinderRewardVariant.Luminous)]
        [TestCase(1f, WayfinderRewardVariant.Luminous)]
        public void ScoreBandBoundaries_OnlySelectRewardVariant(
            float score, WayfinderRewardVariant expected)
        {
            Assert.That(WayfinderMemoryKeeper.GetRewardVariant(score), Is.EqualTo(expected));
        }

        [TestCase(0.59f, WayfinderRewardVariant.Grounded)]
        [TestCase(0.70f, WayfinderRewardVariant.Steady)]
        [TestCase(0.90f, WayfinderRewardVariant.Luminous)]
        public void EverySuccessfulScoreBand_GrantsExactlyOneReward(
            float scoreComponent, WayfinderRewardVariant expectedVariant)
        {
            var f = new Fixture();
            f.ReachReflecting(scoreComponent);
            f.Keeper.ChooseReflection(WayfinderReflectionChoice.PatienceStone);
            f.Keeper.Step(default, 0f);

            Assert.That(f.Reward.GrantCount, Is.EqualTo(1));
            Assert.That(f.Reward.LastArtifactId, Is.EqualTo("patience_stone"));
            Assert.That(f.Reward.LastVariant, Is.EqualTo(expectedVariant));
        }

        [Test]
        public void ActionsAreStrictEnumAndEveryEmittedActionHasReasonCode()
        {
            string[] expected =
            {
                "None", "RequestRecenter", "BeginCalibration", "BeginObservation",
                "ResumeObservation", "ContinueMovement", "CueSlower", "CueSoftenAndSmooth",
                "OpenMemoryWorld", "RequestReflection", "PrepareReward", "GrantReward",
                "SaveMemory", "DemoReset"
            };
            CollectionAssert.AreEqual(expected, Enum.GetNames(typeof(WayfinderMemoryAction)));

            var f = new Fixture();
            WayfinderMemoryDecision[] decisions =
            {
                f.Keeper.Step(Good(false), 0f),
                f.Keeper.Step(Good(), 0f),
                f.Keeper.Step(Good(), 0.1f),
                f.Keeper.Step(Good(), 0.1f),
                f.Keeper.Step(Good(), 0.1f),
                f.Keeper.Step(default, 0f),
                f.Keeper.ChooseReflection(WayfinderReflectionChoice.MemorySeed),
                f.Keeper.Step(default, 0f),
                f.Keeper.Step(default, 0f),
                f.Keeper.DemoReset()
            };
            foreach (WayfinderMemoryDecision decision in decisions)
            {
                if (decision.Action != WayfinderMemoryAction.None)
                {
                    Assert.That(decision.ReasonCode, Is.Not.EqualTo(WayfinderMemoryReasonCode.None));
                }
            }
        }

        [Test]
        public void AggregateJson_RoundTripsWhitelistedSchemaWithoutPrivateData()
        {
            WayfinderMemoryRecord record = SampleRecord("memory_seed", "Steady", 0.71f);
            string json = WayfinderMemoryRecordJson.Serialize(record, true);
            WayfinderMemoryRecord roundTrip = WayfinderMemoryRecordJson.Deserialize(json);

            Assert.That(roundTrip.reflectionArtifact, Is.EqualTo("memory_seed"));
            Assert.That(roundTrip.score01, Is.EqualTo(0.71f).Within(0.0001f));
            Assert.That(roundTrip.distanceMeters, Is.EqualTo(0.42f).Within(0.0001f));
            string lower = json.ToLowerInvariant();
            foreach (string forbidden in new[]
                     { "joint", "trajectory", "mesh", "room", "voice", "biometric", "diagnosis", "tai chi", "userid" })
            {
                Assert.That(lower, Does.Not.Contain(forbidden));
            }
        }

        [Test]
        public void LocalRepository_OverwritesOneBoundedOfflineJsonRecord()
        {
            string directory = Path.Combine(Path.GetTempPath(), "wayfinder-memory-tests-" + Guid.NewGuid().ToString("N"));
            try
            {
                var repository = new WayfinderLocalJsonMemoryRepository(directory);
                repository.Save(SampleRecord("memory_seed", "Grounded", 0.4f));
                repository.Save(SampleRecord("still_water_lantern", "Luminous", 0.9f));

                Assert.That(Directory.GetFiles(directory), Has.Length.EqualTo(1));
                WayfinderMemoryRecord saved = WayfinderMemoryRecordJson.Deserialize(
                    File.ReadAllText(repository.FilePath));
                Assert.That(saved.reflectionArtifact, Is.EqualTo("still_water_lantern"));
                Assert.That(saved.score01, Is.EqualTo(0.9f).Within(0.0001f));
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Test]
        public void DemoReset_ClearsSessionAndPlaceholdersButDoesNotDeleteSavedMemory()
        {
            var f = new Fixture();
            f.ReachReflecting();
            f.Keeper.ChooseReflection(WayfinderReflectionChoice.StillWaterLantern);
            f.Keeper.Step(default, 0f);
            f.Keeper.Step(default, 0f);
            Assert.That(f.Repository.Records, Has.Count.EqualTo(1));

            WayfinderMemoryDecision reset = f.Keeper.DemoReset();
            AssertDecision(reset, WayfinderMemoryKeeperState.AwaitingHands,
                WayfinderMemoryAction.DemoReset, WayfinderMemoryReasonCode.DemoResetRequested);
            Assert.That(f.Keeper.Progress01, Is.Zero);
            Assert.That(f.Keeper.LastRecord, Is.Null);
            Assert.That(f.World.IsOpen, Is.False);
            Assert.That(f.World.OpenCount, Is.Zero);
            Assert.That(f.Reward.GrantCount, Is.Zero);
            Assert.That(f.Repository.Records, Has.Count.EqualTo(1),
                "Demo reset must not silently delete a saved memory.");
        }

        [Test]
        public void InvalidDeltaAndAggregateNumbers_NeverCreateNaNOrUnboundedProgress()
        {
            var f = new Fixture();
            f.ReachObserving();
            WayfinderHandObservation observation = Good();
            observation.CombinedSpeedMps = float.NaN;
            observation.Smoothness01 = float.PositiveInfinity;
            observation.DistanceMeters = float.NegativeInfinity;

            foreach (float dt in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                WayfinderMemoryDecision decision = f.Keeper.Step(observation, dt);
                Assert.That(float.IsNaN(decision.Progress01), Is.False);
                Assert.That(float.IsInfinity(decision.Progress01), Is.False);
                Assert.That(decision.Progress01, Is.InRange(0f, 1f));
            }
        }

        private static WayfinderHandObservation Good(
            bool tracked = true,
            float validity = 1f,
            float speed = 0.2f,
            float smoothness = 0.9f,
            float continuity = 0.9f,
            float efficiency = 0.9f)
        {
            return new WayfinderHandObservation
            {
                TrackingValid = tracked,
                TrackingValidity01 = validity,
                CombinedSpeedMps = speed,
                PeakSpeedMps = speed,
                Smoothness01 = smoothness,
                Continuity01 = continuity,
                DistanceMeters = 0.42f,
                DurationSeconds = 2.1f,
                PathEfficiency01 = efficiency,
                HasSymmetry = true,
                Symmetry01 = 0.8f,
                InInteractionVolume = true,
                IsIntendedDirection = true,
                Classification = WayfinderHandMotionClass.Desired
            };
        }

        private static WayfinderHandObservation Rushed()
        {
            WayfinderHandObservation observation = Good(speed: 0.6f);
            observation.Classification = WayfinderHandMotionClass.Rushed;
            return observation;
        }

        private static WayfinderMemoryRecord SampleRecord(string artifact, string variant, float score)
        {
            return new WayfinderMemoryRecord
            {
                reflectionArtifact = artifact,
                rewardVariant = variant,
                score01 = score,
                averageSpeedMps = 0.2f,
                peakSpeedMps = 0.3f,
                smoothness01 = 0.8f,
                continuity01 = 0.9f,
                distanceMeters = 0.42f,
                durationSeconds = 2.1f,
                pathEfficiency01 = 0.75f,
                symmetryAvailable = true,
                symmetry01 = 0.8f
            };
        }

        private static void AssertDecision(
            WayfinderMemoryDecision actual,
            WayfinderMemoryKeeperState state,
            WayfinderMemoryAction action,
            WayfinderMemoryReasonCode reason)
        {
            Assert.That(actual.State, Is.EqualTo(state));
            Assert.That(actual.Action, Is.EqualTo(action));
            Assert.That(actual.ReasonCode, Is.EqualTo(reason));
        }
    }
}
