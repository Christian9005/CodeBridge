using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace CodeBridge.LiveLink
{
    /// <summary>
    /// Connects the scene to codebridge-livelink: it sends the <see cref="ServoJoint"/>s once as the arm, then their angles several times a second, and
    /// hands each joint the state the link reports. Escape stops the arm (a soft stop: keep a hand near the power switch of a real one).
    /// </summary>
    public class LiveLinkConnection : MonoBehaviour
    {
        public string address = "ws://127.0.0.1:5300/link";

        [Tooltip("The key codebridge-livelink printed when it started. Leave it empty on the same computer: it is read from the file the link writes.")]
        public string key = "";

        [Tooltip("Analog pins to read back: potentiometers on the joints, a current sensor...")]
        public int[] feedbackPins = new int[0];

        [Range(5, 50)] public float sendRate = 30;

        [Tooltip("How long to keep trying when the link is busy (still closing the previous Play session) or not started yet.")]
        public float connectTimeout = 5;

        public KeyCode stopKey = KeyCode.Escape;

        public ServoJoint[] joints = new ServoJoint[0];

        private LiveLinkClient _client;
        private float _nextSend;
        private readonly Dictionary<int, int> _feedback = new Dictionary<int, int>();

        public bool Connected => _client != null && _client.IsOpen;
        public bool Stopped { get; private set; }
        public string BoardName { get; private set; }
        public bool Simulated { get; private set; }
        public string LastError { get; private set; }

        /// <summary>The latest analog readings, by pin.</summary>
        public IReadOnlyDictionary<int, int> Feedback => _feedback;

        public event Action<ServerMessage> MessageReceived;

        private async void Start()
        {
            if (joints == null || joints.Length == 0)
                joints = GetComponentsInChildren<ServoJoint>();

            if (string.IsNullOrEmpty(key))
                ReadKeyFile();

            // Pressing Play right after Stop finds the link still closing the previous session (it answers "busy" for a moment): try again for a few seconds.
            var giveUp = Time.realtimeSinceStartup + Mathf.Max(0, connectTimeout);
            while (this != null)
            {
                _client = new LiveLinkClient();
                try
                {
                    await _client.ConnectAsync(address, key);
                    await _client.ConfigureAsync(joints.Select(j => j.ToConfig()).ToArray(), feedbackPins);
                    LastError = null;
                    return;
                }
                catch (Exception ex)
                {
                    // an async void must never let an exception escape: show it instead
                    _client.Dispose();
                    _client = null;
                    LastError = "Cannot reach the live link at " + address + ": " + ex.Message;
                    if (ex.Message.Contains("401") || Time.realtimeSinceStartup >= giveUp)
                    {
                        Debug.LogWarning(LastError);
                        return;
                    }
                }

                await System.Threading.Tasks.Task.Delay(300);
            }
        }

        /// <summary>The link writes its address and key to %LOCALAPPDATA%/CodeBridge/livelink.txt (~/.local/share on Linux, ~/Library/Application Support... on macOS).</summary>
        private void ReadKeyFile()
        {
            try
            {
                var path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodeBridge", "livelink.txt");
                if (!System.IO.File.Exists(path))
                    return;

                var lines = System.IO.File.ReadAllLines(path);
                if (lines.Length >= 2)
                {
                    address = lines[0].Trim();
                    key = lines[1].Trim();
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning("Cannot read the live link's key file: " + ex.Message);
            }
        }

        private void Update()
        {
            if (_client == null)
                return;

            while (_client.Received.TryDequeue(out var message))
                Handle(message);

            if (_client.LastError != null)
                LastError = _client.LastError;

            if (Input.GetKeyDown(stopKey))
                Stop();

            if (Connected && !Stopped && Time.unscaledTime >= _nextSend)
            {
                _nextSend = Time.unscaledTime + 1f / Mathf.Max(1, sendRate);
                _ = _client.SetTargetsAsync(joints.Select(j => new JointMessage { id = j.jointId, angle = j.DesiredAngle }).ToArray());
            }
        }

        private void Handle(ServerMessage message)
        {
            switch (message.type)
            {
                case "hello":
                    BoardName = message.board;
                    Simulated = message.simulated;
                    break;
                case "state":
                    Stopped = message.stopped;
                    foreach (var state in message.joints ?? new JointState[0])
                    {
                        var joint = joints.FirstOrDefault(j => j.jointId == state.id);
                        if (joint == null) continue;
                        joint.State = state;
                        joint.ShowFeedback();
                    }

                    foreach (var reading in message.feedback ?? new PinReading[0])
                        _feedback[reading.pin] = reading.value;
                    break;
                case "error":
                    LastError = message.message;
                    Debug.LogWarning("Live link: " + message.message);
                    break;
            }

            MessageReceived?.Invoke(message);
        }

        public void Stop()
        {
            Stopped = true;
            if (Connected) _ = _client.StopAsync();
        }

        public void Resume()
        {
            Stopped = false;
            if (Connected) _ = _client.ResumeAsync();
        }

        private void OnDestroy()
        {
            // the link stops the arm by itself when Unity goes away
            _client?.Dispose();
            _client = null;
        }
    }
}
