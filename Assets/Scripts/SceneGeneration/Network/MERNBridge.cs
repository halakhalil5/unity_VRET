using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

public class MERNBridge : MonoBehaviour {

    public static MERNBridge Instance { get; private set; }

    [SerializeField] string backendUrl = "http://localhost:5000/api/scene";

    static readonly HttpClient http = new HttpClient();

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public async Task<SceneDescriptor> FetchScene(string sceneId) {
        try {
            string url      = $"{backendUrl}/{sceneId}";
            var    response = await http.GetAsync(url);
            string json     = await response.Content.ReadAsStringAsync();
            return JsonUtility.FromJson<SceneDescriptor>(json);
        } catch (System.Exception e) {
            Debug.LogError($"[MERNBridge] Failed to fetch scene {sceneId}: {e.Message}");
            return null;
        }
    }
}