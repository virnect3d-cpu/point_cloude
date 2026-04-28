using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// 알고리즘 #2 — SuGaR 스타일 텍스처 베이크.
// vertex color 한계(145k 샘플) 돌파 — 메쉬에 자동 UV 언랩 후 2048² 텍스처에 multi-view 투영.
// 각 ray hit 의 barycentric 으로 triangle UV 계산 → 텍스처 픽셀에 색 누적 (linear-space avg).
public static class TextureBake3DGS
{
    const string SplatName = "Splat_ShinWon_1st_Cutter";
    const int RT_RES = 768;          // capture RT 해상도
    const int TEX_RES = 2048;        // 결과 텍스처 해상도 (2048² = 4.2M 텍셀, vertex 145k 의 ~29배)
    const int PX_STRIDE = 1;
    const int NUM_VIEWS = 64;
    const int INTERIOR_GRID = 4;
    const float ANGLE_THRESHOLD = 0.05f;
    const float Y_OFFSET = 2f;
    const float X_OFFSET = 2f;       // #1 옆에 놓이게
    const int BakeLayer = 31;
    const string OutDir = "Assets/LCC_Generated";
    const string SpawnName = "Test_3DGSTextureBaked_Mesh_1st_Cutter";

    [MenuItem("Tools/Lcc Drop Forge/Hypothesis · 3DGS Texture-bake to mesh (1st_Cutter)")]
    public static void Bake()
    {
        var splat = GameObject.Find(SplatName);
        if (splat == null) { Debug.LogError($"[TexBake] '{SplatName}' 없음"); return; }
        var colTr = splat.transform.Find("__LccCollider");
        var mc = colTr != null ? colTr.GetComponent<MeshCollider>() : null;
        if (mc == null || mc.sharedMesh == null) { Debug.LogError("[TexBake] __LccCollider mesh 없음"); return; }
        var arasTr = splat.transform.Find("_ArasP");
        if (arasTr == null) { Debug.LogError("[TexBake] _ArasP 없음"); return; }

        var srcMesh = mc.sharedMesh;
        int nVerts = srcMesh.vertexCount;
        Debug.Log($"[TexBake] mesh '{srcMesh.name}' {nVerts:N0} verts · target tex {TEX_RES}x{TEX_RES}");

        // ---- 1) baked mesh + UV 자동 언랩 ----
        var baked = new Mesh
        {
            name = SplatName + "_3DGSTexBaked",
            indexFormat = srcMesh.indexFormat
        };
        var verts = srcMesh.vertices;
        var tris = srcMesh.triangles;
        baked.SetVertices(verts);
        baked.SetTriangles(tris, 0, true);
        if (srcMesh.normals != null && srcMesh.normals.Length == nVerts) baked.SetNormals(srcMesh.normals);

        // 빠른 UV — triangle 마다 dominant axis (X/Y/Z) 보고 나머지 2축으로 planar projection
        // (Unity Unwrapping.GeneratePerTriangleUV 는 145k vert 에서 사실상 hang — 자체 구현으로 대체)
        Debug.Log("[TexBake] triangle planar UV 생성...");
        double tu0 = EditorApplication.timeSinceStartup;
        baked = MakeTriPlanarUVMesh(verts, srcMesh.normals, tris, baked.name, srcMesh.indexFormat);
        nVerts = baked.vertexCount;
        double tu1 = EditorApplication.timeSinceStartup;
        Debug.Log($"[TexBake] tri-planar UV 완료 ({(tu1-tu0):F1}s) · {nVerts:N0} verts (corner-split)");

        // ---- 2) bounds (collider world transform 기준) ----
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

        // ---- 3) 다른 렌더러 격리 + _ArasP 만 BakeLayer 로 ----
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
        var camGO = new GameObject("__TexBakeCam_Tmp");
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
        Debug.Log($"[TexBake] 캡처 {captures.Count} ({NUM_VIEWS} ext + {g*g*g*6} int)");

        // ---- 6) 텍스처 누적 버퍼 (linear-space) ----
        var accumR = new float[TEX_RES * TEX_RES];
        var accumG = new float[TEX_RES * TEX_RES];
        var accumB = new float[TEX_RES * TEX_RES];
        var accumW = new float[TEX_RES * TEX_RES];

        // ---- 7) 빠른 lookup 위해 mesh data 로컬 보관 ----
        var meshTris = baked.triangles;
        var meshUVs = baked.uv;

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

                    int triIdx = hit.triangleIndex;
                    int tBase = triIdx * 3;
                    if (tBase + 2 >= meshTris.Length) continue;

                    // baked mesh 의 UV 는 corner-split 이라 vertex idx = triIdx*3+{0,1,2} 가 아니고 meshTris 통해 lookup
                    int v0 = meshTris[tBase], v1 = meshTris[tBase+1], v2 = meshTris[tBase+2];
                    Vector2 uv0 = meshUVs[v0], uv1 = meshUVs[v1], uv2 = meshUVs[v2];

                    Vector3 bc = hit.barycentricCoordinate;
                    Vector2 uv = uv0 * bc.x + uv1 * bc.y + uv2 * bc.z;

                    // UV → texel
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

            if ((v + 1) % 32 == 0) Debug.Log($"[TexBake] view {v+1}/{captures.Count}");
        }

        double t1 = EditorApplication.timeSinceStartup;
        Debug.Log($"[TexBake] capture+raycast {(t1-t0):F1}s · totalHits={totalHits:N0}");

        // ---- 8) 누적 → 텍스처 (linear → sRGB), 빈 텍셀은 nearest 채워 dilation ----
        var texPixels = new Color32[TEX_RES * TEX_RES];
        var hasTex = new bool[TEX_RES * TEX_RES];
        int filledTex = 0;
        for (int i = 0; i < TEX_RES * TEX_RES; i++)
        {
            if (accumW[i] > 0.0001f)
            {
                float r = accumR[i] / accumW[i];
                float gC = accumG[i] / accumW[i];
                float b = accumB[i] / accumW[i];
                texPixels[i] = new Color32((byte)(Mathf.Clamp01(LinearToSrgb(r)) * 255), (byte)(Mathf.Clamp01(LinearToSrgb(gC)) * 255), (byte)(Mathf.Clamp01(LinearToSrgb(b)) * 255), 255);
                hasTex[i] = true;
                filledTex++;
            }
            else
            {
                texPixels[i] = new Color32(128, 128, 128, 255);
            }
        }
        Debug.Log($"[TexBake] raw 텍셀: {filledTex:N0}/{TEX_RES*TEX_RES:N0} ({filledTex*100f/(TEX_RES*TEX_RES):F1}%)");

        // dilation (nearest fill) — 8 iterations 4-neighbor
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
                if (x > 0 && hasTex[i-1])         { var c = texPixels[i-1]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
                if (x < TEX_RES-1 && hasTex[i+1]) { var c = texPixels[i+1]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
                if (y > 0 && hasTex[i-TEX_RES])   { var c = texPixels[i-TEX_RES]; sum += new Vector3(c.r, c.g, c.b); cnt++; }
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
        Debug.Log($"[TexBake] after dilation: {filledTex:N0}/{TEX_RES*TEX_RES:N0} ({filledTex*100f/(TEX_RES*TEX_RES):F1}%)");

        // ---- 9) 텍스처 자산 저장 (PNG 로 export 후 import) ----
        var tex = new Texture2D(TEX_RES, TEX_RES, TextureFormat.RGBA32, false, false);
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.filterMode = FilterMode.Bilinear;
        tex.SetPixels32(texPixels);
        tex.Apply(false);

        System.IO.Directory.CreateDirectory(OutDir);
        string texPath = $"{OutDir}/{baked.name}_BakedTex.png";
        var pngBytes = tex.EncodeToPNG();
        System.IO.File.WriteAllBytes(System.IO.Path.GetFullPath(texPath), pngBytes);
        AssetDatabase.ImportAsset(texPath);
        var importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
        if (importer != null) { importer.sRGBTexture = true; importer.mipmapEnabled = true; importer.SaveAndReimport(); }
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

        // ---- 10) cleanup capture rig ----
        cam.targetTexture = null;
        Object.DestroyImmediate(camGO);
        rt.Release();
        Object.DestroyImmediate(readTex);
        Object.DestroyImmediate(tex);
        SetLayerRecursive(arasTr.gameObject, savedLayerAras);
        foreach (var (r, e) in savedRendererStates) if (r != null) r.enabled = e;
        foreach (var (b, e) in savedGsStates) if (b != null) b.enabled = e;

        // ---- 11) spawn — Y+2m, X+2m (#1 옆) ----
        var parent = splat.transform.parent;
        var existing = parent != null ? parent.Find(SpawnName) : null;
        if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);

        var go = new GameObject(SpawnName);
        Undo.RegisterCreatedObjectUndo(go, "Spawn 3DGS texture-baked mesh");
        if (parent != null) go.transform.SetParent(parent, false);
        go.transform.position = colTr.position + Vector3.up * Y_OFFSET + Vector3.right * X_OFFSET;
        go.transform.rotation = colTr.rotation;
        go.transform.localScale = colTr.lossyScale;
        go.AddComponent<MeshFilter>().sharedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
        go.AddComponent<MeshRenderer>().sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(matPath);

        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(splat.scene);
        Debug.Log($"[TexBake] DONE · '{SpawnName}' spawn (Y+{Y_OFFSET}m, X+{X_OFFSET}m)");
    }

    // triangle 별 planar projection UV — corner-split mesh 생성.
    // 알고리즘: triangle normal 의 dominant axis 검사 → 나머지 2축으로 평면 투영 + atlas 그리드에 packing.
    // 145k verts → tri 약 48k → atlas grid sqrt(48k) ≈ 220 → 220×220 cell 그리드.
    static Mesh MakeTriPlanarUVMesh(Vector3[] verts, Vector3[] normals, int[] tris, string name, UnityEngine.Rendering.IndexFormat fmt)
    {
        int triCount = tris.Length / 3;
        int gridN = Mathf.CeilToInt(Mathf.Sqrt(triCount));
        float cell = 1f / gridN;
        // mesh bounds (전체)
        Vector3 mn = Vector3.positiveInfinity, mx = Vector3.negativeInfinity;
        for (int i = 0; i < verts.Length; i++) { mn = Vector3.Min(mn, verts[i]); mx = Vector3.Max(mx, verts[i]); }
        Vector3 ext = mx - mn; float maxExt = Mathf.Max(ext.x, ext.y, ext.z);

        var newVerts   = new Vector3[tris.Length];
        var newNormals = (normals != null && normals.Length == verts.Length) ? new Vector3[tris.Length] : null;
        var newUVs     = new Vector2[tris.Length];
        var newTris    = new int[tris.Length];

        for (int t = 0; t < triCount; t++)
        {
            int i0 = tris[t*3], i1 = tris[t*3+1], i2 = tris[t*3+2];
            Vector3 a = verts[i0], b = verts[i1], c = verts[i2];

            // triangle normal → dominant axis
            Vector3 nrm = Vector3.Cross(b - a, c - a).normalized;
            int axis = 0;
            float ax = Mathf.Abs(nrm.x), ay = Mathf.Abs(nrm.y), az = Mathf.Abs(nrm.z);
            if (ay >= ax && ay >= az) axis = 1;
            else if (az >= ax && az >= ay) axis = 2;

            // 2축 좌표 추출
            Vector2 pa, pb, pc;
            switch (axis)
            {
                case 0: pa = new Vector2(a.y, a.z); pb = new Vector2(b.y, b.z); pc = new Vector2(c.y, c.z); break;
                case 1: pa = new Vector2(a.x, a.z); pb = new Vector2(b.x, b.z); pc = new Vector2(c.x, c.z); break;
                default: pa = new Vector2(a.x, a.y); pb = new Vector2(b.x, b.y); pc = new Vector2(c.x, c.y); break;
            }

            // local triangle bbox → 0..1 정규화
            Vector2 lmn = Vector2.Min(pa, Vector2.Min(pb, pc));
            Vector2 lmx = Vector2.Max(pa, Vector2.Max(pb, pc));
            Vector2 lExt = lmx - lmn;
            // 영 division 방지 + 1px 마진
            if (lExt.x < 1e-6f) lExt.x = 1e-6f;
            if (lExt.y < 1e-6f) lExt.y = 1e-6f;
            Vector2 ua = (pa - lmn); ua.x /= lExt.x; ua.y /= lExt.y;
            Vector2 ub = (pb - lmn); ub.x /= lExt.x; ub.y /= lExt.y;
            Vector2 uc = (pc - lmn); uc.x /= lExt.x; uc.y /= lExt.y;

            // atlas cell 위치 — t 인덱스 → (cx, cy)
            int cx = t % gridN; int cy = t / gridN;
            float cellMargin = 0.04f; // cell 안쪽 92% 만 사용 (bleed 방지)
            float cellInner = 1f - 2f * cellMargin;
            Vector2 cellMin = new Vector2(cx * cell + cell * cellMargin, cy * cell + cell * cellMargin);

            ua = cellMin + ua * cell * cellInner;
            ub = cellMin + ub * cell * cellInner;
            uc = cellMin + uc * cell * cellInner;

            // 결과 mesh 채우기 (corner-split)
            int idx0 = t*3, idx1 = t*3+1, idx2 = t*3+2;
            newVerts[idx0] = a; newVerts[idx1] = b; newVerts[idx2] = c;
            if (newNormals != null) { newNormals[idx0] = normals[i0]; newNormals[idx1] = normals[i1]; newNormals[idx2] = normals[i2]; }
            newUVs[idx0] = ua; newUVs[idx1] = ub; newUVs[idx2] = uc;
            newTris[idx0] = idx0; newTris[idx1] = idx1; newTris[idx2] = idx2;
        }

        var m = new Mesh { name = name, indexFormat = fmt };
        m.SetVertices(newVerts);
        m.SetTriangles(newTris, 0, true);
        if (newNormals != null) m.SetNormals(newNormals);
        m.SetUVs(0, newUVs);
        return m;
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
    }

    static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
    static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
}
