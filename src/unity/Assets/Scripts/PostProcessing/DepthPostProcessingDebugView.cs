using UnityEngine;

/// <summary>
/// Bottom-left overlay showing per-sensor post-processing stats.
/// Toggle with F4. F5 bypasses the entire post-processing stage so you can
/// A/B raw vs. cleaned output in the renderer.
/// </summary>
public class DepthPostProcessingDebugView : MonoBehaviour
{
    public BridgePointCloudManager bridgeManager;
    public bool   showHUD = true;
    public KeyCode hudKey = KeyCode.F4;
    // F5 (bypass toggle) is owned by BridgePointCloudManager so it works
    // even when this debug component is not attached.

    private GUIStyle _label;
    private GUIStyle _header;
    private GUIStyle _warn;

    void Awake()
    {
        if (!bridgeManager) bridgeManager = FindObjectOfType<BridgePointCloudManager>();
    }

    void Update()
    {
        if (Input.GetKeyDown(hudKey)) showHUD = !showHUD;
    }

    void EnsureStyles()
    {
        if (_label != null) return;
        _label  = new GUIStyle(GUI.skin.label) { fontSize = 11, normal = { textColor = new Color(0.85f,0.85f,0.85f) } };
        _header = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = Color.white } };
        _warn   = new GUIStyle(GUI.skin.label) { fontSize = 12, fontStyle = FontStyle.Bold, normal = { textColor = new Color(1f, 0.55f, 0.15f) } };
    }

    void OnGUI()
    {
        if (!showHUD || !bridgeManager) return;
        EnsureStyles();

        float w = 460f, h = 184f;
        Rect r = new Rect(10, Screen.height - h - 28, w, h);
        GUI.color = new Color(0f, 0f, 0f, 0.78f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(r.x + 8, r.y + 6, r.width - 16, r.height - 10));
        GUILayout.Label("DEPTH POST-PROCESSING  [F4] hide  [F5] bypass", _header);

        if (bridgeManager.bypassPostProcessing)
            GUILayout.Label("● BYPASSED — rendering raw bridge data", _warn);

        var procs = bridgeManager.Processors;
        if (procs == null) { GUILayout.Label("(no processors)", _label); GUILayout.EndArea(); return; }

        for (int i = 0; i < procs.Length; i++)
        {
            var p = procs[i];
            if (p == null || !p.Ready) { GUILayout.Label($"K{i}: idle", _label); continue; }
            var o = p.Output;
            int pixels    = o.Width * o.Height;
            float covPct  = 100f * o.ValidCount / pixels;
            float platePct = 100f * p.PlateCount / pixels;
            string capStr  = p.IsCapturingPlate
                ? $"  SCANNING {p.CaptureFramesDone}/{p.CaptureFramesGoal}"
                : "";
            GUILayout.Label(
                $"K{i}  {o.ProcessingMs:F1} ms   valid {o.ValidCount,6:N0} ({covPct:F0}%)   " +
                $"filled {o.FilledCount,5:N0}   plate {platePct:F0}%   avgZ {o.AvgDepthMeters:F2} m{capStr}",
                _label);
        }
        GUILayout.Space(2);
        GUILayout.Label("[F5] bypass   [F6] reset plate   [F7] SCAN room (3 s)", _label);

        GUILayout.EndArea();
    }
}
