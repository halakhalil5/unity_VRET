using UnityEngine;
#if ENABLE_INPUT_SYSTEM
using UnityEngine.InputSystem;
#endif

public class RelaxationSceneLoadButton : MonoBehaviour {

    const float ButtonDistance = 1.5f;
    const float ButtonVerticalOffset = -0.15f;

    static readonly Vector3 ButtonScale = new(0.42f, 0.12f, 0.03f);

    GameObject buttonRoot;
    TextMesh labelMesh;
    Renderer buttonRenderer;
    Camera activeCamera;

    bool isApplying;
    bool isMouseOver;

    readonly Color idleColor = new(0.08f, 0.45f, 0.65f, 1f);
    readonly Color hoverColor = new(0.12f, 0.55f, 0.75f, 1f);
    readonly Color disabledColor = new(0.24f, 0.24f, 0.24f, 1f);

    void Update() {
        bool shouldShow = SceneBuildController.Instance != null &&
                          SceneBuildController.Instance.HasPendingTherapistSelection &&
                          SceneBuildController.Instance.IsPendingTherapistSelectionReady;

        if (!shouldShow) {
            SetButtonActive(false);
            return;
        }

        EnsureButton();
        UpdateButtonPose();
        UpdateLabel();
        SetButtonActive(true);

        // Handle keyboard and mouse input (for laptop testing)
        HandleDesktopInput();
    }

    void EnsureButton() {
        if (buttonRoot != null) return;

        buttonRoot = GameObject.CreatePrimitive(PrimitiveType.Cube);
        buttonRoot.name = "VR_LoadTherapistSceneButton";
        buttonRoot.transform.localScale = ButtonScale;

        buttonRenderer = buttonRoot.GetComponent<Renderer>();
        if (buttonRenderer != null)
            buttonRenderer.material.color = idleColor;

        var buttonCollider = buttonRoot.GetComponent<Collider>();
        if (buttonCollider != null)
            buttonCollider.enabled = true;

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

        var cam = TryGetActiveCamera();
        if (cam == null) return;

        activeCamera = cam;

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
            ? "Go To Scene"
            : $"Go To: {env}";

        if (buttonRenderer != null) {
            if (isApplying) {
                buttonRenderer.material.color = disabledColor;
            } else if (isMouseOver) {
                buttonRenderer.material.color = hoverColor;
            } else {
                buttonRenderer.material.color = idleColor;
            }
        }
    }

    void SetButtonActive(bool active) {
        if (buttonRoot != null && buttonRoot.activeSelf != active)
            buttonRoot.SetActive(active);
    }

    void HandleDesktopInput() {
        if (buttonRoot == null || isApplying) return;

        // Keep hover state responsive even when not clicking.
        isMouseOver = IsMouseOverButton();

        // Check mouse click
        if (IsPrimaryClickDown()) {
            if (isMouseOver) {
                OnSelected();
            }
        }

        // Check keyboard input (Enter or Space for accessibility)
        if (IsSubmitKeyDown()) {
            OnSelected();
        }
    }

    bool IsMouseOverButton() {
        if (buttonRoot == null) return false;

        var cam = activeCamera != null ? activeCamera : TryGetActiveCamera();
        if (cam == null) {
            isMouseOver = false;
            return false;
        }

        Vector3 pointerPosition = GetPointerPosition();
        Ray ray = cam.ScreenPointToRay(pointerPosition);
        if (Physics.Raycast(ray, out RaycastHit hit, 1000f) &&
            hit.collider != null &&
            hit.collider.gameObject == buttonRoot) {
            isMouseOver = true;
            return true;
        }

        // Fallback for cases where world raycast is unreliable in Game view.
        isMouseOver = IsMouseWithinProjectedButtonRect(cam);
        return isMouseOver;
    }

    bool IsMouseWithinProjectedButtonRect(Camera cam) {
        if (buttonRoot == null || cam == null) return false;

        Vector3 center = buttonRoot.transform.position;
        Vector3 centerScreen = cam.WorldToScreenPoint(center);
        if (centerScreen.z <= 0f) return false;

        Vector3 rightScreen = cam.WorldToScreenPoint(center + buttonRoot.transform.right * (ButtonScale.x * 0.5f));
        Vector3 upScreen = cam.WorldToScreenPoint(center + buttonRoot.transform.up * (ButtonScale.y * 0.5f));

        float halfWidth = Mathf.Max(12f, Mathf.Abs(rightScreen.x - centerScreen.x));
        float halfHeight = Mathf.Max(10f, Mathf.Abs(upScreen.y - centerScreen.y));

        Vector3 mouse = GetPointerPosition();
        return Mathf.Abs(mouse.x - centerScreen.x) <= halfWidth &&
               Mathf.Abs(mouse.y - centerScreen.y) <= halfHeight;
    }

    bool IsPrimaryClickDown() {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
            return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetMouseButtonDown(0);
#else
        return false;
#endif
    }

    bool IsSubmitKeyDown() {
#if ENABLE_INPUT_SYSTEM
        var kb = Keyboard.current;
        if (kb != null && (kb.enterKey.wasPressedThisFrame || kb.spaceKey.wasPressedThisFrame || kb.numpadEnterKey.wasPressedThisFrame))
            return true;
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.GetKeyDown(KeyCode.Return) || Input.GetKeyDown(KeyCode.Space) || Input.GetKeyDown(KeyCode.KeypadEnter);
#else
        return false;
#endif
    }

    Vector3 GetPointerPosition() {
#if ENABLE_INPUT_SYSTEM
        if (Mouse.current != null)
            return Mouse.current.position.ReadValue();
#endif
#if ENABLE_LEGACY_INPUT_MANAGER
        return Input.mousePosition;
#else
        return Vector3.zero;
#endif
    }

    Camera TryGetActiveCamera() {
        if (Camera.main != null) return Camera.main;

        var cameras = FindObjectsByType<Camera>(FindObjectsSortMode.None);
        if (cameras != null && cameras.Length > 0)
            return cameras[0];

        return null;
    }

    void OnSelected() {
        if (isApplying || SceneBuildController.Instance == null) return;

        isApplying = true;

        var result = SceneBuildController.Instance.ApplyPendingTherapistSelection();
        if (result.status != "ok")
            Debug.LogError($"[SceneBuild] {result.error}");
        else
            Debug.Log("[RelaxationButton] Scene loaded via Keyboard/Mouse");

        isApplying = false;
    }

    void OnDestroy() {
        if (buttonRoot != null)
            Destroy(buttonRoot);
    }

}
