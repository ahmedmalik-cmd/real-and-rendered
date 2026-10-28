using UnityEngine;

/// <summary>
/// Output of the post-processing pipeline for a single Kinect frame.
/// All arrays are indexed by pixel:  i = y * Width + x.
/// Buffers are owned and reused by the producing KinectFrameProcessor —
/// do not retain references across frames.
/// </summary>
public class ProcessedDepthFrame
{
    public int Width;
    public int Height;

    public float[]   DepthMeters;     // 0 == invalid
    public Vector3[] LocalPoints;     // sensor-local 3D for valid pixels
    public Color32[] Colors;          // RGBA8 after alignment
    public byte[]    PlayerIndex;     // 0 == none; >0 == player id
    public bool[]    InvalidMask;     // true == no usable depth
    public bool[]    FilledMask;      // true == synthesised by hole-filler
    public byte[]    Stability;       // 0..255 — frames at rest

    // Per-frame statistics
    public int    ValidCount;
    public int    InvalidCount;
    public int    FilledCount;
    public int    RejectedCount;     // raw points outside depth gate
    public float  AvgDepthMeters;
    public float  ProcessingMs;

    public bool Ready => DepthMeters != null && Width > 0 && Height > 0;
}
