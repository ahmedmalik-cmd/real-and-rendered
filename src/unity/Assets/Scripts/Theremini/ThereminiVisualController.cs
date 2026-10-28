using UnityEngine;

/// <summary>
/// Maps the Theremini to the point-cloud shader's synesthesia uniforms.
///   Pitch  (CC20, right antenna)  →  hue rotation
///   Volume (CC2,  left antenna)   →  shimmer intensity
///
/// NOTE: the volume → point-size mapping was removed. The Theremini's volume
/// antenna is not calibrated and sends a constant value, which pinned the
/// point size at its extreme. Point size is back under the calibration
/// panel's control only.
///
/// Diagnostics:
///   logEverySecond — prints raw + scaled values once per second.
///   testForceKey   — hold to force max pitch & volume, to verify the visual
///                    pipeline independently of MIDI input.
/// </summary>
public class ThereminiVisualController : MonoBehaviour
{
    public ThereminiInput input;
    public Material       pointCloudMaterial;

    [Header("Hue (from pitch — CC20, right antenna)")]
    [Tooltip("How much of the colour wheel pitch traverses (1 = full 360°).")]
    [Range(0f, 1f)]    public float hueRange  = 1.0f;
    [Tooltip("Multiplier on raw pitch before the hue calc. Higher = small antenna movements shift hue more.")]
    [Range(0.5f, 10f)] public float pitchGain = 4.0f;
    [Tooltip("Baseline hue when the theremin is silent. 0.6 ≈ teal/green.")]
    [Range(0f, 1f)]    public float hueBase   = 0.6f;
    [Tooltip("Extra frame-to-frame smoothing of the hue so the colour glides rather than steps.")]
    [Range(0f, 0.95f)] public float hueSmoothing = 0.6f;

    [Header("Shimmer (from volume — CC2, left antenna)")]
    [Range(0f, 1.5f)] public float shimmerStrength = 1.0f;
    [Range(0.3f, 4f)] public float volumeCurve     = 0.7f;

    [Header("Diagnostics")]
    public bool   logEverySecond = false;
    public KeyCode testForceKey  = KeyCode.T;

    static readonly int PHue     = Shader.PropertyToID("_ThereminHue");
    static readonly int PShimmer = Shader.PropertyToID("_ThereminShimmer");

    private float _logTimer  = 0f;
    private float _smoothHue = 0.6f;

    void LateUpdate()
    {
        if (!pointCloudMaterial) return;

        bool  testForcing = Input.GetKey(testForceKey);
        float pitch01, vol01;
        bool  live;
        float rawP = 0f, rawV = 0f;

        if (testForcing)
        {
            pitch01 = 1f; vol01 = 1f; live = true; rawP = 1f; rawV = 1f;
        }
        else if (input)
        {
            live    = input.IsLive;
            rawP    = input.Pitch;
            rawV    = input.Volume;
            pitch01 = Mathf.Clamp01(rawP * pitchGain);
            vol01   = Mathf.Clamp01(rawV);
        }
        else
        {
            pitch01 = 0f; vol01 = 0f; live = false;
        }

        // Target hue, then extra glide so the colour shift is silky.
        float targetHue = (hueBase + pitch01 * hueRange) % 1f;
        if (targetHue < 0f) targetHue += 1f;
        // Lerp on the shorter arc around the colour wheel so 0.98 → 0.02 doesn't sweep backwards.
        float diff = Mathf.Repeat(targetHue - _smoothHue + 0.5f, 1f) - 0.5f;
        _smoothHue = Mathf.Repeat(_smoothHue + diff * (1f - hueSmoothing), 1f);

        float hue     = _smoothHue;
        float shimmer = Mathf.Pow(vol01, volumeCurve) * shimmerStrength;
        if ((live || hueBase > 0.001f) && hue <= 0.0001f && hueBase > 0.0001f)
            hue = 0.0002f;

        pointCloudMaterial.SetFloat(PHue,     hue);
        pointCloudMaterial.SetFloat(PShimmer, shimmer);

        if (logEverySecond)
        {
            _logTimer += Time.unscaledDeltaTime;
            if (_logTimer >= 1f)
            {
                _logTimer = 0f;
                string tag = testForcing ? "[TEST] " : "";
                Debug.Log($"[Theremin] {tag}live={live}  raw(p={rawP:F3} v={rawV:F3})  " +
                          $"→ hue={hue:F3} shimmer={shimmer:F2}");
            }
        }
    }

    void OnDisable()
    {
        if (pointCloudMaterial)
        {
            pointCloudMaterial.SetFloat(PHue, 0f);
            pointCloudMaterial.SetFloat(PShimmer, 0f);
        }
    }
}
