using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Detects body presence by comparing the live depth grid to the locked
/// background plate. Any pixel where the current depth is significantly
/// CLOSER than the plate's locked depth is foreground (something blocking the
/// static room).
///
/// On top of the simple "is anyone here" signal, this component now also
/// reports per-person stats sourced from the Kinect SDK's PlayerIndex stream:
/// how many distinct bodies are inside, where each is in world space, and
/// roughly how tall each one is. This is the "skeleton-aware" layer feeding
/// into the perception prompt.
///
/// Workflow:
///   1. Clear the room.
///   2. Press F7 to lock the static plate (~3 s).
///   3. Step into the room — presence lights up on both sensors as soon as
///      you stand in front of any locked wall.
/// </summary>
public class KinectPresenceDetector : MonoBehaviour
{
    public BridgePointCloudManager bridgeManager;

    [Header("Foreground detection")]
    [Tooltip("How much closer than the locked plate a pixel must be to count as foreground.")]
    [Range(0.05f, 0.5f)] public float foregroundThresholdMeters = 0.12f;

    [Tooltip("Minimum foreground pixels (summed across sensors) to declare presence.")]
    public int minPixels = 600;

    [Tooltip("Visit every Nth pixel for speed. 1 = every pixel, 2 = half, etc.")]
    [Range(1, 8)] public int sampleStride = 2;

    [Header("Per-person tracking (PlayerIndex)")]
    [Tooltip("Merge per-sensor PlayerIndex blobs whose centroids are within this distance (metres).")]
    [Range(0.1f, 2f)] public float dedupeDistanceMeters = 0.6f;

    [Tooltip("Drop tracked people with fewer than this many pixels — kills noise blobs.")]
    public int minPixelsPerPerson = 150;

    // ── Public state (the rest of the codebase already reads these — unchanged) ──
    public bool    PersonPresent       { get; private set; }
    public int     PersonPixelCount    { get; private set; }
    public int     SensorsSeeingPerson { get; private set; }
    public Vector3 PersonCentroidWorld { get; private set; }
    public int[]   PerSensorCount      { get; private set; } = new int[2];

    // ── NEW: per-person info ─────────────────────────────────────────────────
    public class PersonInfo
    {
        public int    id;          // (sensorIdx * 100) + PlayerIndex — local-unique
        public int    pixels;
        public Vector3 centroid;   // world space
        public float  minY, maxY;
        public float  HeightMeters => Mathf.Max(0f, maxY - minY);
        public Vector3 HeadApprox  => new Vector3(centroid.x, maxY, centroid.z);
    }
    public List<PersonInfo> People  { get; private set; } = new();
    public int              PeopleCount => People.Count;

    // scratch dict, reused each frame
    private readonly Dictionary<int, PersonInfo> _scratch = new();

    void Update()
    {
        if (!bridgeManager || bridgeManager.Processors == null) { PersonPresent = false; People.Clear(); return; }

        int   totalCount = 0;
        int   sensors    = 0;
        Vector3 sumW     = Vector3.zero;
        float  fgThresh  = foregroundThresholdMeters;
        int    stride    = Mathf.Max(1, sampleStride);

        _scratch.Clear();
        var sensorPeople = new List<PersonInfo>(8);

        for (int si = 0; si < bridgeManager.Processors.Length; si++)
        {
            var proc = bridgeManager.Processors[si];
            if (proc == null || !proc.Ready) { if (si < 2) PerSensorCount[si] = 0; continue; }
            var pf    = proc.Output;
            var plate = proc.PlateDepth;
            bool havePlate = plate != null && proc.PlateCount > 0;
            int n = pf.DepthMeters.Length;

            Transform anchor = (si == 0) ? bridgeManager.kinect0Anchor : bridgeManager.kinect1Anchor;
            Matrix4x4 l2w   = anchor ? anchor.localToWorldMatrix : Matrix4x4.identity;

            int count = 0;
            Vector3 sumLocal = Vector3.zero;
            _scratch.Clear();

            for (int i = 0; i < n; i += stride)
            {
                if (pf.InvalidMask[i]) continue;

                // ── 1. Foreground presence (plate-relative + PlayerIndex fallback) ──
                bool isPerson = false;
                if (havePlate && plate[i] > 0f)
                {
                    if (plate[i] - pf.DepthMeters[i] > fgThresh) isPerson = true;
                }
                else if (pf.PlayerIndex[i] > 0)
                {
                    isPerson = true;
                }
                if (isPerson)
                {
                    sumLocal += pf.LocalPoints[i];
                    count++;
                }

                // ── 2. Per-person bucket (uses PlayerIndex regardless of plate) ──
                byte pid = pf.PlayerIndex[i];
                if (pid > 0)
                {
                    int key = (si + 1) * 100 + pid;
                    if (!_scratch.TryGetValue(key, out var info))
                    {
                        info = new PersonInfo
                        {
                            id = key,
                            minY = float.PositiveInfinity,
                            maxY = float.NegativeInfinity
                        };
                        _scratch[key] = info;
                    }
                    Vector3 world = l2w.MultiplyPoint3x4(pf.LocalPoints[i]);
                    info.pixels++;
                    info.centroid += world;
                    if (world.y < info.minY) info.minY = world.y;
                    if (world.y > info.maxY) info.maxY = world.y;
                }
            }

            if (si < 2) PerSensorCount[si] = count;
            if (count > 0)
            {
                sensors++;
                totalCount += count;
                if (anchor)
                {
                    Vector3 localCentroid = sumLocal / count;
                    sumW += l2w.MultiplyPoint3x4(localCentroid) * count;
                }
            }

            // Finalize this sensor's tracked people
            foreach (var info in _scratch.Values)
            {
                if (info.pixels < minPixelsPerPerson) continue;
                info.centroid /= info.pixels;
                sensorPeople.Add(info);
            }
        }

        // Dedupe: people seen by both sensors will appear twice (different keys).
        // Merge any two whose centroids are within dedupeDistanceMeters.
        People.Clear();
        var merged = new bool[sensorPeople.Count];
        for (int i = 0; i < sensorPeople.Count; i++)
        {
            if (merged[i]) continue;
            var a = sensorPeople[i];
            for (int j = i + 1; j < sensorPeople.Count; j++)
            {
                if (merged[j]) continue;
                if (Vector3.Distance(a.centroid, sensorPeople[j].centroid) < dedupeDistanceMeters)
                {
                    // Weighted-average merge
                    var b = sensorPeople[j];
                    int total = a.pixels + b.pixels;
                    a.centroid = (a.centroid * a.pixels + b.centroid * b.pixels) / total;
                    a.minY = Mathf.Min(a.minY, b.minY);
                    a.maxY = Mathf.Max(a.maxY, b.maxY);
                    a.pixels = total;
                    merged[j] = true;
                }
            }
            People.Add(a);
        }

        PersonPixelCount    = totalCount * stride;
        SensorsSeeingPerson = sensors;
        PersonPresent       = PersonPixelCount >= minPixels || People.Count > 0;
        if (totalCount > 0) PersonCentroidWorld = sumW / totalCount;
    }
}
