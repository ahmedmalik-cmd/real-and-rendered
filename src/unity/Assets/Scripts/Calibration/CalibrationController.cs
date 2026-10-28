using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

/// <summary>
/// Runtime calibration + settings UI.
///
/// KEYBOARD (always):
///   H        — show/hide settings panel
///   C        — toggle calibration mode
///   F1       — auto-calibrate (floor + centroid alignment)
///
/// KEYBOARD (calibration mode only):
///   Tab      — cycle target: Kinect 1 → Kinect 0 → Both
///   WASD     — translate X / Z
///   Q / E    — translate Y
///   I/K      — rotate X (tilt up/down)
///   J/L      — rotate Y (pan left/right)
///   U/O      — rotate Z (roll)
///   Shift    — 5× speed
///   Ctrl     — 0.1× speed (fine)
///   Enter    — save calibration to JSON
///   Backspace — reset current target to defaults
///   Escape   — exit calibration mode
/// </summary>
public class CalibrationController : MonoBehaviour
{
    [Header("References")]
    public BridgePointCloudManager bridgeManager;
    public PointCloudRenderer      cloudRenderer;
    public RoomModel               roomModel; // optional — enables F3 model ICP

    [Header("Room Blueprint")]
    public float roomWidth    = 5f;    // metres along X
    public float roomDepth    = 5f;    // metres along Z
    public float sensorHeight = 2.5f;  // metres — how high the sensors are mounted

    public enum RoomCorner { SouthWest, NorthWest, NorthEast, SouthEast }
    public RoomCorner k0Corner = RoomCorner.SouthWest; // K1 is always the diagonal opposite

    [Header("Calibration speeds (metres / degrees per second)")]
    public float translateSpeed = 0.5f;
    public float rotateSpeed    = 30f;

    // ── State ────────────────────────────────────────────────────────────────
    private bool _calibrating;
    private bool _showPanel = true;

    private enum CalibTarget { Kinect1, Kinect0, Both }
    private CalibTarget _target = CalibTarget.Kinect1;

    private string _autoCalibStatus = "";
    private float  _autoCalibStatusTimer;

    // Writable in both editor and build via the shared helper.
    private const string SaveFileName = "calibration.json";

    // ── Settings (mirrored to components at runtime) ─────────────────────────
    private float _pointSize;
    private float _minDepth;
    private float _maxDepth;
    private int   _density;
    private int   _colorMode;
    private int   _shape;
    private int   _playerFilter;
    private bool  _outliers  = false;
    private float _smoothing = 0f;
    private bool  _subtractBg = false;
    private float _depthSmooth = 0.88f;
    private float _deadband    = 0.006f;
    private float _gamma      = 1.35f;
    private float _contrast   = 1.05f;
    private float _brightness = -0.05f;
    private float _rolloff    = 0.75f;
    private float _floorBright = 0.18f;

    private bool _showBackground = true;

    // ── OnGUI layout constants ───────────────────────────────────────────────
    private const float PanelW = 330f;
    private Rect _panelRect;
    private GUIStyle _headerStyle;
    private GUIStyle _labelStyle;
    private GUIStyle _btnStyle;
    private bool _stylesReady;

    // ────────────────────────────────────────────────────────────────────────

    void Start()
    {
        if (bridgeManager) { _subtractBg   = bridgeManager.subtractBackground;
                             _minDepth     = bridgeManager.minDepth;
                             _maxDepth     = bridgeManager.maxDepth;
                             _density      = bridgeManager.densityStep;
                             _playerFilter = (int)bridgeManager.playerFilter;
                             _outliers     = bridgeManager.removeOutliers;
                             _smoothing    = bridgeManager.smoothing;
                             _depthSmooth  = bridgeManager.depthSettings.temporalSmoothing;
                             _deadband     = bridgeManager.depthSettings.deadbandMeters; }
        if (cloudRenderer) { _pointSize  = cloudRenderer.pointSize;
                             _colorMode  = (int)cloudRenderer.colorMode;
                             _shape      = (int)cloudRenderer.pointShape;
                             _gamma       = cloudRenderer.gamma;
                             _contrast    = cloudRenderer.contrast;
                             _brightness  = cloudRenderer.brightness;
                             _rolloff     = cloudRenderer.highlightRolloff;
                             _floorBright = cloudRenderer.floorBrightness; }
        LoadCalibration();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.H))  _showPanel  = !_showPanel;
        if (Input.GetKeyDown(KeyCode.C))  ToggleCalib();
        if (Input.GetKeyDown(KeyCode.F1)) PerformAutoCalibration();
        if (Input.GetKeyDown(KeyCode.F2)) PerformICP();
        if (Input.GetKeyDown(KeyCode.F3)) PerformModelICP();

        if (_calibrating) HandleCalibKeys();

        Apply();

        if (_autoCalibStatusTimer > 0f)
            _autoCalibStatusTimer -= Time.deltaTime;
    }

    // ── Calibration key handling ─────────────────────────────────────────────

    void ToggleCalib()
    {
        _calibrating = !_calibrating;
        if (cloudRenderer) cloudRenderer.calibrationMode = _calibrating;
    }

    void HandleCalibKeys()
    {
        if (Input.GetKeyDown(KeyCode.Tab))
            _target = (CalibTarget)(((int)_target + 1) % 3);

        if (Input.GetKeyDown(KeyCode.Escape))  { _calibrating = false; if (cloudRenderer) cloudRenderer.calibrationMode = false; return; }
        if (Input.GetKeyDown(KeyCode.Return))  { SaveCalibration(); return; }
        if (Input.GetKeyDown(KeyCode.Backspace)) ResetTarget();

        float dt    = Time.deltaTime;
        float speed = Input.GetKey(KeyCode.LeftShift)   ? 5f
                    : Input.GetKey(KeyCode.LeftControl)  ? 0.1f
                    : 1f;
        float ts = translateSpeed * speed * dt;
        float rs = rotateSpeed    * speed * dt;

        Vector3 dp = Vector3.zero, dr = Vector3.zero;
        if (Input.GetKey(KeyCode.W)) dp.z += ts;
        if (Input.GetKey(KeyCode.S)) dp.z -= ts;
        if (Input.GetKey(KeyCode.A)) dp.x -= ts;
        if (Input.GetKey(KeyCode.D)) dp.x += ts;
        if (Input.GetKey(KeyCode.Q)) dp.y += ts;
        if (Input.GetKey(KeyCode.E)) dp.y -= ts;
        if (Input.GetKey(KeyCode.I)) dr.x -= rs;
        if (Input.GetKey(KeyCode.K)) dr.x += rs;
        if (Input.GetKey(KeyCode.J)) dr.y -= rs;
        if (Input.GetKey(KeyCode.L)) dr.y += rs;
        if (Input.GetKey(KeyCode.U)) dr.z += rs;
        if (Input.GetKey(KeyCode.O)) dr.z -= rs;

        if (dp == Vector3.zero && dr == Vector3.zero) return;

        if (_target == CalibTarget.Kinect0 || _target == CalibTarget.Both)
            MoveAnchor(bridgeManager?.kinect0Anchor, dp, dr);
        if (_target == CalibTarget.Kinect1 || _target == CalibTarget.Both)
            MoveAnchor(bridgeManager?.kinect1Anchor, dp, dr);
    }

    static void MoveAnchor(Transform t, Vector3 dp, Vector3 dr)
    {
        if (!t) return;
        t.position += dp;                          // world-space translation
        if (dr != Vector3.zero)
            t.Rotate(dr, Space.Self);              // local-space rotation — rotates around sensor's own axes
    }

    void ResetTarget()
    {
        // Resets to the blueprint-computed positions/rotations.
        float hw = roomWidth * 0.5f, hd = roomDepth * 0.5f, h = sensorHeight;
        var corners = new Vector3[]
        {
            new Vector3(-hw, h, -hd),
            new Vector3(-hw, h,  hd),
            new Vector3( hw, h,  hd),
            new Vector3( hw, h, -hd),
        };
        int idx0 = (int)k0Corner, idx1 = (idx0 + 2) % 4;

        var k0 = bridgeManager?.kinect0Anchor;
        var k1 = bridgeManager?.kinect1Anchor;
        if ((_target == CalibTarget.Kinect0 || _target == CalibTarget.Both) && k0)
        { k0.position = corners[idx0]; k0.rotation = AimAtFloorCentre(corners[idx0]); }
        if ((_target == CalibTarget.Kinect1 || _target == CalibTarget.Both) && k1)
        { k1.position = corners[idx1]; k1.rotation = AimAtFloorCentre(corners[idx1]); }
    }

    // ── Room blueprint setup ─────────────────────────────────────────────────
    // Places both sensors at the mathematically correct positions and orientations
    // for a rectangular room of given dimensions, with sensors mounted in opposing
    // elevated corners, each aimed at the floor centre.

    void ApplyBlueprintSetup()
    {
        var k0 = bridgeManager?.kinect0Anchor;
        var k1 = bridgeManager?.kinect1Anchor;
        if (!k0 || !k1) { SetStatus("Blueprint: anchors not assigned."); return; }

        float hw = roomWidth  * 0.5f;
        float hd = roomDepth  * 0.5f;
        float h  = sensorHeight;

        // Four corners in clockwise order: SW, NW, NE, SE
        var corners = new Vector3[]
        {
            new Vector3(-hw, h, -hd), // 0 SW
            new Vector3(-hw, h,  hd), // 1 NW
            new Vector3( hw, h,  hd), // 2 NE
            new Vector3( hw, h, -hd), // 3 SE
        };

        int idx0 = (int)k0Corner;
        int idx1 = (idx0 + 2) % 4;  // diagonally opposite

        k0.position = corners[idx0];
        k0.rotation = AimAtFloorCentre(corners[idx0]);

        k1.position = corners[idx1];
        k1.rotation = AimAtFloorCentre(corners[idx1]);

        // Update defaults so Backspace resets to these blueprint values
        // (stored as eulerAngles because that is what ResetTarget writes)

        SetStatus($"Blueprint placed: {roomWidth:F1}×{roomDepth:F1} m, {h:F1} m high — run F1 to refine.");
        SaveCalibration();
    }

    // Returns a rotation that points the sensor's +Z toward the floor centre
    // (world origin XZ) and pitches it down by the correct angle.
    static Quaternion AimAtFloorCentre(Vector3 sensorPos)
    {
        Vector3 toCenter  = new Vector3(-sensorPos.x, 0f, -sensorPos.z); // XZ to origin
        float   horizDist = Mathf.Max(toCenter.magnitude, 0.01f);
        float   yaw       = Mathf.Atan2(toCenter.x, toCenter.z) * Mathf.Rad2Deg;
        float   pitch     = Mathf.Atan2(sensorPos.y, horizDist)  * Mathf.Rad2Deg;
        return Quaternion.Euler(pitch, yaw, 0f);
    }

    // ── Auto-calibration ─────────────────────────────────────────────────────
    // Full single-pass approach:
    //   1. Work in SENSOR-LOCAL space — inverse-transform the world cloud back.
    //   2. Fit a floor plane via least-squares regression in local space.
    //   3. Rotate each anchor so the detected floor normal becomes world-up (fixes pitch).
    //   4. Translate each anchor so the floor center lands at world Y=0 (fixes height).
    //   5. Recompute world positions using the new (corrected) anchor matrices.
    //   6. Align XZ centroids of mid-room points — translate K1 to match K0.

    void PerformAutoCalibration()
    {
        var clouds = bridgeManager?.SensorClouds;
        if (clouds == null || clouds.Length < 2)
        { SetStatus("Auto-calib: no sensor data yet."); return; }

        var c0 = clouds[0];
        var c1 = clouds[1];
        if (c0.Count < 200 || c1.Count < 200)
        { SetStatus("Auto-calib: too few points — aim sensors at the floor."); return; }

        var k0 = bridgeManager.kinect0Anchor;
        var k1 = bridgeManager.kinect1Anchor;
        if (!k0 || !k1) { SetStatus("Auto-calib: anchors not assigned."); return; }

        // Capture old local→world matrices BEFORE modifying anchors
        Matrix4x4 oldL2W0 = k0.localToWorldMatrix;
        Matrix4x4 oldL2W1 = k1.localToWorldMatrix;

        // Step 1–4: align each anchor to its detected floor plane
        AlignAnchorToFloor(k0, c0, oldL2W0);
        AlignAnchorToFloor(k1, c1, oldL2W1);

        // Step 5: remap old world-space cloud points through the correction transform
        // remap = newL2W * inverse(oldL2W) = old_world → local → new_world
        Matrix4x4 remap0 = k0.localToWorldMatrix * oldL2W0.inverse;
        Matrix4x4 remap1 = k1.localToWorldMatrix * oldL2W1.inverse;

        // Step 6: XZ centroid alignment — mid-room points 0.3–1.8 m above floor
        Vector3 cent0 = RemappedXZCentroid(c0, remap0, 0.3f, 1.8f);
        Vector3 cent1 = RemappedXZCentroid(c1, remap1, 0.3f, 1.8f);

        Vector3 xzDelta = new Vector3(cent0.x - cent1.x, 0f, cent0.z - cent1.z);
        k1.position += xzDelta;

        SaveCalibration();
        SetStatus($"Auto-calib done. XZ shift: {xzDelta.magnitude:F2} m — press F1 again to refine.");
    }

    // Fit floor plane in sensor-local space, then correct anchor rotation and Y.
    private static void AlignAnchorToFloor(Transform anchor, PointCloudData cloud, Matrix4x4 localToWorld)
    {
        Matrix4x4 worldToLocal = localToWorld.inverse;

        // Project all world-space cloud points to sensor-local Y
        var localYs = new float[cloud.Count];
        for (int i = 0; i < cloud.Count; i++)
            localYs[i] = worldToLocal.MultiplyPoint3x4(cloud.Positions[i]).y;

        System.Array.Sort(localYs);
        // 3rd-percentile local Y = floor level below sensor (negative for elevated sensor)
        float floorLocalY = localYs[Mathf.Max(0, Mathf.RoundToInt(cloud.Count * 0.03f))];

        // Collect floor candidates: within 12 cm of floor level in local Y
        var floorPts = new List<Vector3>(512);
        for (int i = 0; i < cloud.Count; i++)
        {
            Vector3 lp = worldToLocal.MultiplyPoint3x4(cloud.Positions[i]);
            if (Mathf.Abs(lp.y - floorLocalY) < 0.12f)
                floorPts.Add(lp);
        }
        if (floorPts.Count < 20) return;

        // Floor centroid in local space
        Vector3 floorCenter = Vector3.zero;
        foreach (var p in floorPts) floorCenter += p;
        floorCenter /= floorPts.Count;

        // Fit floor plane normal via linear regression: Y = a·X + b·Z + c
        // Normal = normalise(−a, 1, −b) — exact closed-form solution
        Vector3 normal = FitFloorNormal(floorPts, floorCenter);
        if (normal.y < 0f) normal = -normal; // must point up in local space

        // Rotate anchor so this local normal maps to world up (0,1,0)
        Vector3 worldNormal = anchor.rotation * normal;
        anchor.rotation = Quaternion.FromToRotation(worldNormal, Vector3.up) * anchor.rotation;

        // Translate anchor so the floor center lands at world Y = 0
        // (uses the updated rotation so TransformPoint gives the corrected world position)
        float floorWorldY = anchor.TransformPoint(floorCenter).y;
        anchor.position -= new Vector3(0f, floorWorldY, 0f);
    }

    // Least-squares plane fit: minimises sum of squared Y residuals.
    // Returns the plane normal (-a, 1, -b) normalised.
    private static Vector3 FitFloorNormal(List<Vector3> pts, Vector3 mean)
    {
        double cxx = 0, czz = 0, cxz = 0, cxy = 0, czy = 0;
        foreach (var p in pts)
        {
            double dx = p.x - mean.x, dy = p.y - mean.y, dz = p.z - mean.z;
            cxx += dx * dx;  czz += dz * dz;  cxz += dx * dz;
            cxy += dx * dy;  czy += dz * dy;
        }
        double det = cxx * czz - cxz * cxz;
        if (System.Math.Abs(det) < 1e-9) return Vector3.up;
        double a = (cxy * czz - czy * cxz) / det;
        double b = (cxx * czy - cxz * cxy) / det;
        return new Vector3((float)-a, 1f, (float)-b).normalized;
    }

    // XZ centroid of cloud points after remapping to corrected world space,
    // filtering to mid-room height band [minY, maxY].
    private static Vector3 RemappedXZCentroid(PointCloudData cloud, Matrix4x4 remap,
                                               float minY, float maxY)
    {
        float sx = 0f, sz = 0f;
        int   cnt = 0;
        for (int i = 0; i < cloud.Count; i++)
        {
            Vector3 wp = remap.MultiplyPoint3x4(cloud.Positions[i]);
            if (wp.y < minY || wp.y > maxY) continue;
            sx += wp.x;
            sz += wp.z;
            cnt++;
        }
        return cnt > 0 ? new Vector3(sx / cnt, 0f, sz / cnt) : Vector3.zero;
    }

    // ── ICP — Iterative Closest Point (point-to-point, translation + yaw only) ─
    // Runs up to `icpIterations` steps.  Each step:
    //   1. Subsample both clouds to `icpSamples` random points.
    //   2. For each sample in cloud 1, find the nearest point in cloud 0.
    //   3. Compute the XZ translation that minimises mean closest-point distance.
    //   4. Also compute the yaw correction via SVD of the XZ cross-covariance.
    //   5. Apply the correction to K1's anchor.

    private const int IcpIterations = 10;
    private const int IcpSamples    = 800;

    void PerformICP()
    {
        var clouds = bridgeManager?.SensorClouds;
        if (clouds == null || clouds[0].Count < 50 || clouds[1].Count < 50)
        { SetStatus("ICP: need sensor data from both clouds first."); return; }

        var c0 = clouds[0];
        var c1 = clouds[1];
        var k1 = bridgeManager.kinect1Anchor;
        if (!k1) { SetStatus("ICP: K1 anchor not assigned."); return; }

        float totalErr = 0f;
        for (int iter = 0; iter < IcpIterations; iter++)
        {
            // Subsample source (c1) and target (c0)
            var src = SampleCloud(c1, IcpSamples);
            var tgt = SampleCloud(c0, IcpSamples);

            // For each src point find nearest tgt point
            Vector3 srcMean = Vector3.zero, tgtMean = Vector3.zero;
            var pairs = new (Vector3 s, Vector3 t)[src.Count];
            totalErr = 0f;
            for (int i = 0; i < src.Count; i++)
            {
                Vector3 nearest = NearestPoint(src[i], tgt);
                pairs[i] = (src[i], nearest);
                srcMean += src[i];
                tgtMean += nearest;
                totalErr += Vector3.Distance(src[i], nearest);
            }
            srcMean /= src.Count;
            tgtMean /= src.Count;
            totalErr /= src.Count;

            // XZ-only cross covariance for yaw estimation (SVD 2×2)
            double sxx = 0, sxz = 0, szx = 0, szz = 0;
            for (int i = 0; i < pairs.Length; i++)
            {
                double dsx = pairs[i].s.x - srcMean.x, dsz = pairs[i].s.z - srcMean.z;
                double dtx = pairs[i].t.x - tgtMean.x, dtz = pairs[i].t.z - tgtMean.z;
                sxx += dsx * dtx;  sxz += dsx * dtz;
                szx += dsz * dtx;  szz += dsz * dtz;
            }
            // 2×2 SVD via angle: H = [sxx sxz; szx szz]
            double theta = System.Math.Atan2(szx - sxz, sxx + szz);
            float  yawDeg = (float)(theta * Mathf.Rad2Deg);

            // Translation: tgtMean - R(yaw) * srcMean  (XZ only)
            float cosT = Mathf.Cos((float)theta), sinT = Mathf.Sin((float)theta);
            float tx = tgtMean.x - (cosT * srcMean.x - sinT * srcMean.z);
            float tz = tgtMean.z - (sinT * srcMean.x + cosT * srcMean.z);

            // Clamp corrections to avoid divergence
            float maxT = 0.15f, maxA = 5f;
            float mag = Mathf.Sqrt(tx*tx + tz*tz);
            if (mag > maxT) { tx *= maxT/mag; tz *= maxT/mag; }
            yawDeg = Mathf.Clamp(yawDeg, -maxA, maxA);

            k1.position    += new Vector3(tx, 0f, tz);
            k1.eulerAngles += new Vector3(0f, yawDeg, 0f);
        }

        SaveCalibration();
        SetStatus($"ICP done. Mean error: {totalErr*100f:F1} cm");
    }

    // ── Model-anchored ICP — aligns each sensor against a known room mesh ─────
    // Far more stable than cloud-to-cloud ICP because the room is ground truth.
    // Each iteration: transform a fixed snapshot of source points by the
    // accumulated correction, find their nearest neighbours in the model
    // sample set, compute the optimal XZ-yaw + XYZ-translation update.
    // At the end, apply the full accumulated rigid transform to the anchor.

    void PerformModelICP()
    {
        if (!roomModel || !roomModel.Ready)
        { SetStatus("Model ICP: assign a RoomModel with a mesh first."); return; }

        var clouds = bridgeManager?.SensorClouds;
        if (clouds == null || clouds.Length < 2)
        { SetStatus("Model ICP: no sensor data yet."); return; }

        float err0 = AlignAnchorToModel(bridgeManager.kinect0Anchor, clouds[0]);
        float err1 = AlignAnchorToModel(bridgeManager.kinect1Anchor, clouds[1]);

        SaveCalibration();
        SetStatus($"Model ICP done.  K0 err {err0*100f:F1} cm   K1 err {err1*100f:F1} cm");
    }

    private float AlignAnchorToModel(Transform anchor, PointCloudData cloud)
    {
        if (!anchor || cloud == null || cloud.Count < 100) return 0f;

        var modelPts = roomModel.Samples;

        // Snapshot source samples in current world space
        var srcList = SampleCloud(cloud, IcpSamples);
        var src     = srcList.ToArray();

        // Accumulated correction (world frame, applied around world origin)
        Quaternion cumR = Quaternion.identity;
        Vector3    cumT = Vector3.zero;
        float      lastErr = 0f;

        for (int iter = 0; iter < IcpIterations; iter++)
        {
            // Pair each src with its nearest model point
            Vector3 srcMean = Vector3.zero, tgtMean = Vector3.zero;
            var tgt = new Vector3[src.Length];
            float err = 0f;
            for (int i = 0; i < src.Length; i++)
            {
                tgt[i] = NearestInArray(src[i], modelPts);
                srcMean += src[i];
                tgtMean += tgt[i];
                err     += Vector3.Distance(src[i], tgt[i]);
            }
            srcMean /= src.Length;
            tgtMean /= src.Length;
            lastErr  = err / src.Length;

            // XZ-only SVD → yaw delta (floor-plane rotation only)
            double sxx = 0, sxz = 0, szx = 0, szz = 0;
            for (int i = 0; i < src.Length; i++)
            {
                double dsx = src[i].x - srcMean.x, dsz = src[i].z - srcMean.z;
                double dtx = tgt[i].x - tgtMean.x, dtz = tgt[i].z - tgtMean.z;
                sxx += dsx * dtx;  sxz += dsx * dtz;
                szx += dsz * dtx;  szz += dsz * dtz;
            }
            double theta = System.Math.Atan2(szx - sxz, sxx + szz);

            // XYZ translation (allowed all three axes — F1 already set Y roughly)
            float cosT = Mathf.Cos((float)theta), sinT = Mathf.Sin((float)theta);
            float tx = tgtMean.x - (cosT * srcMean.x - sinT * srcMean.z);
            float ty = tgtMean.y - srcMean.y;
            float tz = tgtMean.z - (sinT * srcMean.x + cosT * srcMean.z);

            // Clamp per-iter step to avoid divergence
            float maxT = 0.12f, maxA = 4f;
            float mag = Mathf.Sqrt(tx*tx + ty*ty + tz*tz);
            if (mag > maxT) { float s = maxT / mag; tx *= s; ty *= s; tz *= s; }
            float yawDegStep = Mathf.Clamp((float)(theta * Mathf.Rad2Deg), -maxA, maxA);

            // Apply this step to the local src snapshot (for next iter's nearest search)
            Quaternion stepR = Quaternion.Euler(0f, yawDegStep, 0f);
            Vector3    stepT = new Vector3(tx, ty, tz);
            for (int i = 0; i < src.Length; i++)
                src[i] = stepR * src[i] + stepT;

            // Accumulate world-frame correction:
            //   new(p) = stepR * (cumR * p + cumT) + stepT
            //         = (stepR * cumR) * p + (stepR * cumT + stepT)
            cumR = stepR * cumR;
            cumT = stepR * cumT + stepT;
        }

        // Apply accumulated rigid transform to the anchor:
        //   newPos = cumR * oldPos + cumT
        //   newRot = cumR * oldRot
        anchor.position = cumR * anchor.position + cumT;
        anchor.rotation = cumR * anchor.rotation;

        return lastErr;
    }

    private static Vector3 NearestInArray(Vector3 query, Vector3[] arr)
    {
        float   best = float.MaxValue;
        Vector3 hit  = arr[0];
        for (int i = 0; i < arr.Length; i++)
        {
            float dx = arr[i].x - query.x, dy = arr[i].y - query.y, dz = arr[i].z - query.z;
            float d2 = dx*dx + dy*dy + dz*dz;
            if (d2 < best) { best = d2; hit = arr[i]; }
        }
        return hit;
    }

    private static List<Vector3> SampleCloud(PointCloudData cloud, int n)
    {
        var result = new List<Vector3>(n);
        int step = Mathf.Max(1, cloud.Count / n);
        for (int i = 0; i < cloud.Count && result.Count < n; i += step)
            result.Add(cloud.Positions[i]);
        return result;
    }

    private static Vector3 NearestPoint(Vector3 query, List<Vector3> pts)
    {
        float   best = float.MaxValue;
        Vector3 hit  = pts[0];
        foreach (var p in pts)
        {
            float d2 = (p.x-query.x)*(p.x-query.x) +
                       (p.y-query.y)*(p.y-query.y) +
                       (p.z-query.z)*(p.z-query.z);
            if (d2 < best) { best = d2; hit = p; }
        }
        return hit;
    }

    void SetStatus(string msg) { _autoCalibStatus = msg; _autoCalibStatusTimer = 5f; Debug.Log($"[Calib] {msg}"); }

    // ── Apply settings to components ─────────────────────────────────────────

    void Apply()
    {
        if (bridgeManager)
        {
            bridgeManager.minDepth           = _minDepth;
            bridgeManager.maxDepth           = _maxDepth;
            bridgeManager.densityStep        = _density;
            bridgeManager.playerFilter       = (BridgePointCloudManager.PlayerFilter)_playerFilter;
            bridgeManager.removeOutliers     = _outliers;
            bridgeManager.smoothing          = _smoothing;
            bridgeManager.subtractBackground = _subtractBg;
            bridgeManager.depthSettings.temporalSmoothing = _depthSmooth;
            bridgeManager.depthSettings.deadbandMeters    = _deadband;
        }
        if (cloudRenderer)
        {
            cloudRenderer.pointSize  = _pointSize;
            cloudRenderer.colorMode  = (PointCloudRenderer.ColorMode)_colorMode;
            cloudRenderer.pointShape = (PointCloudRenderer.PointShape)_shape;
            cloudRenderer.gamma            = _gamma;
            cloudRenderer.contrast         = _contrast;
            cloudRenderer.brightness       = _brightness;
            cloudRenderer.highlightRolloff = _rolloff;
            cloudRenderer.floorBrightness  = _floorBright;
        }
    }

    // ── Save / Load ──────────────────────────────────────────────────────────

    public void SaveCalibration()
    {
        var k0 = bridgeManager?.kinect0Anchor;
        var k1 = bridgeManager?.kinect1Anchor;
        var data = new CalibSave
        {
            k0pos = k0 ? new[] { k0.position.x, k0.position.y, k0.position.z } : new float[3],
            k0rot = k0 ? new[] { k0.eulerAngles.x, k0.eulerAngles.y, k0.eulerAngles.z } : new float[3],
            k1pos = k1 ? new[] { k1.position.x, k1.position.y, k1.position.z } : new float[3],
            k1rot = k1 ? new[] { k1.eulerAngles.x, k1.eulerAngles.y, k1.eulerAngles.z } : new float[3],
        };
        try
        {
            string path = CalibrationPaths.GetWritePath(SaveFileName);
            File.WriteAllText(path, JsonUtility.ToJson(data, prettyPrint: true));
            Debug.Log($"[Calib] Saved → {path}");
            SetStatus($"Calibration saved.");
        }
        catch (Exception e) { Debug.LogError($"[Calib] Save failed: {e.Message}"); }
    }

    /// <summary>Reload the most recent saved calibration. Re-runnable at any time from the UI.</summary>
    public void LoadCalibration()
    {
        string path = CalibrationPaths.FindReadPath(SaveFileName);
        if (path == null)
        {
            Debug.Log("[Calib] No saved calibration found.");
            SetStatus("No saved calibration on disk.");
            return;
        }
        try
        {
            var d  = JsonUtility.FromJson<CalibSave>(File.ReadAllText(path));
            var k0 = bridgeManager?.kinect0Anchor;
            var k1 = bridgeManager?.kinect1Anchor;
            if (k0 && d.k0pos?.Length == 3)
            { k0.position    = new Vector3(d.k0pos[0], d.k0pos[1], d.k0pos[2]);
              k0.eulerAngles = new Vector3(d.k0rot[0], d.k0rot[1], d.k0rot[2]); }
            if (k1 && d.k1pos?.Length == 3)
            { k1.position    = new Vector3(d.k1pos[0], d.k1pos[1], d.k1pos[2]);
              k1.eulerAngles = new Vector3(d.k1rot[0], d.k1rot[1], d.k1rot[2]); }
            Debug.Log($"[Calib] Loaded calibration ← {path}");
            SetStatus($"Calibration loaded from {Path.GetFileName(Path.GetDirectoryName(path))}/{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Calib] Load failed: {ex.Message}");
            SetStatus($"Load failed: {ex.Message}");
        }
    }

    // ── GUI ──────────────────────────────────────────────────────────────────

    void OnGUI()
    {
        EnsureStyles();

        if (_showPanel)
            DrawSettingsPanel();

        if (_calibrating)
            DrawCalibOverlay();
        else
        {
            GUI.color = new Color(1, 1, 1, 0.5f);
            GUI.Label(new Rect(10, Screen.height - 22, 500, 18),
                "[H] settings   [C] calibrate   [F1] auto-calib   [F2] ICP   [F3] model ICP");
            GUI.color = Color.white;
        }

        // Auto-calib status toast
        if (_autoCalibStatusTimer > 0f)
        {
            float alpha = Mathf.Clamp01(_autoCalibStatusTimer);
            GUI.color = new Color(1f, 0.85f, 0.2f, alpha);
            float w = 500f;
            GUI.Label(new Rect((Screen.width - w) * 0.5f, Screen.height - 50, w, 22), _autoCalibStatus);
            GUI.color = Color.white;
        }
    }

    private bool _showBlueprint = true;
    private bool _showRendering = true;

    void DrawSettingsPanel()
    {
        // Estimate height dynamically
        float ph = 30f; // top label + space
        ph += _showBlueprint ? 118f : 18f;
        ph += _showRendering ? 258f : 18f;
        ph += 162f; // calib buttons (auto, ICP, model ICP, reload, manual)
        if (_calibrating) ph += 22f;

        _panelRect = new Rect(Screen.width - PanelW - 10, 10, PanelW, ph);
        GUI.color  = new Color(0.08f, 0.08f, 0.08f, 0.9f);
        GUI.DrawTexture(_panelRect, Texture2D.whiteTexture);
        GUI.color  = Color.white;

        GUILayout.BeginArea(_panelRect);
        GUILayout.Space(6);

        // ── Room Blueprint ────────────────────────────────────────────────────
        if (SectionHeader("  ROOM BLUEPRINT", ref _showBlueprint))
        {
            Slider("Width",   ref roomWidth,    1f, 20f, $"{roomWidth:F1} m");
            Slider("Depth",   ref roomDepth,    1f, 20f, $"{roomDepth:F1} m");
            Slider("Height",  ref sensorHeight, 1f,  6f, $"{sensorHeight:F1} m");

            GUILayout.BeginHorizontal();
            GUILayout.Space(8);
            GUILayout.Label("K0 Corner", _labelStyle, GUILayout.Width(80));
            int ci = GUILayout.SelectionGrid((int)k0Corner,
                new[] { "SW", "NW", "NE", "SE" }, 4, GUILayout.Width(210));
            k0Corner = (RoomCorner)ci;
            GUILayout.EndHorizontal();

            GUILayout.Space(3);
            BtnRow("PLACE SENSORS FROM BLUEPRINT", ApplyBlueprintSetup);
        }

        GUILayout.Space(4);

        // ── Background Model ──────────────────────────────────────────────────
        if (SectionHeader("  BACKGROUND MODEL", ref _showBackground))
        {
            var bg = bridgeManager?.backgroundModel;
            if (bg == null)
            {
                GUILayout.BeginHorizontal(); GUILayout.Space(8);
                GUILayout.Label("No BackgroundModel assigned", _labelStyle);
                GUILayout.EndHorizontal();
            }
            else if (bg.IsCapturing)
            {
                float pct = (float)bg.CaptureProgress / bg.captureFrames;
                GUILayout.BeginHorizontal(); GUILayout.Space(8);
                GUILayout.Label($"Capturing… {bg.CaptureProgress}/{bg.captureFrames}", _labelStyle);
                GUILayout.EndHorizontal();
                // Progress bar
                Rect barBg  = GUILayoutUtility.GetRect(PanelW - 24, 10);
                barBg.x    += 8;  barBg.width -= 16;
                GUI.color   = new Color(0.2f, 0.2f, 0.2f);
                GUI.DrawTexture(barBg, Texture2D.whiteTexture);
                var barFill = barBg; barFill.width *= pct;
                GUI.color   = new Color(0.2f, 0.8f, 0.4f);
                GUI.DrawTexture(barFill, Texture2D.whiteTexture);
                GUI.color   = Color.white;
            }
            else
            {
                string modelLabel = bg.HasModel
                    ? $"Model ready ({bg.BackgroundCloud?.Count ?? 0:N0} pts)"
                    : "No model yet";
                GUILayout.BeginHorizontal(); GUILayout.Space(8);
                GUILayout.Label(modelLabel, _labelStyle);
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal(); GUILayout.Space(8);
                if (GUILayout.Button(bg.HasModel ? "RE-CAPTURE ROOM" : "CAPTURE ROOM (3 s)",
                                     GUILayout.Height(24)))
                {
                    bg.StartCapture();
                    _subtractBg = false; // disable subtraction while capturing
                }
                GUILayout.Space(8); GUILayout.EndHorizontal();

                if (bg.HasModel)
                {
                    GUILayout.BeginHorizontal(); GUILayout.Space(8);
                    _subtractBg = GUILayout.Toggle(_subtractBg, " Subtract Background");
                    GUILayout.EndHorizontal();
                }
            }
        }

        GUILayout.Space(4);

        // ── Rendering & Filtering ─────────────────────────────────────────────
        if (SectionHeader("  RENDERING & FILTER", ref _showRendering))
        {
            Slider("Point Size", ref _pointSize,  1f,    25f,   $"{_pointSize:F0} px");
            Slider("Min Depth",  ref _minDepth,   0.1f,  2f,   $"{_minDepth:F2} m");
            Slider("Max Depth",  ref _maxDepth,   1f,    8f,   $"{_maxDepth:F1} m");
            Slider("Depth EMA",  ref _depthSmooth,0f,    0.95f,$"{_depthSmooth:F2}");
            Slider("Deadband",   ref _deadband,   0f,    0.03f,$"{_deadband*1000f:F0} mm");
            Slider("Gamma",      ref _gamma,      0.5f,  3f,   $"{_gamma:F2}");
            Slider("Contrast",   ref _contrast,   0.5f,  2f,   $"{_contrast:F2}");
            Slider("Brightness", ref _brightness, -0.5f, 0.5f, $"{_brightness:F2}");
            Slider("Hi Rolloff", ref _rolloff,    0f,    1f,   $"{_rolloff:F2}");
            Slider("Dark Lift",  ref _floorBright,0f,    0.6f, $"{_floorBright:F2}");
            DensityRow();
            ColorModeRow();
            ShapeRow();
            PlayerFilterRow();

            GUILayout.BeginHorizontal();
            GUILayout.Space(8);
            GUILayout.Label("Outliers", _labelStyle, GUILayout.Width(80));
            _outliers = GUILayout.Toggle(_outliers, _outliers ? " Remove" : " Keep");
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            BtnRow("CLEAR MODE", ApplyClearMode);
        }

        GUILayout.Space(4);

        // ── Calibration ───────────────────────────────────────────────────────
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        GUI.color = new Color(0.6f, 0.6f, 0.6f);
        GUILayout.Label("── CALIBRATION ──", _labelStyle);
        GUI.color = Color.white;
        GUILayout.EndHorizontal();

        GUILayout.Space(2);
        BtnRow("AUTO CALIBRATE  [F1]", PerformAutoCalibration);
        GUILayout.Space(2);
        BtnRow("ICP REFINE  [F2]",     PerformICP);
        GUILayout.Space(2);
        BtnRow("ICP TO ROOM MODEL  [F3]", PerformModelICP);
        GUILayout.Space(2);
        BtnRow("RELOAD SAVED CALIBRATION", LoadCalibration);
        GUILayout.Space(2);

        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        if (GUILayout.Button(_calibrating ? "● EXIT MANUAL CALIB  [C]" : "  MANUAL CALIB  [C]",
                             GUILayout.Height(26)))
            ToggleCalib();
        GUILayout.Space(8);
        GUILayout.EndHorizontal();

        GUILayout.Space(6);
        GUILayout.EndArea();
    }

    // Draws a collapsible section header. Returns true if expanded.
    bool SectionHeader(string label, ref bool expanded)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(4);
        string arrow = expanded ? "▼" : "▶";
        if (GUILayout.Button($"{arrow} {label}", _labelStyle, GUILayout.ExpandWidth(true)))
            expanded = !expanded;
        GUILayout.EndHorizontal();
        return expanded;
    }

    void BtnRow(string label, Action action)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        if (GUILayout.Button(label, GUILayout.Height(24))) action();
        GUILayout.Space(8);
        GUILayout.EndHorizontal();
    }

    void ApplyClearMode()
    {
        _pointSize  = 3f;
        _density    = 1;      // full density
        _minDepth   = 0.5f;
        _maxDepth   = 4.0f;
        _shape      = 1;      // square — no discard overdraw, sharper at small sizes
        _colorMode  = 0;      // RGB
        _gamma      = 1.35f;
        _contrast   = 1.05f;
        _brightness  = -0.05f;
        _rolloff     = 0.75f;
        _floorBright = 0.18f;
    }

    void DrawCalibOverlay()
    {
        var k0 = bridgeManager?.kinect0Anchor;
        var k1 = bridgeManager?.kinect1Anchor;
        Transform active = _target == CalibTarget.Kinect0 ? k0
                         : _target == CalibTarget.Kinect1 ? k1
                         : null;

        string posStr = active
            ? $"Pos  {active.position.x:F3}  {active.position.y:F3}  {active.position.z:F3}"
            : "Both sensors";
        string rotStr = active
            ? $"Rot  {active.eulerAngles.x:F1}°  {active.eulerAngles.y:F1}°  {active.eulerAngles.z:F1}°"
            : "";

        float bw = 440f, bh = 165f;
        float bx = (Screen.width - bw) * 0.5f, by = Screen.height - bh - 10;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.DrawTexture(new Rect(bx, by, bw, bh), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(bx + 10, by + 8, bw - 20, bh - 16));

        GUI.color = new Color(1f, 0.4f, 0.1f);
        GUILayout.Label("● CALIBRATION MODE", _headerStyle);
        GUI.color = Color.white;

        string targetName = _target switch { CalibTarget.Kinect0 => "KINECT 0  (cyan)",
                                             CalibTarget.Kinect1 => "KINECT 1  (magenta)",
                                             _                   => "BOTH SENSORS" };
        GUILayout.Label($"Target: {targetName}   [Tab] to cycle", _labelStyle);
        GUILayout.Label(posStr, _labelStyle);
        if (rotStr != "") GUILayout.Label(rotStr, _labelStyle);

        GUILayout.Space(6);
        GUI.color = new Color(0.7f, 0.7f, 0.7f);
        GUILayout.Label("WASD/QE  world translate   |   IJKL/UO  local rotate", _labelStyle);
        GUILayout.Label("Shift=5×fast   Ctrl=0.1×fine   [Enter] save   [Esc] exit", _labelStyle);
        GUI.color = Color.white;

        GUILayout.EndArea();
    }

    // ── GUI helpers ───────────────────────────────────────────────────────────

    void Slider(string lbl, ref float val, float min, float max, string display)
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        GUILayout.Label(lbl, _labelStyle, GUILayout.Width(80));
        val = GUILayout.HorizontalSlider(val, min, max, GUILayout.Width(155));
        GUILayout.Label(display, _labelStyle, GUILayout.Width(55));
        GUILayout.EndHorizontal();
    }

    void DensityRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        GUILayout.Label("Density", _labelStyle, GUILayout.Width(80));
        string[] opts = { "Full", "½", "¼", "⅛" };
        int[] vals    = { 1, 2, 4, 8 };
        int cur = Array.IndexOf(vals, _density); if (cur < 0) cur = 0;
        int sel = GUILayout.SelectionGrid(cur, opts, 4, GUILayout.Width(210));
        _density = vals[sel];
        GUILayout.EndHorizontal();
    }

    void ColorModeRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        GUILayout.Label("Color", _labelStyle, GUILayout.Width(80));
        _colorMode = GUILayout.SelectionGrid(_colorMode, new[] { "RGB", "Depth Heat" }, 2, GUILayout.Width(210));
        GUILayout.EndHorizontal();
    }

    void ShapeRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        GUILayout.Label("Shape", _labelStyle, GUILayout.Width(80));
        _shape = GUILayout.SelectionGrid(_shape, new[] { "Circle", "Square" }, 2, GUILayout.Width(210));
        GUILayout.EndHorizontal();
    }

    void PlayerFilterRow()
    {
        GUILayout.BeginHorizontal();
        GUILayout.Space(8);
        GUILayout.Label("Show", _labelStyle, GUILayout.Width(80));
        _playerFilter = GUILayout.SelectionGrid(_playerFilter,
            new[] { "All", "Players", "Bg Only" }, 3, GUILayout.Width(210));
        GUILayout.EndHorizontal();
    }

    void EnsureStyles()
    {
        if (_stylesReady) return;
        _headerStyle = new GUIStyle(GUI.skin.label)
        {
            fontStyle = FontStyle.Bold,
            fontSize  = 12,
            normal    = { textColor = Color.white }
        };
        _labelStyle = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            normal   = { textColor = new Color(0.85f, 0.85f, 0.85f) }
        };
        _stylesReady = true;
    }

    // ── Serialisation types ──────────────────────────────────────────────────
    [Serializable]
    private class CalibSave
    {
        public float[] k0pos, k0rot, k1pos, k1rot;
    }
}
