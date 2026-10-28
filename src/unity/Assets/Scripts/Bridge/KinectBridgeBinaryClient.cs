using System;
using System.Collections;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;

/// <summary>
/// Polls /frame.bin from the Kinect bridge continuously with no artificial delay —
/// back-to-back requests keep latency as low as the bridge's own frame rate allows.
/// </summary>
public sealed class KinectBridgeBinaryClient : MonoBehaviour
{
    public string bridgeUrl          = "http://localhost:8182";
    public float  pollIntervalSeconds = 0f;  // 0 = fire next request immediately

    public event Action<BinaryFrame> FrameReceived;
    public event Action<string>      ErrorReceived;

    private Coroutine _loop;

    public void Begin() { if (_loop == null) _loop = StartCoroutine(Poll()); }
    public void End()   { if (_loop != null) { StopCoroutine(_loop); _loop = null; } }

    private IEnumerator Poll()
    {
        while (true)
        {
            using var req = UnityWebRequest.Get($"{bridgeUrl.TrimEnd('/')}/frame.bin");
            req.downloadHandler = new DownloadHandlerBuffer();
            yield return req.SendWebRequest();

            if (req.result == UnityWebRequest.Result.Success)
            {
                try   { FrameReceived?.Invoke(Parse(req.downloadHandler.data)); }
                catch (Exception ex) { ErrorReceived?.Invoke($"Parse: {ex.Message}"); }
            }
            else
            {
                ErrorReceived?.Invoke(req.error);
                yield return new WaitForSecondsRealtime(0.5f); // back off on network error
            }

            if (pollIntervalSeconds > 0f)
                yield return new WaitForSecondsRealtime(pollIntervalSeconds);
            // else: next iteration starts immediately (one frame yield from SendWebRequest)
        }
    }

    private static BinaryFrame Parse(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var r  = new BinaryReader(ms, Encoding.UTF8);

        var magic     = new string(r.ReadChars(4));
        bool bodies   = magic is "TKB2" or "TKB3";
        bool depthGrid = magic == "TKB3";
        if (magic is not ("TKB1" or "TKB2" or "TKB3"))
            throw new InvalidDataException($"Bad magic: {magic}");

        var frame = new BinaryFrame { Timestamp = r.ReadInt64() };
        int sc    = r.ReadInt32();

        for (int si = 0; si < sc; si++)
        {
            var s = new BinarySensorFrame
            {
                Ordinal        = r.ReadInt32(),
                SensorId       = Str(r), Label = Str(r),
                Connected      = r.ReadBoolean(),
                ColorPath      = Str(r),
                ElevationAngle = r.ReadSingle(),
                Accelerometer  = new Vector4(r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle()),
            };
            if (depthGrid)
            {
                s.DepthWidth  = r.ReadInt32();
                s.DepthHeight = r.ReadInt32();
                s.SampleStep  = Mathf.Max(1, r.ReadInt32());
            }
            if (bodies)
            {
                int bc = r.ReadInt32();
                for (int bi = 0; bi < bc; bi++)
                {
                    var b = new TrackedBody { TrackingId = r.ReadInt64() };
                    int jc = r.ReadInt32();
                    for (int ji = 0; ji < jc; ji++)
                        b.Joints[Str(r)] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    s.Bodies.Add(b);
                }
            }
            int pc    = r.ReadInt32();
            s.Points        = new Vector3[pc];
            s.RgbColors     = new Color32[pc];
            s.PlayerIndices = new int[pc];
            s.DepthX        = depthGrid ? new int[pc] : Array.Empty<int>();
            s.DepthY        = depthGrid ? new int[pc] : Array.Empty<int>();
            for (int pi = 0; pi < pc; pi++)
            {
                s.Points[pi]        = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                int pl              = Mathf.Clamp(Mathf.RoundToInt(r.ReadSingle()), 0, 255);
                byte rb = Clamp(r.ReadSingle()), gb = Clamp(r.ReadSingle()), bb = Clamp(r.ReadSingle());
                s.RgbColors[pi]     = new Color32(rb, gb, bb, 255);
                s.PlayerIndices[pi] = pl;
                if (depthGrid) { s.DepthX[pi] = r.ReadInt32(); s.DepthY[pi] = r.ReadInt32(); }
            }
            frame.Sensors.Add(s);
        }
        return frame;
    }

    static string Str(BinaryReader r) { int l = r.ReadInt32(); return l <= 0 ? "" : Encoding.UTF8.GetString(r.ReadBytes(l)); }
    static byte Clamp(float v) => (byte)Mathf.Clamp(Mathf.RoundToInt(v), 0, 255);

    void OnDestroy() => End();
}
