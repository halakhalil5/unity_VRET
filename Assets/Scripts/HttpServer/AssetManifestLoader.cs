using System;
using System.Collections.Generic;
using UnityEngine;
using Newtonsoft.Json;

/// <summary>
/// Loads and caches the AssetManifest.json from StreamingAssets.
/// Provides access to asset metadata for scene object resolution.
/// </summary>
public class AssetManifestLoader {

    [System.Serializable]
    public class AssetEntry {
        public string id;
        public string addressableKey;
        [JsonProperty("prefabPath")]
        string PrefabPathAlias {
            set {
                if (string.IsNullOrEmpty(addressableKey))
                    addressableKey = value;
            }
        }
        public string category;
        public string[] tags;
        public string polyCount;
        public string style;
        public BoundsData bounds;
        public PlacementRules placement;
    }

    [System.Serializable]
    public class BoundsData {
        public float x;
        public float y;
        public float z;
    }

    [System.Serializable]
    public class ManifestRoot {
        public AssetEntry[] assets;
    }

    static AssetManifestLoader _instance;
    static AssetManifestLoader Instance {
        get {
            if (_instance == null)
                _instance = new AssetManifestLoader();
            return _instance;
        }
    }

    ManifestRoot manifest;
    Dictionary<string, AssetEntry> assetById;

    public static AssetEntry FindAsset(string assetId) => Instance.Find(assetId);
    public static AssetEntry[] GetAllAssets() => Instance.GetAll();
    public static bool TryLoadManifest(out string error) => Instance.Load(out error);

    bool Load(out string error) {
        error = null;

        if (manifest != null && assetById != null)
            return true;

        string manifestPath = System.IO.Path.Combine(
            Application.streamingAssetsPath, "AssetManifest.json");

        if (!System.IO.File.Exists(manifestPath)) {
            error = $"AssetManifest.json not found at {manifestPath}";
            Debug.LogError($"[AssetManifest] {error}");
            return false;
        }

        try {
            string json = System.IO.File.ReadAllText(manifestPath);
            manifest = JsonConvert.DeserializeObject<ManifestRoot>(json);

            if (manifest?.assets == null || manifest.assets.Length == 0) {
                error = "AssetManifest.json is empty or has no 'assets' array";
                Debug.LogError($"[AssetManifest] {error}");
                return false;
            }

            assetById = new Dictionary<string, AssetEntry>();
            foreach (var asset in manifest.assets) {
                if (string.IsNullOrEmpty(asset.id)) continue;
                assetById[asset.id] = asset;
            }

            Debug.Log($"[AssetManifest] Loaded {assetById.Count} assets from manifest");
            return true;
        } catch (Exception e) {
            error = $"Failed to parse AssetManifest.json: {e.Message}";
            Debug.LogError($"[AssetManifest] {error}");
            return false;
        }
    }

    AssetEntry Find(string assetId) {
        if (string.IsNullOrEmpty(assetId)) return null;

        if (!Load(out var _)) 
            return null;

        if (assetById.TryGetValue(assetId, out var entry))
            return entry;

        return null;
    }

    AssetEntry[] GetAll() {
        if (!Load(out var _))
            return new AssetEntry[0];

        return manifest?.assets ?? new AssetEntry[0];
    }
}
