using UnityEngine;

/// <summary>
/// Base class for phobia-specific controllers (arachnophobia, acrophobia, etc).
/// Receives intensity updates from scene builder and applies them to the scene.
/// Intensity ranges 0.0 (minimal) to 1.0 (maximum).
/// </summary>
public class PhobiaController : MonoBehaviour {

    public static PhobiaController Instance { get; protected set; }

    [SerializeField] protected string phobiaType = "generic";
    [SerializeField] protected float currentIntensity = 0f;

    protected virtual void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Apply intensity to the scene. Override in subclasses for specific phobia behavior.
    /// </summary>
    public virtual void ApplyIntensity(float intensity) {
        intensity = Mathf.Clamp01(intensity);
        currentIntensity = intensity;
        Debug.Log($"[{phobiaType}Controller] Intensity set to {intensity:F2}");
    }

    public float GetCurrentIntensity() => currentIntensity;
    public string GetPhobiaType() => phobiaType;
}

/// <summary>
/// Arachnophobia-specific controller for spider exposure therapy.
/// Updates spider spawning, movement, and environmental effects based on intensity.
/// </summary>
public class ArachnophobiaController : PhobiaController {

    [SerializeField] int spiderCountAtMax = 15;
    [SerializeField] float spiderSizeAtMax = 2.0f;
    [SerializeField] float spiderSpeedAtMax = 3.0f;
    [SerializeField] float lightIntensityAtMax = 0.25f;

    protected override void Awake() {
        phobiaType = "arachnophobia";
        base.Awake();
    }

    public override void ApplyIntensity(float intensity) {
        base.ApplyIntensity(intensity);

        // Update spider count
        int targetSpiderCount = Mathf.RoundToInt(spiderCountAtMax * intensity);
        UpdateSpiderCount(targetSpiderCount);

        // Update spider scale
        float targetScale = Mathf.Lerp(0.2f, spiderSizeAtMax, intensity);
        UpdateSpiderScale(targetScale);

        // Update spider speed
        float targetSpeed = Mathf.Lerp(0f, spiderSpeedAtMax, intensity);
        UpdateSpiderSpeed(targetSpeed);

        // Update lighting
        float targetLightIntensity = Mathf.Lerp(1.2f, lightIntensityAtMax, intensity);
        UpdateLighting(targetLightIntensity);

        Debug.Log($"[Arachnophobia] Intensity {intensity:F2} → " +
                  $"spiders={targetSpiderCount}, scale={targetScale:F2}, speed={targetSpeed:F2}");
    }

    void UpdateSpiderCount(int targetCount) {
        // Implementation would update active spider count in scene
        // Placeholder for now
    }

    void UpdateSpiderScale(float scale) {
        var spiders = FindObjectsByType<Spider>(FindObjectsSortMode.None);
        foreach (var spider in spiders) {
            spider.SetScale(scale);
        }
    }

    void UpdateSpiderSpeed(float speed) {
        var spiders = FindObjectsByType<Spider>(FindObjectsSortMode.None);
        foreach (var spider in spiders) {
            spider.SetMovementSpeed(speed);
        }
    }

    void UpdateLighting(float intensity) {
        var mainLight = FindFirstObjectByType<Light>();
        if (mainLight != null) {
            mainLight.intensity = intensity;
        }
    }
}

/// <summary>
/// Placeholder interface for spider objects to receive intensity updates.
/// Actual spider AI would implement or attach this.
/// </summary>
public interface ISpider {
    void SetScale(float scale);
    void SetMovementSpeed(float speed);
}

/// <summary>
/// Simple spider component placeholder.
/// </summary>
public class Spider : MonoBehaviour, ISpider {
    public void SetScale(float scale) {
        transform.localScale = Vector3.one * scale;
    }

    public void SetMovementSpeed(float speed) {
        // Would affect spider AI movement behavior
    }
}
