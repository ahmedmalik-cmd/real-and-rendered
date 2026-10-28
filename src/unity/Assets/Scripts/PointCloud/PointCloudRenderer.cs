using UnityEngine;

/// <summary>
/// GPU point-cloud renderer. Normal mode = merged RGB cloud.
/// Calibration mode = per-sensor clouds with cyan (K0) / magenta (K1) tint.
/// Clear mode = full density, small sharp square points, tight depth range.
/// </summary>
public class PointCloudRenderer : MonoBehaviour
{
    public enum ColorMode  { RGB, DepthHeat }
    public enum PointShape { Circle, Square }

    [Header("References")]
    public BridgePointCloudManager bridgeManager;
    public Material                pointCloudMaterial;

    [Header("Rendering")]
    [Range(1f, 30f)] public float pointSize    = 5f;
    public ColorMode  colorMode                = ColorMode.RGB;
    public PointShape pointShape               = PointShape.Circle;
    [Range(0f, 10f)] public float heatMinDepth = 0.4f;
    [Range(0f, 10f)] public float heatMaxDepth = 5f;

    [Header("Color Correction")]
    [Range(0.5f, 3f)]  public float gamma      = 1.35f;   // > 1 = darker highlights
    [Range(0.5f, 2f)]  public float contrast   = 1.05f;
    [Range(-0.5f, 0.5f)] public float brightness = -0.05f;
    [Range(0f, 1f)]    public float highlightRolloff   = 0.75f;
    [Range(0.5f, 1f)]  public float rolloffThreshold   = 0.78f;
    [Tooltip("Dark-room lift: darkest points glow at this baseline brightness. " +
             "Raises HSV value only, so colours are preserved (no wash-out).")]
    [Range(0f, 0.6f)]  public float floorBrightness    = 0.18f;

    [Header("Calibration")]
    public bool calibrationMode    = false;
    [Range(0f, 1f)] public float calibTintStrength = 0.75f;

    [Header("Debug")]
    public bool testMode     = false;
    public bool showDebugHUD = true;

    // GPU layout (stride 16) — must match PointData in PointCloud.shader
    private struct PointGPU { public Vector3 position; public uint color; }

    private ComputeBuffer  _buf;
    private PointGPU[]     _scratch;
    private int            _activeCount;
    private PointCloudData _testCloud;

    private static readonly Color32 TintK0 = new Color32(0,   220, 255, 255); // cyan
    private static readonly Color32 TintK1 = new Color32(255, 0,   200, 255); // magenta

    private static readonly int PPts    = Shader.PropertyToID("_Points");
    private static readonly int PSize   = Shader.PropertyToID("_PointSize");
    private static readonly int PMode   = Shader.PropertyToID("_ColorMode");
    private static readonly int PHMin   = Shader.PropertyToID("_HeatMin");
    private static readonly int PHMax   = Shader.PropertyToID("_HeatMax");
    private static readonly int PShape  = Shader.PropertyToID("_Shape");
    private static readonly int PGamma  = Shader.PropertyToID("_Gamma");
    private static readonly int PCont   = Shader.PropertyToID("_Contrast");
    private static readonly int PBright = Shader.PropertyToID("_Brightness");
    private static readonly int PRoll   = Shader.PropertyToID("_HighlightRolloff");
    private static readonly int PRollT  = Shader.PropertyToID("_RolloffThreshold");
    private static readonly int PFloor  = Shader.PropertyToID("_FloorBrightness");

    void Start()
    {
        int cap  = 640 * 480 * 2;
        _scratch = new PointGPU[cap];
        _buf     = new ComputeBuffer(cap, 16, ComputeBufferType.Default);
        _testCloud = MakeTestSphere(10000);
    }

    void LateUpdate()
    {
        int n = BuildScratch();
        if (n == 0) { _activeCount = 0; return; }
        _buf.SetData(_scratch, 0, 0, n);
        _activeCount = n;
    }

    void OnRenderObject()
    {
        if (_activeCount == 0 || !pointCloudMaterial) return;
        pointCloudMaterial.SetBuffer(PPts,    _buf);
        pointCloudMaterial.SetFloat(PSize,   pointSize);
        pointCloudMaterial.SetInt(PMode,     (int)colorMode);
        pointCloudMaterial.SetFloat(PHMin,   heatMinDepth);
        pointCloudMaterial.SetFloat(PHMax,   heatMaxDepth);
        pointCloudMaterial.SetInt(PShape,    (int)pointShape);
        pointCloudMaterial.SetFloat(PGamma,  gamma);
        pointCloudMaterial.SetFloat(PCont,   contrast);
        pointCloudMaterial.SetFloat(PBright, brightness);
        pointCloudMaterial.SetFloat(PRoll,   highlightRolloff);
        pointCloudMaterial.SetFloat(PRollT,  rolloffThreshold);
        pointCloudMaterial.SetFloat(PFloor,  floorBrightness);
        pointCloudMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, _activeCount * 6);
    }

    void OnGUI()
    {
        if (!showDebugHUD) return;
        string status = testMode ? "TEST SPHERE" : bridgeManager?.BridgeStatus ?? "no manager";
        GUI.color = _activeCount > 0 ? Color.green : Color.yellow;
        GUI.Label(new Rect(10, 10, Screen.width, 20),
            $"[PointCloud] {status}  |  rendering {_activeCount:N0} pts");
        GUI.color = Color.white;
    }

    // ── Build scratch buffer ─────────────────────────────────────────────────

    private int BuildScratch()
    {
        if (testMode) return Pack(_testCloud, null, 0);
        if (!bridgeManager) return 0;

        if (calibrationMode && bridgeManager.SensorClouds != null)
        {
            // Full RGB — misalignment shows as a double image of the scene,
            // correct alignment merges them into one clean picture.
            int n = 0;
            n = Pack(bridgeManager.SensorClouds[0], null, n);
            n = Pack(bridgeManager.SensorClouds[1], null, n);
            return n;
        }

        return Pack(bridgeManager.MergedCloud, null, 0);
    }

    private int Pack(PointCloudData cloud, Color32? tint, int offset)
    {
        if (cloud == null || cloud.Count == 0) return offset;
        var pos  = cloud.Positions;
        var col  = cloud.Colors;
        int lim  = Mathf.Min(cloud.Count, _scratch.Length - offset);
        float ts = calibTintStrength;
        float ks = 1f - ts;

        if (tint.HasValue)
        {
            Color32 t = tint.Value;
            for (int i = 0; i < lim; i++)
            {
                Color32 c = col[i];
                byte r = (byte)(c.r * ks + t.r * ts);
                byte g = (byte)(c.g * ks + t.g * ts);
                byte b = (byte)(c.b * ks + t.b * ts);
                _scratch[offset + i].position = pos[i];
                _scratch[offset + i].color    = (uint)(r | (g << 8) | (b << 16) | (255u << 24));
            }
        }
        else
        {
            for (int i = 0; i < lim; i++)
            {
                Color32 c = col[i];
                _scratch[offset + i].position = pos[i];
                _scratch[offset + i].color    = (uint)(c.r | (c.g << 8) | (c.b << 16) | (255u << 24));
            }
        }
        return offset + lim;
    }

    private static PointCloudData MakeTestSphere(int n)
    {
        var d = new PointCloudData(n);
        for (int i = 0; i < n; i++)
        {
            Vector3 v = Random.onUnitSphere * 1.5f;
            d.Positions[i] = v;
            float t = v.y * 0.5f + 0.5f;
            d.Colors[i] = new Color32((byte)(t * 255), 80, (byte)((1 - t) * 255), 255);
        }
        d.Count = n;
        return d;
    }

    void OnDestroy() => _buf?.Release();
}
