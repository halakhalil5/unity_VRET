using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Newtonsoft.Json;

public class UnityHttpServer : MonoBehaviour {

    public static UnityHttpServer Instance { get; private set; }

    [SerializeField] int port = 8080;

    HttpListener      listener;
    CancellationTokenSource cts;

    // Thread-safe queue to push work onto Unity main thread
    readonly System.Collections.Concurrent.ConcurrentQueue<Action>
        mainThreadQueue = new();

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start() {
        StartServer();
    }

    void Update() {
        // Drain the queue on the main thread every frame
        while (mainThreadQueue.TryDequeue(out var action))
            action?.Invoke();
    }

    void StartServer() {
        cts      = new CancellationTokenSource();
        listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");
        listener.Start();

        Debug.Log($"[HttpServer] Listening on http://localhost:{port}/");

        Task.Run(() => ListenLoop(cts.Token));
    }

    async Task ListenLoop(CancellationToken token) {
        while (!token.IsCancellationRequested) {
            try {
                var context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleRequest(context));
            } catch (Exception e) when (!token.IsCancellationRequested) {
                Debug.LogWarning($"[HttpServer] Listener error: {e.Message}");
            }
        }
    }

    async Task HandleRequest(HttpListenerContext context) {
        var req  = context.Request;
        var res  = context.Response;
        string body = "";

        try {
            // Read body
            using var reader = new StreamReader(req.InputStream, req.ContentEncoding);
            body = await reader.ReadToEndAsync();

            Debug.Log($"[HttpServer] {req.HttpMethod} {req.Url.AbsolutePath}");

            string responseJson = req.Url.AbsolutePath switch {
                "/status"      when req.HttpMethod == "GET"  => HandleStatus(),
                "/build-scene" when req.HttpMethod == "POST" => await HandleBuildScene(body),
                _ => NotFound()
            };

            byte[] buffer = Encoding.UTF8.GetBytes(responseJson);
            res.ContentType     = "application/json";
            res.ContentLength64 = buffer.Length;
            res.StatusCode      = 200;
            await res.OutputStream.WriteAsync(buffer, 0, buffer.Length);

        } catch (Exception e) {
            Debug.LogError($"[HttpServer] Request error: {e.Message}");
            byte[] err = Encoding.UTF8.GetBytes(
                JsonConvert.SerializeObject(new { status = "error", error = e.Message }));
            res.StatusCode      = 500;
            res.ContentLength64 = err.Length;
            await res.OutputStream.WriteAsync(err, 0, err.Length);
        } finally {
            res.OutputStream.Close();
        }
    }

    // ── Route Handlers ────────────────────────────────────────────

    string HandleStatus() {
        var response = new StatusResponse {
            status       = "ready",
            version      = "1.0.0",
            capabilities = new[] { "build-scene", "capture-preview" }
        };
        Debug.Log("[HttpServer] /status called — returning ready");
        return JsonConvert.SerializeObject(response);
    }

    async Task<string> HandleBuildScene(string body) {
        // Validate
        if (string.IsNullOrEmpty(body))
            return Error("Empty request body");

        BuildRequest request;
        try {
            request = JsonConvert.DeserializeObject<BuildRequest>(body);
        } catch (Exception e) {
            return Error($"Invalid JSON: {e.Message}");
        }

        if (request?.scene == null)
            return Error("Missing 'scene' field");

        if (string.IsNullOrEmpty(request.scene.environment))
            return Error("Missing 'scene.environment' field");

        // Use TaskCompletionSource to wait for main thread result
        var tcs = new TaskCompletionSource<string>();

        mainThreadQueue.Enqueue(() => {
            try {
                var result = SceneBuildController.Instance.BuildScene(request);
                tcs.SetResult(JsonConvert.SerializeObject(result));
            } catch (Exception e) {
                tcs.SetResult(Error(e.Message));
            }
        });

        return await tcs.Task;
    }

    string NotFound() =>
        JsonConvert.SerializeObject(new { status = "error", error = "endpoint not found" });

    string Error(string message) {
        Debug.LogError($"[HttpServer] Error: {message}");
        return JsonConvert.SerializeObject(new BuildResponse {
            status = "error", error = message });
    }

    void OnApplicationQuit() {
        cts?.Cancel();
        listener?.Stop();
        listener?.Close();
    }
}