using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Adds a 4-corner quad-warp pipeline to a projector camera:
///   1. The scene camera now renders to an internal RenderTexture
///      (its previous targetDisplay is moved to a sibling output cam).
///   2. A sibling "output camera" with the original targetDisplay runs a
///      CommandBuffer at the end of its rendering that blits the RT to the
///      display through QuadWarp.shader. The shader places the 4 source-RT
///      corners at the user-specified screen positions.
///
/// Outside the warped quad on the projector beam, no pixels are rasterised,
/// so the framer camera's black remains — the beam is effectively masked.
///
/// All four corners independently adjustable from ProjectorCalibrator at
/// runtime. Persisted via projector_calibration.json.
/// </summary>
[RequireComponent(typeof(Camera))]
public class ProjectorWarp : MonoBehaviour
{
    [Header("Corners (normalized [0,1] on the projector display)")]
    public Vector2 cornerBL = new Vector2(0f, 0f);
    public Vector2 cornerBR = new Vector2(1f, 0f);
    public Vector2 cornerTL = new Vector2(0f, 1f);
    public Vector2 cornerTR = new Vector2(1f, 1f);

    [Header("Internal RT")]
    public int rtWidth  = 1920;
    public int rtHeight = 1080;

    [Header("Edge feathering (in source-UV units; 0 = hard edges)")]
    [Range(0f, 0.2f)] public float featherWidth = 0f;

    // Public so ProjectorCalibrator can find the corners conveniently
    public Camera SceneCamera  { get; private set; }
    public Camera OutputCamera { get; private set; }

    private RenderTexture _rt;
    private Material      _mat;
    private CommandBuffer _cb;

    static readonly int P_HRow0   = Shader.PropertyToID("_HRow0");
    static readonly int P_HRow1   = Shader.PropertyToID("_HRow1");
    static readonly int P_HRow2   = Shader.PropertyToID("_HRow2");
    static readonly int P_Feather = Shader.PropertyToID("_FeatherWidth");
    static readonly int P_MainTex = Shader.PropertyToID("_MainTex");

    void Awake()
    {
        // ── DISABLED ──
        // The warp/homography pipeline is currently disabled to restore the
        // simple direct-to-display projector rendering that was working before.
        // The component stays attached (so the calibrator's corner UI doesn't
        // throw NullReferences) but does no rendering work — scene camera
        // renders straight to its targetDisplay as before, with the framer
        // camera still painting black under it.
        //
        // To re-enable warp later: restore the body of Awake() from version
        // control / earlier in this conversation, then test on one projector
        // first before pushing to production.
        SceneCamera = GetComponent<Camera>();
        // Make sure no leftover state from a previous warp attempt is sticking:
        if (SceneCamera != null)
        {
            SceneCamera.targetTexture   = null;
            SceneCamera.clearFlags      = CameraClearFlags.SolidColor;
            SceneCamera.backgroundColor = Color.black;
        }
        enabled = false;   // skip LateUpdate too
    }

    void LateUpdate()
    {
        if (_mat == null) return;

        // Solve the perspective-correct homography mapping
        //   display normalized position  →  source RT UV
        // so the fragment shader, given a display pixel, samples the right
        // source pixel. Display corners are the user-defined quad on the
        // projector; source corners are the four corners of the source RT.
        HomographySolver.Solve(
            srcBL: cornerBL, srcBR: cornerBR, srcTL: cornerTL, srcTR: cornerTR,
            dstBL: new Vector2(0f, 0f),
            dstBR: new Vector2(1f, 0f),
            dstTL: new Vector2(0f, 1f),
            dstTR: new Vector2(1f, 1f),
            out Vector4 r0, out Vector4 r1, out Vector4 r2);

        _mat.SetVector(P_HRow0,   r0);
        _mat.SetVector(P_HRow1,   r1);
        _mat.SetVector(P_HRow2,   r2);
        _mat.SetFloat (P_Feather, featherWidth);
        _mat.SetTexture(P_MainTex, _rt);
    }

    public void ResetCornersToFullscreen()
    {
        cornerBL = new Vector2(0, 0);
        cornerBR = new Vector2(1, 0);
        cornerTL = new Vector2(0, 1);
        cornerTR = new Vector2(1, 1);
    }

    void OnDestroy()
    {
        if (_cb != null && OutputCamera != null)
            OutputCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, _cb);
        _cb?.Dispose();
        if (_rt) { _rt.Release(); Destroy(_rt); }
        if (_mat) Destroy(_mat);
        if (OutputCamera) Destroy(OutputCamera.gameObject);
    }
}
