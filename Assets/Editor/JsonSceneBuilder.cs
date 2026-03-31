using UnityEngine;
using UnityEditor;
using System.IO;

public class JsonSceneBuilder : EditorWindow
{
    [MenuItem("Tools/Build Scene From JSON")]
    public static void BuildFromJson()
    {
        // Opens a file explorer to pick your JSON
        string path = EditorUtility.OpenFilePanel("Select Level JSON", "Assets", "json");
        
        if (!string.IsNullOrEmpty(path))
        {
            string jsonContent = File.ReadAllText(path);
            SceneData data = JsonUtility.FromJson<SceneData>(jsonContent);

            // Create a parent object to keep the hierarchy clean
            GameObject root = new GameObject("Generated_Level");

            if (data?.sceneObjects == null || data.sceneObjects.Length == 0)
            {
                Debug.LogWarning("JSON contains no sceneObjects to spawn.");
                return;
            }

            foreach (var item in data.sceneObjects)
            {
                // Prefer a matching prefab name from Resources using object type, fallback to a cube.
                GameObject prefab = Resources.Load<GameObject>(item.type);
                GameObject instance = prefab != null
                    ? (GameObject)PrefabUtility.InstantiatePrefab(prefab)
                    : GameObject.CreatePrimitive(PrimitiveType.Cube);

                instance.name = string.IsNullOrEmpty(item.id)
                    ? $"OBJ_{item.type}"
                    : $"OBJ_{item.type}_{item.id}";

                instance.transform.SetParent(root.transform);

                if (item.position != null)
                {
                    instance.transform.position = new Vector3(
                        item.position.x,
                        item.position.y,
                        item.position.z);
                }
            }
        }
    }
}