[System.Serializable]
public class LocalAssetEntry {
    public string id;
    public string addressableKey;
    public string category;
    public string[] tags;
    public string polyCount;
    public string style;
    public BoundsData bounds;
}

[System.Serializable]
public class AssetManifest {
    public LocalAssetEntry[] assets;
}

public class MatchResult {
    public LocalAssetEntry entry;
    public int score;
    public string[] matchedTags;
    public string[] missingTags;
}