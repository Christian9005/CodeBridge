using UnityEngine;

namespace CodeBridge.LiveLink
{
    /// <summary>
    /// Ties this transform's rotation to a servo: turn it in the scene (by hand, an animation, inverse kinematics) and the real servo follows. The servo
    /// angle is <see cref="home"/> plus this transform's local rotation around <see cref="axis"/>, clamped to <see cref="min"/>..<see cref="max"/> (the
    /// link enforces the same limits and the speed, whatever Unity sends).
    /// </summary>
    public class ServoJoint : MonoBehaviour
    {
        [Tooltip("A short unique name, such as base, shoulder, elbow.")]
        public string jointId = "joint";

        [Tooltip("The board GPIO the servo's signal wire is on.")]
        public int pin = 13;

        public Vector3 axis = Vector3.up;

        [Range(0, 180)] public float min = 0;
        [Range(0, 180)] public float max = 180;

        [Tooltip("The servo angle when this transform has no rotation.")]
        [Range(0, 180)] public float home = 90;

        [Tooltip("Degrees per second; 0 uses the link's default (90).")]
        public float maxSpeed = 120;

        [Tooltip("Turn on when the servo turns the other way from the model.")]
        public bool invert;

        [Tooltip("Optional: a copy of this joint that shows where the real servo is (the angle the link has sent).")]
        public Transform feedback;

        private Quaternion _rest;

        /// <summary>The latest state from the link for this joint (target and position), or null before the first one.</summary>
        public JointState State { get; internal set; }

        private void Awake()
        {
            _rest = transform.localRotation;
        }

        /// <summary>The servo angle the scene wants now.</summary>
        public float DesiredAngle
        {
            get
            {
                var delta = Quaternion.Inverse(_rest) * transform.localRotation;
                delta.ToAngleAxis(out var angle, out var around);
                if (angle > 180) angle -= 360;
                var signed = Vector3.Dot(around, axis.normalized) >= 0 ? angle : -angle;
                return Mathf.Clamp(home + (invert ? -signed : signed), min, max);
            }
        }

        public JointMessage ToConfig() => new JointMessage { id = jointId, pin = pin, min = min, max = max, home = home, maxSpeed = maxSpeed };

        /// <summary>Moves the feedback copy to the angle the link reports for the real servo.</summary>
        internal void ShowFeedback()
        {
            if (feedback == null || State == null)
                return;

            var signed = State.position - home;
            if (invert) signed = -signed;
            feedback.localRotation = _rest * Quaternion.AngleAxis(signed, axis.normalized);
        }
    }
}
