using UnityEngine;

[System.Serializable]
public class SceneAsset {
    public string id;
    public string category;
    public string[] tags;
    public string preferredStyle;
    public Vector3 position;
    public Vector3 rotation;
    public Vector3 scale;
    public BoundsData expectedBounds;
}

[System.Serializable]
public class BoundsData {
    public float x, y, z;
}

[System.Serializable]
public class SceneDescriptor {
    public string sceneId;
    public string sceneName;
    public SceneAsset[] assets;
}