using NUnit.Framework;
using UnityEngine;

namespace Wayfinder.Tests
{
    public sealed class WayfinderCircularGestureTests
    {
        private static readonly Pose Head = new Pose(Vector3.zero, Quaternion.identity);

        [Test]
        public void SteadyTrackedPalm_AutomaticallyAttachesPearlWithoutInput()
        {
            var tracker = NewTracker();
            Pose palm = Palm(new Vector3(0.12f, -0.08f, 0.55f));
            WayfinderCircularGestureResult before = tracker.Update(
                true, palm, Head, WayfinderHandMotionClass.Still, 0.04f);
            WayfinderCircularGestureResult attached = tracker.Update(
                true, palm, Head, WayfinderHandMotionClass.Still, 0.07f);
            Assert.That(before.PearlAttached, Is.False);
            Assert.That(attached.PearlAttached, Is.True);
            Assert.That(attached.Stage, Is.EqualTo(WayfinderCircularTutorialStage.PlaceYourCircle));
        }

        [Test]
        public void AttachedPearl_FollowsUserSelectedPalmPlacement()
        {
            var tracker = NewTracker();
            Pose first = Palm(new Vector3(-0.18f, -0.12f, 0.54f));
            tracker.Update(true, first, Head, WayfinderHandMotionClass.Still, 0.10f);
            Pose selected = Palm(new Vector3(0.24f, 0.16f, 0.64f));
            tracker.Update(true, selected, Head, WayfinderHandMotionClass.Desired, 0.10f);
            WayfinderCircularGestureResult result = tracker.Update(
                true, selected, Head, WayfinderHandMotionClass.Still, 0.10f);
            Assert.That(Vector3.Distance(result.PearlWorldPosition, selected.position),
                Is.LessThan(0.001f));
        }

        [Test]
        public void PlacementHold_LocksAfterConfiguredStillness()
        {
            var tracker = NewTracker();
            Pose palm = Palm(new Vector3(0.10f, -0.10f, 0.58f));
            tracker.Update(true, palm, Head, WayfinderHandMotionClass.Still, 0.10f);
            WayfinderCircularGestureResult partial = tracker.Update(
                true, palm, Head, WayfinderHandMotionClass.Still, 0.15f);
            Assert.That(partial.PlacementHold01, Is.InRange(0.5f, 0.7f));
            WayfinderCircularGestureResult locked = tracker.Update(
                true, palm, Head, WayfinderHandMotionClass.Still, 0.15f);
            Assert.That(locked.PlacementLocked, Is.True);
            Assert.That(locked.Stage, Is.EqualTo(WayfinderCircularTutorialStage.CircleHere));
        }

        [Test]
        public void PlacementDistance_IsClampedOnlyToSafeRange()
        {
            var tracker = NewTracker();
            Vector3 near = tracker.ClampPlacement(new Vector3(0f, 0f, 0.05f), Head);
            Vector3 far = tracker.ClampPlacement(new Vector3(1.5f, 1f, 2f), Head);
            Assert.That(near.magnitude, Is.EqualTo(0.35f).Within(0.001f));
            Assert.That(far.magnitude, Is.EqualTo(0.80f).Within(0.001f));
            Assert.That(far.z, Is.GreaterThan(0f));
        }

        [Test]
        public void LockedCircle_RemainsInWorldWhenHeadAndPalmMove()
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0.15f, -0.12f, 0.60f));
            Pose movedHead = new Pose(new Vector3(0.4f, 0.2f, -0.3f), Quaternion.Euler(0f, 70f, 0f));
            WayfinderCircularGestureResult after = tracker.Update(
                false, default, movedHead, WayfinderHandMotionClass.Untracked, 0.2f);
            Assert.That(Vector3.Distance(after.CirclePose.position, locked.CirclePose.position),
                Is.LessThan(0.0001f));
            Assert.That(Quaternion.Angle(after.CirclePose.rotation, locked.CirclePose.rotation),
                Is.LessThan(0.001f));
        }

        [Test]
        public void CirclePlane_OrientsTowardHeadAtPlacementTime()
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0.20f, 0.10f, 0.62f));
            Vector3 normal = locked.CirclePose.rotation * Vector3.forward;
            Vector3 towardHead = (Head.position - locked.CirclePose.position).normalized;
            Assert.That(Vector3.Dot(normal, towardHead), Is.GreaterThan(0.999f));
        }

        [TestCase(15f)]
        [TestCase(142f)]
        [TestCase(286f)]
        public void PalmMayEnterRibbonAtAnyPoint(float entryAngle)
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0f, -0.1f, 0.58f));
            WayfinderCircularGestureResult entered = tracker.Update(true,
                Palm(CirclePoint(locked.CirclePose, entryAngle, 0.15f, 0f)), Head,
                WayfinderHandMotionClass.Desired, 0.02f);
            Assert.That(entered.Stage, Is.EqualTo(WayfinderCircularTutorialStage.FollowLight));
            Assert.That(entered.Progress01, Is.Zero);
        }

        [Test]
        public void BroadRadiusAndStereoDepthTolerance_AreAccepted()
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0f, -0.08f, 0.60f));
            WayfinderCircularGestureResult entered = tracker.Update(true,
                Palm(CirclePoint(locked.CirclePose, 80f, 0.09f, 0.18f)), Head,
                WayfinderHandMotionClass.Desired, 0.02f);
            Assert.That(entered.Stage, Is.EqualTo(WayfinderCircularTutorialStage.FollowLight));
        }

        [Test]
        public void PacingDirection_WaitsForSustainedIntent()
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0f, -0.08f, 0.60f));
            Enter(tracker, locked.CirclePose, 40f);
            WayfinderCircularGestureResult result = default;
            for (int i = 1; i <= 8; i++)
            {
                result = tracker.Update(true,
                    Palm(CirclePoint(locked.CirclePose, 40f + i * 2f, 0.15f, 0f)), Head,
                    WayfinderHandMotionClass.Desired, 0.01f);
            }
            Assert.That(result.PacingDirection, Is.Zero);
        }

        [TestCase(1)]
        [TestCase(-1)]
        public void TolerantOrbit_CompletesInEitherDirection(int direction)
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0f, -0.1f, 0.58f));
            Enter(tracker, locked.CirclePose, 67f);
            WayfinderCircularGestureResult result = Trace(tracker, locked.CirclePose, 67f, direction);
            Assert.That(result.CompletedThisFrame, Is.True);
            Assert.That(result.PacingDirection, Is.EqualTo(direction));
            Assert.That(result.AngularCoverageDegrees, Is.GreaterThanOrEqualTo(270f));
        }

        [Test]
        public void Reset_ClearsPlacedCircleAndReturnsToPlacement()
        {
            var tracker = NewTracker();
            Place(tracker, new Vector3(0.15f, -0.08f, 0.62f));
            tracker.Reset();
            WayfinderCircularGestureResult reset = tracker.Update(
                false, default, Head, WayfinderHandMotionClass.Untracked, 0f);
            Assert.That(reset.Stage, Is.EqualTo(WayfinderCircularTutorialStage.PlaceYourCircle));
            Assert.That(reset.PlacementLocked, Is.False);
            Assert.That(reset.Progress01, Is.Zero);
        }

        [Test]
        public void Completion_IsEmittedExactlyOnceUntilReset()
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0f, -0.1f, 0.58f));
            Enter(tracker, locked.CirclePose, 20f);
            WayfinderCircularGestureResult first = Trace(tracker, locked.CirclePose, 20f, 1);
            Assert.That(first.CompletedThisFrame, Is.True);
            WayfinderCircularGestureResult duplicate = tracker.Update(
                true, Palm(CirclePoint(locked.CirclePose, 30f, 0.15f, 0f)), Head,
                WayfinderHandMotionClass.Desired, 1f);
            Assert.That(duplicate.CompletedThisFrame, Is.False);
            Assert.That(duplicate.CompletionLatched, Is.True);
        }

        [Test]
        public void Defaults_PrioritizeComfortAndForgivingRibbon()
        {
            var config = new WayfinderCircularGestureConfig();
            Assert.That(config.MinimumPlacementDistanceMeters, Is.EqualTo(0.35f));
            Assert.That(config.MaximumPlacementDistanceMeters, Is.EqualTo(0.80f));
            Assert.That(config.PlacementHoldSeconds, Is.EqualTo(0.80f));
            Assert.That(config.GuideRadiusMeters, Is.InRange(0.29f, 0.31f));
            Assert.That(config.RibbonWidthMeters, Is.InRange(0.04f, 0.06f));
            Assert.That(config.GuidePlaneDepthToleranceMeters, Is.GreaterThanOrEqualTo(0.18f));
        }

        [Test]
        public void BeginNextOrbit_PreservesPlacedCircleAndClearsCompletionLatch()
        {
            var tracker = NewTracker();
            WayfinderCircularGestureResult locked = Place(tracker, new Vector3(0f, -0.1f, 0.58f));
            Enter(tracker, locked.CirclePose, 20f);
            WayfinderCircularGestureResult completed = Trace(tracker, locked.CirclePose, 20f, 1);
            Assert.That(completed.CompletedThisFrame, Is.True);

            tracker.BeginNextOrbit();
            WayfinderCircularGestureResult next = tracker.Update(
                true, Palm(locked.CirclePose.position), Head,
                WayfinderHandMotionClass.Still, 1f / 60f);

            Assert.That(next.Stage, Is.EqualTo(WayfinderCircularTutorialStage.CircleHere));
            Assert.That(next.CompletionLatched, Is.False);
            Assert.That(next.Progress01, Is.Zero);
            Assert.That(Vector3.Distance(next.CirclePose.position, locked.CirclePose.position),
                Is.LessThan(0.0001f));
        }

        private static WayfinderCircularGestureTracker NewTracker()
        {
            return new WayfinderCircularGestureTracker(new WayfinderCircularGestureConfig
            {
                PalmAttachSteadySeconds = 0.10f,
                PlacementHoldSeconds = 0.40f,
                CircleGrowSeconds = 0.10f,
                GuideRadiusMeters = 0.15f,
                MinimumRadiusMeters = 0.085f,
                MaximumRadiusMeters = 0.215f,
                TargetDurationSeconds = 1f,
                MinimumAngularCoverageDegrees = 270f,
                DirectionCommitDegrees = 6f,
                MinimumDirectionConsistency01 = 0.45f
            });
        }

        private static WayfinderCircularGestureResult Place(
            WayfinderCircularGestureTracker tracker, Vector3 center)
        {
            Pose palm = Palm(center);
            WayfinderCircularGestureResult result = default;
            for (int i = 0; i < 4; i++)
                result = tracker.Update(true, palm, Head, WayfinderHandMotionClass.Still, 0.10f);
            Assert.That(result.PlacementLocked, Is.True, "test fixture failed to place circle");
            return result;
        }

        private static void Enter(WayfinderCircularGestureTracker tracker, Pose circle, float angle)
        {
            tracker.Update(true, Palm(CirclePoint(circle, angle, 0.15f, 0f)), Head,
                WayfinderHandMotionClass.Desired, 1f / 120f);
        }

        private static WayfinderCircularGestureResult Trace(
            WayfinderCircularGestureTracker tracker, Pose circle, float startAngle, int direction)
        {
            WayfinderCircularGestureResult result = default;
            for (int i = 1; i <= 120; i++)
            {
                float angle = startAngle + direction * i * 3f;
                float radius = 0.15f + Mathf.Sin(i * 0.17f) * 0.018f;
                float depth = Mathf.Sin(i * 0.11f) * 0.055f;
                result = tracker.Update(true, Palm(CirclePoint(circle, angle, radius, depth)), Head,
                    WayfinderHandMotionClass.Desired, 1f / 120f);
                if (result.CompletedThisFrame) return result;
            }
            return result;
        }

        private static Vector3 CirclePoint(Pose circle, float degrees, float radius, float depth)
        {
            float radians = degrees * Mathf.Deg2Rad;
            return circle.position + circle.rotation * new Vector3(
                Mathf.Cos(radians) * radius, Mathf.Sin(radians) * radius, depth);
        }

        private static Pose Palm(Vector3 position) => new Pose(position, Quaternion.identity);
    }
}
