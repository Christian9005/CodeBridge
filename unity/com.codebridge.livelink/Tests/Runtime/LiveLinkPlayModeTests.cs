using System;
using System.Collections;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace CodeBridge.LiveLink.Tests
{
    /// <summary>
    /// The components in a running scene against a real codebridge-livelink (usually on the simulator). Start it, then set
    /// CODEBRIDGE_LIVELINK_ADDRESS and CODEBRIDGE_LIVELINK_KEY before running the Play Mode tests; without them the tests are skipped.
    /// </summary>
    public class LiveLinkPlayModeTests
    {
        private GameObject _root;

        private static string Address => Environment.GetEnvironmentVariable("CODEBRIDGE_LIVELINK_ADDRESS");
        private static string Key => Environment.GetEnvironmentVariable("CODEBRIDGE_LIVELINK_KEY");

        [TearDown]
        public void TearDown()
        {
            if (_root != null)
                UnityEngine.Object.Destroy(_root);
        }

        private (LiveLinkConnection Link, ServoJoint Joint, Transform Ghost) BuildOneJointArm()
        {
            if (string.IsNullOrEmpty(Address) || string.IsNullOrEmpty(Key))
                Assert.Ignore("Start codebridge-livelink and set CODEBRIDGE_LIVELINK_ADDRESS and CODEBRIDGE_LIVELINK_KEY to run this test.");

            _root = new GameObject("Arm");
            var pivot = new GameObject("Base").transform;
            pivot.SetParent(_root.transform, false);
            var ghost = new GameObject("Base (real)").transform;
            ghost.SetParent(_root.transform, false);

            var joint = pivot.gameObject.AddComponent<ServoJoint>();
            joint.jointId = "base";
            joint.pin = 13;
            joint.axis = Vector3.up;
            joint.min = 0;
            joint.max = 180;
            joint.home = 90;
            joint.maxSpeed = 360;
            joint.feedback = ghost;

            var link = _root.AddComponent<LiveLinkConnection>();
            link.address = Address;
            link.key = Key;
            link.joints = new[] { joint };
            return (link, joint, ghost);
        }

        private static IEnumerator Until(Func<bool> condition, float seconds, string what, Func<string> detail = null)
        {
            var end = Time.realtimeSinceStartup + seconds;
            while (!condition())
            {
                if (Time.realtimeSinceStartup > end)
                    Assert.Fail("Timed out waiting for " + what + (detail == null ? "" : " (" + detail() + ")"));
                yield return null;
            }
        }

        [UnityTest]
        public IEnumerator Turning_the_joint_in_the_scene_turns_the_servo_and_the_ghost_follows()
        {
            var (link, joint, ghost) = BuildOneJointArm();
            yield return Until(() => joint.State != null, 10, "the first state from the link", () => link.LastError);

            joint.transform.localRotation = Quaternion.AngleAxis(45, Vector3.up);
            Assert.AreEqual(135, joint.DesiredAngle, 0.5f);

            yield return Until(() => Mathf.Abs(joint.State.position - 135) < 0.5f, 5, "the servo to reach 135°");
            yield return null;

            Assert.IsTrue(link.Connected);
            Assert.AreEqual(45, Quaternion.Angle(Quaternion.identity, ghost.localRotation), 1f);
        }

        [UnityTest]
        public IEnumerator A_rotation_past_the_limit_stops_at_the_limit()
        {
            var (_, joint, _) = BuildOneJointArm();
            joint.max = 150;   // tighter than the servo: the scene clamps, and so does the link
            yield return Until(() => joint.State != null, 10, "the first state from the link");

            joint.transform.localRotation = Quaternion.AngleAxis(120, Vector3.up);

            Assert.AreEqual(150, joint.DesiredAngle, 0.5f);
            yield return Until(() => Mathf.Abs(joint.State.position - 150) < 0.5f, 5, "the servo to stop at 150°");
        }

        [UnityTest]
        public IEnumerator Stop_holds_the_servo_while_the_scene_keeps_moving()
        {
            var (link, joint, _) = BuildOneJointArm();
            yield return Until(() => joint.State != null, 10, "the first state from the link");

            link.Stop();
            yield return Until(() => joint.State.target == joint.State.position && link.Stopped, 3, "the stop");
            var held = joint.State.position;

            joint.transform.localRotation = Quaternion.AngleAxis(-60, Vector3.up);
            yield return new WaitForSecondsRealtime(1);

            Assert.AreEqual(held, joint.State.position, 0.5f);
            Assert.IsTrue(link.Stopped);
        }
    }
}
