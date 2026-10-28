using UnityEngine;

/// <summary>
/// Top-right HUD: theremin pitch/volume meters + latest LLaVA response.
/// Toggle the on-screen LLaVA caption with F8 (off by default — the user
/// mentioned the previous iteration cluttered the scene).
/// </summary>
public class PerceptionDisplay : MonoBehaviour
{
    public PerceptionReceiver       receiver;
    public ThereminiInput           theremin;
    public KinectPresenceDetector   presence;
    public TribePerceptionReceiver  tribe;

    public bool   showCaption   = false;
    public KeyCode captionKey   = KeyCode.F8;
    [Range(2f, 30f)] public float captionHoldSeconds = 14f;

    GUIStyle _meta, _caption;

    void Update()
    {
        if (Input.GetKeyDown(captionKey))
        {
            showCaption = !showCaption;
            Debug.Log($"[Perception] On-screen LLaVA caption {(showCaption ? "ON" : "OFF")}");
        }
    }

    void EnsureStyles()
    {
        if (_meta != null) return;
        _meta = new GUIStyle(GUI.skin.label)
        {
            fontSize = 11,
            alignment = TextAnchor.UpperRight,
            normal = { textColor = new Color(0.85f, 0.85f, 0.85f) }
        };
        _caption = new GUIStyle(GUI.skin.label)
        {
            fontSize = 18,
            fontStyle = FontStyle.Italic,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true,
            normal = { textColor = new Color(0.95f, 0.95f, 0.9f) }
        };
    }

    void OnGUI()
    {
        EnsureStyles();
        float x = Screen.width - 360f;
        float y = 32f;

        // Theremin
        if (theremin)
        {
            string th = theremin.IsLive
                ? $"Theremin   pitch {theremin.Pitch:F2}  ░  volume {theremin.Volume:F2}"
                : "Theremin   — offline —";
            GUI.color = theremin.IsLive ? new Color(0.5f, 0.95f, 1f) : new Color(0.45f, 0.45f, 0.5f);
            GUI.Label(new Rect(x, y, 350, 18), th, _meta); y += 16;
        }

        // Presence
        if (presence)
        {
            string pr = presence.PersonPresent
                ? $"Presence   K0:{presence.PerSensorCount[0]:N0}  K1:{presence.PerSensorCount[1]:N0}  ({presence.SensorsSeeingPerson} sensor)"
                : "Presence   — empty —";
            GUI.color = presence.PersonPresent ? new Color(1f, 0.9f, 0.5f) : new Color(0.45f, 0.45f, 0.5f);
            GUI.Label(new Rect(x, y, 350, 18), pr, _meta); y += 16;
        }

        // LLaVA status
        if (receiver)
        {
            string lv;
            if (receiver.LlavaCount == 0)
                lv = "LLaVA      — waiting for first frame —";
            else
            {
                float age = Time.time - receiver.LlavaUpdateTime;
                lv = $"LLaVA      {age:F0}s ago  ({receiver.LlavaLatency:F1}s gen)  #{receiver.LlavaCount}";
            }
            GUI.color = receiver.LlavaCount > 0 ? new Color(0.7f, 0.95f, 0.7f) : new Color(0.45f, 0.45f, 0.5f);
            GUI.Label(new Rect(x, y, 350, 18), lv, _meta); y += 16;
        }

        // TRIBE v2 status (brain encoding)
        if (tribe)
        {
            string tr;
            if (tribe.PollOk == 0)
                tr = "TRIBE      — offline —";
            else
            {
                float age = Time.time - tribe.LastUpdateTime;
                tr = $"TRIBE      {age:F0}s   mean {tribe.activationMean:F2}  peak {tribe.activationPeak:F2}  trend {tribe.activationTrend:+0.00;-0.00}";
            }
            GUI.color = tribe.IsLive ? new Color(1f, 0.7f, 0.95f) : new Color(0.45f, 0.45f, 0.5f);
            GUI.Label(new Rect(x, y, 350, 18), tr, _meta); y += 16;
        }

        GUI.color = new Color(0.55f, 0.55f, 0.55f);
        GUI.Label(new Rect(x, y, 350, 18), $"[F8] {(showCaption ? "hide" : "show")} room caption", _meta);
        GUI.color = Color.white;

        // Caption overlay (bottom centre, fades out). TRIBE brain_text takes
        // priority when TRIBE is live; otherwise falls back to plain LLaVA.
        if (showCaption)
        {
            string text = null;
            float  refT = 0f;
            if (tribe && tribe.IsLive && !string.IsNullOrEmpty(tribe.brainText))
            { text = tribe.brainText; refT = tribe.LastUpdateTime; }
            else if (receiver && !string.IsNullOrEmpty(receiver.LlavaResponse))
            { text = receiver.LlavaResponse; refT = receiver.LlavaUpdateTime; }

            if (!string.IsNullOrEmpty(text))
            {
                float age = Time.time - refT;
                float alpha = Mathf.Clamp01(1f - (age - captionHoldSeconds) / 4f);
                if (alpha > 0.01f)
                {
                    float w = Mathf.Min(Screen.width * 0.7f, 900f);
                    float h = 90f;
                    var r = new Rect((Screen.width - w) * 0.5f, Screen.height - h - 40f, w, h);
                    GUI.color = new Color(0f, 0f, 0f, 0.55f * alpha);
                    GUI.DrawTexture(r, Texture2D.whiteTexture);
                    GUI.color = new Color(0.95f, 0.95f, 0.9f, alpha);
                    GUI.Label(new Rect(r.x + 16, r.y + 12, r.width - 32, r.height - 24), text, _caption);
                    GUI.color = Color.white;
                }
            }
        }
    }
}
