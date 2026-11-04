using UnityEditor;
using UnityEditor.SceneManagement;

/// <summary>Opens the robot arm scene when the project opens on an empty, unsaved scene, so pressing Play shows the arm straight away.</summary>
[InitializeOnLoad]
public static class OpenDemoScene
{
    private const string Scene = "Assets/Scenes/RobotArm.unity";

    static OpenDemoScene()
    {
        EditorApplication.delayCall += () =>
        {
            var active = EditorSceneManager.GetActiveScene();
            if (EditorApplication.isPlayingOrWillChangePlaymode || !string.IsNullOrEmpty(active.path) || active.isDirty || SessionState.GetBool("CodeBridge.DemoOpened", false))
                return;

            SessionState.SetBool("CodeBridge.DemoOpened", true);
            EditorSceneManager.OpenScene(Scene);
        };
    }
}
