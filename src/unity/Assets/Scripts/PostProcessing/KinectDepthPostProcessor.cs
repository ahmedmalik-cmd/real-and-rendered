using UnityEngine;

/// <summary>
/// Stateless filter passes that operate on pre-allocated buffers.
/// Each step is one linear scan of the depth image so it can be ported to
/// a compute shader later without restructuring.
///
/// Pipeline order (run by KinectFrameProcessor):
///   1. ValidateAndPopulate  — splat raw sensor frame into the depth/colour grid
///   2. TemporalSmooth       — per-pixel EMA with history-invalidation + dead-band
///   3. SpatialBilateral     — edge-preserving denoise
///   4. FillHoles            — median-fill small invalid regions
///   5. RebuildLocalPoints   — re-project (X,Y) to match new Z
///   6. ApplyRGBAlignment    — refine bridge-side colour registration
/// </summary>
public static class KinectDepthPostProcessor
{
    // ── 1. validation ────────────────────────────────────────────────────────
    public static void ValidateAndPopulate(
        BinarySensorFrame sensor,
        float minD, float maxD,
        float[] depth, Vector3[] localPts, Color32[] colorsIn, byte[] playerIdx, bool[] invalid,
        out int rejected)
    {
        int w = sensor.DepthWidth, h = sensor.DepthHeight;
        int n = w * h;
        rejected = 0;

        // Clear masks + depth
        for (int i = 0; i < n; i++) { depth[i] = 0f; invalid[i] = true; playerIdx[i] = 0; }

        var pts  = sensor.Points;
        var dx   = sensor.DepthX;
        var dy   = sensor.DepthY;
        var cols = sensor.RgbColors;
        var pis  = sensor.PlayerIndices;
        bool havePI = pis != null && pis.Length == pts.Length;

        for (int pi = 0; pi < pts.Length; pi++)
        {
            int px = dx[pi], py = dy[pi];
            if ((uint)px >= (uint)w || (uint)py >= (uint)h) continue;
            float z = pts[pi].z;
            if (z < minD || z > maxD) { rejected++; continue; }
            int idx       = py * w + px;
            depth[idx]    = z;
            localPts[idx] = pts[pi];
            colorsIn[idx] = cols[pi];
            invalid[idx]  = false;
            if (havePI) playerIdx[idx] = (byte)pis[pi];
        }
    }

    // ── 2. temporal smoothing with hold + dead-band + history invalidation ───
    // plateDepth is optional — pass null if persistent plate is disabled.
    // Plate is cleared on major depth changes so dynamic objects don't lock in.
    public static void TemporalSmooth(
        int w, int h,
        float[] depth, bool[] invalid,
        float[] history, byte[] stable, int[] holdCount,
        float[] plateDepth,
        KinectDepthPostProcessingSettings s)
    {
        int   n         = w * h;
        float alphaSlow = 1f - s.temporalSmoothing;
        float alphaFast = s.temporalCorrection;
        float dead      = s.deadbandMeters;
        float midGate   = s.temporalMaxDelta;
        float resetGate = s.historyInvalidationThreshold;
        int   maxHold   = s.holdFramesOnInvalid;

        for (int i = 0; i < n; i++)
        {
            float prev = history[i];

            // ── pixel invalid this frame ──────────────────────────────────────
            if (invalid[i])
            {
                if (prev > 0f && holdCount[i] < maxHold)
                {
                    // brief dropout — substitute the held value
                    depth[i]    = prev;
                    invalid[i]  = false;
                    holdCount[i]++;
                }
                else
                {
                    history[i]   = 0f;
                    stable[i]    = 0;
                    holdCount[i] = 0;
                }
                continue;
            }

            holdCount[i] = 0;

            // ── first valid sample at this pixel ──────────────────────────────
            if (prev <= 0f)
            {
                history[i] = depth[i];
                stable[i]  = 0;
                continue;
            }

            float d  = depth[i] - prev;
            float ad = d < 0 ? -d : d;

            if (ad > resetGate)
            {
                // huge jump → wipe history (person walked into this pixel)
                history[i] = depth[i];
                stable[i]  = 0;
                if (plateDepth != null) plateDepth[i] = 0f;   // invalidate plate
            }
            else if (ad < dead)
            {
                // frozen
                depth[i] = prev;
                if (stable[i] < 255) stable[i]++;
            }
            else if (ad > midGate)
            {
                // mid-range change → fast correction
                float v    = prev + d * alphaFast;
                depth[i]   = v;
                history[i] = v;
                stable[i]  = 0;
                if (plateDepth != null) plateDepth[i] = 0f;   // invalidate plate
            }
            else
            {
                // small change → slow EMA
                float v    = prev + d * alphaSlow;
                depth[i]   = v;
                history[i] = v;
                if (stable[i] > 0) stable[i]--;
            }
        }
    }

    // ── 2b. depth-weight LUT for bilateral (replaces per-pixel Mathf.Exp) ────
    public static void BuildDepthLUT(float[] lut, float edge, float sigmaDepth)
    {
        float invVar = -0.5f / (sigmaDepth * sigmaDepth);
        int n = lut.Length;
        float step = edge / n;
        for (int i = 0; i < n; i++)
        {
            float d = (i + 0.5f) * step;
            lut[i] = Mathf.Exp(d * d * invVar);
        }
    }

    // ── 2c. persistent background plate ──────────────────────────────────────
    // Two-in-one pass:
    //   • valid + stable enough  → lock current depth/colour/local into the plate
    //   • invalid + plate exists → restore from the plate, mark as filled
    public static void UpdateAndApplyPlate(
        int n,
        float[] depth, Vector3[] localPts, Color32[] colorsRaw,
        bool[] invalid, bool[] filled, byte[] stable,
        float[] plateDepth, Vector3[] plateLocal, Color32[] plateColor,
        KinectDepthPostProcessingSettings s)
    {
        if (!s.plateEnabled) return;
        int lockFrames = s.plateLockFrames;

        for (int i = 0; i < n; i++)
        {
            if (invalid[i])
            {
                if (plateDepth[i] > 0f)
                {
                    depth[i]     = plateDepth[i];
                    localPts[i]  = plateLocal[i];
                    colorsRaw[i] = plateColor[i];
                    invalid[i]   = false;
                    filled[i]    = true;
                }
            }
            else if (stable[i] >= lockFrames)
            {
                plateDepth[i] = depth[i];
                plateLocal[i] = localPts[i];
                plateColor[i] = colorsRaw[i];
            }
        }
    }

    // ── 3. edge-preserving bilateral on depth (LUT + skip-stable) ────────────
    public static void SpatialBilateral(
        int w, int h,
        float[] inDepth, float[] outDepth, bool[] invalid,
        byte[] stable, float[] depthLut, float[] spatialWeights,
        KinectDepthPostProcessingSettings s)
    {
        int r = s.spatialRadius;
        if (r <= 0) { System.Array.Copy(inDepth, outDepth, w * h); return; }

        int k        = 2 * r + 1;
        float edge   = s.edgePreserveThreshold;
        int lutN     = depthLut.Length;
        float lutMul = lutN / edge;
        int skipS    = s.spatialSkipStableThreshold;

        System.Array.Copy(inDepth, outDepth, w * h);

        for (int y = 0; y < h; y++)
        {
            int yMin = y - r; if (yMin < 0) yMin = 0;
            int yMax = y + r; if (yMax >= h) yMax = h - 1;
            for (int x = 0; x < w; x++)
            {
                int idx = y * w + x;
                if (invalid[idx]) continue;
                // Skip already-stable pixels — bilateral would only disturb the dead-band freeze
                if (stable != null && stable[idx] > skipS) continue;
                float zc = inDepth[idx];

                int xMin = x - r; if (xMin < 0) xMin = 0;
                int xMax = x + r; if (xMax >= w) xMax = w - 1;

                float wSum = 0f, dSum = 0f;
                for (int ny = yMin; ny <= yMax; ny++)
                {
                    int rowBase = ny * w;
                    int kRow    = (ny - y + r) * k;
                    for (int nx = xMin; nx <= xMax; nx++)
                    {
                        int ni = rowBase + nx;
                        if (invalid[ni]) continue;
                        float zn = inDepth[ni];
                        float dd = zn - zc;
                        if (dd < 0) dd = -dd;
                        if (dd >= edge) continue;          // hard edge gate

                        int bin = (int)(dd * lutMul);
                        if (bin >= lutN) bin = lutN - 1;
                        float wgt = spatialWeights[kRow + (nx - x + r)] * depthLut[bin];
                        wSum += wgt;
                        dSum += wgt * zn;
                    }
                }
                if (wSum > 0f) outDepth[idx] = dSum / wSum;
            }
        }
    }

    public static float[] BuildSpatialWeights(int radius, float sigmaSpace)
    {
        int k = 2 * radius + 1;
        var w = new float[k * k];
        float inv = -0.5f / (sigmaSpace * sigmaSpace);
        for (int dy = -radius; dy <= radius; dy++)
        for (int dx = -radius; dx <= radius; dx++)
            w[(dy + radius) * k + (dx + radius)] = Mathf.Exp((dx * dx + dy * dy) * inv);
        return w;
    }

    // ── 4. small-hole median fill ────────────────────────────────────────────
    public static void FillHoles(
        int w, int h,
        float[] depth, Vector3[] localPts, Color32[] colors,
        bool[] invalid, bool[] filled,
        KinectDepthPostProcessingSettings s)
    {
        int r    = s.holeFillRadius;
        int minN = s.holeFillMinNeighbours;
        float tol = s.holeFillDepthTolerance;
        int kCap = (2 * r + 1) * (2 * r + 1);
        var nbrZ = new float[kCap];

        for (int iter = 0; iter < s.holeFillIterations; iter++)
        {
            for (int y = r; y < h - r; y++)
            for (int x = r; x < w - r; x++)
            {
                int idx = y * w + x;
                if (!invalid[idx]) continue;

                int   cnt   = 0;
                float zMin  = float.MaxValue, zMax = 0f;
                float sumR  = 0, sumG = 0, sumB = 0;
                float sumXZ = 0, sumYZ = 0;

                for (int dy = -r; dy <= r; dy++)
                {
                    int rowBase = (y + dy) * w;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int ni = rowBase + (x + dx);
                        if (invalid[ni]) continue;
                        float z   = depth[ni];
                        nbrZ[cnt++] = z;
                        if (z < zMin) zMin = z;
                        if (z > zMax) zMax = z;
                        Vector3 p = localPts[ni];
                        if (p.z > 0.001f) { sumXZ += p.x / p.z; sumYZ += p.y / p.z; }
                        Color32 c = colors[ni];
                        sumR += c.r; sumG += c.g; sumB += c.b;
                    }
                }

                if (cnt < minN) continue;
                if (zMax - zMin > tol) continue;        // straddles a depth edge

                // Median of cnt elements
                InsertionSort(nbrZ, cnt);
                float zMed = nbrZ[cnt / 2];

                float inv = 1f / cnt;
                depth[idx]    = zMed;
                localPts[idx] = new Vector3(sumXZ * inv * zMed, sumYZ * inv * zMed, zMed);
                colors[idx]   = new Color32(
                    (byte)(sumR * inv), (byte)(sumG * inv), (byte)(sumB * inv), 255);
                invalid[idx]  = false;
                filled[idx]   = true;
            }
        }
    }

    // ── 5. re-project (X,Y) so the angular direction matches the new Z ───────
    public static void RebuildLocalPoints(int n, float[] depth, Vector3[] localPts, bool[] invalid)
    {
        for (int i = 0; i < n; i++)
        {
            if (invalid[i]) continue;
            Vector3 p = localPts[i];
            if (p.z < 0.001f) continue;
            float scale = depth[i] / p.z;
            localPts[i] = new Vector3(p.x * scale, p.y * scale, depth[i]);
        }
    }

    // ── 6. resample colour to compensate for residual RGB↔depth misalignment ─
    public static void ApplyRGBAlignment(
        int w, int h,
        Color32[] inColors, Color32[] outColors, bool[] invalid,
        RGBDepthAlignmentSettings a)
    {
        if (a.rgbOffsetX == 0 && a.rgbOffsetY == 0 &&
            Mathf.Approximately(a.rgbScaleX, 1f) && Mathf.Approximately(a.rgbScaleY, 1f))
        {
            System.Array.Copy(inColors, outColors, w * h);
            return;
        }

        float sx = a.rgbScaleX, sy = a.rgbScaleY;
        float cx = w * 0.5f,   cy = h * 0.5f;
        int   ox = a.rgbOffsetX, oy = a.rgbOffsetY;
        bool  bil = a.bilinearSampling;

        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int idx = y * w + x;
            if (invalid[idx]) { outColors[idx] = default; continue; }

            float fx = (x - cx) * sx + cx + ox;
            float fy = (y - cy) * sy + cy + oy;
            int ix = Mathf.FloorToInt(fx);
            int iy = Mathf.FloorToInt(fy);

            if (ix < 0 || iy < 0 || ix >= w - 1 || iy >= h - 1)
            { outColors[idx] = inColors[idx]; continue; }

            if (bil)
            {
                float tx = fx - ix, ty = fy - iy;
                Color32 c00 = inColors[iy * w + ix];
                Color32 c10 = inColors[iy * w + ix + 1];
                Color32 c01 = inColors[(iy + 1) * w + ix];
                Color32 c11 = inColors[(iy + 1) * w + ix + 1];
                outColors[idx] = LerpC32(LerpC32(c00, c10, tx), LerpC32(c01, c11, tx), ty);
            }
            else
            {
                outColors[idx] = inColors[iy * w + ix];
            }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────
    private static Color32 LerpC32(Color32 a, Color32 b, float t)
    {
        return new Color32(
            (byte)(a.r + (b.r - a.r) * t),
            (byte)(a.g + (b.g - a.g) * t),
            (byte)(a.b + (b.b - a.b) * t),
            255);
    }

    private static void InsertionSort(float[] arr, int n)
    {
        for (int i = 1; i < n; i++)
        {
            float v = arr[i]; int j = i - 1;
            while (j >= 0 && arr[j] > v) { arr[j + 1] = arr[j]; j--; }
            arr[j + 1] = v;
        }
    }
}
