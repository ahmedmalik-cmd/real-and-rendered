using UnityEngine;

/// <summary>
/// Tunable parameters for the per-sensor depth post-processing pipeline.
/// One instance is shared across both Kinect streams unless you want
/// per-sensor tuning (then make this an array on BridgePointCloudManager).
/// </summary>
[System.Serializable]
public class KinectDepthPostProcessingSettings
{
    [Header("Depth gate (m) — pixels outside are dropped")]
    [Range(0.1f, 2f)] public float minDepthMeters = 0.5f;
    [Range(1f,   8f)] public float maxDepthMeters = 4.0f;

    [Header("Temporal smoothing")]
    [Tooltip("EMA blend for stable pixels. 0=no smooth, 0.95=very heavy.")]
    [Range(0f, 0.95f)] public float temporalSmoothing = 0.88f;

    [Tooltip("Faster EMA used when |new-prev| is in the 'mid-range' band " +
             "(deadband < Δ ≤ temporalMaxDelta). Lets edges respond quickly.")]
    [Range(0f, 1f)] public float temporalCorrection = 0.35f;

    [Tooltip("Threshold (m) above which we switch from slow EMA to fast EMA.")]
    [Range(0.005f, 0.5f)] public float temporalMaxDelta = 0.08f;

    [Tooltip("Threshold (m) above which we throw away history and snap to " +
             "the new value — prevents ghost trails when a person walks into a pixel.")]
    [Range(0.01f, 1f)] public float historyInvalidationThreshold = 0.30f;

    [Tooltip("Dead-band: changes smaller than this are ignored entirely; " +
             "the previous value is kept exactly. Static surfaces become 100% rigid.")]
    [Range(0f, 0.05f)] public float deadbandMeters = 0.006f;

    [Tooltip("If a pixel goes invalid this frame, hold the last valid value for " +
             "up to this many frames before declaring it dropped. Fixes per-pixel twinkle.")]
    [Range(0, 8)] public int holdFramesOnInvalid = 2;

    [Header("Edge-preserving spatial smoothing (bilateral)")]
    public bool spatialEnabled = true;
    [Range(0, 3)] public int spatialRadius = 2;
    [Tooltip("Larger = more depth-tolerant smoothing across surfaces.")]
    [Range(0.005f, 0.2f)] public float spatialSigmaDepth = 0.04f;
    [Tooltip("Larger = wider spatial averaging within the kernel.")]
    [Range(0.5f, 4f)] public float spatialSigmaSpace = 1.5f;
    [Tooltip("Neighbours with depth difference > this are ignored entirely " +
             "(hard edge gate). Prevents foreground bleeding into background.")]
    [Range(0.01f, 0.3f)] public float edgePreserveThreshold = 0.05f;

    [Header("Small-hole filling")]
    public bool holeFillEnabled = true;
    [Range(1, 4)] public int holeFillRadius = 2;
    [Tooltip("Minimum valid neighbours required to fill a hole. Higher = only fill in dense areas.")]
    [Range(2, 12)] public int holeFillMinNeighbours = 3;
    [Tooltip("Multiple iterations let newly-filled pixels become valid neighbours for the next pass — fills larger gaps.")]
    [Range(1, 5)] public int holeFillIterations = 2;
    [Tooltip("Skip filling if neighbour depth spread exceeds this — avoids smearing across edges.")]
    [Range(0.01f, 0.2f)] public float holeFillDepthTolerance = 0.08f;

    [Header("Persistent background plate (fills BIG static gaps)")]
    [Tooltip("After a pixel has been stable for plateLockFrames, its depth/colour are locked " +
             "into a permanent plate. When the pixel later goes invalid (Kinect blind spot, IR " +
             "shadow), the plate is used as the fallback. Walk around the room briefly to populate " +
             "the plate; everything seen becomes static geometry until it's overwritten by movement.")]
    public bool plateEnabled = true;
    [Range(5, 120)] public int plateLockFrames = 20;
    [Tooltip("Pixel-stability threshold above which bilateral is skipped for that pixel " +
             "(static surfaces are already frozen by the dead-band so re-averaging them is wasteful).")]
    [Range(0, 30)] public int spatialSkipStableThreshold = 4;

    public KinectDepthPostProcessingSettings Clone()
    {
        return (KinectDepthPostProcessingSettings)MemberwiseClone();
    }
}
