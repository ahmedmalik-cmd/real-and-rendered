using System;
using System.IO;
using UnityEngine;

/// <summary>
/// Periodically writes the current Kinect presence state to a JSON file
/// inside Application.persistentDataPath so the Python TRIBE client can
/// read it when composing the LLaVA prompt.
///
/// Path: %USERPROFILE%/AppData/LocalLow/&lt;Company&gt;/&lt;Product&gt;/Presence/presence.json
/// </summary>
public class PresenceExporter : MonoBehaviour
{
    public KinectPresenceDetector source;
    [Range(0.5f, 30f)] public float writeIntervalSeconds = 2f;
    public string folderName = "Presence";
    public string fileName   = "presence.json";
    public bool   verboseLogging = true;

    private float  _nextWriteTime;
    private string _path;

    [Serializable]
    private class PresenceJson
    {
        public bool   present;
        public int    pixel_count;
        public int    sensors_seeing;
        public float  cx, cy, cz;        // world-space centroid (legacy, kept for compat)
        public long   timestamp_ms;
        // ── NEW skeleton-aware fields ───────────────────────────────────────
        public int    person_count;      // distinct dedup'd people from PlayerIndex
        public PersonJson[] people;      // per-person world position + height
    }

    [Serializable]
    private class PersonJson
    {
        public int    id;
        public int    pixels;
        public float  cx, cy, cz;        // centroid (world)
        public float  hx, hy, hz;        // approx head position (top of bounding box)
        public float  height;            // metres (maxY - minY)
    }

    void Start()
    {
        if (!source) source = FindObjectOfType<KinectPresenceDetector>();
        string dir = Path.Combine(Application.persistentDataPath, folderName);
        try
        {
            Directory.CreateDirectory(dir);
            _path = Path.Combine(dir, fileName);
            if (verboseLogging) Debug.Log($"[Presence] Writing → {_path}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[Presence] Could not create dir {dir}: {e.Message}");
        }
    }

    void Update()
    {
        if (source == null || string.IsNullOrEmpty(_path)) return;
        if (Time.unscaledTime < _nextWriteTime) return;
        _nextWriteTime = Time.unscaledTime + writeIntervalSeconds;

        // Snapshot per-person info
        var srcPeople = source.People;
        var peopleArr = new PersonJson[srcPeople.Count];
        for (int i = 0; i < srcPeople.Count; i++)
        {
            var p = srcPeople[i];
            peopleArr[i] = new PersonJson
            {
                id     = p.id,
                pixels = p.pixels,
                cx     = p.centroid.x, cy = p.centroid.y, cz = p.centroid.z,
                hx     = p.HeadApprox.x, hy = p.HeadApprox.y, hz = p.HeadApprox.z,
                height = p.HeightMeters,
            };
        }

        var data = new PresenceJson
        {
            present        = source.PersonPresent,
            pixel_count    = source.PersonPixelCount,
            sensors_seeing = source.SensorsSeeingPerson,
            cx             = source.PersonCentroidWorld.x,
            cy             = source.PersonCentroidWorld.y,
            cz             = source.PersonCentroidWorld.z,
            timestamp_ms   = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            person_count   = source.PeopleCount,
            people         = peopleArr,
        };

        try
        {
            string tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonUtility.ToJson(data));
            if (File.Exists(_path)) File.Delete(_path);
            File.Move(tmp, _path);
        }
        catch (Exception e)
        {
            if (verboseLogging) Debug.LogWarning($"[Presence] Write failed: {e.Message}");
        }
    }
}
