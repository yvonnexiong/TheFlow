using NUnit.Framework;
using UnityEngine;

namespace Wayfinder.Tests
{
    public sealed class WayfinderHandMotionTests
    {
        [Test]
        public void ConstantKnownVelocity_ProducesMetersAndSecondsMetrics()
        {
            var estimator = NewEstimator(true);
            const float velocity = 0.20f;
            const float dt = 0.01f;
            estimator.Update(Frame(0f), dt);

            WayfinderHandMotionMetrics metrics = default;
            for (int index = 1; index <= 100; index++)
            {
                metrics = estimator.Update(Frame(velocity * dt * index), dt);
            }

            Assert.That(metrics.RightSpeedMps, Is.EqualTo(velocity).Within(0.002f));
            Assert.That(metrics.CombinedSpeedMps, Is.EqualTo(velocity).Within(0.002f));
            Assert.That(metrics.PeakSpeedMps, Is.EqualTo(velocity).Within(0.002f));
            Assert.That(metrics.DistanceMeters, Is.EqualTo(0.20f).Within(0.004f));
            Assert.That(metrics.DurationSeconds, Is.EqualTo(1.01f).Within(0.001f));
            Assert.That(metrics.PathEfficiency01, Is.GreaterThan(0.995f));
            Assert.That(metrics.Smoothness01, Is.GreaterThan(0.99f));
        }

        [Test]
        public void VariableFrameTimes_PreserveKnownVelocity()
        {
            var estimator = NewEstimator(true);
            float[] frameTimes = { 0.008f, 0.021f, 0.013f, 0.033f, 0.011f, 0.017f, 0.026f, 0.009f };
            const float velocity = 0.30f;
            float x = 0f;
            estimator.Update(Frame(x), 0.01f);

            WayfinderHandMotionMetrics metrics = default;
            for (int cycle = 0; cycle < 12; cycle++)
            {
                foreach (float dt in frameTimes)
                {
                    x += velocity * dt;
                    metrics = estimator.Update(Frame(x), dt);
                }
            }

            Assert.That(metrics.CombinedSpeedMps, Is.EqualTo(velocity).Within(0.003f));
            Assert.That(metrics.Classification, Is.EqualTo(WayfinderHandMotionClass.Desired));
        }

        [Test]
        public void StationaryJitter_IsSuppressedWithoutDistanceDrift()
        {
            var estimator = NewEstimator(true);
            estimator.Update(Frame(0f), 0.01f);
            WayfinderHandMotionMetrics metrics = default;

            for (int index = 0; index < 240; index++)
            {
                float jitter = index % 2 == 0 ? 0.00015f : -0.00015f;
                metrics = estimator.Update(Frame(jitter), 0.01f);
            }

            Assert.That(metrics.CombinedSpeedMps, Is.LessThan(0.01f));
            Assert.That(metrics.Classification, Is.EqualTo(WayfinderHandMotionClass.Still));
            Assert.That(metrics.DistanceMeters, Is.LessThan(0.01f));
        }

        [Test]
        public void HeadAndHandsTranslateTogether_KeepHeadRelativeDirectionStill_ButRetainPhysicalSpeed()
        {
            var estimator = NewEstimator(true);
            const float dt = 0.01f;
            estimator.Update(Frame(0f, headX: 0f), dt);
            WayfinderHandMotionMetrics metrics = default;

            for (int index = 1; index <= 50; index++)
            {
                float translation = 0.20f * dt * index;
                metrics = estimator.Update(Frame(translation, headX: translation), dt);
            }

            Assert.That(metrics.CombinedSpeedMps, Is.EqualTo(0.20f).Within(0.002f),
                "Physical speed must be calculated in tracking-origin space.");
            Assert.That(metrics.HeadRelativeDelta.magnitude, Is.LessThan(0.00001f));
            Assert.That(metrics.IsIntendedDirection, Is.False,
                "Whole-body/head translation is not a head-relative hand push.");
            Assert.That(metrics.InInteractionVolume, Is.True);
        }

        [Test]
        public void TrackingLossAndReacquisition_ResetVelocityBaselineWithoutSpike()
        {
            var estimator = NewEstimator(true);
            estimator.Update(Frame(0f), 0.01f);
            estimator.Update(Frame(0.002f), 0.01f);
            WayfinderHandMotionMetrics lost = estimator.Update(Frame(0f, rightTracked: false), 0.02f);
            WayfinderHandMotionMetrics reacquired = estimator.Update(Frame(1.0f), 0.01f);
            WayfinderHandMotionMetrics resumed = estimator.Update(Frame(1.002f), 0.01f);

            Assert.That(lost.TrackingValid, Is.False);
            Assert.That(reacquired.TrackingValid, Is.True);
            Assert.That(reacquired.CombinedSpeedMps, Is.Zero);
            Assert.That(resumed.CombinedSpeedMps, Is.EqualTo(0.20f).Within(0.002f));
            Assert.That(resumed.PeakSpeedMps, Is.LessThan(0.25f));
            Assert.That(resumed.Continuity01, Is.LessThan(1f));
        }

        [Test]
        public void OneHandAndTwoHandModes_ApplyIndependentTrackingValidity()
        {
            WayfinderHandProviderFrame rightOnly = Frame(0f);
            var oneHand = NewEstimator(true);
            var twoHand = NewEstimator(false);

            Assert.That(oneHand.Update(rightOnly, 0.01f).TrackingValid, Is.True);
            Assert.That(twoHand.Update(rightOnly, 0.01f).TrackingValid, Is.False);

            WayfinderHandProviderFrame both = Frame(0f, leftX: -0.2f, leftTracked: true);
            Assert.That(twoHand.Update(both, 0.01f).TrackingValid, Is.True);
        }

        [Test]
        public void TwoOppositeMovingHands_CombineWithRmsAndDoNotCancel()
        {
            var estimator = NewEstimator(false);
            const float dt = 0.01f;
            estimator.Update(Frame(0.2f, leftX: -0.2f, leftTracked: true), dt);
            WayfinderHandMotionMetrics metrics = default;
            for (int index = 1; index <= 30; index++)
            {
                metrics = estimator.Update(
                    Frame(0.2f + 0.2f * dt * index,
                        leftX: -0.2f - 0.2f * dt * index,
                        leftTracked: true), dt);
            }

            Assert.That(metrics.LeftSpeedMps, Is.EqualTo(0.20f).Within(0.002f));
            Assert.That(metrics.RightSpeedMps, Is.EqualTo(0.20f).Within(0.002f));
            Assert.That(metrics.CombinedSpeedMps, Is.EqualTo(0.20f).Within(0.002f));
            Assert.That(metrics.HasSymmetry, Is.True);
            Assert.That(metrics.Symmetry01, Is.GreaterThan(0.99f));
        }

        [Test]
        public void DesiredAndRushedSpeeds_UseConfigurableClassificationThresholds()
        {
            var desiredEstimator = NewEstimator(true);
            desiredEstimator.Update(Frame(0f), 0.01f);
            WayfinderHandMotionMetrics desired = desiredEstimator.Update(Frame(0.002f), 0.01f);

            var rushedEstimator = NewEstimator(true);
            rushedEstimator.Update(Frame(0f), 0.01f);
            WayfinderHandMotionMetrics rushed = rushedEstimator.Update(Frame(0.007f), 0.01f);

            Assert.That(desired.Classification, Is.EqualTo(WayfinderHandMotionClass.Desired));
            Assert.That(rushed.Classification, Is.EqualTo(WayfinderHandMotionClass.Rushed));
        }

        [Test]
        public void ZeroNaNAndInfiniteDeltaTime_NeverProduceNonFiniteMetrics()
        {
            var provider = new WayfinderScriptedHandProvider(new[]
            {
                Frame(0f), Frame(0.1f), Frame(0.2f), Frame(0.3f)
            });
            var estimator = NewEstimator(true);
            float[] invalidTimes = { 0f, float.NaN, float.PositiveInfinity, -0.1f };

            foreach (float dt in invalidTimes)
            {
                WayfinderHandMotionMetrics metrics = estimator.Update(provider.Sample(), dt);
                AssertFinite(metrics.LeftSpeedMps);
                AssertFinite(metrics.RightSpeedMps);
                AssertFinite(metrics.CombinedSpeedMps);
                AssertFinite(metrics.PeakSpeedMps);
                AssertFinite(metrics.Smoothness01);
                AssertFinite(metrics.Continuity01);
                AssertFinite(metrics.DistanceMeters);
                AssertFinite(metrics.DurationSeconds);
                AssertFinite(metrics.PathEfficiency01);
                AssertFinite(metrics.Symmetry01);
            }
        }

        private static WayfinderHandMotionEstimator NewEstimator(bool allowOneHand)
        {
            return new WayfinderHandMotionEstimator(new WayfinderHandMotionConfig
            {
                AllowOneHand = allowOneHand,
                VelocitySmoothingSeconds = 0.04f,
                StationaryNoiseFloorMps = 0.025f,
                DesiredSpeedMinMps = 0.10f,
                DesiredSpeedMaxMps = 0.35f,
                RushedSpeedMps = 0.55f
            });
        }

        private static WayfinderHandProviderFrame Frame(
            float rightX,
            float headX = 0f,
            bool rightTracked = true,
            float leftX = 0f,
            bool leftTracked = false)
        {
            return new WayfinderHandProviderFrame
            {
                Left = new WayfinderTrackedHandPose(
                    leftTracked, new Pose(new Vector3(leftX, -0.15f, 0.55f), Quaternion.identity)),
                Right = new WayfinderTrackedHandPose(
                    rightTracked, new Pose(new Vector3(rightX, -0.15f, 0.55f), Quaternion.identity)),
                IsHeadTracked = true,
                HeadTrackingOriginPose = new Pose(new Vector3(headX, 0f, 0f), Quaternion.identity)
            };
        }

        private static void AssertFinite(float value)
        {
            Assert.That(float.IsNaN(value), Is.False);
            Assert.That(float.IsInfinity(value), Is.False);
        }
    }
}
