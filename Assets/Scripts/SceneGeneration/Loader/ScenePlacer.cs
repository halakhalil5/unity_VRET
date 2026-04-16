using UnityEngine;

public class ScenePlacer : MonoBehaviour {

    public static ScenePlacer Instance { get; private set; }

    [SerializeField] Transform sceneRoot;   // parent object for all placed assets

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
        if (sceneRoot == null) sceneRoot = transform;
    }

    public void Place(GameObject go, SceneAsset asset) {
        go.transform.SetParent(sceneRoot);
        go.transform.position    = asset.position;
        go.transform.rotation    = Quaternion.Euler(asset.rotation);
        go.transform.localScale  = asset.scale;
        go.name                  = asset.id;
    }

    public GameObject PlacePlaceholder(SceneAsset asset) {
        var go  = GameObject.CreatePrimitive(PrimitiveType.Cube);
        go.name = $"PLACEHOLDER_{asset.id}";
        go.transform.SetParent(sceneRoot);
        go.transform.position   = asset.position;
        go.transform.localScale = asset.scale;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.color = Color.magenta;
        go.GetComponent<Renderer>().material = mat;

        return go;
    }
}