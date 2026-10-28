using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Per-sensor pipeline each frame:
///   1. KinectFrameProcessor.Process(sensor)   — runs the full depth post-processing
///      pipeline (validate → temporal → bilateral → hole fill → reproject → RGB align)
///   2. Iterate cleaned depth grid, apply player filter, world transform, BG subtract
///   3. Optional outlier removal (every N frames)
///   4. World-space temporal smooth fallback   (only when bypass enabled or non-TKB3)
///
/// All depth-image-space smoothing lives in KinectDepthPostProcessor / KinectFrameProcessor.
/// This class is now just orchestration + downstream world-space stages.
/// </summary>
public class BridgePointCloudManager : MonoBehaviour
{
    public static BridgePointCloudManager Instance { get; private set; }

    [Header("Bridge")]
    public string bridgeUrl = "http://localhost:8182";

    [Header("Sensor World Transforms")]
    public Transform kinect0Anchor;
    public Transform kinect1Anchor;

    [Header("Depth Filter (metres)")]
    [Range(0.1f, 2f)]  public float minDepth = 0.5f;
    [Range(1f,  8f)]   public float maxDepth = 4.0f;

    [Header("Density (1=full, 2=half …)")]
    [Range(1, 8)] public int densityStep = 1;

    [Header("Foreground Filtering")]
    public PlayerFilter playerFilter = PlayerFilter.All;

    [Header("Background Subtraction")]
    public BackgroundModel backgroundModel;
    public bool            subtractBackground = false;

    [Header("Depth Post-Processing Pipeline (TKB3 only)")]
    public KinectDepthPostProcessingSettings depthSettings = new KinectDepthPostProcessingSettings();
    public RGBDepthAlignmentSettings         alignK0       = new RGBDepthAlignmentSettings();
    public RGBDepthAlignmentSettings         alignK1       = new RGBDepthAlignmentSettings();
    [Tooltip("Skip the depth post-processing pipeline entirely and use raw bridge points " +
             "(for A/B comparison). F5 toggles at runtime.")]
    public bool bypassPostProcessing = false;

    [Header("World-Space Outlier Removal (optional)")]
    public bool  removeOutliers           = false;
    [Range(1, 30)]   public int   outlierInterval      = 6;
    [Range(2, 16)]   public int   outlierMinNeighbours = 4;
    [Range(0.02f, 0.4f)] public float outlierRadius   = 0.08f;

    [Header("World-Space Temporal Smooth (fallback / extra pass)")]
    [Range(0f, 0.9f)] public float smoothing = 0f;

    // ── Public output ────────────────────────────────────────────────────────
    public PointCloudData   MergedCloud    { get; private set; }
    public PointCloudData[] SensorClouds   { get; private set; }
    public string           BridgeStatus   { get; private set; } = "Waiting for bridge…";
    public int              LastPointCount { get; private set; }
    public int              SensorCount    { get; private set; }

    public enum PlayerFilter { All, PlayersOnly, BackgroundOnly }

    // ── Internal ─────────────────────────────────────────────────────────────
    private KinectBridgeBinaryClient _client;
    private BinaryFrame _pending;
    private bool        _hasFrame;

    private const int MaxDepthPixels = 640 * 480;

    // Per-sensor depth post-processors (pipeline state lives here)
    private KinectFrameProcessor[] _processors;
    public  KinectFrameProcessor[] Processors => _processors;

    // Per-frame: did the post-processor actually run for at least one sensor?
    private bool _ppRanThisFrame;
    private bool _tkb3Available;

    // Last depth range the plate was valid for — see auto-reset in BuildClouds.
    private float _lastMinDepth = float.NaN;
    private float _lastMaxDepth = float.NaN;

    // World-space smoothing fallback
    private Vector3[][] _prevPositions = { new Vector3[MaxDepthPixels], new Vector3[MaxDepthPixels] };
    private int[]       _prevCounts    = new int[2];

    // Outlier removal grid
    private Dictionary<long, List<int>> _grid = new(4096);
    private int[] _outlierFrame = { 0, 0 };

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        int perSensor = MaxDepthPixels;
        MergedCloud  = new PointCloudData(perSensor * 2);
        SensorClouds = new[] { new PointCloudData(perSensor), new PointCloudData(perSensor) };

        _processors = new KinectFrameProcessor[]
        {
            new KinectFrameProcessor { Settings = depthSettings, Alignment = alignK0 },
            new KinectFrameProcessor { Settings = depthSettings, Alignment = alignK1 },
        };

        _client                     = gameObject.AddComponent<KinectBridgeBinaryClient>();
        _client.bridgeUrl           = bridgeUrl;
        _client.pollIntervalSeconds = 0f;
        _client.FrameReceived      += f => { _pending = f; _hasFrame = true; };
        _client.ErrorReceived      += e => BridgeStatus = $"Bridge: {e}";
        _client.Begin();
        Debug.Log($"[Bridge] Connecting to {bridgeUrl}");
    }

    void Update()
    {
        // F5: toggle post-processing bypass (handled here so it works without
        // the optional DepthPostProcessingDebugView component being present).
        if (Input.GetKeyDown(KeyCode.F5))
        {
            bypassPostProcessing = !bypassPostProcessing;
            Debug.Log($"[Bridge] Post-processing {(bypassPostProcessing ? "BYPASSED (raw)" : "ENABLED (cleaned)")}");
        }
        // F6: wipe the persistent plate (re-learn the static room from scratch)
        if (Input.GetKeyDown(KeyCode.F6) && _processors != null)
        {
            foreach (var p in _processors) p?.ResetPlate();
            Debug.Log("[Bridge] Plate reset — re-learning static geometry");
        }
        // F7: ONE-SHOT static-room scan (90 frames ≈ 3 seconds — clear the room first)
        if (Input.GetKeyDown(KeyCode.F7) && _processors != null)
        {
            foreach (var p in _processors) p?.StartPlateCapture(90);
            Debug.Log("[Bridge] Scanning static room — hold still for ~3 seconds");
        }

        if (!_hasFrame) return;
        _hasFrame = false;
        BuildClouds(_pending);
    }

    // ── Main pipeline ─────────────────────────────────────────────────────────

    private void BuildClouds(BinaryFrame frame)
    {
        int mergedTotal = 0, connected = 0;
        bool bgCapturing = backgroundModel != null && backgroundModel.IsCapturing;
        bool bgSubtract  = subtractBackground && backgroundModel != null && backgroundModel.HasModel;

        // Push live settings into post-processor (so inspector edits take effect)
        depthSettings.minDepthMeters = minDepth;
        depthSettings.maxDepthMeters = maxDepth;

        // The persistent plate stores geometry captured at one depth range.
        // If Min/Max Depth changed, the plate would keep restoring stale,
        // clipped depth — the "sharp cut-off" bug. Wipe it on any change so
        // it re-locks cleanly at the new range (same as pressing F6).
        if (_processors != null &&
            (minDepth != _lastMinDepth || maxDepth != _lastMaxDepth))
        {
            foreach (var p in _processors) p?.ResetPlate();
            _lastMinDepth = minDepth;
            _lastMaxDepth = maxDepth;
        }

        _ppRanThisFrame = false;
        _tkb3Available  = false;

        for (int si = 0; si < frame.Sensors.Count && si < 2; si++)
        {
            var sensor = frame.Sensors[si];
            var sCloud = SensorClouds[si];
            sCloud.Count = 0;

            if (!sensor.Connected || sensor.Points.Length == 0) continue;
            connected++;

            bool hasDepthGrid = sensor.DepthX != null   &&
                                sensor.DepthX.Length > 0 &&
                                sensor.DepthWidth  > 0   &&
                                sensor.DepthHeight > 0;
            if (hasDepthGrid) _tkb3Available = true;

            Matrix4x4 toWorld = GetAnchorMatrix(si);
            int step = Mathf.Max(1, densityStep);

            if (hasDepthGrid && !bypassPostProcessing && _processors != null)
            {
                _ppRanThisFrame = true;
                // ─── Post-processed path: pipeline → iterate cleaned grid ─────
                var proc = _processors[si];
                proc.Settings  = depthSettings;
                proc.Alignment = (si == 0) ? alignK0 : alignK1;
                proc.Process(sensor);
                var pf = proc.Output;

                int w = pf.Width, h = pf.Height;
                int total = w * h;
                for (int i = 0; i < total; i += step)
                {
                    if (pf.InvalidMask[i]) continue;

                    if (playerFilter != PlayerFilter.All)
                    {
                        bool isPlayer = pf.PlayerIndex[i] > 0;
                        if (playerFilter == PlayerFilter.PlayersOnly   && !isPlayer) continue;
                        if (playerFilter == PlayerFilter.BackgroundOnly &&  isPlayer) continue;
                    }

                    if (sCloud.Count >= sCloud.Positions.Length) break;
                    Vector3 wp = toWorld.MultiplyPoint3x4(pf.LocalPoints[i]);

                    if (bgSubtract && backgroundModel.IsBackground(wp)) continue;

                    sCloud.Positions[sCloud.Count] = wp;
                    sCloud.Colors[sCloud.Count]    = pf.Colors[i];
                    sCloud.Count++;
                }
            }
            else
            {
                // ─── Bypass / non-TKB3 path: legacy iterate over raw points ───
                float minZ = minDepth, maxZ = maxDepth;
                bool  havePI = sensor.PlayerIndices != null &&
                               sensor.PlayerIndices.Length == sensor.Points.Length;

                for (int pi = 0; pi < sensor.Points.Length; pi += step)
                {
                    Vector3 p = sensor.Points[pi];
                    if (p.z < minZ || p.z > maxZ) continue;
                    if (havePI && playerFilter != PlayerFilter.All)
                    {
                        bool isPlayer = sensor.PlayerIndices[pi] > 0;
                        if (playerFilter == PlayerFilter.PlayersOnly   && !isPlayer) continue;
                        if (playerFilter == PlayerFilter.BackgroundOnly &&  isPlayer) continue;
                    }
                    if (sCloud.Count >= sCloud.Positions.Length) break;
                    Vector3 wp = toWorld.MultiplyPoint3x4(p);
                    if (bgSubtract && backgroundModel.IsBackground(wp)) continue;
                    sCloud.Positions[sCloud.Count] = wp;
                    sCloud.Colors[sCloud.Count]    = sensor.RgbColors[pi];
                    sCloud.Count++;
                }
            }

            if (bgCapturing) backgroundModel.AddFrame(sCloud);

            // World-space outlier removal (optional, runs on post-pipeline output)
            if (removeOutliers && sCloud.Count > outlierMinNeighbours)
            {
                if (++_outlierFrame[si] >= outlierInterval)
                {
                    _outlierFrame[si] = 0;
                    RemoveOutliers(sCloud);
                }
            }

            // World-space temporal fallback (only active when bypassing or no depth-grid)
            if ((bypassPostProcessing || !hasDepthGrid) && smoothing > 0f)
                ApplySmoothingWorld(sCloud, si);

            // Merge into the unified cloud
            int toCopy = Mathf.Min(sCloud.Count, MergedCloud.Positions.Length - mergedTotal);
            System.Array.Copy(sCloud.Positions, 0, MergedCloud.Positions, mergedTotal, toCopy);
            System.Array.Copy(sCloud.Colors,    0, MergedCloud.Colors,    mergedTotal, toCopy);
            mergedTotal += toCopy;
        }

        MergedCloud.Count = mergedTotal;
        LastPointCount    = mergedTotal;
        SensorCount       = connected;
        string ppTag;
        if (!_tkb3Available)             ppTag = " [no-TKB3 — pipeline unavailable, upgrade bridge]";
        else if (bypassPostProcessing)   ppTag = " [PP-BYPASS]";
        else if (!_ppRanThisFrame)       ppTag = " [PP-IDLE]";
        else
        {
            float ms0 = _processors[0]?.Output?.ProcessingMs ?? 0f;
            float ms1 = _processors[1]?.Output?.ProcessingMs ?? 0f;
            ppTag = $" [PP-ON {ms0:F1}+{ms1:F1}ms]";
        }

        // Plate / capture indicator
        if (_processors != null)
        {
            var p0 = _processors[0]; var p1 = _processors[1];
            if (p0 != null && p0.IsCapturingPlate)
                ppTag += $" [SCAN K0 {p0.CaptureFramesDone}/{p0.CaptureFramesGoal}]";
            else if (p1 != null && p1.IsCapturingPlate)
                ppTag += $" [SCAN K1 {p1.CaptureFramesDone}/{p1.CaptureFramesGoal}]";
            else if (p0 != null && p0.PlateCount > 0)
            {
                int pixels = (p0.Output?.Width ?? 1) * (p0.Output?.Height ?? 1);
                int total  = p0.PlateCount + (p1?.PlateCount ?? 0);
                ppTag += $" [plate {100f * (p0.PlateCount / (float)pixels):F0}%/{100f * ((p1?.PlateCount ?? 0) / (float)pixels):F0}%]";
            }
        }

        BridgeStatus      = connected > 0
            ? $"Live — {connected} sensor(s) — {mergedTotal:N0} pts"
              + ppTag
              + (bgCapturing ? $" [BG {backgroundModel.CaptureProgress}/{backgroundModel.captureFrames}]" : "")
              + (bgSubtract  ? " [BG-sub]" : "")
            : "Bridge OK — 0 sensors active";
    }

    // ── World-space outlier removal ───────────────────────────────────────────
    private void RemoveOutliers(PointCloudData cloud)
    {
        int n = cloud.Count; float r2 = outlierRadius * outlierRadius;
        float cell = outlierRadius; int minN = outlierMinNeighbours;
        _grid.Clear();
        for (int i = 0; i < n; i++)
        {
            long key = GridKey(cloud.Positions[i], cell);
            if (!_grid.TryGetValue(key, out var list))
                _grid[key] = list = new List<int>(8);
            list.Add(i);
        }
        int w2 = 0;
        for (int i = 0; i < n; i++)
        {
            Vector3 p  = cloud.Positions[i];
            int nx = Mathf.FloorToInt(p.x/cell), ny = Mathf.FloorToInt(p.y/cell),
                nz = Mathf.FloorToInt(p.z/cell), nb = 0;
            for (int dx = -1; dx <= 1 && nb < minN; dx++)
            for (int dy = -1; dy <= 1 && nb < minN; dy++)
            for (int dz = -1; dz <= 1 && nb < minN; dz++)
            {
                if (!_grid.TryGetValue(PackKey(nx+dx,ny+dy,nz+dz), out var c2)) continue;
                foreach (int j in c2) {
                    if (j==i) continue;
                    Vector3 d = cloud.Positions[j]-p;
                    if (d.x*d.x+d.y*d.y+d.z*d.z < r2) { nb++; if(nb>=minN) break; }
                }
            }
            if (nb >= minN)
            { cloud.Positions[w2]=cloud.Positions[i]; cloud.Colors[w2]=cloud.Colors[i]; w2++; }
        }
        cloud.Count = w2;
    }

    // ── World-space temporal smooth (index-based, O(n)) ───────────────────────
    private void ApplySmoothingWorld(PointCloudData cloud, int si)
    {
        var prev = _prevPositions[si]; int pc = _prevCounts[si];
        float alpha = 1f - smoothing;
        int match = Mathf.Min(cloud.Count, pc);
        for (int i = 0; i < match; i++)
            cloud.Positions[i] = Vector3.LerpUnclamped(prev[i], cloud.Positions[i], alpha);
        int save = Mathf.Min(cloud.Count, prev.Length);
        System.Array.Copy(cloud.Positions, prev, save);
        _prevCounts[si] = save;
    }

    private static long GridKey(Vector3 p, float cell) =>
        PackKey(Mathf.FloorToInt(p.x/cell), Mathf.FloorToInt(p.y/cell), Mathf.FloorToInt(p.z/cell));
    private static long PackKey(int x, int y, int z) =>
        ((long)(x&0xFFFFF)<<40)|((long)(y&0xFFFFF)<<20)|(long)(z&0xFFFFF);

    private Matrix4x4 GetAnchorMatrix(int si) =>
        (si==0 ? kinect0Anchor : kinect1Anchor) is { } t ? t.localToWorldMatrix : Matrix4x4.identity;

#if UNITY_EDITOR
    void OnDrawGizmos()
    {
        DrawGizmo(kinect0Anchor, Color.cyan,    "Kinect 0");
        DrawGizmo(kinect1Anchor, Color.magenta, "Kinect 1");
    }
    static void DrawGizmo(Transform t, Color c, string lbl)
    {
        if (!t) return;
        Gizmos.color = c;
        Gizmos.DrawWireSphere(t.position, 0.07f);
        Gizmos.DrawRay(t.position, t.forward * 0.5f);
        UnityEditor.Handles.Label(t.position + Vector3.up * 0.13f, lbl);
    }
#endif
}
