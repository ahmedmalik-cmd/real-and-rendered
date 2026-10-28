using System;
using UnityEngine;

/// <summary>
/// Pure-C# Direct Linear Transform homography solver. Computes the 3×3
/// matrix H mapping 4 source points to 4 destination points in homogeneous
/// coordinates: for each pair, H · (src, 1)ᵀ ≅ (dst, 1)ᵀ.
///
/// Sets up the standard 8-equation, 8-unknown system (with h22 = 1) and
/// solves via Gaussian elimination with partial pivoting. No external
/// dependencies.
///
/// Returns the rows packed as three Vector4s (xyz = matrix row, w unused)
/// so they can be sent straight to a shader via Material.SetVector.
/// If the system is singular (degenerate corner configuration) the solver
/// returns the identity matrix and reports false.
/// </summary>
public static class HomographySolver
{
    /// <summary>
    /// Solve for H such that H · src = dst in homogeneous coords.
    /// Corners passed in the order BL, BR, TL, TR.
    /// </summary>
    public static bool Solve(
        Vector2 srcBL, Vector2 srcBR, Vector2 srcTL, Vector2 srcTR,
        Vector2 dstBL, Vector2 dstBR, Vector2 dstTL, Vector2 dstTR,
        out Vector4 row0, out Vector4 row1, out Vector4 row2)
    {
        var srcA = new[] { srcBL, srcBR, srcTL, srcTR };
        var dstA = new[] { dstBL, dstBR, dstTL, dstTR };

        // Build the 8×9 augmented matrix A | b
        var A = new double[8, 9];
        for (int i = 0; i < 4; i++)
        {
            double x = srcA[i].x, y = srcA[i].y;
            double X = dstA[i].x, Y = dstA[i].y;

            int r = i * 2;
            A[r, 0] = x;     A[r, 1] = y;     A[r, 2] = 1;
            A[r, 3] = 0;     A[r, 4] = 0;     A[r, 5] = 0;
            A[r, 6] = -X * x; A[r, 7] = -X * y;
            A[r, 8] = X;

            int s = r + 1;
            A[s, 0] = 0;     A[s, 1] = 0;     A[s, 2] = 0;
            A[s, 3] = x;     A[s, 4] = y;     A[s, 5] = 1;
            A[s, 6] = -Y * x; A[s, 7] = -Y * y;
            A[s, 8] = Y;
        }

        if (GaussianSolve8(A, out double[] h))
        {
            row0 = new Vector4((float)h[0], (float)h[1], (float)h[2], 0f);
            row1 = new Vector4((float)h[3], (float)h[4], (float)h[5], 0f);
            row2 = new Vector4((float)h[6], (float)h[7], 1f,           0f);
            return true;
        }

        // Singular system — fall back to identity (no warp)
        row0 = new Vector4(1, 0, 0, 0);
        row1 = new Vector4(0, 1, 0, 0);
        row2 = new Vector4(0, 0, 1, 0);
        return false;
    }

    private static bool GaussianSolve8(double[,] A, out double[] x)
    {
        const int N = 8;
        for (int k = 0; k < N; k++)
        {
            // Partial pivot — swap in the row with the largest |A[*,k]|
            int piv = k;
            double maxAbs = Math.Abs(A[k, k]);
            for (int i = k + 1; i < N; i++)
            {
                double v = Math.Abs(A[i, k]);
                if (v > maxAbs) { maxAbs = v; piv = i; }
            }
            if (maxAbs < 1e-12) { x = null; return false; }
            if (piv != k)
            {
                for (int j = 0; j <= N; j++)
                {
                    double tmp = A[k, j]; A[k, j] = A[piv, j]; A[piv, j] = tmp;
                }
            }
            // Eliminate below
            for (int i = k + 1; i < N; i++)
            {
                double f = A[i, k] / A[k, k];
                if (f == 0) continue;
                for (int j = k; j <= N; j++)
                    A[i, j] -= f * A[k, j];
            }
        }

        x = new double[N];
        for (int i = N - 1; i >= 0; i--)
        {
            double s = A[i, N];
            for (int j = i + 1; j < N; j++) s -= A[i, j] * x[j];
            x[i] = s / A[i, i];
        }
        return true;
    }
}
