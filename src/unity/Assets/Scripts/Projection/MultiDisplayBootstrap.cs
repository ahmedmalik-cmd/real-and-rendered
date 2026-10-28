using System;
using UnityEngine;

/// <summary>
/// Multi-display initialiser. Activates every Display slot Unity reports so
/// cameras can target Display 1+ in builds.
///
/// We deliberately do NOT touch Screen.fullScreenMode here — that broke
/// cursor / input handling on multi-monitor setups. Set fullscreen mode the
/// normal way: Project Settings → Player → Resolution and Presentation.
/// </summary>
[DefaultExecutionOrder(-1000)]
public class MultiDisplayBootstrap : MonoBehaviour
{
    public bool   verboseLogging   = true;
    public bool   showHud          = true;
    public KeyCode hudKey          = KeyCode.F9;
    public KeyCode forceActivateKey = KeyCode.F10;

    private GUIStyle _style;

    void Awake()
    {
        LogDisplays("Awake");
        ActivateAll();
    }

    void Update()
    {
        if (Input.GetKeyDown(hudKey))         showHud = !showHud;
        if (Input.GetKeyDown(forceActivateKey))
        {
            Debug.Log("[Display] Force re-activate ALL displays (F10)");
            ActivateAll();
            LogDisplays("after F10");
        }
    }

    void ActivateAll()
    {
        for (int i = 1; i < Display.displays.Length; i++)
        {
            try { Display.displays[i].Activate(); }
            catch (Exception e) { if (verboseLogging) Debug.LogWarning($"[Display] Activate({i}): {e.Message}"); }
        }
    }

    void LogDisplays(string ctx)
    {
        if (!verboseLogging) return;
        Debug.Log($"[Display] ({ctx}) Unity slots: {Display.displays.Length}  " +
                  $"editor={Application.isEditor}  fullscreenMode={Screen.fullScreenMode}");
        for (int i = 0; i < Display.displays.Length; i++)
        {
            var d = Display.displays[i];
            Debug.Log($"[Display]   slot {i}: system={d.systemWidth}x{d.systemHeight}  " +
                      $"rendering={d.renderingWidth}x{d.renderingHeight}  active={d.active}");
        }
    }

    void OnGUI()
    {
        if (!showHud) return;
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                normal   = { textColor = new Color(0.9f, 0.9f, 0.9f) }
            };
        }

        float w = 360f, h = 24f + Display.displays.Length * 16f + 18f;
        var r = new Rect(10, Screen.height - h - 200, w, h);
        GUI.color = new Color(0, 0, 0, 0.78f);
        GUI.DrawTexture(r, Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(r.x + 8, r.y + 4, r.width - 16, r.height - 8));
        GUILayout.Label("DISPLAYS  [F9 hide]  [F10 re-activate]", _style);
        for (int i = 0; i < Display.displays.Length; i++)
        {
            var d = Display.displays[i];
            string state;
            if (d.active && d.systemWidth > 0)
                state = $"ACTIVE  {d.systemWidth}x{d.systemHeight}";
            else if (d.systemWidth > 0)
                state = $"detected {d.systemWidth}x{d.systemHeight}  not active";
            else
                state = "— not present —";
            GUI.color = d.active ? new Color(0.4f, 1f, 0.5f) : new Color(0.8f, 0.5f, 0.4f);
            GUILayout.Label($"  slot {i}: {state}", _style);
        }
        GUI.color = Color.white;
        GUILayout.EndArea();
    }
}
