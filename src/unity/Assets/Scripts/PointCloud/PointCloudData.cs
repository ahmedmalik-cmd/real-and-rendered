using UnityEngine;

/// <summary>
/// Pre-allocated container for a merged point cloud.
/// Only indices [0, Count) are valid each frame.
/// </summary>
public class PointCloudData
{
    public Vector3[] Positions;
    public Color32[] Colors;
    public int Count;

    public PointCloudData(int capacity)
    {
        Positions = new Vector3[capacity];
        Colors    = new Color32[capacity];
    }
}
