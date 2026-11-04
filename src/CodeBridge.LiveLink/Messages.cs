namespace CodeBridge.LiveLink;

// The messages on the WebSocket, as JSON with camelCase names. They are plain arrays and fields (no dictionaries) so Unity's JsonUtility can read and
// write them without any library.

/// <summary>A message from the engine. <see cref="Type"/> says which fields matter: configure, target, stop, resume.</summary>
public sealed class ClientMessage
{
    public string Type { get; set; } = string.Empty;

    /// <summary>configure: the joints of the arm. target: the angle wanted for each joint (only <see cref="JointConfig.Id"/> and <see cref="JointConfig.Angle"/>).</summary>
    public JointConfig[]? Joints { get; set; }

    /// <summary>configure: analog pins to read back (potentiometers on the joints, a current sensor...).</summary>
    public int[]? FeedbackPins { get; set; }
}

public sealed class JointConfig
{
    public string Id { get; set; } = string.Empty;
    public int Pin { get; set; }

    /// <summary>The servo never goes below this angle, whatever the engine asks.</summary>
    public double Min { get; set; }

    /// <summary>The servo never goes above this angle, whatever the engine asks.</summary>
    public double Max { get; set; } = 180;

    /// <summary>Where the joint starts.</summary>
    public double Home { get; set; } = 90;

    /// <summary>The fastest the joint may turn, in degrees per second (0 = the default, 90).</summary>
    public double MaxSpeed { get; set; }

    /// <summary>target only: the angle wanted.</summary>
    public double Angle { get; set; }
}

/// <summary>A message to the engine: hello (once), state (about 20 times a second), error.</summary>
public sealed class ServerMessage
{
    public string Type { get; set; } = string.Empty;
    public string? Board { get; set; }
    public string? Firmware { get; set; }
    public bool Simulated { get; set; }
    public bool Stopped { get; set; }
    public string? Message { get; set; }
    public JointState[]? Joints { get; set; }
    public PinReading[]? Feedback { get; set; }
}

public sealed class JointState
{
    public string Id { get; set; } = string.Empty;

    /// <summary>The angle the engine asked for, after the limits.</summary>
    public double Target { get; set; }

    /// <summary>The angle sent to the servo, moving towards the target at the joint's speed. A hobby servo does not report its real position: read a
    /// potentiometer on the joint (<see cref="ClientMessage.FeedbackPins"/>) for that.</summary>
    public double Position { get; set; }
}

public sealed class PinReading
{
    public int Pin { get; set; }
    public int Value { get; set; }
}
