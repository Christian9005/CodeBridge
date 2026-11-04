using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>Builds Assets/Scenes/RobotArm.unity: a camera, a light and the RobotArmDemo. Menu: CodeBridge > Rebuild robot arm scene.</summary>
public static class BuildDemoScene
{
    [MenuItem("CodeBridge/Rebuild robot arm scene")]
    public static void Build()
    {
        var scene = EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);
        new GameObject("Robot Arm").AddComponent<RobotArmDemo>();
        EditorSceneManager.SaveScene(scene, "Assets/Scenes/RobotArm.unity");
        EditorBuildSettings.scenes = new[] { new EditorBuildSettingsScene("Assets/Scenes/RobotArm.unity", true) };
        AssetDatabase.SaveAssets();
    }
}
