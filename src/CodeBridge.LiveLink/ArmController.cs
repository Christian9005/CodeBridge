using CodeBridge.Flow;

namespace CodeBridge.LiveLink;

/// <summary>
/// The safety layer between the engine and the servos: every joint stays inside its limits, turns no faster than its speed, and stops when asked or
/// when the engine goes away. It knows nothing about sockets or boards, so it can be tested on its own.
/// </summary>
public sealed class ArmController
{
    public const double DefaultSpeed = 90;
    public const double MaxAllowedSpeed = 360;
    public const int MaxJoints = 16;

    private readonly Dictionary<string, Joint> _joints = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<int> _feedbackPins = [];

    private sealed class Joint
    {
        public required JointConfig Config { get; init; }
        public double Target;
        public double Position;
        public int? Sent;
    }

    public bool Stopped { get; private set; }

    public IReadOnlyList<int> FeedbackPins => _feedbackPins;

    /// <summary>Replaces the arm. Every pin is checked against the board: it must drive a servo (joints) or read analog (feedback). Returns the problems, empty when it was accepted.</summary>
    public IReadOnlyList<string> Configure(IReadOnlyList<JointConfig> joints, IReadOnlyList<int>? feedbackPins, BoardProfile board)
    {
        var problems = new List<string>();
        var servoPins = board.ServoPinOptions.Select(o => Convert.ToInt32(o.Value)).ToHashSet();
        var analogPins = board.AnalogReadPinOptions.Select(o => Convert.ToInt32(o.Value)).ToHashSet();

        if (joints.Count is 0 or > MaxJoints)
            problems.Add($"An arm has 1 to {MaxJoints} joints.");

        foreach (var joint in joints)
        {
            if (string.IsNullOrWhiteSpace(joint.Id))
                problems.Add("Every joint needs an id.");
            if (!servoPins.Contains(joint.Pin))
                problems.Add($"Joint '{joint.Id}': GPIO {joint.Pin} cannot drive a servo on {board.DisplayName}.");
            if (joint.Min < 0 || joint.Max > 180 || joint.Min >= joint.Max)
                problems.Add($"Joint '{joint.Id}': the limits must be within 0 to 180 degrees, with min below max.");
            if (joint.Home < joint.Min || joint.Home > joint.Max)
                problems.Add($"Joint '{joint.Id}': home ({joint.Home}) must be between its limits.");
        }

        foreach (var duplicate in joints.GroupBy(j => j.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            problems.Add($"Joint id '{duplicate.Key}' is used twice.");
        foreach (var duplicate in joints.GroupBy(j => j.Pin).Where(g => g.Count() > 1))
            problems.Add($"GPIO {duplicate.Key} is used by two joints.");
        foreach (var pin in feedbackPins ?? [])
        {
            if (!analogPins.Contains(pin))
                problems.Add($"Feedback: GPIO {pin} cannot read analog values on {board.DisplayName}.");
        }

        if (problems.Count > 0)
            return problems;

        _joints.Clear();
        foreach (var config in joints)
            _joints[config.Id] = new Joint { Config = config, Target = config.Home, Position = config.Home };

        _feedbackPins.Clear();
        _feedbackPins.AddRange((feedbackPins ?? []).Distinct());
        Stopped = false;
        return problems;
    }

    /// <summary>Sets where joints should go; each angle is clamped to the joint's limits. Ignored while stopped. Unknown ids are reported.</summary>
    public IReadOnlyList<string> SetTargets(IEnumerable<JointConfig> targets)
    {
        var unknown = new List<string>();
        foreach (var target in targets)
        {
            if (!_joints.TryGetValue(target.Id, out var joint))
            {
                unknown.Add(target.Id);
                continue;
            }

            if (!Stopped && double.IsFinite(target.Angle))
                joint.Target = Math.Clamp(target.Angle, joint.Config.Min, joint.Config.Max);
        }

        return unknown;
    }

    /// <summary>Every joint holds where it is now and targets are ignored until <see cref="Resume"/>. Used for the stop button and when the engine disconnects.</summary>
    public void Stop()
    {
        Stopped = true;
        foreach (var joint in _joints.Values)
            joint.Target = joint.Position;
    }

    public void Resume() => Stopped = false;

    /// <summary>Moves every joint towards its target by at most its speed × <paramref name="elapsed"/>, and returns the servos whose whole-degree angle changed.</summary>
    public IReadOnlyList<(int Pin, int Angle)> Tick(TimeSpan elapsed)
    {
        var writes = new List<(int, int)>();
        foreach (var joint in _joints.Values)
        {
            var speed = joint.Config.MaxSpeed <= 0 ? DefaultSpeed : Math.Min(joint.Config.MaxSpeed, MaxAllowedSpeed);
            var step = speed * Math.Max(0, elapsed.TotalSeconds);
            var delta = joint.Target - joint.Position;
            joint.Position = Math.Abs(delta) <= step ? joint.Target : joint.Position + Math.Sign(delta) * step;

            var angle = (int)Math.Round(joint.Position);
            if (joint.Sent != angle)
            {
                joint.Sent = angle;
                writes.Add((joint.Config.Pin, angle));
            }
        }

        return writes;
    }

    public JointState[] Snapshot() =>
        _joints.Values.Select(j => new JointState { Id = j.Config.Id, Target = Math.Round(j.Target, 2), Position = Math.Round(j.Position, 2) }).ToArray();

    public IEnumerable<int> Pins => _joints.Values.Select(j => j.Config.Pin);
}
