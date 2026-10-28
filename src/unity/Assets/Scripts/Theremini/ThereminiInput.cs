using UnityEngine;

/// <summary>
/// Consumes /theremin/pitch and /theremin/volume from the Python theremini_bridge.
/// Both arrive as floats in [0, 1] already smoothed and dead-zoned at the source.
/// We add a light secondary EMA so Unity-side framerate jitter doesn't show through.
///
/// Pitch corresponds to CC20 (right antenna).
/// Volume corresponds to CC2  (left antenna).
/// </summary>
public class ThereminiInput : MonoBehaviour
{
    [Tooltip("Assign the OscReceiver that's listening on the OSC port.")]
    public OscReceiver osc;

    [Header("Secondary smoothing (on top of Python-side smoothing)")]
    [Range(0f, 0.95f)] public float smoothing = 0.5f;

    [Header("Liveness")]
    [Tooltip("If no MIDI message arrives within this many seconds, " +
             "pitch/volume decay toward 0 so visuals don't 'stick'.")]
    public float liveTimeout = 1.5f;

    public float Pitch          { get; private set; }
    public float Volume         { get; private set; }
    public float RawPitch       { get; private set; }
    public float RawVolume      { get; private set; }
    public float LastUpdateTime { get; private set; } = -10f;
    public bool  IsLive         => Time.time - LastUpdateTime < liveTimeout;

    void OnEnable()
    {
        if (osc) osc.OnMessage += HandleMessage;
    }
    void OnDisable()
    {
        if (osc) osc.OnMessage -= HandleMessage;
    }

    void HandleMessage(string addr, object[] args)
    {
        if (args.Length == 0 || !(args[0] is float v)) return;
        if (addr == "/theremin/pitch")       { RawPitch  = Mathf.Clamp01(v); LastUpdateTime = Time.time; }
        else if (addr == "/theremin/volume") { RawVolume = Mathf.Clamp01(v); LastUpdateTime = Time.time; }
    }

    void Update()
    {
        float alpha = 1f - smoothing;
        float targetP = IsLive ? RawPitch  : 0f;
        float targetV = IsLive ? RawVolume : 0f;
        Pitch  += (targetP - Pitch)  * alpha;
        Volume += (targetV - Volume) * alpha;
    }
}
