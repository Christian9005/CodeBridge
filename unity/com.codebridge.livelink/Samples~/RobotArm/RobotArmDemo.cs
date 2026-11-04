using CodeBridge.LiveLink;
using UnityEngine;

/// <summary>
/// Put this on an empty GameObject in an empty scene and press Play. It builds a three-joint arm from primitives (base, shoulder, elbow), connects it
/// to codebridge-livelink and shows sliders to move it. The solid arm is what Unity asks for; the see-through arm is where the real servos are, as the
/// link reports it, so you see the speed limit and the joint limits at work. Escape stops the arm.
/// </summary>
public class RobotArmDemo : MonoBehaviour
{
    public string address = "ws://127.0.0.1:5300/link";
    public string key = "";

    [Header("Board pins (ESP32 DevKit)")]
    public int basePin = 13;
    public int shoulderPin = 14;
    public int elbowPin = 27;

    private LiveLinkConnection _link;
    private ServoJoint _base, _shoulder, _elbow;
    private float _baseAngle, _shoulderAngle, _elbowAngle;

    private void Awake()
    {
        var solid = new Color(0.25f, 0.55f, 0.95f);
        var ghost = new Color(1f, 0.6f, 0.1f, 0.35f);

        // the arm Unity drives, and a copy that follows the real servos
        var (b, s, e) = BuildArm(transform, solid);
        var ghostRoot = new GameObject("Real servos").transform;
        ghostRoot.SetParent(transform, false);
        var (gb, gs, ge) = BuildArm(ghostRoot, ghost);

        _base = Joint(b, "base", basePin, Vector3.up, 0, 180, gb);
        _shoulder = Joint(s, "shoulder", shoulderPin, Vector3.forward, 20, 160, gs);
        _elbow = Joint(e, "elbow", elbowPin, Vector3.forward, 10, 170, ge);

        _link = gameObject.AddComponent<LiveLinkConnection>();
        _link.address = address;
        _link.key = key;
        _link.joints = new[] { _base, _shoulder, _elbow };

        var camera = Camera.main != null ? Camera.main : new GameObject("Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(0, 2.2f, -4.2f);
        camera.transform.LookAt(new Vector3(0, 1.2f, 0));
        if (FindObjectOfType<Light>() == null)
            new GameObject("Light").AddComponent<Light>().type = LightType.Directional;
    }

    private static ServoJoint Joint(Transform pivot, string id, int pin, Vector3 axis, float min, float max, Transform feedback)
    {
        var joint = pivot.gameObject.AddComponent<ServoJoint>();
        joint.jointId = id;
        joint.pin = pin;
        joint.axis = axis;
        joint.min = min;
        joint.max = max;
        joint.home = 90;
        joint.maxSpeed = 90;
        joint.feedback = feedback;
        return joint;
    }

    /// <summary>Base (turns around Y) → shoulder → upper arm → elbow → forearm, each pivot at the joint.</summary>
    private static (Transform Base, Transform Shoulder, Transform Elbow) BuildArm(Transform parent, Color color)
    {
        var material = new Material(Shader.Find("Standard")) { color = color };
        if (color.a < 1)
        {
            material.SetFloat("_Mode", 3);
            material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
            material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            material.SetInt("_ZWrite", 0);
            material.EnableKeyword("_ALPHABLEND_ON");
            material.renderQueue = 3000;
        }

        Transform Piece(PrimitiveType type, Transform owner, Vector3 position, Vector3 scale)
        {
            var piece = GameObject.CreatePrimitive(type);
            Object.Destroy(piece.GetComponent<Collider>());
            piece.GetComponent<Renderer>().sharedMaterial = material;
            piece.transform.SetParent(owner, false);
            piece.transform.localPosition = position;
            piece.transform.localScale = scale;
            return piece.transform;
        }

        var baseJoint = new GameObject("Base").transform;
        baseJoint.SetParent(parent, false);
        Piece(PrimitiveType.Cylinder, baseJoint, new Vector3(0, 0.1f, 0), new Vector3(0.8f, 0.1f, 0.8f));

        var shoulder = new GameObject("Shoulder").transform;
        shoulder.SetParent(baseJoint, false);
        shoulder.localPosition = new Vector3(0, 0.3f, 0);
        Piece(PrimitiveType.Cube, shoulder, new Vector3(0, 0.6f, 0), new Vector3(0.18f, 1.2f, 0.18f));

        var elbow = new GameObject("Elbow").transform;
        elbow.SetParent(shoulder, false);
        elbow.localPosition = new Vector3(0, 1.2f, 0);
        Piece(PrimitiveType.Cube, elbow, new Vector3(0, 0.45f, 0), new Vector3(0.14f, 0.9f, 0.14f));
        Piece(PrimitiveType.Sphere, elbow, new Vector3(0, 0.95f, 0), new Vector3(0.2f, 0.2f, 0.2f));

        return (baseJoint, shoulder, elbow);
    }

    private void Update()
    {
        // the sliders turn the solid arm; ServoJoint turns that rotation into servo angles
        _base.transform.localRotation = Quaternion.AngleAxis(_baseAngle, Vector3.up);
        _shoulder.transform.localRotation = Quaternion.AngleAxis(_shoulderAngle, Vector3.forward);
        _elbow.transform.localRotation = Quaternion.AngleAxis(_elbowAngle, Vector3.forward);
    }

    private void OnGUI()
    {
        GUILayout.BeginArea(new Rect(12, 12, 360, 400), GUI.skin.box);
        GUILayout.Label(_link.Connected
            ? $"Connected to {_link.BoardName}{(_link.Simulated ? " (simulator)" : "")}"
            : "Not connected: start codebridge-livelink and copy its key here");
        if (!string.IsNullOrEmpty(_link.LastError))
            GUILayout.Label("⚠ " + _link.LastError);

        _baseAngle = Slider("Base", _baseAngle, _base);
        _shoulderAngle = Slider("Shoulder", _shoulderAngle, _shoulder);
        _elbowAngle = Slider("Elbow", _elbowAngle, _elbow);

        if (_link.Stopped)
        {
            GUILayout.Label("STOPPED: the arm holds its position.");
            if (GUILayout.Button("Resume")) _link.Resume();
        }
        else if (GUILayout.Button("Stop (Esc)"))
        {
            _link.Stop();
        }

        foreach (var reading in _link.Feedback)
            GUILayout.Label($"GPIO {reading.Key}: {reading.Value}");

        GUILayout.EndArea();
    }

    private static float Slider(string label, float value, ServoJoint joint)
    {
        var state = joint.State;
        GUILayout.Label(state == null
            ? $"{label}: {joint.DesiredAngle:0}°"
            : $"{label}: asked {joint.DesiredAngle:0}°, servo at {state.position:0}°");
        return GUILayout.HorizontalSlider(value, joint.min - joint.home, joint.max - joint.home);
    }
}
