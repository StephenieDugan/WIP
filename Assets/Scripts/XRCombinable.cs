using System.Collections.Generic;
using UnityEngine;

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public class VRCombinable : MonoBehaviour
{
    [Header("Recipe Matching (choose one approach)")]
    public string combineWithTag = "";
    public string myKey = "";
    public string combineWithKey = "";

    [Header("Spawn on successful combine")]
    public GameObject resultPrefab;
    public GameObject particlePrefab;

    [Header("Behavior")]
    public bool destroyOriginals = true;
    public bool requireBothInitial = true;
    public bool isInitial = true;
    public float minRelativeSpeed = 0f;
    public float pairCooldownSeconds = 0.2f;
    public Vector3 spawnOffset = Vector3.zero;

    private static readonly HashSet<long> _recentPairs = new HashSet<long>();
    private static readonly Queue<(long pairId, float time)> _pairTimestamps = new Queue<(long, float)>();
    private static float _lastCleanup = 0f;

    [HideInInspector] public Rigidbody rb;

    private void Reset()
    {
        rb = GetComponent<Rigidbody>();
        if (rb)
        {
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            rb.interpolation = RigidbodyInterpolation.Interpolate;
        }
    }

    private void Awake()
    {
        rb = GetComponent<Rigidbody>();
        if (rb && rb.collisionDetectionMode == CollisionDetectionMode.Discrete)
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
    }

    private void OnTriggerEnter(Collider other) => TryCombineWithCollider(other);

    // Call this from child trigger proxies too
    public void TryCombineWithCollider(Collider other)
    {
        var otherComb = other.GetComponentInParent<VRCombinable>();
        if (otherComb == null) return;

        if (requireBothInitial && !(isInitial && otherComb.isInitial)) return;

        if (minRelativeSpeed > 0f)
        {
            float relSpeed = EstimateRelativeSpeed(otherComb);
            if (relSpeed < minRelativeSpeed) return;
        }

        if (!RecipeMatches(otherComb)) return;

        long pairId = MakePairId(GetInstanceID(), otherComb.GetInstanceID());
        if (IsPairCoolingDown(pairId)) return;
        MarkPair(pairId);

        Vector3 spawnPos = (GetWorldBoundsCenter(gameObject) + GetWorldBoundsCenter(otherComb.gameObject)) * 0.5f + spawnOffset;
        Quaternion rot = Quaternion.identity;

        if (resultPrefab) Instantiate(resultPrefab, spawnPos, rot);
        if (particlePrefab) Instantiate(particlePrefab, spawnPos, rot);

        if (destroyOriginals)
        {
            Destroy(gameObject);
            Destroy(otherComb.gameObject);
        }
        else
        {
            isInitial = false;
            otherComb.isInitial = false;
        }
    }

    private bool RecipeMatches(VRCombinable other)
    {
        bool keysProvided = !string.IsNullOrEmpty(myKey) || !string.IsNullOrEmpty(combineWithKey)
                         || !string.IsNullOrEmpty(other.myKey) || !string.IsNullOrEmpty(other.combineWithKey);
        if (keysProvided)
        {
            bool aToB = !string.IsNullOrEmpty(myKey) && !string.IsNullOrEmpty(other.combineWithKey) && myKey == other.combineWithKey;
            bool bToA = !string.IsNullOrEmpty(other.myKey) && !string.IsNullOrEmpty(combineWithKey) && other.myKey == combineWithKey;
            if (aToB || bToA) return true;
        }

        if (!string.IsNullOrEmpty(combineWithTag) && other.CompareTag(combineWithTag)) return true;
        if (!string.IsNullOrEmpty(other.combineWithTag) && CompareTag(other.combineWithTag)) return true;

        return false;
    }

    private float EstimateRelativeSpeed(VRCombinable other)
    {
        Vector3 vA = rb ? rb.linearVelocity : Vector3.zero;
        Vector3 vB = other.rb ? other.rb.linearVelocity : Vector3.zero;
        return (vA - vB).magnitude;
    }

    private static long MakePairId(int a, int b)
    {
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
        if (Time.time - _lastCleanup < 0.1f) return;
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
