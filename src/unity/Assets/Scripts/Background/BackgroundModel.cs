using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Voxel-grid background model.
///
/// USAGE:
///   1. Clear the room of people.
///   2. Call StartCapture() — collects captureFrames of merged world-space points.
///   3. Finalize() runs automatically → builds BackgroundCloud (stable averaged mesh)
///      and a HashSet for fast per-point IsBackground() queries.
///   4. BridgePointCloudManager filters every incoming point through IsBackground()
///      so only foreground (people) survive into the real-time stream.
///
/// The background cloud itself is rendered by a separate static renderer, giving a
/// rock-solid non-flickering room while people are the only dynamic layer.
/// </summary>
public class BackgroundModel : MonoBehaviour
{
    [Header("Voxel grid")]
    [Tooltip("Smaller = more detail, more memory. 4 cm is a good default.")]
    public float voxelSize = 0.04f;

    [Header("Capture")]
    [Tooltip("Number of merged frames to accumulate (~3 s at 30 fps = 90).")]
    public int captureFrames = 90;

    [Tooltip("Fraction of frames a voxel must be hit to count as background (0–1).")]
    [Range(0f, 1f)] public float occupancyThreshold = 0.35f;

    [Tooltip("Expand confirmed background voxels by this many cells to swallow edge noise.")]
    [Range(0, 2)] public int expansionRadius = 1;

    // ── Public state ─────────────────────────────────────────────────────────
    public bool  IsCapturing { get; private set; }
    public bool  HasModel    { get; private set; }
    public int   CaptureProgress => _frameCount;
    public PointCloudData BackgroundCloud { get; private set; }

    public System.Action OnModelReady;

    // ── Internal ─────────────────────────────────────────────────────────────
    private class VoxelAcc
    {
        public double px, py, pz;
        public int r, g, b, count;
    }

    private Dictionary<long, VoxelAcc> _acc    = new(131072);
    private HashSet<long>              _bgKeys  = new(131072);
    private int _frameCount;
    private float _invV;

    // ────────────────────────────────────────────────────────────────────────

    void Awake() => _invV = 1f / voxelSize;

    // Call this with the room empty, then feed frames via AddFrame().
    public void StartCapture()
    {
        _acc.Clear();
        _bgKeys.Clear();
        _frameCount = 0;
        IsCapturing = true;
        HasModel    = false;
        BackgroundCloud = null;
        Debug.Log($"[BG] Capturing {captureFrames} frames — clear the room of people.");
    }

    // Feed the merged world-space cloud each frame during capture.
    public void AddFrame(PointCloudData cloud)
    {
        if (!IsCapturing) return;

        for (int i = 0; i < cloud.Count; i++)
        {
            long key = Key(cloud.Positions[i]);
            if (!_acc.TryGetValue(key, out var v))
                _acc[key] = v = new VoxelAcc();

            v.px += cloud.Positions[i].x;
            v.py += cloud.Positions[i].y;
            v.pz += cloud.Positions[i].z;
            v.r  += cloud.Colors[i].r;
            v.g  += cloud.Colors[i].g;
            v.b  += cloud.Colors[i].b;
            v.count++;
        }

        if (++_frameCount >= captureFrames)
            Finalize();
    }

    void Finalize()
    {
        IsCapturing = false;
        int minHits = Mathf.Max(1, Mathf.RoundToInt(captureFrames * occupancyThreshold));

        // Build confirmed background voxel set from accumulated data
        var confirmed = new HashSet<long>();
        var cloudPts  = new List<Vector3>(65536);
        var cloudCols = new List<Color32>(65536);

        foreach (var kv in _acc)
        {
            if (kv.Value.count < minHits) continue;
            confirmed.Add(kv.Key);

            double n = kv.Value.count;
            cloudPts.Add(new Vector3((float)(kv.Value.px/n),
                                     (float)(kv.Value.py/n),
                                     (float)(kv.Value.pz/n)));
            cloudCols.Add(new Color32((byte)(kv.Value.r/n),
                                      (byte)(kv.Value.g/n),
                                      (byte)(kv.Value.b/n), 255));
        }

        // Expand confirmed set by expansionRadius to swallow boundary noise
        if (expansionRadius > 0)
        {
            _bgKeys.Clear();
            foreach (var key in confirmed)
            {
                (int cx, int cy, int cz) = Unpack(key);
                for (int dx = -expansionRadius; dx <= expansionRadius; dx++)
                for (int dy = -expansionRadius; dy <= expansionRadius; dy++)
                for (int dz = -expansionRadius; dz <= expansionRadius; dz++)
                    _bgKeys.Add(Pack(cx+dx, cy+dy, cz+dz));
            }
        }
        else
        {
            _bgKeys = confirmed;
        }

        // Build stable background cloud (averaged positions = much less noise)
        var bg      = new PointCloudData(cloudPts.Count);
        bg.Count    = cloudPts.Count;
        cloudPts.CopyTo(bg.Positions,  0);
        cloudCols.CopyTo(bg.Colors,    0);
        BackgroundCloud = bg;

        HasModel = true;
        _acc.Clear(); // free capture memory

        Debug.Log($"[BG] Ready — {confirmed.Count} voxels ({_bgKeys.Count} with expansion), " +
                  $"{cloudPts.Count} averaged points.");
        OnModelReady?.Invoke();
    }

    // O(1) — call per incoming real-time point.
    public bool IsBackground(Vector3 p) => HasModel && _bgKeys.Contains(Key(p));

    // ── Key helpers ──────────────────────────────────────────────────────────
    private long Key(Vector3 p) =>
        Pack(Mathf.FloorToInt(p.x * _invV),
             Mathf.FloorToInt(p.y * _invV),
             Mathf.FloorToInt(p.z * _invV));

    private static long Pack(int x, int y, int z) =>
        ((long)(x & 0xFFFFF) << 40) | ((long)(y & 0xFFFFF) << 20) | (long)(z & 0xFFFFF);

    private static (int x, int y, int z) Unpack(long k) =>
        (SignExtend20((int)(k >> 40)),
         SignExtend20((int)((k >> 20) & 0xFFFFF)),
         SignExtend20((int)(k & 0xFFFFF)));

    // Restore sign for negative coordinates
    private static int SignExtend20(int v) => (v & 0x80000) != 0 ? v | unchecked((int)0xFFF00000) : v;
}
