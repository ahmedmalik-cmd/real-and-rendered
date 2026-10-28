using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Periodically renders the camera into a RenderTexture and writes it as a JPG
/// to PerceptionFrames/frame.jpg in the project root. The Python perception
/// pipeline polls that file's mtime and forwards it to LLaVA.
///
/// Path is project-relative (Application.dataPath/../PerceptionFrames) so it
/// works without elevated permissions and is easy to find while developing.
/// </summary>
public class GameViewCapture : MonoBehaviour
{
    [Header("Source")]
    public Camera sourceCamera;

    [Header("Cadence")]
    [Range(2f, 60f)] public float intervalSeconds = 13f;
    public bool enableCapture = true;

    [Header("Output")]
    [Range(256, 1280)] public int width  = 640;
    [Range(192, 720)]  public int height = 480;
    [Range(40, 95)]    public int jpegQuality = 78;
    [Tooltip("Subfolder under the project root.")]
    public string folderName = "PerceptionFrames";
    [Tooltip("Filename within the folder. Python watches this.")]
    public string fileName = "frame.jpg";

    public string AbsolutePath { get; private set; }
    public DateTime LastCaptureTime { get; private set; }
    public int      CaptureCount    { get; private set; }
    public float    LastWriteMs     { get; private set; }

    private RenderTexture _rt;
    private Texture2D     _tex;
    private float         _nextCaptureTime;

    void Start()
    {
        if (!sourceCamera) sourceCamera = Camera.main;
        // persistentDataPath is the SAME location in editor and build for the
        // same Unity product, so Python can use one default path either way.
        // Windows: %USERPROFILE%/AppData/LocalLow/<Company>/<Product>/PerceptionFrames/
        string dir = Path.GetFullPath(Path.Combine(Application.persistentDataPath, folderName));
        Directory.CreateDirectory(dir);
        AbsolutePath = Path.Combine(dir, fileName);

        _rt  = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
        _tex = new Texture2D(width, height, TextureFormat.RGB24, false);

        Debug.Log($"[Capture] Output → {AbsolutePath}  ({width}x{height} every {intervalSeconds}s)");
        _nextCaptureTime = Time.unscaledTime + 1f;     // small warm-up
    }

    void Update()
    {
        if (!enableCapture || !sourceCamera) return;
        if (Time.unscaledTime < _nextCaptureTime) return;
        _nextCaptureTime = Time.unscaledTime + intervalSeconds;
        Capture();
    }

    [ContextMenu("Capture Now")]
    public void Capture()
    {
        if (!sourceCamera) return;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Capture the camera into our RT. WRAP IN TRY-FINALLY so that even if
        // ReadPixels or JPG encoding throws, the camera's targetTexture and
        // RenderTexture.active are always restored. Otherwise Main Camera
        // would silently lose its screen output and "freeze".
        var prevTarget = sourceCamera.targetTexture;
        var prevActive = RenderTexture.active;
        try
        {
            sourceCamera.targetTexture = _rt;
            sourceCamera.Render();
            RenderTexture.active = _rt;
            _tex.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            _tex.Apply(false);
        }
        finally
        {
            RenderTexture.active       = prevActive;
            sourceCamera.targetTexture = prevTarget;
        }

        try
        {
            byte[] jpg = _tex.EncodeToJPG(jpegQuality);
            string tmp = AbsolutePath + ".tmp";
            File.WriteAllBytes(tmp, jpg);
            if (File.Exists(AbsolutePath)) File.Delete(AbsolutePath);
            File.Move(tmp, AbsolutePath);
            CaptureCount++;
            LastCaptureTime = DateTime.Now;
        }
        catch (Exception e)
        {
            Debug.LogError($"[Capture] Write failed: {e.Message}");
        }

        sw.Stop();
        LastWriteMs = (float)sw.Elapsed.TotalMilliseconds;
    }

    void OnDestroy()
    {
        if (_rt) _rt.Release();
        if (_tex) Destroy(_tex);
    }
}
