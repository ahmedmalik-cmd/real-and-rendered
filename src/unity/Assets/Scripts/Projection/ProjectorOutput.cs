using UnityEngine;

/// <summary>
/// Routes a Camera to a specific physical Display (projector). If that display
/// is not available in the build, this camera is DISABLED — it does NOT fall
/// back to Display 0, which would otherwise stack on top of the Main Camera
/// and obscure it.
/// </summary>
[RequireComponent(typeof(Camera))]
public class ProjectorOutput : MonoBehaviour
{
    [Range(0, 7)] public int displayIndex = 1;
    public string projectorLabel = "Projector A";

    private Camera _cam;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cam.clearFlags      = CameraClearFlags.SolidColor;
        _cam.backgroundColor = Color.black;
        _cam.targetDisplay   = Mathf.Clamp(displayIndex, 0, 7);
    }

    void Start()
    {
        // Bootstrap (DefaultExecutionOrder -1000) ran first and activated displays.
        // We just verify our target is actually available; if not, switch off.
        if (Application.isEditor)
        {
            // In Editor, only Display 0 truly renders, but cameras with target >0
            // still render in the Game view dropdown preview. Leave enabled.
            return;
        }

        if (displayIndex < 0 || displayIndex >= Display.displays.Length)
        {
            Debug.LogWarning($"[{projectorLabel}] displayIndex {displayIndex} out of range " +
                             $"(slots={Display.displays.Length}). Disabling camera.");
            _cam.enabled = false;
            return;
        }

        var d = Display.displays[displayIndex];
        if (!d.active || d.systemWidth == 0)
        {
            Debug.LogWarning($"[{projectorLabel}] Display {displayIndex} not active " +
                             $"(systemWidth={d.systemWidth}). Disabling camera so it " +
                             "doesn't stack onto Display 0.");
            _cam.enabled = false;
            return;
        }

        Debug.Log($"[{projectorLabel}] → Display {displayIndex}  " +
                  $"system {d.systemWidth}x{d.systemHeight}  rendering {d.renderingWidth}x{d.renderingHeight}");
    }
}
