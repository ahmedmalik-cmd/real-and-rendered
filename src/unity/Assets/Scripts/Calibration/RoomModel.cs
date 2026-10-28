using UnityEngine;

/// <summary>
/// Wraps a static room mesh (imported from GLB/FBX) and exposes a cached
/// world-space sample set for model-anchored ICP calibration.
///
/// Setup:
///   1. Drag your room scan (GLB/FBX) into Assets/Models. Unity imports
///      it as a prefab with a MeshFilter+MeshRenderer hierarchy.
///   2. Drag the imported prefab into the scene as a child of "RoomModel".
///   3. Position/rotate it so the floor is at world Y=0 and walls roughly
///      align with the world X/Z axes — the F3 ICP will fine-tune from there.
///   4. Assign the inner MeshFilter to this component's `sourceMesh` field.
///   5. Toggle `renderModel` off once aligned — the mesh is then used only
///      as an invisible reference for ICP and (later) boundary clipping.
///
/// GLB import: Unity 2022+ has built-in glTF importer when the Unity GLTFast
/// package is installed (Package Manager → "Add by name" → com.unity.cloud.gltfast).
/// Without it, convert your GLB to FBX in Blender (File → Export → FBX).
/// </summary>
public class RoomModel : MonoBehaviour
{
    [Header("Mesh")]
    [Tooltip("MeshFilter holding the imported room scan. Required.")]
    public MeshFilter sourceMesh;

    [Tooltip("How many vertices to sample as ICP targets. 3000–8000 is plenty; " +
             "higher = more accurate but slower per ICP iteration.")]
    [Range(500, 20000)] public int sampleCount = 5000;

    [Header("Display")]
    public bool renderModel = true;

    public Vector3[] Samples { get; private set; }
    public bool      Ready   => Samples != null && Samples.Length > 0;

    void Start() => Rebuild();

    [ContextMenu("Rebuild Samples")]
    public void Rebuild()
    {
        if (!sourceMesh || !sourceMesh.sharedMesh) { Samples = null; return; }

        var verts = sourceMesh.sharedMesh.vertices;
        if (verts.Length == 0) { Samples = null; return; }

        int n = Mathf.Min(sampleCount, verts.Length);
        Samples = new Vector3[n];

        Transform tr = sourceMesh.transform;
        // Evenly stride through the vertex array for spatial diversity
        float step = (float)verts.Length / n;
        for (int i = 0; i < n; i++)
        {
            int idx = Mathf.Clamp(Mathf.FloorToInt(i * step), 0, verts.Length - 1);
            Samples[i] = tr.TransformPoint(verts[idx]);
        }

        Debug.Log($"[RoomModel] Cached {n:N0} sample points from {verts.Length:N0} mesh vertices.");
    }

    void Update()
    {
        if (sourceMesh)
        {
            var mr = sourceMesh.GetComponent<MeshRenderer>();
            if (mr && mr.enabled != renderModel) mr.enabled = renderModel;
        }
    }

    /// <summary>
    /// Nearest-neighbor in the cached sample set (O(n) brute force).
    /// Good for &lt;10k samples; faster than a kd-tree at that size.
    /// </summary>
    public Vector3 NearestSample(Vector3 query)
    {
        var arr = Samples;
        float best = float.MaxValue;
        Vector3 hit = arr[0];
        for (int i = 0; i < arr.Length; i++)
        {
            Vector3 d = arr[i] - query;
            float d2 = d.x * d.x + d.y * d.y + d.z * d.z;
            if (d2 < best) { best = d2; hit = arr[i]; }
        }
        return hit;
    }

#if UNITY_EDITOR
    void OnDrawGizmosSelected()
    {
        if (!Ready) return;
        Gizmos.color = new Color(0.4f, 1f, 0.6f, 0.4f);
        // Draw bounding box of samples
        Vector3 min = Samples[0], max = Samples[0];
        for (int i = 1; i < Samples.Length; i++)
        {
            min = Vector3.Min(min, Samples[i]);
            max = Vector3.Max(max, Samples[i]);
        }
        Gizmos.DrawWireCube((min + max) * 0.5f, max - min);
    }
#endif
}
