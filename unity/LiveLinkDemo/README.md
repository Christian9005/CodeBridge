# Live link demo project

A Unity 2022.3 project with the robot arm scene ready: `Assets/Scenes/RobotArm.unity`.

1. Start the link: `dotnet run --project src/CodeBridge.LiveLink -- --port COM3` (or `--port simulator`).
2. Open this folder in Unity Hub (*Add > Add project from disk*), open `Assets/Scenes/RobotArm.unity` and press **Play**.

The scene reads the link's address and key from the file the link writes, so there is nothing to copy. `Assets/RobotArm/RobotArmDemo.cs`
is a copy of the package's *Robot arm* sample. See [docs/unity.md](../../docs/unity.md).
