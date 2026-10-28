using UnityEngine;

/// <summary>
/// Renders the static averaged background cloud from BackgroundModel.
/// Because it's a single averaged cloud (not re-uploaded every frame),
/// the room looks rock-solid with zero flicker.
/// </summary>
[RequireComponent(typeof(BackgroundModel))]
public class BackgroundRenderer : MonoBehaviour
{
    public Material pointCloudMaterial;
    [Range(1f, 20f)] public float pointSize = 4f;

    private BackgroundModel _model;
    private ComputeBuffer   _buf;
    private int             _count;

    private struct PointGPU { public Vector3 position; public uint color; }

    private static readonly int PPts  = Shader.PropertyToID("_Points");
    private static readonly int PSize = Shader.PropertyToID("_PointSize");
    private static readonly int PMode = Shader.PropertyToID("_ColorMode");
    private static readonly int PShape= Shader.PropertyToID("_Shape");

    void Awake()
    {
        _model = GetComponent<BackgroundModel>();
        _model.OnModelReady += UploadBackground;
    }

    void UploadBackground()
    {
        var cloud = _model.BackgroundCloud;
        if (cloud == null || cloud.Count == 0) return;

        _buf?.Release();
        _buf = new ComputeBuffer(cloud.Count, 16);

        var scratch = new PointGPU[cloud.Count];
        for (int i = 0; i < cloud.Count; i++)
        {
            Color32 c = cloud.Colors[i];
            scratch[i].position = cloud.Positions[i];
            scratch[i].color    = (uint)(c.r | (c.g << 8) | (c.b << 16) | (255u << 24));
        }
        _buf.SetData(scratch);
        _count = cloud.Count;
        Debug.Log($"[BGRenderer] Uploaded {_count:N0} background points.");
    }

    void OnRenderObject()
    {
        if (_count == 0 || _buf == null || !pointCloudMaterial) return;
        pointCloudMaterial.SetBuffer(PPts,  _buf);
        pointCloudMaterial.SetFloat(PSize,  pointSize);
        pointCloudMaterial.SetInt(PMode,    0);    // RGB
        pointCloudMaterial.SetInt(PShape,   1);    // square — sharper at small sizes
        pointCloudMaterial.SetPass(0);
        Graphics.DrawProceduralNow(MeshTopology.Triangles, _count * 6);
    }

    void OnDestroy() => _buf?.Release();
}
