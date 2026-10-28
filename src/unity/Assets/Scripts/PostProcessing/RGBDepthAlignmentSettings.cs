using UnityEngine;

/// <summary>
/// Per-sensor refinement of the Kinect SDK's depth↔color CoordinateMapper.
/// The bridge already runs the SDK's MapDepthFrameToColorFrame, so each
/// depth pixel arrives with an associated colour sampled at the corresponding
/// colour-frame pixel. These settings let you compensate for residual
/// misalignment (typical: 1–3 pixels) by resampling the per-pixel colour grid.
/// </summary>
[System.Serializable]
public class RGBDepthAlignmentSettings
{
    [Header("Pixel offset (depth-image pixels)")]
    [Range(-20, 20)] public int rgbOffsetX = 0;
    [Range(-20, 20)] public int rgbOffsetY = 0;

    [Header("Pixel scale (around image centre)")]
    [Range(0.85f, 1.15f)] public float rgbScaleX = 1f;
    [Range(0.85f, 1.15f)] public float rgbScaleY = 1f;

    [Tooltip("Bilinear blend of the 4 nearest colour cells (smoother) vs. " +
             "nearest neighbour (sharper but more aliased).")]
    public bool bilinearSampling = true;
}
