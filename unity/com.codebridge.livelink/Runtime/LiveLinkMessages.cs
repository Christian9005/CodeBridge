using System;

namespace CodeBridge.LiveLink
{
    // The messages of codebridge-livelink, as JsonUtility reads and writes them (public camelCase fields, arrays instead of dictionaries).

    [Serializable]
    public class JointMessage
    {
        public string id;
        public int pin;
        public float min;
        public float max = 180;
        public float home = 90;
        public float maxSpeed;
        public float angle;
    }

    [Serializable]
    public class ClientMessage
    {
        public string type;
        public JointMessage[] joints;
        public int[] feedbackPins;
    }

    [Serializable]
    public class JointState
    {
        public string id;

        /// <summary>The angle Unity asked for, after the link applied the joint's limits.</summary>
        public float target;

        /// <summary>The angle the link sent to the servo; it moves towards the target at the joint's speed.</summary>
        public float position;
    }

    [Serializable]
    public class PinReading
    {
        public int pin;
        public int value;
    }

    [Serializable]
    public class ServerMessage
    {
        public string type;
        public string board;
        public string firmware;
        public bool simulated;
        public bool stopped;
        public string message;
        public JointState[] joints;
        public PinReading[] feedback;
    }
}
