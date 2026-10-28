using System.Collections.Generic;
using UnityEngine;

public class BinaryFrame
{
    public long Timestamp;
    public List<BinarySensorFrame> Sensors = new List<BinarySensorFrame>();
}

public class BinarySensorFrame
{
    public int Ordinal;
    public string SensorId   = "";
    public string Label      = "";
    public bool   Connected;
    public string ColorPath  = "";
    public float  ElevationAngle;
    public Vector4 Accelerometer;
    public int DepthWidth;
    public int DepthHeight;
    public int SampleStep = 1;
    public List<TrackedBody> Bodies = new List<TrackedBody>();
    public Vector3[] Points       = System.Array.Empty<Vector3>();
    public Color32[] RgbColors    = System.Array.Empty<Color32>();
    public int[]     PlayerIndices = System.Array.Empty<int>();
    public int[]     DepthX       = System.Array.Empty<int>();
    public int[]     DepthY       = System.Array.Empty<int>();
}

public class TrackedBody
{
    public long TrackingId;
    public Dictionary<string, Vector3> Joints = new Dictionary<string, Vector3>();
}
