using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class AIGenerationResolver : MonoBehaviour {

    public static AIGenerationResolver Instance { get; private set; }

    [SerializeField] string aiApiBase    = "http://localhost:5001";
    [SerializeField] int    pollInterval = 3000;   // ms between polls
    [SerializeField] int    maxPolls     = 20;

    static readonly HttpClient http = new HttpClient();

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    public async Task<GameObject> TryResolve(SceneAsset asset) {
        string glbUrl = await TryGenerateDirectUrl(asset);
        if (glbUrl == null) {
            string jobId = await SubmitGenerationJob(asset);
            if (jobId == null) return null;
            glbUrl = await PollForCompletion(jobId);
        }

        if (glbUrl == null) return null;

        var go = await GLBLoader.Instance.LoadFromUrl(glbUrl);
        if (go == null) return null;

        string savedPath = System.IO.Path.Combine(
            Application.persistentDataPath, "CachedAssets", asset.id + ".glb");
        var bounds = new BoundsData { x = 1, y = 1, z = 1 };
        await ManifestUpdater.Instance.RegisterNewAsset(asset, savedPath, bounds);

        return go;
    }

    async Task<string> TryGenerateDirectUrl(SceneAsset asset) {
        string prompt = BuildPrompt(asset);
        var body = JsonUtility.ToJson(new AIGenerationRequest { prompt = prompt });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var response = await http.PostAsync($"{aiApiBase}/generate_shap_e", content);
            if (!response.IsSuccessStatusCode) {
                var errorBody = await response.Content.ReadAsStringAsync();
                Debug.LogWarning($"[AIResolver] Direct endpoint returned {(int)response.StatusCode}: {errorBody}");
                return null;
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonUtility.FromJson<AIDirectResponse>(json);
            if (result == null || !result.found || string.IsNullOrEmpty(result.glbUrl)) {
                return null;
            }

            Debug.Log("[AIResolver] Resolved using direct Shape-E endpoint.");
            return result.glbUrl;
        } catch (System.Exception e) {
            Debug.LogWarning($"[AIResolver] Direct generation failed: {e.Message}");
            return null;
        }
    }

    static string BuildPrompt(SceneAsset asset) {
        return $"A {asset.preferredStyle} 3D model of a " +
               $"{string.Join(", ", asset.tags)} {asset.category}. " +
               $"Game-ready, low-poly, centered at origin.";
    }

    async Task<string> SubmitGenerationJob(SceneAsset asset) {
        string prompt = BuildPrompt(asset);

        var body    = JsonUtility.ToJson(new AIGenerationRequest { prompt = prompt });
        var content = new StringContent(body, Encoding.UTF8, "application/json");

        try {
            var response = await http.PostAsync($"{aiApiBase}/generate", content);
            var json     = await response.Content.ReadAsStringAsync();
            var result   = JsonUtility.FromJson<AIJobResponse>(json);
            return result?.jobId;
        } catch (System.Exception e) {
            Debug.LogWarning($"[AIResolver] Submit failed: {e.Message}");
            return null;
        }
    }

    async Task<string> PollForCompletion(string jobId) {
        for (int i = 0; i < maxPolls; i++) {
            await Task.Delay(pollInterval);
            try {
                var response = await http.GetAsync($"{aiApiBase}/status/{jobId}");
                var json     = await response.Content.ReadAsStringAsync();
                var status   = JsonUtility.FromJson<AIStatusResponse>(json);

                if (status.state == "completed") return status.glbUrl;
                if (status.state == "failed")    return null;

                Debug.Log($"[AIResolver] Job {jobId} still {status.state}... ({i+1}/{maxPolls})");
            } catch (System.Exception e) {
                Debug.LogWarning($"[AIResolver] Poll failed: {e.Message}");
            }
        }
        Debug.LogWarning($"[AIResolver] Job {jobId} timed out");
        return null;
    }
}

[System.Serializable] public class AIGenerationRequest { public string prompt; }
[System.Serializable] public class AIDirectResponse { public bool found; public string glbUrl; public string reason; }
[System.Serializable] public class AIJobResponse    { public string jobId; }
[System.Serializable] public class AIStatusResponse { public string state; public string glbUrl; }