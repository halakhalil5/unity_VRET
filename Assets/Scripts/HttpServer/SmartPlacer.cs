using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

public class SmartPlacer : MonoBehaviour {

    public static SmartPlacer Instance { get; private set; }

    [Header("Settings")]
    [SerializeField] LayerMask environmentLayer = ~0;
    [SerializeField] float raycastHeight = 10f;
    [SerializeField] float overlapPadding = 0.05f;
    [SerializeField] int maxPlacementTries = 10;
    [SerializeField] float searchStep = 0.5f;

    readonly List<Bounds> placedBounds = new List<Bounds>();

    void Awake() {
        if (Instance != null && Instance != this) {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    public static SmartPlacer EnsureInstance() {
        if (Instance != null)
            return Instance;

        var go = new GameObject("SmartPlacer");
        return go.AddComponent<SmartPlacer>();
    }

    public bool TryPlace(GameObject go, Vector3 requestedPos, PlacementType type) {
        if (go == null)
            return false;

        Vector3 finalPos = SnapToSurface(requestedPos, type);

        if (IsOverlapping(go, finalPos)) {
            if (!TryFindNearbyFreePosition(go, finalPos, type, out finalPos)) {
                Debug.LogWarning($"[SmartPlacer] Could not place {go.name} - no free space");
                return false;
            }
        }

        go.transform.position = finalPos;
        AlignToSurface(go, type);
        RegisterBounds(go);

        Debug.Log($"[SmartPlacer] Placed {go.name} at {finalPos}");
        return true;
    }

    public void ClearRegisteredBounds() {
        placedBounds.Clear();
    }

    public async Task ResolveOverlaps(GameObject go) {
        if (go == null)
            return;

        var current = go.transform.position;
        if (IsOverlapping(go, current) && TryFindNearbyFreePosition(go, current, PlacementType.Floor, out var freePos)) {
            go.transform.position = freePos;
        }

        RegisterBounds(go);
        await Task.Yield();
    }

    Vector3 SnapToSurface(Vector3 pos, PlacementType type) {
        switch (type) {
            case PlacementType.Wall:
                return SnapToWall(pos);
            case PlacementType.Ceiling:
                return SnapToCeiling(pos);
            case PlacementType.Ground:
            case PlacementType.Floor:
                return SnapToFloor(pos);
            case PlacementType.OnObject:
            default:
                return pos;
        }
    }

    Vector3 SnapToFloor(Vector3 pos) {
        Vector3 origin = new Vector3(pos.x, raycastHeight, pos.z);
        if (Physics.Raycast(origin, Vector3.down, out var hit, raycastHeight * 2f, environmentLayer, QueryTriggerInteraction.Ignore))
            return new Vector3(pos.x, hit.point.y, pos.z);

        return new Vector3(pos.x, 0f, pos.z);
    }

    Vector3 SnapToWall(Vector3 pos) {
        Vector3[] directions = {
            Vector3.forward, Vector3.back,
            Vector3.left, Vector3.right
        };

        float closest = float.MaxValue;
        Vector3 result = pos;

        foreach (var dir in directions) {
            if (Physics.Raycast(pos, dir, out var hit, raycastHeight, environmentLayer, QueryTriggerInteraction.Ignore)) {
                if (hit.distance < closest) {
                    closest = hit.distance;
                    result = hit.point - dir * 0.03f;
                }
            }
        }

        return result;
    }

    Vector3 SnapToCeiling(Vector3 pos) {
        Vector3 origin = new Vector3(pos.x, 0f, pos.z);
        if (Physics.Raycast(origin, Vector3.up, out var hit, raycastHeight * 2f, environmentLayer, QueryTriggerInteraction.Ignore))
            return new Vector3(pos.x, hit.point.y, pos.z);

        return new Vector3(pos.x, 3f, pos.z);
    }

    bool IsOverlapping(GameObject go, Vector3 pos) {
        var newBounds = GetObjectBounds(go, pos);
        newBounds.Expand(overlapPadding * 2f);

        foreach (var existing in placedBounds) {
            if (newBounds.Intersects(existing))
                return true;
        }

        return false;
    }

    bool TryFindNearbyFreePosition(GameObject go, Vector3 origin, PlacementType type, out Vector3 freePos) {
        freePos = origin;

        for (int ring = 1; ring <= maxPlacementTries; ring++) {
            float dist = ring * searchStep;
            for (int angle = 0; angle < 360; angle += 30) {
                float rad = angle * Mathf.Deg2Rad;
                Vector3 candidate = origin + new Vector3(Mathf.Cos(rad) * dist, 0f, Mathf.Sin(rad) * dist);
                candidate = SnapToSurface(candidate, type);

                if (!IsOverlapping(go, candidate)) {
                    freePos = candidate;
                    return true;
                }
            }
        }

        return false;
    }

    void AlignToSurface(GameObject go, PlacementType type) {
        switch (type) {
            case PlacementType.Wall:
                Vector3 toCenter = Vector3.zero - go.transform.position;
                toCenter.y = 0f;
                if (toCenter.sqrMagnitude > 0.001f)
                    go.transform.rotation = Quaternion.LookRotation(toCenter.normalized, Vector3.up);
                break;

            case PlacementType.Ceiling:
                go.transform.rotation = Quaternion.Euler(180f, 0f, 0f);
                break;

            case PlacementType.Floor:
            case PlacementType.Ground:
                go.transform.rotation = Quaternion.Euler(0f, Random.Range(0f, 360f), 0f);
                break;
        }
    }

    Bounds GetObjectBounds(GameObject go, Vector3 pos) {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return new Bounds(pos, Vector3.one * 0.2f);

        Bounds b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);

        b.center = pos + (b.center - go.transform.position);
        return b;
    }

    void RegisterBounds(GameObject go) {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0)
            return;

        Bounds b = renderers[0].bounds;
        foreach (var r in renderers)
            b.Encapsulate(r.bounds);

        b.Expand(overlapPadding * 2f);
        placedBounds.Add(b);
    }
}
