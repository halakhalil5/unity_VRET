using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

public static class HttpListenerLauncher {

    const string RuntimeScenePath = "Assets/Scenes/SampleScene.unity";

    [InitializeOnLoadMethod]
    static void AutoStartFromEnvironment() {
        string autostart = System.Environment.GetEnvironmentVariable("UNITY_HTTP_LISTENER_AUTOSTART");
        if (autostart != "1") return;

        EditorApplication.delayCall += StartUnityHttpListener;
    }

    public static void StartUnityHttpListener() {
        if (!EditorApplication.isPlayingOrWillChangePlaymode) {
            if (System.IO.File.Exists(RuntimeScenePath))
                EditorSceneManager.OpenScene(RuntimeScenePath);

            Debug.Log("[HttpServerLauncher] Entering Play Mode to start Unity HTTP listener");
            EditorApplication.isPlaying = true;
            return;
        }

        Debug.Log("[HttpServerLauncher] Unity is already entering or in Play Mode");
    }
}