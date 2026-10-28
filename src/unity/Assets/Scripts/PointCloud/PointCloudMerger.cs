using UnityEngine;

/// <summary>
/// Utility for concatenating pre-built sensor clouds into one output buffer.
/// Merging is now done inline in BridgePointCloudManager; this class is kept
/// as a helper for any future use that needs to combine two PointCloudData objects.
/// </summary>
public static class PointCloudMerger
{
    public static void Merge(
        PointCloudData src0, Matrix4x4 toWorld0,
        PointCloudData src1, Matrix4x4 toWorld1,
        PointCloudData output)
    {
        int total = 0;
        total = Copy(src0, toWorld0, output, total);
        total = Copy(src1, toWorld1, output, total);
        output.Count = total;
    }

    private static int Copy(PointCloudData src, Matrix4x4 m, PointCloudData dst, int offset)
    {
        if (src == null || src.Count == 0) return offset;
        for (int i = 0; i < src.Count && offset < dst.Positions.Length; i++, offset++)
        {
            dst.Positions[offset] = m.MultiplyPoint3x4(src.Positions[i]);
            dst.Colors[offset]    = src.Colors[i];
        }
        return offset;
    }
}
