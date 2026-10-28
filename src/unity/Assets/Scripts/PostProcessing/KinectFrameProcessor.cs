using UnityEngine;

/// <summary>
/// Owns the per-pixel history buffers for one Kinect stream and drives the
/// post-processing pipeline. Two instances exist on BridgePointCloudManager —
/// one per sensor — so each Kinect has its own temporal state.
///
/// All large arrays are allocated once in EnsureCapacity() and reused every
/// frame. ProcessedDepthFrame references those buffers directly — downstream
/// code reads but must not mutate them.
/// </summary>
public class KinectFrameProcessor
{
    public KinectDepthPostProcessingSettings Settings  { get; set; }
    public RGBDepthAlignmentSettings         Alignment { get; set; }
    public ProcessedDepthFrame               Output    { get; } = new ProcessedDepthFrame();

    // History (persists across frames)
    private float[] _history;
    private byte[]  _stable;
    private int[]   _holdCount;

    // Persistent plate (locks static surfaces forever)
    private float[]   _plateDepth;
    private Vector3[] _plateLocal;
    private Color32[] _plateColor;

    /// <summary>Per-pixel locked depth (0 = pixel not in plate). Read-only — do not mutate.</summary>
    public float[] PlateDepth => _plateDepth;

    // Per-frame working buffers
    private float[]   _workDepth;
    private Color32[] _colorsRaw;

    // Bilateral acceleration (rebuilt only when settings change)
    private float[] _depthLut       = new float[256];
    private float[] _spatialWeights;
    private int     _lutRadiusCache = -1;
    private float   _lutEdgeCache;
    private float   _lutSigmaDCache;
    private float   _lutSigmaSCache;

    // ── One-shot "scan room" capture ─────────────────────────────────────────
    public bool IsCapturingPlate  { get; private set; }
    public int  CaptureFramesGoal { get; private set; } = 90;
    public int  CaptureFramesDone { get; private set; }
    public int  PlateCount        { get; private set; }   // pixels currently locked in plate

    private float[] _capSumZ;
    private int[]   _capCount;
    private int[]   _capSumR;
    private int[]   _capSumG;
    private int[]   _capSumB;

    private int _w, _h;
    private readonly System.Diagnostics.Stopwatch _sw = new System.Diagnostics.Stopwatch();

    public bool Ready => Output.Ready;

    public void EnsureCapacity(int w, int h)
    {
        if (w == _w && h == _h && _history != null) return;
        _w = w; _h = h;
        int n = w * h;

        _history    = new float[n];
        _stable     = new byte[n];
        _holdCount  = new int[n];
        _workDepth  = new float[n];
        _colorsRaw  = new Color32[n];
        _plateDepth = new float[n];
        _plateLocal = new Vector3[n];
        _plateColor = new Color32[n];

        Output.Width       = w;
        Output.Height      = h;
        Output.DepthMeters = new float[n];
        Output.LocalPoints = new Vector3[n];
        Output.Colors      = new Color32[n];
        Output.PlayerIndex = new byte[n];
        Output.InvalidMask = new bool[n];
        Output.FilledMask  = new bool[n];
        Output.Stability   = new byte[n];
    }

    public void ResetHistory()
    {
        if (_history == null) return;
        System.Array.Clear(_history,    0, _history.Length);
        System.Array.Clear(_stable,     0, _stable.Length);
        System.Array.Clear(_holdCount,  0, _holdCount.Length);
        System.Array.Clear(_plateDepth, 0, _plateDepth.Length);
    }

    public void ResetPlate()
    {
        if (_plateDepth != null) System.Array.Clear(_plateDepth, 0, _plateDepth.Length);
        PlateCount = 0;
    }

    /// <summary>
    /// One-shot static-room capture. Clears the plate, then averages the next
    /// `frames` frames of valid depth + colour per pixel. Pixels that are valid
    /// in &lt; 25% of frames get rejected. The mean depth/colour is then locked
    /// permanently into the plate. Best used with the room empty (no people).
    /// </summary>
    public void StartPlateCapture(int frames = 90)
    {
        if (_w <= 0 || _h <= 0)
        {
            Debug.LogWarning("[Processor] Can't start plate capture — no frame received yet");
            return;
        }
        int n = _w * _h;
        if (_capSumZ == null || _capSumZ.Length != n)
        {
            _capSumZ  = new float[n];
            _capCount = new int[n];
            _capSumR  = new int[n];
            _capSumG  = new int[n];
            _capSumB  = new int[n];
        }
        System.Array.Clear(_capSumZ,    0, n);
        System.Array.Clear(_capCount,   0, n);
        System.Array.Clear(_capSumR,    0, n);
        System.Array.Clear(_capSumG,    0, n);
        System.Array.Clear(_capSumB,    0, n);
        System.Array.Clear(_plateDepth, 0, n);   // start fresh
        PlateCount        = 0;
        CaptureFramesGoal = frames;
        CaptureFramesDone = 0;
        IsCapturingPlate  = true;
    }

    void EnsureBilateralLuts()
    {
        var s = Settings;
        if (_lutRadiusCache == s.spatialRadius &&
            Mathf.Approximately(_lutEdgeCache,   s.edgePreserveThreshold) &&
            Mathf.Approximately(_lutSigmaDCache, s.spatialSigmaDepth)     &&
            Mathf.Approximately(_lutSigmaSCache, s.spatialSigmaSpace)     &&
            _spatialWeights != null) return;

        KinectDepthPostProcessor.BuildDepthLUT(_depthLut, s.edgePreserveThreshold, s.spatialSigmaDepth);
        _spatialWeights  = KinectDepthPostProcessor.BuildSpatialWeights(s.spatialRadius, s.spatialSigmaSpace);
        _lutRadiusCache  = s.spatialRadius;
        _lutEdgeCache    = s.edgePreserveThreshold;
        _lutSigmaDCache  = s.spatialSigmaDepth;
        _lutSigmaSCache  = s.spatialSigmaSpace;
    }

    public void Process(BinarySensorFrame sensor)
    {
        if (Settings == null || Alignment == null) return;
        int w = sensor.DepthWidth, h = sensor.DepthHeight;
        if (w <= 0 || h <= 0) return;

        _sw.Restart();
        EnsureCapacity(w, h);
        EnsureBilateralLuts();
        int n = w * h;

        var s = Settings;
        var o = Output;

        // Each frame: re-clear filled mask (history + plate persist across frames)
        System.Array.Clear(o.FilledMask, 0, n);

        // 1) populate grid from raw sensor frame, apply depth gate
        int rejected;
        KinectDepthPostProcessor.ValidateAndPopulate(
            sensor, s.minDepthMeters, s.maxDepthMeters,
            o.DepthMeters, o.LocalPoints, _colorsRaw, o.PlayerIndex, o.InvalidMask,
            out rejected);

        // 2) bilateral spatial filter FIRST — denoises raw before temporal locks it in.
        //    Stable pixels are skipped (already frozen by dead-band), so this is cheap.
        if (s.spatialEnabled && s.spatialRadius > 0)
        {
            KinectDepthPostProcessor.SpatialBilateral(
                w, h, o.DepthMeters, _workDepth, o.InvalidMask,
                _stable, _depthLut, _spatialWeights, s);
            System.Array.Copy(_workDepth, o.DepthMeters, n);
        }

        // 3) temporal smoothing — locks dead-band values, ages plate on big changes
        KinectDepthPostProcessor.TemporalSmooth(
            w, h, o.DepthMeters, o.InvalidMask,
            _history, _stable, _holdCount, _plateDepth, s);

        // 4) persistent plate — fill invalid pixels from frozen geometry,
        //    promote newly-stable pixels into the plate
        KinectDepthPostProcessor.UpdateAndApplyPlate(
            n, o.DepthMeters, o.LocalPoints, _colorsRaw,
            o.InvalidMask, o.FilledMask, _stable,
            _plateDepth, _plateLocal, _plateColor, s);

        // 5) median fill smaller holes (multi-iter; uses radius 2 by default)
        if (s.holeFillEnabled)
        {
            KinectDepthPostProcessor.FillHoles(
                w, h, o.DepthMeters, o.LocalPoints, _colorsRaw,
                o.InvalidMask, o.FilledMask, s);
        }

        // 6) re-project (X,Y) of each local point to match the smoothed Z
        KinectDepthPostProcessor.RebuildLocalPoints(n, o.DepthMeters, o.LocalPoints, o.InvalidMask);

        // 7) refine RGB sampling (uses _colorsRaw → o.Colors)
        KinectDepthPostProcessor.ApplyRGBAlignment(w, h, _colorsRaw, o.Colors, o.InvalidMask, Alignment);

        // Copy stability snapshot
        System.Array.Copy(_stable, o.Stability, n);

        // ─── One-shot plate capture: accumulate real sensor data only ────────
        if (IsCapturingPlate)
        {
            for (int i = 0; i < n; i++)
            {
                // Only count pixels that have REAL data this frame
                // (skip plate-restored and hole-filled since plate is empty mid-capture anyway)
                if (o.InvalidMask[i] || o.FilledMask[i]) continue;
                _capSumZ[i] += o.DepthMeters[i];
                Color32 c = _colorsRaw[i];
                _capSumR[i] += c.r;
                _capSumG[i] += c.g;
                _capSumB[i] += c.b;
                _capCount[i]++;
            }
            CaptureFramesDone++;
            if (CaptureFramesDone >= CaptureFramesGoal)
            {
                int minValid = Mathf.Max(2, CaptureFramesGoal / 4);
                int locked = 0;
                for (int i = 0; i < n; i++)
                {
                    if (_capCount[i] < minValid) continue;
                    int c = _capCount[i];
                    float mean = _capSumZ[i] / c;
                    // Reproject (X,Y) using the last seen angular direction
                    Vector3 lp = o.LocalPoints[i];
                    if (lp.z > 0.001f)
                    {
                        float scl = mean / lp.z;
                        _plateLocal[i] = new Vector3(lp.x * scl, lp.y * scl, mean);
                    }
                    else _plateLocal[i] = new Vector3(0, 0, mean);
                    _plateDepth[i] = mean;
                    _plateColor[i] = new Color32(
                        (byte)(_capSumR[i] / c),
                        (byte)(_capSumG[i] / c),
                        (byte)(_capSumB[i] / c), 255);
                    locked++;
                }
                IsCapturingPlate = false;
                PlateCount       = locked;
                Debug.Log($"[Processor] Plate scan complete: locked {locked:N0} / {n:N0} pixels ({100f * locked / n:F1}%)");
            }
        }

        // Stats (incl. live plate coverage count)
        int validCnt = 0, filledCnt = 0, plateCnt = 0;
        float sumZ = 0f;
        for (int i = 0; i < n; i++)
        {
            if (!o.InvalidMask[i]) { validCnt++; sumZ += o.DepthMeters[i]; }
            if (o.FilledMask[i])  filledCnt++;
            if (_plateDepth[i] > 0f) plateCnt++;
        }
        if (!IsCapturingPlate) PlateCount = plateCnt;  // keep authoritative count fresh
        o.ValidCount     = validCnt;
        o.InvalidCount   = n - validCnt;
        o.FilledCount    = filledCnt;
        o.RejectedCount  = rejected;
        o.AvgDepthMeters = validCnt > 0 ? sumZ / validCnt : 0f;

        _sw.Stop();
        o.ProcessingMs = (float)_sw.Elapsed.TotalMilliseconds;
    }
}
