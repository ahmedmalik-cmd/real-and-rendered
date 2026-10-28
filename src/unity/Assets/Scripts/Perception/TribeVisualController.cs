using UnityEngine;

/// <summary>
/// Maps TRIBE v2 brain-encoding activations to the point-cloud shader.
///
///   activation_mean   → _TribeIntensity   (overall vividness / brightness)
///   activation_peak   → _TribePulse       (latched then decays — spatial wave)
///   activation_trend  → _TribeTrend       (warm/cool colour-temperature shift)
///
/// All three are zero when TRIBE goes offline, so the base render is untouched
/// if no TRIBE pipeline is running. Lives in a different uniform set than
/// the Theremini layer so the two can coexist.
/// </summary>
public class TribeVisualController : MonoBehaviour
{
    public TribePerceptionReceiver receiver;
    public Material                pointCloudMaterial;

    [Header("Smoothing (per-second decay rates)")]
    [Range(0f, 0.99f)] public float intensitySmoothing = 0.88f;
    [Range(0f, 0.99f)] public float trendSmoothing     = 0.92f;
    [Tooltip("Seconds for a triggered peak pulse to decay to ~37% of its trigger value.")]
    [Range(0.5f, 10f)] public float pulseDecaySeconds  = 2.5f;
    [Tooltip("activation_peak below this is ignored.")]
    [Range(0f, 1f)]    public float peakTriggerMin     = 0.4f;

    [Header("Strength multipliers")]
    [Range(0f, 1.5f)] public float intensityStrength = 1f;
    [Range(0f, 1.5f)] public float pulseStrength     = 1f;
    [Range(0f, 1.5f)] public float trendStrength     = 1f;

    private float _intensity, _pulse, _trend;
    private float _lastPeakTriggerTime = -1000f;
    private float _lastPeakValue;
    private float _lastReceiverUpdate  = -1f;

    private static readonly int PIntensity = Shader.PropertyToID("_TribeIntensity");
    private static readonly int PPulse     = Shader.PropertyToID("_TribePulse");
    private static readonly int PTrend     = Shader.PropertyToID("_TribeTrend");

    void LateUpdate()
    {
        if (!pointCloudMaterial) return;

        bool  live    = receiver != null && receiver.IsLive;
        float tIntens = live ? receiver.activationMean  : 0f;
        float tTrend  = live ? receiver.activationTrend : 0f;

        // EMA smoothing (frame-rate-independent enough at typical 30-60 fps)
        _intensity += (tIntens - _intensity) * (1f - intensitySmoothing);
        _trend     += (tTrend  - _trend)     * (1f - trendSmoothing);

        // Peak: trigger a latched pulse each time the receiver gets a fresh
        // update where activation_peak exceeds the threshold; decay over time.
        if (live && receiver.LastUpdateTime != _lastReceiverUpdate)
        {
            _lastReceiverUpdate = receiver.LastUpdateTime;
            if (receiver.activationPeak > peakTriggerMin)
            {
                _lastPeakTriggerTime = Time.time;
                _lastPeakValue       = receiver.activationPeak;
            }
        }
        float age = Time.time - _lastPeakTriggerTime;
        _pulse = live ? _lastPeakValue * Mathf.Exp(-age / Mathf.Max(0.1f, pulseDecaySeconds)) : 0f;

        pointCloudMaterial.SetFloat(PIntensity, Mathf.Clamp01(_intensity * intensityStrength));
        pointCloudMaterial.SetFloat(PPulse,     Mathf.Clamp01(_pulse     * pulseStrength));
        pointCloudMaterial.SetFloat(PTrend,     Mathf.Clamp(_trend   * trendStrength, -1f, 1f));
    }

    void OnDisable()
    {
        if (pointCloudMaterial)
        {
            pointCloudMaterial.SetFloat(PIntensity, 0f);
            pointCloudMaterial.SetFloat(PPulse,     0f);
            pointCloudMaterial.SetFloat(PTrend,     0f);
        }
    }
}
