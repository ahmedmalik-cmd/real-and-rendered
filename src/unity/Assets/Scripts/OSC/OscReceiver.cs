using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/// <summary>
/// Minimal UDP-based OSC 1.0 receiver. No third-party packages required.
/// Handles single (non-bundled) OSC messages with float / int / string args,
/// which is what python-osc's SimpleUDPClient sends by default.
///
/// Multiple consumers subscribe to OnMessage and filter by address pattern:
///
///     osc.OnMessage += (addr, args) =&gt; {
///         if (addr == "/theremin/pitch") pitch = (float)args[0];
///     };
/// </summary>
public class OscReceiver : MonoBehaviour
{
    [Tooltip("UDP port to listen on. Python senders target this port.")]
    public int port = 9000;

    public event Action<string, object[]> OnMessage;

    /// <summary>Total messages received (debug stat).</summary>
    public int MessagesReceived { get; private set; }
    public float LastMessageTime { get; private set; }

    private UdpClient _udp;
    private Thread    _thread;
    private volatile bool _running;
    private readonly Queue<(string addr, object[] args)> _queue = new(64);
    private readonly object _lock = new();

    void OnEnable()  => Begin();
    void OnDisable() => Stop();

    void Begin()
    {
        if (_running) return;
        try
        {
            _udp = new UdpClient(port) { Client = { ReceiveTimeout = 500 } };
        }
        catch (Exception ex)
        {
            Debug.LogError($"[OSC] Cannot bind UDP port {port}: {ex.Message}");
            return;
        }
        _running = true;
        _thread  = new Thread(ListenLoop) { IsBackground = true, Name = $"OSC:{port}" };
        _thread.Start();
        Debug.Log($"[OSC] Listening on port {port}");
    }

    void Stop()
    {
        _running = false;
        try { _udp?.Close(); } catch { }
        _udp = null;
    }

    void ListenLoop()
    {
        var ep = new IPEndPoint(IPAddress.Any, 0);
        while (_running)
        {
            try
            {
                var data = _udp.Receive(ref ep);
                ParseAndEnqueue(data);
            }
            catch (SocketException se)
            {
                if (se.SocketErrorCode == SocketError.TimedOut) continue;
                if (!_running) return;
            }
            catch (ObjectDisposedException) { return; }
            catch (Exception ex)
            {
                Debug.LogError($"[OSC] {ex.Message}");
                Thread.Sleep(100);
            }
        }
    }

    void Update()
    {
        lock (_lock)
        {
            while (_queue.Count > 0)
            {
                var (addr, args) = _queue.Dequeue();
                MessagesReceived++;
                LastMessageTime = Time.time;
                try { OnMessage?.Invoke(addr, args); }
                catch (Exception ex) { Debug.LogError($"[OSC] handler: {ex.Message}"); }
            }
        }
    }

    // ── OSC parser ───────────────────────────────────────────────────────────

    void ParseAndEnqueue(byte[] data)
    {
        // python-osc by default sends one message per UDP packet (not bundled).
        // For safety, detect "#bundle" header and ignore (we don't need it).
        if (data.Length >= 8 && data[0] == '#' && data[1] == 'b' &&
            data[2] == 'u' && data[3] == 'n' && data[4] == 'd' && data[5] == 'l' &&
            data[6] == 'e' && data[7] == 0)
        {
            // Could implement bundle unpacking — not needed for our pipeline
            return;
        }

        int i = 0;
        string addr  = ReadString(data, ref i); if (addr == null) return;
        string types = ReadString(data, ref i); if (types == null || !types.StartsWith(",")) return;

        var args = new object[types.Length - 1];
        for (int t = 1; t < types.Length; t++)
        {
            char c = types[t];
            if (c == 'f')      args[t - 1] = ReadFloat32BE(data, ref i);
            else if (c == 'i') args[t - 1] = ReadInt32BE  (data, ref i);
            else if (c == 's') args[t - 1] = ReadString   (data, ref i) ?? "";
            else if (c == 'T') args[t - 1] = true;
            else if (c == 'F') args[t - 1] = false;
            else               args[t - 1] = null;          // unsupported type
        }

        lock (_lock) _queue.Enqueue((addr, args));
    }

    static string ReadString(byte[] data, ref int i)
    {
        if (i >= data.Length) return null;
        int start = i;
        while (i < data.Length && data[i] != 0) i++;
        if (i >= data.Length) return null;
        string s = Encoding.ASCII.GetString(data, start, i - start);
        i++;                                    // skip null
        while ((i & 3) != 0 && i < data.Length) i++;   // pad to 4-byte boundary
        return s;
    }

    static float ReadFloat32BE(byte[] data, ref int i)
    {
        if (i + 4 > data.Length) return 0f;
        var b = new byte[4] { data[i + 3], data[i + 2], data[i + 1], data[i] };
        i += 4;
        return BitConverter.ToSingle(b, 0);
    }

    static int ReadInt32BE(byte[] data, ref int i)
    {
        if (i + 4 > data.Length) return 0;
        int v = (data[i] << 24) | (data[i + 1] << 16) | (data[i + 2] << 8) | data[i + 3];
        i += 4;
        return v;
    }
}
