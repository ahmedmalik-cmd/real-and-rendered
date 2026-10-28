using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Polls the TRIBE v2 perception JSON endpoint served by
/// tribe_kinect_client.py (default http://localhost:8183/perception).
///
/// Expected JSON shape:
///   {
///     "brain_text":        "...",
///     "activation_mean":   0.42,
///     "activation_peak":   0.81,
///     "activation_trend":  0.15
///   }
///
/// Unity-side this is exposed as smoothed public state for shaders and HUDs.
/// Missing or extra JSON fields are tolerated by JsonUtility.
/// </summary>
public class TribePerceptionReceiver : MonoBehaviour
{
    [Header("Endpoint")]
    public string url = "http://localhost:8183/perception";
    [Range(1f, 30f)] public float pollIntervalSeconds = 4f;
    [Range(1, 30)]   public int   timeoutSeconds      = 6;
    public bool enablePolling = true;

    [Header("Liveness")]
    [Tooltip("Mark TRIBE 'offline' after this many polling intervals without success.")]
    [Range(1, 8)] public int liveThresholdIntervals = 3;

    [Header("Live state (read-only)")]
    public string  brainText;
    public float   activationMean;
    public float   activationPeak;
    public float   activationTrend;

    public float   LastUpdateTime { get; private set; } = -10f;
    public int     PollOk         { get; private set; }
    public int     PollErr        { get; private set; }
    public string  LastError      { get; private set; } = "";
    public bool    IsLive         => Time.time - LastUpdateTime < pollIntervalSeconds * liveThresholdIntervals;

    [Serializable]
    private class TribeJson
    {
        public string brain_text;
        public float  activation_mean;
        public float  activation_peak;
        public float  activation_trend;
    }

    private Coroutine _loop;

    void OnEnable()
    {
        if (enablePolling) _loop = StartCoroutine(PollLoop());
    }

    void OnDisable()
    {
        if (_loop != null) { StopCoroutine(_loop); _loop = null; }
    }

    IEnumerator PollLoop()
    {
        // Brief startup delay so the rest of the scene initialises first
        yield return new WaitForSeconds(2f);
        while (true)
        {
            using (var req = UnityWebRequest.Get(url))
            {
                req.timeout = timeoutSeconds;
                // Harmless on plain localhost; useful if you ever swap to ngrok.
                req.SetRequestHeader("ngrok-skip-browser-warning", "1");
                req.SetRequestHeader("Accept", "application/json");

                yield return req.SendWebRequest();

                if (req.result == UnityWebRequest.Result.Success && req.responseCode == 200)
                {
                    try
                    {
                        var d = JsonUtility.FromJson<TribeJson>(req.downloadHandler.text);
                        if (d != null)
                        {
                            string newText = string.IsNullOrWhiteSpace(d.brain_text) ? "" : d.brain_text.Trim();
                            bool   changed = !string.IsNullOrEmpty(newText) && newText != brainText;

                            if (!string.IsNullOrEmpty(newText)) brainText = newText;
                            activationMean  = d.activation_mean;
                            activationPeak  = d.activation_peak;
                            activationTrend = d.activation_trend;
                            // ONLY refresh the caption-fade timer when the text is actually new.
                            // Repeated identical or empty broadcasts no longer hold the caption alive.
                            if (changed) LastUpdateTime = Time.time;
                            LastError = "";
                            PollOk++;

                            // Log new TRIBE thoughts to console so they're visible
                            // there too (mirrors PerceptionReceiver's behaviour).
                            if (changed)
                                Debug.Log($"[TRIBE] {brainText}  " +
                                          $"(mean={activationMean:F2} peak={activationPeak:F2} trend={activationTrend})");
                        }
                    }
                    catch (Exception ex)
                    {
                        PollErr++;
                        LastError = "parse: " + ex.Message;
                    }
                }
                else
                {
                    PollErr++;
                    LastError = string.IsNullOrEmpty(req.error)
                        ? $"HTTP {req.responseCode}" : req.error;
                }
            }

            yield return new WaitForSeconds(pollIntervalSeconds);
        }
    }
}
