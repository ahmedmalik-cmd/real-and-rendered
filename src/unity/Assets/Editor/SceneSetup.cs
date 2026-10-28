using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.IO;

/// <summary>
/// Menu: Kinect → Setup Scene  (Ctrl+Shift+K)
/// Builds the full scene hierarchy wired to the bridge-based pipeline.
/// Safe to re-run — finds existing objects by name instead of duplicating.
/// </summary>
public static class SceneSetup
{
    private const string ShaderPath  = "Assets/Shaders/PointCloud.shader";
    private const string MatPath     = "Assets/Materials/PointCloudMat.mat";
    private const string RoomFbxPath = "Assets/Models/usdan_space.fbx";

    [MenuItem("Kinect/Setup Scene %#k")]
    public static void Setup()
    {
        // ── Material ─────────────────────────────────────────────────────────
        if (!Directory.Exists("Assets/Materials"))
            AssetDatabase.CreateFolder("Assets", "Materials");

        Material mat = AssetDatabase.LoadAssetAtPath<Material>(MatPath);
        if (mat == null)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            if (shader == null)
            {
                Debug.LogError($"PointCloud shader not found at {ShaderPath}.");
                return;
            }
            mat = new Material(shader) { name = "PointCloudMat" };
            mat.SetFloat("_PointSize", 5f);
            AssetDatabase.CreateAsset(mat, MatPath);
            AssetDatabase.SaveAssets();
        }

        // ── Main Camera ───────────────────────────────────────────────────────
        // CRITICAL: look up by tag, not FindObjectOfType<Camera>(). Once we add
        // projector cameras to the scene, FindObjectOfType might return one of
        // those instead of the actual Main Camera, and OrbitCamera would end
        // up on the wrong object.
        Camera cam = null;
        var mainGo = GameObject.FindWithTag("MainCamera");
        if (mainGo) cam = mainGo.GetComponent<Camera>();
        if (cam == null)
        {
            var go = new GameObject("Main Camera") { tag = "MainCamera" };
            cam = go.AddComponent<Camera>();
            go.AddComponent<AudioListener>();
            mainGo = go;
        }
        cam.transform.position  = new Vector3(0f, 1.8f, -5f);
        cam.transform.rotation  = Quaternion.Euler(12f, 0f, 0f);
        cam.farClipPlane        = 50f;
        cam.backgroundColor     = Color.black;
        cam.clearFlags          = CameraClearFlags.SolidColor;
        cam.targetDisplay       = 0;          // Main Camera always renders to Display 1
        cam.depth               = -1;         // and renders FIRST (below any UI cameras)

        // OrbitCamera lives on Main Camera and only Main Camera. Defensive:
        // strip OrbitCamera from any other camera that might have one.
        foreach (var anyCam in Object.FindObjectsOfType<Camera>(true))
        {
            if (anyCam.gameObject == mainGo) continue;
            var stray = anyCam.gameObject.GetComponent<OrbitCamera>();
            if (stray)
            {
                Debug.LogWarning($"[Setup] Removing stray OrbitCamera from {anyCam.name}");
                Object.DestroyImmediate(stray);
            }
        }
        var orbit = mainGo.GetOrAddComponent<OrbitCamera>();
        orbit.target   = new Vector3(0f, 1f, 0f);
        orbit.distance = 5f;
        orbit.yaw      = 0f;
        orbit.pitch    = 25f;

        // ── Light ─────────────────────────────────────────────────────────────
        if (Object.FindObjectOfType<Light>() == null)
        {
            var go = new GameObject("Directional Light");
            var l  = go.AddComponent<Light>();
            l.type = LightType.Directional;
            go.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
        }

        // ── Floor guide ───────────────────────────────────────────────────────
        var floor = GameObject.Find("RoomFloor");
        if (floor == null)
        {
            floor = GameObject.CreatePrimitive(PrimitiveType.Cube);
            floor.name = "RoomFloor";
            floor.transform.localScale = new Vector3(5f, 0.02f, 5f);
            var shader = Shader.Find("Standard") ?? Shader.Find("Diffuse");
            if (shader != null)
                floor.GetComponent<MeshRenderer>().sharedMaterial =
                    new Material(shader) { color = new Color(0.15f, 0.15f, 0.15f) };
        }

        // ── Kinect Anchors ────────────────────────────────────────────────────
        // These are APPROXIMATE defaults for a 5×5 m room.
        // The anchor's +Z (blue arrow in Scene view) must face into the room.
        // Adjust position + rotation to match your physical setup.
        var k0 = GetOrCreate("Kinect0Anchor").transform;
        k0.position    = new Vector3(-2.5f, 2.5f, -2.5f);
        k0.eulerAngles = new Vector3(35f,  45f,   0f);

        var k1 = GetOrCreate("Kinect1Anchor").transform;
        k1.position    = new Vector3( 2.5f, 2.5f,  2.5f);
        k1.eulerAngles = new Vector3(35f, 225f,   0f);

        // ── Bridge Manager ────────────────────────────────────────────────────
        var mgrGo = GetOrCreate("KinectBridgeManager");
        var mgr   = mgrGo.GetOrAddComponent<BridgePointCloudManager>();
        mgr.kinect0Anchor = k0;
        mgr.kinect1Anchor = k1;
        mgr.bridgeUrl     = "http://localhost:8182";

        // ── Point Cloud Renderer ──────────────────────────────────────────────
        var rdrGo = GetOrCreate("PointCloudRenderer");
        var rdr   = rdrGo.GetOrAddComponent<PointCloudRenderer>();
        rdr.bridgeManager      = mgr;
        rdr.pointCloudMaterial = mat;
        rdr.pointSize          = 5f;
        rdr.testMode           = false;
        rdr.showDebugHUD       = true;

        // ── Background Model + Renderer ───────────────────────────────────────
        var bgGo  = GetOrCreate("BackgroundCapture");
        var bgMod = bgGo.GetOrAddComponent<BackgroundModel>();
        var bgRdr = bgGo.GetOrAddComponent<BackgroundRenderer>();
        bgRdr.pointCloudMaterial = mat;
        bgRdr.pointSize          = 3f;
        mgr.backgroundModel      = bgMod;

        // ── Room Model (auto-wired from Assets/Models/usdan_space.fbx) ────────
        var rmGo = GetOrCreate("RoomModel");
        var rm   = rmGo.GetOrAddComponent<RoomModel>();

        var fbxPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(RoomFbxPath);
        if (fbxPrefab != null)
        {
            // Find/create the prefab instance under the RoomModel anchor
            Transform existing = rmGo.transform.Find("usdan_space");
            GameObject inst = existing ? existing.gameObject
                                       : (GameObject)PrefabUtility.InstantiatePrefab(fbxPrefab, rmGo.transform);
            inst.name = "usdan_space";
            inst.transform.localPosition = Vector3.zero;
            inst.transform.localRotation = Quaternion.identity;

            // Auto-assign first MeshFilter found in the imported hierarchy
            var mf = inst.GetComponentInChildren<MeshFilter>();
            if (mf) rm.sourceMesh = mf;

            // Default to hidden so it doesn't overlay the cloud — toggle in inspector
            rm.renderModel = false;
            var mr = mf ? mf.GetComponent<MeshRenderer>() : null;
            if (mr) mr.enabled = false;
        }
        else
        {
            Debug.LogWarning($"[Kinect] Room FBX not found at {RoomFbxPath}. " +
                             "Drop a scan there and re-run Setup.");
        }

        // ── Calibration Controller ────────────────────────────────────────────
        var calGo = GetOrCreate("CalibrationController");
        var cal   = calGo.GetOrAddComponent<CalibrationController>();
        cal.bridgeManager = mgr;
        cal.cloudRenderer = rdr;
        cal.roomModel     = rm;

        // ── Post-processing debug HUD ─────────────────────────────────────────
        var dbg = mgrGo.GetOrAddComponent<DepthPostProcessingDebugView>();
        dbg.bridgeManager = mgr;

        // ── OSC (shared by Theremin + LLaVA perception) ───────────────────────
        var oscGo = GetOrCreate("OSC");
        var osc   = oscGo.GetOrAddComponent<OscReceiver>();
        osc.port  = 9000;

        // ── Theremini ─────────────────────────────────────────────────────────
        var thGo    = GetOrCreate("Theremini");
        var thInput = thGo.GetOrAddComponent<ThereminiInput>();
        thInput.osc = osc;
        var thCtrl  = thGo.GetOrAddComponent<ThereminiVisualController>();
        thCtrl.input              = thInput;
        thCtrl.pointCloudMaterial = mat;

        // ── Perception (LLaVA via Python bridge + Kinect presence) ────────────
        var percGo  = GetOrCreate("Perception");
        var cap     = percGo.GetOrAddComponent<GameViewCapture>();
        cap.sourceCamera = cam;
        var perc    = percGo.GetOrAddComponent<PerceptionReceiver>();
        perc.osc    = osc;
        var pres    = percGo.GetOrAddComponent<KinectPresenceDetector>();
        pres.bridgeManager = mgr;

        // Presence exporter — writes a JSON file every 2 s for the Python TRIBE client to read
        var presExp = percGo.GetOrAddComponent<PresenceExporter>();
        presExp.source = pres;

        // TRIBE v2 (HTTP poll of tribe_kinect_client at localhost:8183)
        var trbRecv = percGo.GetOrAddComponent<TribePerceptionReceiver>();
        var trbCtrl = percGo.GetOrAddComponent<TribeVisualController>();
        trbCtrl.receiver           = trbRecv;
        trbCtrl.pointCloudMaterial = mat;

        var disp    = percGo.GetOrAddComponent<PerceptionDisplay>();
        disp.receiver = perc;
        disp.theremin = thInput;
        disp.presence = pres;
        disp.tribe    = trbRecv;

        // ── Multi-display bootstrap (MUST exist before any projector camera renders) ──
        mgrGo.GetOrAddComponent<MultiDisplayBootstrap>();

        // ── Projector Rig ────────────────────────────────────────────────────
        // Edit-time: create real Camera GameObjects so they show in the
        // Hierarchy and you can preview each Display in the Game-view dropdown.
        // Runtime: ProjectorRig (DefaultExecutionOrder -900) finds them; if any
        // are missing (e.g. scene wasn't saved before build) it creates them.
        DestroyStale("Projectors");          // remove obsolete parent GO if present
        DestroyStale("ProjectorA_Camera_RT"); // runtime-only siblings from earlier code
        DestroyStale("ProjectorB_Camera_RT");
        DestroyStale("ProjectorC_Caption_RT");

        var rigGo = GetOrCreate("ProjectorRig");
        var rig   = rigGo.GetOrAddComponent<ProjectorRig>();
        rig.perceptionReceiver = perc;

        // ProjectorCalibrator listens for the P key at runtime to enter projector
        // calibration mode. Attach to the same GameObject as the rig and wire it.
        var pcal = rigGo.GetOrAddComponent<ProjectorCalibrator>();
        pcal.rig = rig;

        Camera MakeProjCam(string name, Vector3 defaultPos, int dispIdx, string label, float fov, bool aimAtScene)
        {
            // Defensive: recreate if existing GO has no Camera component
            var go = GameObject.Find(name);
            if (go != null && go.GetComponent<Camera>() == null)
            {
                Debug.LogWarning($"[Setup] {name} broken (no Camera) — recreating.");
                Object.DestroyImmediate(go);
                go = null;
            }
            if (go == null)
            {
                go = new GameObject(name);
                go.transform.SetParent(rigGo.transform, true);
                go.transform.position = defaultPos;
                if (aimAtScene) go.transform.LookAt(new Vector3(0f, 0.9f, 0f));
            }
            else go.transform.SetParent(rigGo.transform, true);

            var c = go.GetComponent<Camera>();
            if (c == null) c = go.AddComponent<Camera>();
            if (c == null) { Debug.LogError($"[Setup] AddComponent<Camera>() failed on {name}"); return null; }
            c.clearFlags       = CameraClearFlags.SolidColor;
            c.backgroundColor  = Color.black;
            c.fieldOfView      = fov;
            c.farClipPlane     = 30f;
            c.depth            = 0;
            c.enabled          = true;
            c.targetDisplay    = dispIdx;

            // Ensure a framer child exists (clears the FULL display to black so
            // off-viewport-rect area on the projector stays black)
            string framerName = name + "_BG";
            var bgChild = go.transform.Find(framerName);
            GameObject bgGo = bgChild ? bgChild.gameObject : new GameObject(framerName);
            bgGo.transform.SetParent(go.transform, false);
            var bg = bgGo.GetComponent<Camera>() ?? bgGo.AddComponent<Camera>();
            bg.clearFlags      = CameraClearFlags.SolidColor;
            bg.backgroundColor = Color.black;
            bg.cullingMask     = 0;
            bg.depth           = -10;
            bg.enabled         = true;
            bg.targetDisplay   = dispIdx;
            bg.allowHDR        = false;
            bg.allowMSAA       = false;
            bg.useOcclusionCulling = false;
            return c;
        }

        var camA = MakeProjCam("ProjectorA_Camera", new Vector3(-1.6f, 2.0f, -2.6f), 1, "Projector A", 45f, true);
        if (camA != null)
        {
            var po = camA.gameObject.GetComponent<ProjectorOutput>() ?? camA.gameObject.AddComponent<ProjectorOutput>();
            po.displayIndex   = 1;
            po.projectorLabel = "Projector A";
            if (camA.gameObject.GetComponent<ProjectorWarp>() == null)
                camA.gameObject.AddComponent<ProjectorWarp>();
            rig.projectorACam = camA;
        }

        var camB = MakeProjCam("ProjectorB_Camera", new Vector3(1.6f, 2.0f, -2.6f), 2, "Projector B", 45f, true);
        if (camB != null)
        {
            var po = camB.gameObject.GetComponent<ProjectorOutput>() ?? camB.gameObject.AddComponent<ProjectorOutput>();
            po.displayIndex   = 2;
            po.projectorLabel = "Projector B";
            if (camB.gameObject.GetComponent<ProjectorWarp>() == null)
                camB.gameObject.AddComponent<ProjectorWarp>();
            rig.projectorBCam = camB;
        }

        // Caption projector is now a TOP-DOWN view of the cloud with the
        // perception caption layered along the bottom of the projector display.
        // Position the virtual camera high above the scene origin, rotated to
        // look straight down. Wider FOV than the side projectors so it can
        // see the whole footprint.
        var camC = MakeProjCam("ProjectorC_Caption", new Vector3(0f, 4.5f, 0f), 3, "Caption", 75f, false);
        if (camC != null)
        {
            camC.transform.rotation = Quaternion.Euler(90f, 0f, 0f);  // straight down
            // Render cloud + UI both (CaptionProjector.Awake also sets cullingMask = ~0)
            camC.cullingMask = ~0;
            var cp = camC.gameObject.GetComponent<CaptionProjector>() ?? camC.gameObject.AddComponent<CaptionProjector>();
            cp.receiver       = perc;
            cp.tribe          = trbRecv;
            cp.displayIndex   = 3;
            rig.captionProjector = cp;
        }

        // ── Remove stale objects from old setups ──────────────────────────────
        DestroyStale("DualKinectManager");

        var scene = EditorSceneManager.GetActiveScene();
        EditorSceneManager.MarkSceneDirty(scene);
        // Save immediately so subsequent builds include all components we just added
        if (!string.IsNullOrEmpty(scene.path))
        {
            EditorSceneManager.SaveScene(scene);
            Debug.Log($"[Kinect] Saved scene: {scene.path}");

            // Make sure the scene is enabled in Build Settings
            bool found = false;
            var scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
            {
                if (scenes[i].path == scene.path) { found = true; scenes[i].enabled = true; }
            }
            if (!found)
            {
                var newList = new EditorBuildSettingsScene[scenes.Length + 1];
                System.Array.Copy(scenes, newList, scenes.Length);
                newList[scenes.Length] = new EditorBuildSettingsScene(scene.path, true);
                EditorBuildSettings.scenes = newList;
                Debug.Log($"[Kinect] Added {scene.path} to Build Settings.");
            }
            else
            {
                EditorBuildSettings.scenes = scenes;
            }
        }
        else
        {
            Debug.LogWarning("[Kinect] Scene has not been saved to disk — open File → Save As, then re-run Setup.");
        }

        Debug.Log("[Kinect] Scene setup complete.\n" +
            "1. Start ThereminKinectBridge.exe (Kinect frames)\n" +
            "2. Start ollama serve   +   python Python/perception_pipeline.py (LLaVA)\n" +
            "3. Start python Python/theremini_bridge.py (Theremini → OSC)\n" +
            "4. Plug in 2 cloud projectors + 1 caption projector → Windows display 2,3,4\n" +
            "5. Build & Run (multi-display only works in standalone builds, not Editor)\n" +
            "   Position ProjectorA_Camera / ProjectorB_Camera in the scene to match your physical projectors.\n" +
            "6. F1 auto-cal · F2 ICP · F3 model-ICP · F5 PP bypass · F7 scan room · F8 LLaVA caption");

        Selection.activeGameObject = calGo;
    }

    static GameObject GetOrCreate(string name) =>
        GameObject.Find(name) ?? new GameObject(name);

    static void DestroyStale(string name)
    {
        var go = GameObject.Find(name);
        if (go != null) Object.DestroyImmediate(go);
    }
}

public static class GOExt
{
    public static T GetOrAddComponent<T>(this GameObject go) where T : Component =>
        go.GetComponent<T>() ?? go.AddComponent<T>();
}
