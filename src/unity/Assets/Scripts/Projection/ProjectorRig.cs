using UnityEngine;

/// <summary>
/// Holds references to the three projector cameras and re-creates any that
/// are missing in the runtime scene. The cameras themselves are normally
/// created at edit-time by SceneSetup (so you can see them in the Hierarchy
/// and preview each display in the Game-view dropdown). This rig is the
/// safety net for the case where the scene was built without them.
/// </summary>
[DefaultExecutionOrder(-900)]
public class ProjectorRig : MonoBehaviour
{
    [Header("References (filled by SceneSetup at edit time)")]
    public Camera           projectorACam;
    public Camera           projectorBCam;
    public CaptionProjector captionProjector;
    public PerceptionReceiver perceptionReceiver;

    [Header("Fallback layout (only used if a camera is missing in the scene)")]
    public Vector3 projectorAPos = new Vector3(-1.6f, 2.0f, -2.6f);
    public Vector3 projectorBPos = new Vector3( 1.6f, 2.0f, -2.6f);
    public Vector3 sceneTarget   = new Vector3( 0f,   0.9f,  0f);
    [Range(20f, 100f)] public float projectorFov = 45f;

    void Awake()
    {
        if (!perceptionReceiver) perceptionReceiver = FindObjectOfType<PerceptionReceiver>();

        // Ensure a ProjectorCalibrator exists on the same GameObject so P-key
        // calibration works even if the scene wasn't re-run through SceneSetup
        // after the calibrator was added.
        var calib = GetComponent<ProjectorCalibrator>();
        if (calib == null)
        {
            calib = gameObject.AddComponent<ProjectorCalibrator>();
            Debug.Log("[ProjectorRig] Added missing ProjectorCalibrator component");
        }
        if (calib.rig == null) calib.rig = this;

        // Try to recover by name first (saved-scene cameras)
        if (projectorACam == null)
        {
            var go = GameObject.Find("ProjectorA_Camera");
            if (go) projectorACam = go.GetComponent<Camera>();
        }
        if (projectorBCam == null)
        {
            var go = GameObject.Find("ProjectorB_Camera");
            if (go) projectorBCam = go.GetComponent<Camera>();
        }
        if (captionProjector == null)
        {
            var go = GameObject.Find("ProjectorC_Caption");
            if (go) captionProjector = go.GetComponent<CaptionProjector>();
        }

        // Build any that are still missing
        if (projectorACam == null) projectorACam = Build("ProjectorA_Camera", projectorAPos, 1, "Projector A");
        if (projectorBCam == null) projectorBCam = Build("ProjectorB_Camera", projectorBPos, 2, "Projector B");
        if (captionProjector == null) captionProjector = BuildCaption();

        // CRITICAL: ProjectorWarp may be missing on cameras that were saved
        // into the scene BEFORE the warp was added to the codebase. Add it now
        // so the homography blit pipeline + corner controls actually work.
        EnsureWarp(projectorACam, "Projector A");
        EnsureWarp(projectorBCam, "Projector B");

        // Diagnostic dump
        var cams = FindObjectsOfType<Camera>(true);
        Debug.Log($"[ProjectorRig] {cams.Length} camera(s) in scene:");
        foreach (var c in cams)
            Debug.Log($"[ProjectorRig]   '{c.name}'  display={c.targetDisplay}  enabled={c.enabled}  depth={c.depth}  " +
                      $"warp={(c.GetComponent<ProjectorWarp>() != null ? "yes" : "no")}");
    }

    static void EnsureWarp(Camera cam, string label)
    {
        if (cam == null) return;
        if (cam.GetComponent<ProjectorWarp>() == null)
        {
            cam.gameObject.AddComponent<ProjectorWarp>();
            Debug.Log($"[ProjectorRig] Added missing ProjectorWarp to {label} ({cam.name})");
        }
    }

    Camera Build(string name, Vector3 pos, int displayIdx, string label)
    {
        var go = new GameObject(name);
        go.transform.SetParent(transform, true);
        go.transform.position = pos;
        go.transform.LookAt(sceneTarget);

        // Background-fill camera: renders nothing, just clears the FULL display
        // to black so the area outside the projector camera's viewport rect
        // stays black instead of whatever was there last frame.
        var bgGo = new GameObject(name + "_BG");
        bgGo.transform.SetParent(go.transform, false);
        var bg = bgGo.AddComponent<Camera>();
        bg.clearFlags      = CameraClearFlags.SolidColor;
        bg.backgroundColor = Color.black;
        bg.cullingMask     = 0;
        bg.depth           = -10;          // renders BEFORE the projector cam
        bg.enabled         = true;
        bg.targetDisplay   = displayIdx;
        bg.allowHDR        = false;
        bg.allowMSAA       = false;
        bg.useOcclusionCulling = false;

        // Projector camera — clears full color so it works without needing
        // the framer to paint underneath (safe direct rendering).
        var c = go.AddComponent<Camera>();
        c.clearFlags      = CameraClearFlags.SolidColor;
        c.backgroundColor = Color.black;
        c.fieldOfView     = projectorFov;
        c.farClipPlane    = 30f;
        c.depth           = 0;
        c.enabled         = true;
        c.targetDisplay   = displayIdx;

        var po = go.AddComponent<ProjectorOutput>();
        po.displayIndex   = displayIdx;
        po.projectorLabel = label;

        // Attach quad warp so the beam can be corner-trimmed at runtime
        if (go.GetComponent<ProjectorWarp>() == null)
            go.AddComponent<ProjectorWarp>();

        Debug.Log($"[ProjectorRig] Built missing {label} → Display {displayIdx}");
        return c;
    }

    CaptionProjector BuildCaption()
    {
        var go = new GameObject("ProjectorC_Caption");
        go.transform.SetParent(transform, true);

        // Top-down view of the cloud — camera high above scene origin,
        // pitched straight down. The CaptionProjector overlays the perception
        // caption in a band at the bottom of the display.
        go.transform.position = new Vector3(0f, 4.5f, 0f);
        go.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        var c = go.AddComponent<Camera>();
        c.clearFlags      = CameraClearFlags.SolidColor;
        c.backgroundColor = Color.black;
        c.fieldOfView     = 75f;
        c.farClipPlane    = 30f;
        c.depth           = 0;
        c.enabled         = true;
        c.targetDisplay   = 3;
        c.cullingMask     = ~0;        // everything (cloud + caption canvas)

        var cp = go.AddComponent<CaptionProjector>();
        cp.receiver     = perceptionReceiver;
        cp.displayIndex = 3;
        Debug.Log("[ProjectorRig] Built missing Caption Projector → Display 3 (top-down)");
        return cp;
    }
}
