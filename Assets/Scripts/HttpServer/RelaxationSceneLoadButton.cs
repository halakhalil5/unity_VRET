using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactables;

public class RelaxationSceneLoadButton : MonoBehaviour {

    const float ButtonDistance = 1.5f;
    const float ButtonVerticalOffset = -0.15f;

    static readonly Vector3 ButtonScale = new(0.42f, 0.12f, 0.03f);

    GameObject buttonRoot;
    XRSimpleInteractable interactable;
    TextMesh labelMesh;
    Renderer buttonRenderer;

    bool isApplying;

    readonly Color idleColor = new(0.08f, 0.45f, 0.65f, 1f);
    readonly Color disabledColor = new(0.24f, 0.24f, 0.24f, 1f);

    void Update() {
        bool shouldShow = IsRelaxationSceneActive() &&
                          SceneBuildController.Instance != null &&
                          SceneBuildController.Instance.HasPendingTherapistSelection;

        if (!shouldShow) {
            SetButtonActive(false);
            return;
        }

        EnsureButton();
        UpdateButtonPose();
        UpdateLabel();
        SetButtonActive(true);
    }

    void EnsureButton() {
        if (buttonRoot != null) return;

        buttonRoot = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonRoot.name = "VR_LoadTherapistSceneButton";
        buttonRoot.transform.localScale = ButtonScale;

        buttonRenderer = buttonRoot.GetComponent<Renderer>();
        if (buttonRenderer != null)
            buttonRenderer.material.color = idleColor;

        interactable = buttonRoot.AddComponent<XRSimpleInteractable>();
        interactable.selectEntered.AddListener(OnSelected);

        var labelGo = new GameObject("Label");
        labelGo.transform.SetParent(buttonRoot.transform, false);
        labelGo.transform.localPosition = new Vector3(0f, 0f, ButtonScale.z * 0.5f + 0.001f);

        labelMesh = labelGo.AddComponent<TextMesh>();
        labelMesh.anchor = TextAnchor.MiddleCenter;
        labelMesh.alignment = TextAlignment.Center;
        labelMesh.fontSize = 80;
        labelMesh.characterSize = 0.01f;
        labelMesh.color = Color.white;
        labelMesh.text = "Load Therapist Scene";
    }

    void UpdateButtonPose() {
        if (buttonRoot == null) return;

        var cam = Camera.main;
        if (cam == null) {
            var cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
            if (cameras != null && cameras.Length > 0)
                cam = cameras[0];
        }
        if (cam == null) return;

        var targetPosition = cam.transform.position +
                             cam.transform.forward * ButtonDistance +
                             Vector3.up * ButtonVerticalOffset;

        buttonRoot.transform.position = targetPosition;
        buttonRoot.transform.rotation = Quaternion.LookRotation(
            cam.transform.position - buttonRoot.transform.position,
            Vector3.up);
    }

    void UpdateLabel() {
        if (labelMesh == null || SceneBuildController.Instance == null) return;

        var env = SceneBuildController.Instance.PendingEnvironment;
        labelMesh.text = string.IsNullOrEmpty(env)
            ? "Load Therapist Scene"
            : $"Load: {env}";

        if (buttonRenderer != null)
            buttonRenderer.material.color = isApplying ? disabledColor : idleColor;
    }

    void SetButtonActive(bool active) {
        if (buttonRoot != null && buttonRoot.activeSelf != active)
            buttonRoot.SetActive(active);
    }

    void OnSelected(SelectEnterEventArgs args) {
        if (isApplying || SceneBuildController.Instance == null) return;

        isApplying = true;

        var result = SceneBuildController.Instance.ApplyPendingTherapistSelection();
        if (result.status != "ok")
            Debug.LogError($"[SceneBuild] {result.error}");

        isApplying = false;
    }

    void OnDestroy() {
        if (interactable != null)
            interactable.selectEntered.RemoveListener(OnSelected);

        if (buttonRoot != null)
            Destroy(buttonRoot);
    }

    static bool IsRelaxationSceneActive() {
        var active = SceneManager.GetActiveScene();
        return active.IsValid() && active.name == "RelaxationScene";
    }
}
