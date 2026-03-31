using UnityEngine;

public static class HttpServerBootstrap {

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    static void EnsureHttpServerRuntime() {
        if (UnityHttpServer.Instance != null) return;

        var bootstrap = new GameObject("HttpServerRuntime");
        Object.DontDestroyOnLoad(bootstrap);

        // Ensure singleton dependencies exist before server requests hit them.
        bootstrap.AddComponent<ObjectSpawner>();
        bootstrap.AddComponent<PreviewCaptureService>();
        bootstrap.AddComponent<SceneBuildController>();
        bootstrap.AddComponent<RelaxationSceneLoadButton>();
        bootstrap.AddComponent<UnityHttpServer>();

        Debug.Log("[HttpServer] Runtime bootstrap created and listener starting");
    }
}