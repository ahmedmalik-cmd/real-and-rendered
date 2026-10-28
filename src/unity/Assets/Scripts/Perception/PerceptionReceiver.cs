using UnityEngine;

/// <summary>
/// Receives perception messages from the Python pipelines:
///   /perception/llava_response  (string)  — the latest LLaVA description
///   /perception/llava_latency   (float)   — inference time in seconds
/// Bridges them to public fields any other component can read.
/// </summary>
public class PerceptionReceiver : MonoBehaviour
{
    public OscReceiver osc;

    public string LlavaResponse  { get; private set; } = "";
    public float  LlavaLatency   { get; private set; }
    public float  LlavaUpdateTime{ get; private set; } = -10f;
    public int    LlavaCount     { get; private set; }

    void OnEnable()  { if (osc) osc.OnMessage += HandleMessage; }
    void OnDisable() { if (osc) osc.OnMessage -= HandleMessage; }

    void HandleMessage(string addr, object[] args)
    {
        if (args.Length == 0) return;
        switch (addr)
        {
            case "/perception/llava_response":
                if (args[0] is string s)
                {
                    LlavaResponse  = s;
                    LlavaUpdateTime = Time.time;
                    LlavaCount++;
                    Debug.Log($"[Perception] {s}");
                }
                break;
            case "/perception/llava_latency":
                if (args[0] is float lf) LlavaLatency = lf;
                else if (args[0] is int  li) LlavaLatency = li;
                break;
        }
    }
}
