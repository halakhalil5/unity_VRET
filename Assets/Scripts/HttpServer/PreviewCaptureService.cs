using UnityEngine;

public class PreviewCaptureService : MonoBehaviour {

    public static PreviewCaptureService Instance { get; private set; }

    [SerializeField] int previewWidth  = 512;
    [SerializeField] int previewHeight = 512;

    void Awake() {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
    }

    public string Capture() {
        // For MVP — return mock URL, implement real capture in milestone 2
        Debug.Log($"[Preview] Returning mock preview URL ({previewWidth}x{previewHeight})");
        return "/mock-preview.png";

        // Real implementation (uncomment for milestone 2):
        // var rt  = new RenderTexture(previewWidth, previewHeight, 24);
        // Camera.main.targetTexture = rt;
        // Camera.main.Render();
        // RenderTexture.active = rt;
        // var tex = new Texture2D(previewWidth, previewHeight, TextureFormat.RGB24, false);
        // tex.ReadPixels(new Rect(0, 0, previewWidth, previewHeight), 0, 0);
        // tex.Apply();
        // Camera.main.targetTexture = null;
        // RenderTexture.active = null;
        // byte[] bytes = tex.EncodeToPNG();
        // string path = Path.Combine(Application.persistentDataPath, "preview.png");
        // File.WriteAllBytes(path, bytes);
        // return $"/preview/{Path.GetFileName(path)}";
    }
}
