using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Collider))]
[RequireComponent(typeof(Rigidbody))]
public class VRCombinable : MonoBehaviour
{
    [Header("Recipe Matching (choose one approach)")]
    [Tooltip("If set, will combine when it overlaps with an object using this tag.")]
    public string combineWithTag = ""; // e.g., "Metal", "Fire", etc.

    [Tooltip("If you prefer key-based matching, fill 'myKey' and 'combineWithKey' (ignore tags).")]
    public string myKey = "";          // e.g., "Metal"
    public string combineWithKey = ""; // e.g., "Fire"

    [Header("Spawn on successful combine")]
    public GameObject resultPrefab;
    public GameObject particlePrefab;

    [Header("Behavior")]
    [Tooltip("Delete the original two objects after spawning the result.")]
    public bool destroyOriginals = true;

    [Tooltip("Only allow combination if BOTH objects are marked as 'initial' (prevents re-combining results).")]
    public bool requireBothInitial = true;

    [Tooltip("Mark this object as part of the initial pair (not a spawned result).")]
    public bool isInitial = true;

    [Tooltip("Minimum relative speed for the combine to trigger (0 = any gentle touch).")]
    public float minRelativeSpeed = 0f;

    [Tooltip("Cooldown to avoid double-triggering when both objects fire the event.")]
    public float pairCooldownSeconds = 0.2f;

    [Header("Spawn Offset (optional)")]
    [Tooltip("If your particle/result should float a bit above the contact midpoint.")]
    public Vector3 spawnOffset = Vector3.zero;

    // Prevent double-spawns by tracking processed pair IDs for a short window.
    private static readonly HashSet<long> _recentPairs = new HashSet<long>();
    private static readonly Queue<(long pairId, float time)> _pairTimestamps = new Queue<(long, float)>();
    private static float _lastCleanup = 0f;

    private Rigidbody _rb;
    private Collider _col;

    private void Reset()
    {
        // Ensure collider is a trigger (works with XR kinematic rigidbodies)
        _col = GetComponent<Collider>();
        if (_col) _col.isTrigger = true;

        // XR grabbables are often kinematic; that's fine for triggers
        _rb = GetComponent<Rigidbody>();
        if (_rb)
        {
            _rb.isKinematic = true;
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
    }

    private void Awake()
    {
        _rb = GetComponent<Rigidbody>();
        _col = GetComponent<Collider>();
        if (_col && !_col.isTrigger)
        {
            // For VR, triggers are more reliable than non-kinematic collisions.
            _col.isTrigger = true;
        }
        if (_rb && _rb.collisionDetectionMode == CollisionDetectionMode.Discrete)
        {
            _rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        }
    }

    private void OnTriggerEnter(Collider other)
    {
        var otherComb = other.GetComponent<VRCombinable>();
        if (otherComb == null) return;

        if (requireBothInitial && !(isInitial && otherComb.isInitial)) return;

        // Optional relative-speed gate
        if (minRelativeSpeed > 0f)
        {
            float relSpeed = EstimateRelativeSpeed(otherComb);
            if (relSpeed < minRelativeSpeed) return;
        }

        if (!RecipeMatches(otherComb)) return;

        // Build a stable pair key (order-independent)
        long pairId = MakePairId(GetInstanceID(), otherComb.GetInstanceID());
        if (IsPairCoolingDown(pairId)) return;
        MarkPair(pairId);

        // Spawn at midpoint between bounds (more stable than transform positions)
        Vector3 myCenter = GetWorldBoundsCenter(gameObject);
        Vector3 otherCenter = GetWorldBoundsCenter(otherComb.gameObject);
        Vector3 spawnPos = (myCenter + otherCenter) * 0.5f + spawnOffset;
        Quaternion spawnRot = Quaternion.identity;

        if (resultPrefab) Instantiate(resultPrefab, spawnPos, spawnRot);
        if (particlePrefab) Instantiate(particlePrefab, spawnPos, spawnRot);

        if (destroyOriginals)
        {
            // Destroy both originals
            Destroy(gameObject);
            Destroy(otherComb.gameObject);
        }
        else
        {
            // Mark as not-initial to avoid infinite chaining unless you want that
            isInitial = false;
            otherComb.isInitial = false;
        }
    }

    private bool RecipeMatches(VRCombinable other)
    {
        // Key-based logic (if keys provided)
        bool keysProvided = !string.IsNullOrEmpty(myKey) || !string.IsNullOrEmpty(combineWithKey)
                          || !string.IsNullOrEmpty(other.myKey) || !string.IsNullOrEmpty(other.combineWithKey);
        if (keysProvided)
        {
            bool aToB = !string.IsNullOrEmpty(myKey) && !string.IsNullOrEmpty(other.combineWithKey) && myKey == other.combineWithKey;
            bool bToA = !string.IsNullOrEmpty(other.myKey) && !string.IsNullOrEmpty(combineWithKey) && other.myKey == combineWithKey;
            if (aToB || bToA) return true;
        }

        // Tag-based fallback (simple and convenient)
        if (!string.IsNullOrEmpty(combineWithTag) && other.CompareTag(combineWithTag)) return true;
        if (!string.IsNullOrEmpty(other.combineWithTag) && CompareTag(other.combineWithTag)) return true;

        return false;
    }

    private float EstimateRelativeSpeed(VRCombinable other)
    {
        // Handles kinematic cases (velocity = 0). We approximate by recent displacement magnitude if available.
        // If both are kinematic, this will likely be ~0; you can keep minRelativeSpeed at 0 for VR grabbing.
        Vector3 vA = (_rb != null) ? _rb.linearVelocity : Vector3.zero;
        Rigidbody rbB = other._rb;
        Vector3 vB = (rbB != null) ? rbB.linearVelocity : Vector3.zero;
        return (vA - vB).magnitude;
    }

    private static long MakePairId(int a, int b)
    {
        // order-independent key from two ints
        int min = a < b ? a : b;
        int max = a < b ? b : a;
        return ((long)min << 32) ^ (uint)max;
    }

    private bool IsPairCoolingDown(long pairId)
    {
        CleanupOldPairs();
        return _recentPairs.Contains(pairId);
    }

    private void MarkPair(long pairId)
    {
        _recentPairs.Add(pairId);
        _pairTimestamps.Enqueue((pairId, Time.time));
    }

    private void CleanupOldPairs()
    {
        if (Time.time - _lastCleanup < 0.1f) return; // throttle cleanup
        _lastCleanup = Time.time;

        while (_pairTimestamps.Count > 0)
        {
            var (pid, t) = _pairTimestamps.Peek();
            if (Time.time - t > pairCooldownSeconds)
            {
                _pairTimestamps.Dequeue();
                _recentPairs.Remove(pid);
            }
            else break;
        }
    }

    private static Vector3 GetWorldBoundsCenter(GameObject go)
    {
        var cols = go.GetComponentsInChildren<Collider>();
        if (cols.Length == 0) return go.transform.position;

        Bounds b = cols[0].bounds;
        for (int i = 1; i < cols.Length; i++) b.Encapsulate(cols[i].bounds);
        return b.center;
    }
}
