using UnityEngine;

public class ObjectSpawner : MonoBehaviour {

    public static ObjectSpawner Instance { get; private set; }

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    public void SpawnObjects(SceneObjectData[] objects) {
        foreach (var obj in objects) {
            Debug.Log($"[Spawner] Spawning: {obj.type} at " +
                      $"({obj.position?.x}, {obj.position?.y}, {obj.position?.z})");

            var go  = GameObject.CreatePrimitive(PrimitiveType.Cube);
            go.name = $"OBJ_{obj.type}_{obj.id}";
            go.tag  = "SpawnedObject";

            if (obj.position != null)
                go.transform.position = new Vector3(
                    obj.position.x,
                    obj.position.y,
                    obj.position.z);

            go.transform.localScale = Vector3.one * 0.3f;
        }
    }

    public void ClearSpawnedObjects() {
        foreach (var go in GameObject.FindGameObjectsWithTag("SpawnedObject"))
            Destroy(go);
    }
}
