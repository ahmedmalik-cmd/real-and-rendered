using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Runtime projector calibration. Two layers of control:
///
///   CAMERA layer  — where the virtual camera sits / aims / its FOV.
///                   Controls what is *rendered* into the projector's RT.
///   WARP layer    — four corners on the projector's display [0,1].
///                   Controls *where* on the physical projector beam the
///                   rendered content lands. Outside the warped quad is
///                   black, so the projector beam is effectively masked.
///
/// Keys (active only in projector-calib mode, toggled with P):
///   Tab                cycle target: Projector A ↔ Projector B
///
///   CAMERA:
///     WASD / QE        translate world XZ / Y
///     IJKL / UO        rotate pitch / yaw / roll
///     = / -            FOV
///
///   WARP CORNERS:
///     1 / 2 / 3 / 4    select corner BL / BR / TL / TR
///     Arrow keys       move selected corner
///     [                reset corners to fullscreen quad
///     ]                center selected corner on display
///
///   COMMON:
///     Shift            5× fast
///     Ctrl             0.2× fine
///     Backspace        reset target (position, rotation, FOV, corners)
///     Enter            save layout to JSON
///     Escape           exit calib mode
///
/// Use camera to frame what you want to project. Then use the 4 corners to
/// drag the projected quad onto exactly the surface you want it on — kills
/// spill light, fixes keystone, shapes the projection to fit the pedestal.
/// </summary>
[DefaultExecutionOrder(-800)]
public class ProjectorCalibrator : MonoBehaviour
{
    public ProjectorRig rig;

    [Header("Speeds (per second; Shift = 5×, Ctrl = 0.2×)")]
    public float translateSpeed = 0.5f;
    public float rotateSpeed    = 30f;
    public float fovSpeed       = 12f;
    public float cornerSpeed    = 0.4f;     // fraction of screen per second

    [Header("Persistence")]
    public bool autoLoadOnStart = true;

    public enum Target { ProjectorA, ProjectorB, CaptionC }
    public enum Corner { BL = 0, BR = 1, TL = 2, TR = 3 }

    private Target _target = Target.ProjectorA;
    private Corner _corner = Corner.BL;
    private bool   _active = false;

    private const string SaveFileName = "projector_calibration.json";

    private GUIStyle _header, _label, _value;

    void Start()
    {
        if (autoLoadOnStart) Load();
    }

    void Update()
    {
        if (Input.GetKeyDown(KeyCode.P))
        {
            _active = !_active;
            Debug.Log($"[ProjCalib] {(_active ? "ENTERED" : "exited")} projector calibration mode");
        }
        if (!_active) return;

        if (Input.GetKeyDown(KeyCode.Escape))     { _active = false; return; }
        if (Input.GetKeyDown(KeyCode.Tab))
        {
            // Cycle A → B → CaptionC → A
            _target = (Target)(((int)_target + 1) % 3);
            Debug.Log($"[ProjCalib] target → {_target}");
            return;
        }
        if (Input.GetKeyDown(KeyCode.Return))     { Save();        return; }
        if (Input.GetKeyDown(KeyCode.Backspace))  { ResetTarget(); return; }

        if (Input.GetKeyDown(KeyCode.Alpha1) || Input.GetKeyDown(KeyCode.Keypad1))
        { _corner = Corner.BL; Debug.Log("[ProjCalib] Corner BL selected"); }
        if (Input.GetKeyDown(KeyCode.Alpha2) || Input.GetKeyDown(KeyCode.Keypad2))
        { _corner = Corner.BR; Debug.Log("[ProjCalib] Corner BR selected"); }
        if (Input.GetKeyDown(KeyCode.Alpha3) || Input.GetKeyDown(KeyCode.Keypad3))
        { _corner = Corner.TL; Debug.Log("[ProjCalib] Corner TL selected"); }
        if (Input.GetKeyDown(KeyCode.Alpha4) || Input.GetKeyDown(KeyCode.Keypad4))
        { _corner = Corner.TR; Debug.Log("[ProjCalib] Corner TR selected"); }

        var t = TargetCamera();
        if (!t) return;
        var w = t.GetComponent<ProjectorWarp>();

        float speed = Input.GetKey(KeyCode.LeftShift)   ? 5f
                    : Input.GetKey(KeyCode.LeftControl) ? 0.2f : 1f;
        float dt = Time.deltaTime;
        float ts = translateSpeed * speed * dt;
        float rs = rotateSpeed    * speed * dt;
        float fs = fovSpeed       * speed * dt;
        float cs = cornerSpeed    * speed * dt;

        // ── Camera position / rotation / FOV ──
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

        float fovDelta = 0f;
        if (Input.GetKey(KeyCode.Equals) || Input.GetKey(KeyCode.KeypadPlus))  fovDelta += fs;
        if (Input.GetKey(KeyCode.Minus)  || Input.GetKey(KeyCode.KeypadMinus)) fovDelta -= fs;

        if (dp != Vector3.zero) t.transform.position += dp;
        if (dr != Vector3.zero) t.transform.Rotate(dr, Space.Self);
        if (fovDelta != 0f)     t.fieldOfView = Mathf.Clamp(t.fieldOfView + fovDelta, 10f, 120f);

        // ── Warp corners ──
        if (w != null)
        {
            Vector2 dCorner = Vector2.zero;
            if (Input.GetKey(KeyCode.UpArrow))    dCorner.y += cs;
            if (Input.GetKey(KeyCode.DownArrow))  dCorner.y -= cs;
            if (Input.GetKey(KeyCode.LeftArrow))  dCorner.x -= cs;
            if (Input.GetKey(KeyCode.RightArrow)) dCorner.x += cs;

            if (dCorner != Vector2.zero)
            {
                Vector2 c = GetCorner(w, _corner);
                c += dCorner;
                c.x = Mathf.Clamp(c.x, -0.5f, 1.5f);
                c.y = Mathf.Clamp(c.y, -0.5f, 1.5f);
                SetCorner(w, _corner, c);
                // Throttled log so we don't spam: log only on initial press
                if (Input.GetKeyDown(KeyCode.UpArrow)   || Input.GetKeyDown(KeyCode.DownArrow) ||
                    Input.GetKeyDown(KeyCode.LeftArrow) || Input.GetKeyDown(KeyCode.RightArrow))
                    Debug.Log($"[ProjCalib] {_target} {_corner} → ({c.x:F2}, {c.y:F2})");
            }

            if (Input.GetKeyDown(KeyCode.LeftBracket))  w.ResetCornersToFullscreen();
            if (Input.GetKeyDown(KeyCode.RightBracket)) SetCorner(w, _corner, new Vector2(0.5f, 0.5f));

            // Edge feather (F = more, V = less). Range clamped 0–0.2.
            if (Input.GetKey(KeyCode.F)) w.featherWidth = Mathf.Min(0.2f,  w.featherWidth + 0.15f * dt * speed);
            if (Input.GetKey(KeyCode.V)) w.featherWidth = Mathf.Max(0f,    w.featherWidth - 0.15f * dt * speed);
        }
    }

    Camera TargetCamera()
    {
        if (!rig) return null;
        switch (_target)
        {
            case Target.ProjectorA: return rig.projectorACam;
            case Target.ProjectorB: return rig.projectorBCam;
            case Target.CaptionC:
                return rig.captionProjector ? rig.captionProjector.GetComponent<Camera>() : null;
        }
        return null;
    }

    void ResetTarget()
    {
        if (!rig) return;
        Camera c = TargetCamera();
        if (c == null) return;

        if (_target == Target.CaptionC)
        {
            // Top-down default for the caption projector
            c.transform.position = new Vector3(0f, 4.5f, 0f);
            c.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            c.fieldOfView        = 75f;
            Debug.Log("[ProjCalib] Reset CaptionC to top-down default");
            return;
        }

        Vector3 pos = _target == Target.ProjectorA ? rig.projectorAPos : rig.projectorBPos;
        c.transform.position = pos;
        c.transform.LookAt(rig.sceneTarget);
        c.fieldOfView = rig.projectorFov;
        var w = c.GetComponent<ProjectorWarp>();
        if (w) w.ResetCornersToFullscreen();
        Debug.Log($"[ProjCalib] Reset {_target} to defaults");
    }

    static Vector2 GetCorner(ProjectorWarp w, Corner k)
    {
        switch (k) {
            case Corner.BL: return w.cornerBL;
            case Corner.BR: return w.cornerBR;
            case Corner.TL: return w.cornerTL;
            case Corner.TR: return w.cornerTR;
        }
        return Vector2.zero;
    }
    static void SetCorner(ProjectorWarp w, Corner k, Vector2 v)
    {
        switch (k) {
            case Corner.BL: w.cornerBL = v; break;
            case Corner.BR: w.cornerBR = v; break;
            case Corner.TL: w.cornerTL = v; break;
            case Corner.TR: w.cornerTR = v; break;
        }
    }

    // ── persistence ──────────────────────────────────────────────────────────

    [Serializable]
    private class Data
    {
        public float[] aPos, aRot;
        public float   aFov;
        public float[] aCorners;     // 8 floats: BL.x, BL.y, BR.x, BR.y, TL.x, TL.y, TR.x, TR.y
        public float   aFeather;
        public float[] bPos, bRot;
        public float   bFov;
        public float[] bCorners;
        public float   bFeather;
        public float[] cPos, cRot;   // caption projector camera (top-down view)
        public float   cFov;
    }

    public void Save()
    {
        if (rig == null || rig.projectorACam == null || rig.projectorBCam == null) return;
        var a = rig.projectorACam;
        var b = rig.projectorBCam;
        var wA = a.GetComponent<ProjectorWarp>();
        var wB = b.GetComponent<ProjectorWarp>();
        var d = new Data
        {
            aPos      = new[] { a.transform.position.x, a.transform.position.y, a.transform.position.z },
            aRot      = new[] { a.transform.eulerAngles.x, a.transform.eulerAngles.y, a.transform.eulerAngles.z },
            aFov      = a.fieldOfView,
            aCorners  = SerialiseCorners(wA),
            aFeather  = wA != null ? wA.featherWidth : 0f,
            bPos      = new[] { b.transform.position.x, b.transform.position.y, b.transform.position.z },
            bRot      = new[] { b.transform.eulerAngles.x, b.transform.eulerAngles.y, b.transform.eulerAngles.z },
            bFov      = b.fieldOfView,
            bCorners  = SerialiseCorners(wB),
            bFeather  = wB != null ? wB.featherWidth : 0f,
        };
        var capCam = rig.captionProjector ? rig.captionProjector.GetComponent<Camera>() : null;
        if (capCam != null)
        {
            d.cPos = new[] { capCam.transform.position.x, capCam.transform.position.y, capCam.transform.position.z };
            d.cRot = new[] { capCam.transform.eulerAngles.x, capCam.transform.eulerAngles.y, capCam.transform.eulerAngles.z };
            d.cFov = capCam.fieldOfView;
        }
        try
        {
            string path = CalibrationPaths.GetWritePath(SaveFileName);
            File.WriteAllText(path, JsonUtility.ToJson(d, prettyPrint: true));
            Debug.Log($"[ProjCalib] Saved → {path}");
        }
        catch (Exception e) { Debug.LogError($"[ProjCalib] Save failed: {e.Message}"); }
    }

    public void Load()
    {
        if (rig == null || rig.projectorACam == null || rig.projectorBCam == null) return;
        string path = CalibrationPaths.FindReadPath(SaveFileName);
        if (path == null) return;
        try
        {
            var d = JsonUtility.FromJson<Data>(File.ReadAllText(path));
            ApplySide(d.aPos, d.aRot, d.aFov, d.aCorners, d.aFeather, rig.projectorACam);
            ApplySide(d.bPos, d.bRot, d.bFov, d.bCorners, d.bFeather, rig.projectorBCam);
            var capCam = rig.captionProjector ? rig.captionProjector.GetComponent<Camera>() : null;
            if (capCam != null && d.cPos != null && d.cPos.Length == 3)
                ApplySide(d.cPos, d.cRot, d.cFov, null, 0f, capCam);
            Debug.Log($"[ProjCalib] Loaded ← {path}");
        }
        catch (Exception e) { Debug.LogWarning($"[ProjCalib] Load failed: {e.Message}"); }
    }

    static float[] SerialiseCorners(ProjectorWarp w)
    {
        if (w == null) return null;
        return new[] {
            w.cornerBL.x, w.cornerBL.y,
            w.cornerBR.x, w.cornerBR.y,
            w.cornerTL.x, w.cornerTL.y,
            w.cornerTR.x, w.cornerTR.y,
        };
    }

    static void ApplySide(float[] pos, float[] rot, float fov, float[] corners, float feather, Camera cam)
    {
        if (cam == null) return;
        if (pos != null && pos.Length == 3)
            cam.transform.position = new Vector3(pos[0], pos[1], pos[2]);
        if (rot != null && rot.Length == 3)
            cam.transform.eulerAngles = new Vector3(rot[0], rot[1], rot[2]);
        if (fov > 0f) cam.fieldOfView = fov;
        var w = cam.GetComponent<ProjectorWarp>();
        if (w != null && corners != null && corners.Length == 8)
        {
            w.cornerBL = new Vector2(corners[0], corners[1]);
            w.cornerBR = new Vector2(corners[2], corners[3]);
            w.cornerTL = new Vector2(corners[4], corners[5]);
            w.cornerTR = new Vector2(corners[6], corners[7]);
            w.featherWidth = Mathf.Clamp(feather, 0f, 0.2f);
        }
    }

    // ── HUD ──────────────────────────────────────────────────────────────────

    void EnsureStyles()
    {
        if (_header != null) return;
        _header = new GUIStyle(GUI.skin.label) { fontSize = 13, fontStyle = FontStyle.Bold,
                                                 normal = { textColor = new Color(0.5f, 0.95f, 1f) } };
        _label  = new GUIStyle(GUI.skin.label) { fontSize = 11,
                                                 normal = { textColor = new Color(0.85f, 0.85f, 0.85f) } };
        _value  = new GUIStyle(GUI.skin.label) { fontSize = 11, fontStyle = FontStyle.Bold,
                                                 normal = { textColor = Color.white } };
    }

    void OnGUI()
    {
        EnsureStyles();
        if (!_active)
        {
            GUI.color = new Color(1, 1, 1, 0.4f);
            GUI.Label(new Rect(Screen.width - 270, Screen.height - 22, 260, 18),
                      "[P] projector calibrate", _label);
            GUI.color = Color.white;
            return;
        }

        var t = TargetCamera();
        if (!t) return;
        var w = t.GetComponent<ProjectorWarp>();

        float bw = 600f, bh = 240f;
        float bx = (Screen.width - bw) * 0.5f, by = Screen.height - bh - 10f;
        GUI.color = new Color(0f, 0f, 0f, 0.85f);
        GUI.DrawTexture(new Rect(bx, by, bw, bh), Texture2D.whiteTexture);
        GUI.color = Color.white;

        GUILayout.BeginArea(new Rect(bx + 10, by + 8, bw - 20, bh - 16));

        GUILayout.Label("● PROJECTOR CALIBRATION", _header);
        GUILayout.Label($"Target: {_target}   [Tab] cycle   FOV: {t.fieldOfView:F1}°", _value);
        var p = t.transform.position;
        var r = t.transform.eulerAngles;
        GUILayout.Label($"Pos    {p.x:F2}   {p.y:F2}   {p.z:F2}", _label);
        GUILayout.Label($"Rot    {r.x:F1}°   {r.y:F1}°   {r.z:F1}°", _label);

        if (w != null)
        {
            GUILayout.Label($"Corner [1]BL  {w.cornerBL.x:F2}, {w.cornerBL.y:F2}    " +
                            $"[2]BR  {w.cornerBR.x:F2}, {w.cornerBR.y:F2}", _label);
            GUILayout.Label($"Corner [3]TL  {w.cornerTL.x:F2}, {w.cornerTL.y:F2}    " +
                            $"[4]TR  {w.cornerTR.x:F2}, {w.cornerTR.y:F2}", _label);
            GUILayout.Label($"Feather: {w.featherWidth:F2}   (F = more, V = less)", _label);
            GUI.color = new Color(0.5f, 0.95f, 1f);
            GUILayout.Label($"Selected corner: {_corner}  (use arrow keys to drag)", _value);
            GUI.color = Color.white;
        }
        else
        {
            GUI.color = new Color(1f, 0.6f, 0.4f);
            GUILayout.Label("(no ProjectorWarp on this camera — rebuild scene)", _label);
            GUI.color = Color.white;
        }

        GUILayout.Space(4);
        GUILayout.Label("CAMERA: WASD/QE move   IJKL/UO rotate   = / -  FOV", _label);
        GUILayout.Label("WARP CORNERS: 1/2/3/4 select corner   Arrows = drag", _label);
        GUILayout.Label("  [ reset all corners to fullscreen   ] center selected corner", _label);
        GUILayout.Label("Shift = 5× fast   Ctrl = 0.2× fine   [Enter] save   " +
                        "[Backspace] reset   [Esc] exit", _label);
        GUILayout.EndArea();
    }
}
