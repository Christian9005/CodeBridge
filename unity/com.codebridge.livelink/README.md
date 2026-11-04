# CodeBridge Live Link for Unity

Drive the servos of an ESP32 or Arduino from a Unity scene, and see the board's feedback in the scene.

1. Start the link: `dotnet run --project src/CodeBridge.LiveLink -- --port simulator` (or `--port COM3` for a board). Copy the key it prints.
2. Add this package (*Package Manager > Add package from disk*) and import the **Robot arm** sample.
3. Put `RobotArmDemo` on an empty GameObject, paste the key, press Play.

Components:

- `LiveLinkConnection`: the connection; sends the joints' angles, receives the state. Escape stops the arm.
- `ServoJoint`: ties a transform's rotation to a servo pin, with limits, a home angle and a speed; `feedback` shows where the real servo is.
- `LiveLinkClient`: the connection without a MonoBehaviour, for editor tools and tests.

The full guide, the protocol and the safety rules: [docs/unity.md](../../docs/unity.md).
