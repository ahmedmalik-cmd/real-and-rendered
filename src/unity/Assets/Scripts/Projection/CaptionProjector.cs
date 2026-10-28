using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Dedicated projector showing only the LLaVA caption on black. Builds its
/// own Canvas + Text at runtime. If Display 3 isn't available in the build,
/// the camera is disabled — it does NOT fall back to Display 0.
/// </summary>
[RequireComponent(typeof(Camera))]
public class CaptionProjector : MonoBehaviour
{
    public PerceptionReceiver      receiver;
    public TribePerceptionReceiver tribe;
    [Range(0, 7)] public int displayIndex = 2;

    [Header("Appearance")]
    [Tooltip("Caption colour. A warm amber stands out clearly against the bluish point cloud.")]
    public Color textColor       = new Color(1.0f, 0.72f, 0.30f, 1f);   // warm amber
    public Color backgroundColor = Color.black;
    [Range(16, 200)] public int fontSize = 44;
    [Range(0.3f, 0.95f)] public float lineWidthFraction = 0.85f;
    [Range(2f, 600f)] public float captionHoldSeconds = 60f;    // 1 min fully visible
    [Range(0f, 600f)] public float fadeSeconds        = 240f;   // 4 min fade  → ~5 min total to gone
    [Tooltip("Seconds to smoothly fade a new caption IN (blend from the cloud).")]
    [Range(0f, 6f)] public float fadeInSeconds = 1.6f;
    [Tooltip("Adds a slow gentle alpha breathing so the text feels alive against the cloud.")]
    public bool   breathe          = true;
    [Range(0f, 0.3f)] public float breatheDepth = 0.12f;   // how much the alpha dips
    [Range(0.05f, 1f)] public float breatheSpeed = 0.22f;  // cycles per second
    public string placeholderText = "";

    [Header("Legibility")]
    [Tooltip("Dark band drawn behind the caption so it stays readable over a bright point cloud. 0 = off.")]
    [Range(0f, 1f)] public float panelOpacity = 0.55f;
    [Tooltip("Dark outline thickness around each glyph (px). Keeps text crisp against any background.")]
    [Range(0f, 5f)] public float outlineThickness = 2.5f;

    [Header("Caption placement on the projector display")]
    [Tooltip("Vertical anchor band (0=bottom, 1=top). Default places caption in the bottom 20% so cloud fills the rest.")]
    [Range(0f, 1f)] public float textBandBottom = 0.04f;
    [Range(0f, 1f)] public float textBandTop    = 0.22f;

    private Camera      _cam;
    private Canvas      _canvas;
    private Text        _text;
    private CanvasGroup _group;

    void Awake()
    {
        _cam = GetComponent<Camera>();
        _cam.clearFlags      = CameraClearFlags.SolidColor;
        _cam.backgroundColor = backgroundColor;
        // Render EVERYTHING (cloud + UI). The third projector now shows a
        // top-down view of the point cloud with the caption layered on it,
        // not just text on black.
        _cam.cullingMask     = ~0;
        _cam.targetDisplay   = Mathf.Clamp(displayIndex, 0, 7);
    }

    void Start()
    {
        BuildUI();

        if (Application.isEditor) return;

        if (displayIndex < 0 || displayIndex >= Display.displays.Length ||
            !Display.displays[displayIndex].active ||
            Display.displays[displayIndex].systemWidth == 0)
        {
            Debug.LogWarning($"[CaptionProjector] Display {displayIndex} not available — disabling.");
            _cam.enabled = false;
        }
    }

    void BuildUI()
    {
        var cgo = new GameObject("CaptionCanvas") { layer = 5 };  // UI layer
        cgo.transform.SetParent(transform, false);
        _canvas = cgo.AddComponent<Canvas>();
        // Overlay mode: the caption is composited AFTER the camera has drawn
        // the point cloud, so it can NEVER be hidden behind cloud geometry.
        // ScreenSpaceCamera (the old mode) placed the canvas as geometry at a
        // fixed depth, which the cloud's ZWrite shader drew over.
        _canvas.renderMode    = RenderMode.ScreenSpaceOverlay;
        _canvas.targetDisplay = Mathf.Clamp(displayIndex, 0, 7);
        _canvas.sortingOrder  = 100;

        var scaler = cgo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight  = 0.5f;

        _group = cgo.AddComponent<CanvasGroup>();
        _group.alpha          = 1f;
        _group.interactable   = false;
        _group.blocksRaycasts = false;

        // ── Dark backing band — guarantees contrast over a bright cloud ──────
        // Full screen width, covering the caption band plus a little padding.
        if (panelOpacity > 0.001f)
        {
            var pgo = new GameObject("CaptionPanel") { layer = 5 };
            pgo.transform.SetParent(cgo.transform, false);
            var prt = pgo.AddComponent<RectTransform>();
            const float pad = 0.03f;
            prt.anchorMin = new Vector2(0f, Mathf.Clamp01(textBandBottom - pad));
            prt.anchorMax = new Vector2(1f, Mathf.Clamp01(textBandTop    + pad));
            prt.offsetMin = Vector2.zero;
            prt.offsetMax = Vector2.zero;
            var pimg = pgo.AddComponent<Image>();
            pimg.color         = new Color(0f, 0f, 0f, panelOpacity);
            pimg.raycastTarget = false;
        }

        var tgo = new GameObject("CaptionText") { layer = 5 };
        tgo.transform.SetParent(cgo.transform, false);
        var rt = tgo.AddComponent<RectTransform>();
        // Caption sits in a horizontal band at the bottom of the screen so the
        // cloud (which fills the rest) stays uncovered.
        rt.anchorMin        = new Vector2(0.5f - lineWidthFraction * 0.5f, textBandBottom);
        rt.anchorMax        = new Vector2(0.5f + lineWidthFraction * 0.5f, textBandTop);
        rt.offsetMin        = Vector2.zero;
        rt.offsetMax        = Vector2.zero;
        rt.anchoredPosition = Vector2.zero;

        _text = tgo.AddComponent<Text>();
        _text.font  = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (_text.font == null) _text.font = Font.CreateDynamicFontFromOSFont("Arial", fontSize);
        _text.fontSize  = fontSize;
        _text.color     = textColor;
        _text.alignment = TextAnchor.MiddleCenter;
        _text.horizontalOverflow = HorizontalWrapMode.Wrap;
        _text.verticalOverflow   = VerticalWrapMode.Overflow;
        _text.text          = placeholderText;
        _text.fontStyle     = FontStyle.Italic;
        _text.raycastTarget = false;

        // Dark outline — keeps each glyph crisp even where the panel is faint
        // or a bright patch of cloud sits directly behind the text.
        if (outlineThickness > 0.001f)
        {
            var outline = tgo.AddComponent<Outline>();
            outline.effectColor    = new Color(0f, 0f, 0f, 0.95f);
            outline.effectDistance = new Vector2(outlineThickness, outlineThickness);
        }
    }

    void Update()
    {
        if (!_text) return;

        // Prefer TRIBE brain_text when live; fall back to LLaVA otherwise.
        string text  = placeholderText;
        float  refT  = -10f;
        bool   any   = false;
        if (tribe && tribe.IsLive && !string.IsNullOrEmpty(tribe.brainText))
        { text = tribe.brainText; refT = tribe.LastUpdateTime; any = true; }
        else if (receiver && !string.IsNullOrEmpty(receiver.LlavaResponse))
        { text = receiver.LlavaResponse; refT = receiver.LlavaUpdateTime; any = receiver.LlavaCount > 0; }

        if (_text.text != text) _text.text = text;

        float age = Time.time - refT;
        float alpha;
        if (!any)
        {
            alpha = string.IsNullOrEmpty(placeholderText) ? 0f : 0.4f;
        }
        else if (age < fadeInSeconds)
        {
            // Smooth blend-IN of a new caption (ease-in-out curve)
            float t = Mathf.Clamp01(age / Mathf.Max(0.01f, fadeInSeconds));
            alpha = t * t * (3f - 2f * t);   // smoothstep
        }
        else if (age < captionHoldSeconds)
        {
            alpha = 1f;
        }
        else
        {
            alpha = Mathf.Clamp01(1f - (age - captionHoldSeconds) / Mathf.Max(0.1f, fadeSeconds));
        }

        // Gentle "breathing" so the caption feels alive layered over the cloud.
        if (breathe && alpha > 0.01f)
        {
            float b = 1f - breatheDepth * (0.5f - 0.5f * Mathf.Cos(Time.time * breatheSpeed * 2f * Mathf.PI));
            alpha *= b;
        }

        if (_group) _group.alpha = alpha;
    }
}
