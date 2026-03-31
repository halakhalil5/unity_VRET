using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

public class LocalAssetRegistry : MonoBehaviour {

    public static LocalAssetRegistry Instance { get; private set; }

    private AssetManifest manifest;
    private Dictionary<string, LocalAssetEntry> idLookup;
    public bool IsReady { get; private set; }

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    async void Start() {
        await LoadManifest();
        BuildIdLookup();
        IsReady = true;
        Debug.Log($"[Registry] Loaded {manifest.assets.Length} local assets");
    }

    async Task LoadManifest() {
        string path = Path.Combine(Application.streamingAssetsPath, "AssetManifest.json");

#if UNITY_ANDROID && !UNITY_EDITOR
        var req = UnityWebRequest.Get(path);
        var op = req.SendWebRequest();
        while (!op.isDone) await Task.Yield();
    manifest = JsonUtility.FromJson<AssetManifest>(req.downloadHandler.text);
#else
        string json = await File.ReadAllTextAsync(path);
    manifest = JsonUtility.FromJson<AssetManifest>(json);
#endif

    if (manifest == null) {
        manifest = new AssetManifest { assets = new LocalAssetEntry[0] };
    }

    if (manifest.assets == null) {
        manifest.assets = new LocalAssetEntry[0];
    }
    }

    void BuildIdLookup() {
        idLookup = new Dictionary<string, LocalAssetEntry>();
        foreach (var entry in manifest.assets)
            idLookup[entry.id] = entry;
    }

    public MatchResult FindBestMatch(SceneAsset requested, int minScore = 50) {
        if (idLookup.TryGetValue(requested.id, out var exact))
            return new MatchResult { entry = exact, score = 200,
                matchedTags = requested.tags, missingTags = new string[0] };

        var results = manifest.assets
            .Select(e => ScoreCandidate(e, requested))
            .Where(r => r.score >= minScore)
            .OrderByDescending(r => r.score)
            .ToList();

        return results.Count > 0 ? results[0] : null;
    }

    MatchResult ScoreCandidate(LocalAssetEntry candidate, SceneAsset requested) {
        var result = new MatchResult { entry = candidate };
        var matched = new List<string>();
        var missing = new List<string>();
        var requestedTags = requested.tags ?? new string[0];
        var candidateTags = candidate.tags ?? new string[0];

        if (candidate.id == requested.id)           result.score += 100;
        if (candidate.category == requested.category) result.score += 40;
        else                                          result.score -= 50;
        if (candidate.style == requested.preferredStyle) result.score += 20;

        foreach (var tag in requestedTags) {
            if (((IEnumerable<string>)candidateTags).Contains(tag)) {
                matched.Add(tag); result.score += 10;
            } else {
                missing.Add(tag); result.score -= 2;
            }
        }

        result.score -= (candidateTags.Length - matched.Count);
        result.matchedTags = matched.ToArray();
        result.missingTags  = missing.ToArray();
        return result;
    }

    public bool BoundsFit(LocalAssetEntry candidate, SceneAsset requested, float tolerance = 0.3f) {
        if (requested.expectedBounds == null) return true;
        return Mathf.Abs(candidate.bounds.x - requested.expectedBounds.x) <= tolerance &&
               Mathf.Abs(candidate.bounds.y - requested.expectedBounds.y) <= tolerance &&
               Mathf.Abs(candidate.bounds.z - requested.expectedBounds.z) <= tolerance;
    }

    public AssetManifest GetManifest() => manifest;
    public Dictionary<string, LocalAssetEntry> GetLookup() => idLookup;
}