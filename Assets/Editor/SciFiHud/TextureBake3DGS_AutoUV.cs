using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// 알고리즘 #3 — auto-chart UV unwrap + 텍스처 베이크.
// Unity Unwrapping.GenerateSecondaryUVSet (chart-based) 으로 메쉬를 자동 펴서 UV 생성 →
// 픽셀별 raycast 로 텍스처에 색 누적. #2 의 planar 격자 무늬 해결.
public static class TextureBake3DGS_AutoUV
{
    const string SplatName = "Splat_ShinWon_1st_Cutter";
    const int RT_RES = 768;
    const int TEX_RES = 2048;
    const int PX_STRIDE = 1;
    const int NUM_VIEWS = 64;
    const int INTERIOR_GRID = 4;
    const float ANGLE_THRESHOLD = 0.05f;
    const float Y_OFFSET = 2f;
    const float X_OFFSET = 4f;       // #1 (Y+2), #2 (Y+2,X+2) 옆에 #3
    const int BakeLayer = 31;
    const string OutDir = "Assets/LCC_Generated";
    const string SpawnName = "Test_3DGSAutoUVBaked_Mesh_1st_Cutter";

    [MenuItem("Tools/Lcc Drop Forge/Hypothesis · 3DGS auto-chart UV texture bake (1st_Cutter)")]
    public static void Bake()
    {
        var splat = GameObject.Find(SplatName);
        if (splat == null) { Debug.LogError($"[AutoUVBake] '{SplatName}' 없음"); return; }
        var colTr = splat.transform.Find("__LccCollider");
        var mc = colTr != null ? colTr.GetComponent<MeshCollider>() : null;
        if (mc == null || mc.sharedMesh == null) { Debug.LogError("[AutoUVBake] __LccCollider mesh 없음"); return; }
        var arasTr = splat.transform.Find("_ArasP");
        if (arasTr == null) { Debug.LogError("[AutoUVBake] _ArasP 없음"); return; }

        var srcMesh = mc.sharedMesh;
        int nVerts = srcMesh.vertexCount;
        Debug.Log($"[AutoUVBake] mesh '{srcMesh.name}' {nVerts:N0} verts · auto-chart UV unwrap 시작");

        // ---- 1) baked mesh 복제 + GenerateSecondaryUVSet 호출 ----
        var baked = new Mesh
        {
            name = SplatName + "_3DGSAutoUVBaked",
            indexFormat = srcMesh.indexFormat
        };
        baked.SetVertices(srcMesh.vertices);
        baked.SetTriangles(srcMesh.triangles, 0, true);
        if (srcMesh.normals != null && srcMesh.normals.Length == nVerts) baked.SetNormals(srcMesh.normals);

        double tu0 = EditorApplication.timeSinceStartup;
        // angleError/areaError 가 클수록 chart 적게 생성 → 빠름. hardAngle 88°.
        bool ok = Unwrapping.GenerateSecondaryUVSet(baked, new UnwrapParam {
            angleError = 0.30f,   // 0~1 (default 0.08)
            areaError  = 0.30f,   // 0~1 (default 0.15)
            hardAngle  = 75f,     // deg (default 88)
            packMargin = 0.005f,
        });
        double tu1 = EditorApplication.timeSinceStartup;
        if (!ok)
        {
            Debug.LogError("[AutoUVBake] GenerateSecondaryUVSet 실패");
            return;
        }
        // SecondaryUVSet 은 mesh.uv2 에 박힘 — uv (UV0) 으로 복사 (텍스처 베이크에 uv 사용)
        var uv2 = baked.uv2;
        if (uv2 == null || uv2.Length == 0)
        {
            Debug.LogError("[AutoUVBake] uv2 비어있음");
            return;
        }
        baked.uv = uv2;
        Debug.Log($"[AutoUVBake] auto-chart UV 완료 ({(tu1-tu0):F1}s) · {uv2.Length:N0} uvs");

        // ---- 2) bounds (collider world transform) ----
        var bnds = mc.sharedMesh.bounds;
        var localToWorld = colTr.localToWorldMatrix;
        Vector3 wMin = Vector3.positiveInfinity, wMax = Vector3.negativeInfinity;
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = bnds.center + Vector3.Scale(bnds.extents, new Vector3(((i&1)==0?-1:1), ((i&2)==0?-1:1), ((i&4)==0?-1:1)));
            Vector3 w = localToWorld.MultiplyPoint3x4(p);
            wMin = Vector3.Min(wMin, w); wMax = Vector3.Max(wMax, w);
        }
        Vector3 center = (wMin + wMax) * 0.5f;
        float radius = (wMax - wMin).magnitude * 0.6f;

        // ---- 3) GS 격리 (다른 모든 렌더러/GS 비활성) ----
        var savedRendererStates = new List<(Renderer r, bool e)>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r == null) continue;
            if (r.transform.IsChildOf(arasTr) || r.transform == arasTr) continue;
            savedRendererStates.Add((r, r.enabled));
            r.enabled = false;
        }
        var savedGsStates = new List<(MonoBehaviour b, bool e)>();
        foreach (var mb in Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            if (mb.GetType().FullName != "GaussianSplatting.Runtime.GaussianSplatRenderer") continue;
            if (mb.transform == arasTr) continue;
            savedGsStates.Add((mb, mb.enabled));
            mb.enabled = false;
        }
        var savedLayerAras = arasTr.gameObject.layer;
        SetLayerRecursive(arasTr.gameObject, BakeLayer);

        // ---- 4) 캡처 카메라 ----
        var camGO = new GameObject("__AutoUVBakeCam_Tmp");
        var cam = camGO.AddComponent<Camera>();
        cam.cullingMask = 1 << BakeLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0,0,0,0);
        cam.nearClipPlane = Mathf.Max(0.01f, radius * 0.05f);
        cam.farClipPlane = radius * 5f;
        cam.allowHDR = true;

        var rt = new RenderTexture(RT_RES, RT_RES, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        cam.targetTexture = rt;
        var readTex = new Texture2D(RT_RES, RT_RES, TextureFormat.RGBA32, false);

        // ---- 5) 캡처 위치/방향 ----
        var captures = new List<(Vector3 pos, Quaternion rot, float fov)>();
        for (int v = 0; v < NUM_VIEWS; v++)
        {
            float phi = Mathf.Acos(1f - 2f * (v + 0.5f) / NUM_VIEWS);
            float theta = Mathf.PI * (1f + Mathf.Sqrt(5f)) * v;
            Vector3 dir = new Vector3(Mathf.Sin(phi)*Mathf.Cos(theta), Mathf.Sin(phi)*Mathf.Sin(theta), Mathf.Cos(phi));
            Vector3 pos = center + dir * radius;
            captures.Add((pos, Quaternion.LookRotation(center - pos, Vector3.up), 50f));
        }
        Vector3 boundsSize = wMax - wMin;
        Quaternion[] cubeFaces = {
            Quaternion.LookRotation(Vector3.right, Vector3.up),
            Quaternion.LookRotation(Vector3.left, Vector3.up),
            Quaternion.LookRotation(Vector3.forward, Vector3.up),
            Quaternion.LookRotation(Vector3.back, Vector3.up),
            Quaternion.LookRotation(Vector3.up, Vector3.forward),
            Quaternion.LookRotation(Vector3.down, Vector3.forward),
        };
        int g = INTERIOR_GRID;
        for (int gx = 0; gx < g; gx++)
        for (int gy = 0; gy < g; gy++)
        for (int gz = 0; gz < g; gz++)
        {
            Vector3 t = new Vector3((gx+0.5f)/g, (gy+0.5f)/g, (gz+0.5f)/g);
            Vector3 pos = wMin + boundsSize * 0.1f + Vector3.Scale(boundsSize * 0.8f, t);
            foreach (var rot in cubeFaces) captures.Add((pos, rot, 90f));
        }
        Debug.Log($"[AutoUVBake] 캡처 {captures.Count}");

        // ---- 6) 텍스처 누적 버퍼 (linear-space) ----
        var accumR = new float[TEX_RES * TEX_RES];
        var accumG = new float[TEX_RES * TEX_RES];
        var accumB = new float[TEX_RES * TEX_RES];
        var accumW = new float[TEX_RES * TEX_RES];

        var meshTris = baked.triangles;
        var meshUVs  = baked.uv;
        // mesh가 src 와 같은 vertex 순서(우리 SetVertices/SetTriangles 그대로). UV0(=uv2 복사) 이 채워짐.

        double t0 = EditorApplication.timeSinceStartup;
        int totalHits = 0;

        for (int v = 0; v < captures.Count; v++)
        {
            cam.transform.position = captures[v].pos;
            cam.transform.rotation = captures[v].rot;
            cam.fieldOfView = captures[v].fov;
            cam.Render();

            RenderTexture.active = rt;
            readTex.ReadPixels(new Rect(0, 0, RT_RES, RT_RES), 0, 0);
            readTex.Apply(false);
            RenderTexture.active = null;
            var pixels = readTex.GetPixels32();

            for (int py = 0; py < RT_RES; py += PX_STRIDE)
            {
                for (int px = 0; px < RT_RES; px += PX_STRIDE)
                {
                    var c = pixels[py * RT_RES + px];
                    if (c.a < 16) continue;
                    var ray = cam.ScreenPointToRay(new Vector3(px + 0.5f, py + 0.5f, 0));
                    if (!Physics.Raycast(ray, out RaycastHit hit, cam.farClipPlane, ~0, QueryTriggerInteraction.Ignore)) continue;
                    if (hit.collider != mc) continue;

                    int tBase = hit.triangleIndex * 3;
                    if (tBase + 2 >= meshTris.Length) continue;
                    int v0 = meshTris[tBase], v1 = meshTris[tBase+1], v2 = meshTris[tBase+2];
                    Vector2 uv0 = meshUVs[v0], uv1 = meshUVs[v1], uv2t = meshUVs[v2];

                    Vector3 bc = hit.barycentricCoordinate;
                    Vector2 uv = uv0 * bc.x + uv1 * bc.y + uv2t * bc.z;

                    int tx = Mathf.Clamp((int)(uv.x * TEX_RES), 0, TEX_RES - 1);
                    int ty = Mathf.Clamp((int)(uv.y * TEX_RES), 0, TEX_RES - 1);
                    int idx = ty * TEX_RES + tx;

                    float cosA = Mathf.Max(0, Vector3.Dot(-ray.direction, hit.normal));
                    if (cosA < ANGLE_THRESHOLD) continue;
                    float w = cosA * cosA;

                    accumR[idx] += SrgbToLinear(c.r / 255f) * w;
                    accumG[idx] += SrgbToLinear(c.g / 255f) * w;
                    accumB[idx] += SrgbToLinear(c.b / 255f) * w;
                    accumW[idx] += w;
                    totalHits++;
                }
            }
            if ((v + 1) % 32 == 0) Debug.Log($"[AutoUVBake] view {v+1}/{captures.Count}");
        }

        double t1 = EditorApplication.timeSinceStartup;
        Debug.Log($"[AutoUVBake] capture+raycast {(t1-t0):F1}s · totalHits={totalHits:N0}");

        // ---- 7) 텍스처 finalize + dilation ----
        var texPixels = new Color32[TEX_RES * TEX_RES];
        var hasTex = new bool[TEX_RES * TEX_RES];
        int filledTex = 0;
        for (int i = 0; i < TEX_RES * TEX_RES; i++)
        {
            if (accumW[i] > 0.0001f)
            {
                texPixels[i] = new Color32(
                    (byte)(Mathf.Clamp01(LinearToSrgb(accumR[i] / accumW[i])) * 255),
                    (byte)(Mathf.Clamp01(LinearToSrgb(accumG[i] / accumW[i])) * 255),
                    (byte)(Mathf.Clamp01(LinearToSrgb(accumB[i] / accumW[i])) * 255),
                    255);
                hasTex[i] = true;
                filledTex++;
            }
            else texPixels[i] = new Color32(128, 128, 128, 255);
        }
        Debug.Log($"[AutoUVBake] raw 텍셀: {filledTex:N0}/{TEX_RES*TEX_RES:N0} ({filledTex*100f/(TEX_RES*TEX_RES):F1}%)");

        for (int iter = 0; iter < 8; iter++)
        {
            int gained = 0;
            var newHas = (bool[])hasTex.Clone();
            for (int y = 0; y < TEX_RES; y++)
            for (int x = 0; x < TEX_RES; x++)
            {
                int i = y * TEX_RES + x;
                if (hasTex[i]) continue;
                Vector3 sum = Vector3.zero; int cnt = 0;
                if (x > 0 && hasTex[i-1])               { var c = texPixels[i-1]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
                if (x < TEX_RES-1 && hasTex[i+1])       { var c = texPixels[i+1]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
                if (y > 0 && hasTex[i-TEX_RES])         { var c = texPixels[i-TEX_RES]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
                if (y < TEX_RES-1 && hasTex[i+TEX_RES]) { var c = texPixels[i+TEX_RES]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
                if (cnt > 0)
                {
                    sum /= cnt;
                    texPixels[i] = new Color32((byte)sum.x, (byte)sum.y, (byte)sum.z, 255);
                    newHas[i] = true;
                    gained++;
                }
            }
            hasTex = newHas;
            filledTex += gained;
            if (gained == 0) break;
        }
        Debug.Log($"[AutoUVBake] after dilation: {filledTex:N0}/{TEX_RES*TEX_RES:N0} ({filledTex*100f/(TEX_RES*TEX_RES):F1}%)");

        // ---- 8) save ----
        var tex = new Texture2D(TEX_RES, TEX_RES, TextureFormat.RGBA32, false, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.SetPixels32(texPixels);
        tex.Apply(false);

        System.IO.Directory.CreateDirectory(OutDir);
        string texPath = $"{OutDir}/{baked.name}_BakedTex.png";
        System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(texPath), tex.EncodeToPNG());
        AssetDatabase.ImportAsset(texPath);
        var imp = AssetImporter.GetAtPath(texPath) as TextureImporter;
        if (imp != null) { imp.sRGBTexture = true; imp.mipmapEnabled = true; imp.SaveAndReimport(); }
        var loadedTex = AssetDatabase.LoadAssetAtPath<Texture2D>(texPath);

        string meshPath = $"{OutDir}/{baked.name}.asset";
        if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(baked, meshPath);

        var shader = Shader.Find("Universal Render Pipeline/Unlit");
        var mat = new Material(shader) { name = baked.name + "_TexMat" };
        mat.mainTexture = loadedTex;
        if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", loadedTex);
        string matPath = $"{OutDir}/{baked.name}_TexMat.mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null) AssetDatabase.DeleteAsset(matPath);
        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        // cleanup
        cam.targetTexture = null;
        Object.DestroyImmediate(camGO);
        rt.Release();
        Object.DestroyImmediate(readTex);
        Object.DestroyImmediate(tex);
        SetLayerRecursive(arasTr.gameObject, savedLayerAras);
        foreach (var (r, e) in savedRendererStates) if (r != null) r.enabled = e;
        foreach (var (b, e) in savedGsStates) if (b != null) b.enabled = e;

        // spawn — Y+2m, X+4m (#1, #2 옆)
        var parent = splat.transform.parent;
        var existing = parent != null ? parent.Find(SpawnName) : null;
        if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);

        var go = new GameObject(SpawnName);
        Undo.RegisterCreatedObjectUndo(go, "Spawn 3DGS auto-UV texture-baked mesh");
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = colTr.position + Vector3.up * Y_OFFSET + Vector3.right * X_OFFSET;
        go.transform.rotation = colTr.rotation;
        go.transform.localScale = colTr.lossyScale;
        go.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        go.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);

        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(splat.scene);
        Debug.Log($"[AutoUVBake] DONE · '{SpawnName}' spawn (Y+{Y_OFFSET}m, X+{X_OFFSET}m)");
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
    }

    static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
    static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
}
