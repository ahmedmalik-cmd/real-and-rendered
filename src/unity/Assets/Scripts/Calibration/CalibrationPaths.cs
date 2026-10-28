using System.IO;
using UnityEngine;

/// <summary>
/// One source of truth for where calibration JSON lives, so editor saves
/// survive into builds.
///
/// Strategy:
///   Editor save → Assets/StreamingAssets/CalibrationData/  (ships with every build)
///   Build save  → persistentDataPath/CalibrationData/      (always writable)
///
///   Load priority (build): persistentDataPath > StreamingAssets > legacy dataPath
///   Load priority (editor): StreamingAssets > persistentDataPath > legacy dataPath
///
/// "Legacy" = the old Application.dataPath/CalibrationData/ location used by
/// the original save code. Falls back to it so existing calibrations migrate
/// the moment the user hits Save again.
/// </summary>
public static class CalibrationPaths
{
    public static string ShippedDir => Path.Combine(Application.streamingAssetsPath, "CalibrationData");
    public static string RuntimeDir => Path.Combine(Application.persistentDataPath,  "CalibrationData");
    public static string LegacyDir  => Path.Combine(Application.dataPath,            "CalibrationData");

    /// <summary>Where Save() should write. Differs between editor and build.</summary>
    public static string GetWritePath(string filename)
    {
#if UNITY_EDITOR
        Directory.CreateDirectory(ShippedDir);
        return Path.Combine(ShippedDir, filename);
#else
        Directory.CreateDirectory(RuntimeDir);
        return Path.Combine(RuntimeDir, filename);
#endif
    }

    /// <summary>Where Load() should read from. Returns null if nothing exists anywhere.</summary>
    public static string FindReadPath(string filename)
    {
#if UNITY_EDITOR
        // Editor: prefer the shipped (StreamingAssets) copy because that's
        // what we just wrote when the user pressed save.
        string a = Path.Combine(ShippedDir, filename);
        if (File.Exists(a)) return a;
        string b = Path.Combine(RuntimeDir, filename);
        if (File.Exists(b)) return b;
#else
        // Build: prefer the runtime (persistent) copy because that's the
        // user's most recent in-build save (if any).
        string a = Path.Combine(RuntimeDir, filename);
        if (File.Exists(a)) return a;
        string b = Path.Combine(ShippedDir, filename);
        if (File.Exists(b)) return b;
#endif
        // Legacy fallback — old dataPath location. Read-only fall-through;
        // the next Save() migrates it to the new location.
        string c = Path.Combine(LegacyDir, filename);
        if (File.Exists(c)) return c;
        return null;
    }
}
