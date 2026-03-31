public enum AssetResolutionState {
    Pending,
    CheckingLocal,
    CheckingObjaverse,
    GeneratingAI,
    Resolved,
    Failed
}

public class AssetResolutionContext {
    public SceneAsset asset;
    public AssetResolutionState state;
    public string resolvedBy;    // "local", "objaverse", "ai"
    public UnityEngine.GameObject placedObject;
    public string errorMessage;
}