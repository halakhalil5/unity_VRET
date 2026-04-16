using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

public class AssetPipelineOrchestrator : MonoBehaviour {

    public static AssetPipelineOrchestrator Instance { get; private set; }

    [SerializeField] string  sceneIdToLoad  = "scene_001";
    [SerializeField] int     maxConcurrent  = 5;
    [SerializeField] bool    loadOnStart    = true;

    readonly Dictionary<string, AssetResolutionContext> contexts = new();

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    async void Start() {
        if (!loadOnStart) return;
        await WaitForRegistry();
        await LoadScene(sceneIdToLoad);
    }

    async Task WaitForRegistry() {
        while (!LocalAssetRegistry.Instance.IsReady)
            await Task.Yield();
    }

    public async Task LoadScene(string sceneId) {
        Debug.Log($"[Orchestrator] Fetching scene: {sceneId}");
        var descriptor = await MERNBridge.Instance.FetchScene(sceneId);

        if (descriptor == null) {
            Debug.LogError("[Orchestrator] Failed to fetch scene descriptor");
            return;
        }

        Debug.Log($"[Orchestrator] Resolving {descriptor.assets.Length} assets");
        var semaphore = new SemaphoreSlim(maxConcurrent);

        var tasks = new List<Task>();
        foreach (var asset in descriptor.assets) {
            tasks.Add(ResolveWithSemaphore(asset, semaphore));
        }

        await Task.WhenAll(tasks);
        Debug.Log("[Orchestrator] Scene load complete");
    }

    async Task ResolveWithSemaphore(SceneAsset asset, SemaphoreSlim semaphore) {
        await semaphore.WaitAsync();
        try   { await ResolveAsset(asset); }
        finally { semaphore.Release(); }
    }

    async Task ResolveAsset(SceneAsset asset) {
        var ctx = new AssetResolutionContext { asset = asset, state = AssetResolutionState.Pending };
        contexts[asset.id] = ctx;

        // Stage 1 — local
        ctx.state = AssetResolutionState.CheckingLocal;
        var go = await LocalResolver.Instance.TryResolve(asset);

        if (go != null) {
            Finalize(go, asset, ctx, "local"); return;
        }

        // Stage 2 — Objaverse
        ctx.state = AssetResolutionState.CheckingObjaverse;
        go = await ObjaverseResolver.Instance.TryResolve(asset);

        if (go != null) {
            Finalize(go, asset, ctx, "objaverse"); return;
        }

        // Stage 3 — AI generation
        ctx.state = AssetResolutionState.GeneratingAI;
        go = await AIGenerationResolver.Instance.TryResolve(asset);

        if (go != null) {
            Finalize(go, asset, ctx, "ai"); return;
        }

        // All stages failed
        ctx.state        = AssetResolutionState.Failed;
        ctx.errorMessage = "All resolution stages failed";
        Debug.LogError($"[Orchestrator] Could not resolve asset: {asset.id}");
        ScenePlacer.Instance.PlacePlaceholder(asset);
    }

    void Finalize(GameObject go, SceneAsset asset, AssetResolutionContext ctx, string source) {
        ScenePlacer.Instance.Place(go, asset);
        ctx.state        = AssetResolutionState.Resolved;
        ctx.resolvedBy   = source;
        ctx.placedObject = go;
        Debug.Log($"[Orchestrator] Resolved '{asset.id}' via {source}");
    }

    public Dictionary<string, AssetResolutionContext> GetContexts() => contexts;
}
// ```

// ---

// ## Step 8 — Scene Setup in Unity Editor

// Create one GameObject called **`SceneGenerationManager`** and attach all of these in order:
// ```
// SceneGenerationManager (GameObject)
// ├── LocalAssetRegistry       (script)
// ├── ManifestUpdater          (script)
// ├── LocalResolver            (script)
// ├── ObjaverseResolver        (script)  ← set API base URL in inspector
// ├── AIGenerationResolver     (script)  ← set AI API base URL in inspector
// ├── GLBLoader                (script)
// ├── ScenePlacer              (script)  ← assign Scene Root transform
// ├── MERNBridge               (script)  ← set backend URL in inspector
// └── AssetPipelineOrchestrator(script)  ← set scene ID + maxConcurrent