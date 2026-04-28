using System.Collections.Generic;
using UnityEngine;
using UnityEditor;

// Multi-view rasterize & bake — Aras-P 3DGS의 SH-평가된 진짜 색을 메쉬 vertex로 역투영.
// 64개 viewpoint를 mesh bounds 둘레 Fibonacci sphere로 분포 → 각 뷰에서 GS만 렌더링 →
// pixel별 raycast → MeshCollider hit → barycentric vertex 3개에 view-angle weighted 누적.
public static class MultiViewBake3DGS
{
    const string SplatName = "Splat_ShinWon_1st_Cutter";
    const int RT_RES = 768;          // capture 해상도 (384→768 = 4배 샘플)
    const int PX_STRIDE = 1;         // pixel sub-sampling (1 = 모든 픽셀, 2 = 1/4)
    const int NUM_VIEWS = 64;        // 외부 sphere 뷰 개수
    const int INTERIOR_GRID = 4;     // 4x4x4 = 64 인테리어 위치 × 6 큐브맵 = 384 추가 캡처
    const float ANGLE_THRESHOLD = 0.05f;
    const int DIFFUSION_ITERATIONS = 12;  // 미채색 vertex 그래프 디퓨전 — 100%까지 강제
    const float Y_OFFSET = 2f;       // 기존 씬에서 Y+2m 위에 spawn
    const string OutDir = "Assets/LCC_Generated";
    const string SpawnName = "Test_3DGSBaked_Mesh_1st_Cutter";
    const int BakeLayer = 31; // 임시 레이어 (Unity 예약 안 된 마지막 user layer)

    [MenuItem("Tools/Lcc Drop Forge/Hypothesis · 3DGS Multi-view bake to mesh (1st_Cutter)")]
    public static void Bake()
    {
        var splat = GameObject.Find(SplatName);
        if (splat == null) { Debug.LogError($"[3DGSBake] '{SplatName}' 없음"); return; }
        var colTr = splat.transform.Find("__LccCollider");
        var mc = colTr != null ? colTr.GetComponent<MeshCollider>() : null;
        if (mc == null || mc.sharedMesh == null) { Debug.LogError("[3DGSBake] __LccCollider mesh 없음"); return; }
        var arasTr = splat.transform.Find("_ArasP");
        if (arasTr == null) { Debug.LogError("[3DGSBake] _ArasP 자식 없음"); return; }

        var srcMesh = mc.sharedMesh;
        int nVerts = srcMesh.vertexCount;
        Debug.Log($"[3DGSBake] start · mesh '{srcMesh.name}' {nVerts:N0} verts · {NUM_VIEWS} views @ {RT_RES}x{RT_RES}");

        // ---- 1) baked mesh 복제 ----
        var baked = new Mesh
        {
            name = SplatName + "_3DGSBaked",
            indexFormat = srcMesh.indexFormat
        };
        baked.SetVertices(srcMesh.vertices);
        baked.SetTriangles(srcMesh.triangles, 0, true);
        if (srcMesh.normals != null && srcMesh.normals.Length == nVerts) baked.SetNormals(srcMesh.normals);

        // ---- 2) world bounds (collider 자식의 world transform 기준) ----
        var bnds = mc.sharedMesh.bounds;
        var localToWorld = colTr.localToWorldMatrix;
        Vector3 wMin = Vector3.positiveInfinity, wMax = Vector3.negativeInfinity;
        // 8 corner sample
        for (int i = 0; i < 8; i++)
        {
            Vector3 p = bnds.center + Vector3.Scale(bnds.extents, new Vector3(((i&1)==0?-1:1), ((i&2)==0?-1:1), ((i&4)==0?-1:1)));
            Vector3 w = localToWorld.MultiplyPoint3x4(p);
            wMin = Vector3.Min(wMin, w);
            wMax = Vector3.Max(wMax, w);
        }
        Vector3 center = (wMin + wMax) * 0.5f;
        float radius = (wMax - wMin).magnitude * 0.6f;
        Debug.Log($"[3DGSBake] world bounds center={center} radius={radius:F2}m");

        // ---- 3) 다른 GS/메쉬 잠시 가리기 + _ArasP 만 BakeLayer 로 ----
        var savedRendererStates = new List<(Renderer r, bool e)>();
        foreach (var r in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
        {
            if (r == null) continue;
            // _ArasP 의 GaussianSplatRenderer 는 Renderer 가 아닐 수도 있어서 layer 로 격리
            if (r.transform.IsChildOf(arasTr) || r.transform == arasTr) continue;
            savedRendererStates.Add((r, r.enabled));
            r.enabled = false;
        }
        // GaussianSplatRenderer 인스턴스 모음 — _ArasP 외 모두 비활성화
        var savedGsStates = new List<(MonoBehaviour b, bool e)>();
        var arasGsType = arasTr.GetComponent<MonoBehaviour>()?.GetType();
        var allMonos = Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
        foreach (var mb in allMonos)
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
        var camGO = new GameObject("__3DGSBakeCam_Tmp");
        var cam = camGO.AddComponent<Camera>();
        cam.cullingMask = 1 << BakeLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0, 0, 0, 0); // 투명 BG → alpha 로 GS hit 판정
        cam.fieldOfView = 50;
        cam.nearClipPlane = Mathf.Max(0.01f, radius * 0.1f);
        cam.farClipPlane = radius * 5f;
        cam.allowHDR = true;

        var rt = new RenderTexture(RT_RES, RT_RES, 24, RenderTextureFormat.ARGB32);
        rt.Create();
        cam.targetTexture = rt;

        var readTex = new Texture2D(RT_RES, RT_RES, TextureFormat.RGBA32, false);

        // ---- 5) 누적 버퍼 ----
        var accumColor = new Vector3[nVerts];
        var accumWeight = new float[nVerts];

        var triangles = srcMesh.triangles;

        // ---- 5b) 캡처 위치/방향 리스트 ----
        var captures = new List<(Vector3 pos, Quaternion rot, float fov)>();
        // (a) 외부 sphere 뷰 — Fibonacci, 향하는 방향 = 중심
        for (int v = 0; v < NUM_VIEWS; v++)
        {
            float phi = Mathf.Acos(1f - 2f * (v + 0.5f) / NUM_VIEWS);
            float ga = Mathf.PI * (1f + Mathf.Sqrt(5f));
            float theta = ga * v;
            Vector3 dir = new Vector3(
                Mathf.Sin(phi) * Mathf.Cos(theta),
                Mathf.Sin(phi) * Mathf.Sin(theta),
                Mathf.Cos(phi));
            Vector3 pos = center + dir * radius;
            captures.Add((pos, Quaternion.LookRotation(center - pos, Vector3.up), 50f));
        }
        // (b) 인테리어 큐브맵 — bounds 내 INTERIOR_GRID^3 위치에서 6방향
        Vector3 boundsSize = wMax - wMin;
        Quaternion[] cubeFaces = {
            Quaternion.LookRotation(Vector3.right,    Vector3.up),
            Quaternion.LookRotation(Vector3.left,     Vector3.up),
            Quaternion.LookRotation(Vector3.forward,  Vector3.up),
            Quaternion.LookRotation(Vector3.back,     Vector3.up),
            Quaternion.LookRotation(Vector3.up,       Vector3.forward),
            Quaternion.LookRotation(Vector3.down,     Vector3.forward),
        };
        int g = INTERIOR_GRID;
        for (int gx = 0; gx < g; gx++)
        for (int gy = 0; gy < g; gy++)
        for (int gz = 0; gz < g; gz++)
        {
            // grid 점 — bounds 내부 80% 영역에 위치
            Vector3 t = new Vector3((gx + 0.5f) / g, (gy + 0.5f) / g, (gz + 0.5f) / g);
            Vector3 pos = Vector3.Lerp(wMin + boundsSize * 0.1f, wMax - boundsSize * 0.1f, 0f) + Vector3.Scale(boundsSize * 0.8f, t);
            foreach (var rot in cubeFaces)
                captures.Add((pos, rot, 90f));
        }
        Debug.Log($"[3DGSBake] 총 캡처 수 = {captures.Count} ({NUM_VIEWS} ext + {g*g*g*6} int cubemap)");

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

            // 픽셀별 raycast (이 부분이 메인 코스트)
            for (int py = 0; py < RT_RES; py += PX_STRIDE)
            {
                for (int px = 0; px < RT_RES; px += PX_STRIDE)
                {
                    var c = pixels[py * RT_RES + px];
                    if (c.a < 16) continue; // BG 또는 GS 못 본 픽셀 skip
                    // 카메라 ray
                    var ray = cam.ScreenPointToRay(new Vector3(px + 0.5f, py + 0.5f, 0));
                    if (!Physics.Raycast(ray, out RaycastHit hit, cam.farClipPlane, ~0, QueryTriggerInteraction.Ignore)) continue;
                    if (hit.collider != mc) continue;

                    int ti = hit.triangleIndex * 3;
                    if (ti + 2 >= triangles.Length) continue;
                    int i0 = triangles[ti], i1 = triangles[ti + 1], i2 = triangles[ti + 2];

                    Vector3 bc = hit.barycentricCoordinate;
                    float cosA = Mathf.Max(0, Vector3.Dot(-ray.direction, hit.normal));
                    if (cosA < ANGLE_THRESHOLD) continue;
                    float angleW = cosA * cosA; // cos² — 그레이징 각도 강하게 페널티

                    // sRGB → Linear (감마 정확 평균을 위해)
                    Vector3 col = new Vector3(SrgbToLinear(c.r / 255f), SrgbToLinear(c.g / 255f), SrgbToLinear(c.b / 255f));

                    accumColor[i0]  += col * bc.x * angleW;
                    accumWeight[i0] += bc.x * angleW;
                    accumColor[i1]  += col * bc.y * angleW;
                    accumWeight[i1] += bc.y * angleW;
                    accumColor[i2]  += col * bc.z * angleW;
                    accumWeight[i2] += bc.z * angleW;
                    totalHits++;
                }
            }
            if ((v + 1) % 16 == 0) Debug.Log($"[3DGSBake] view {v+1}/{captures.Count}");
        }

        double t1 = EditorApplication.timeSinceStartup;
        Debug.Log($"[3DGSBake] capture+raycast {(t1 - t0):F1}s · totalHits={totalHits:N0}");

        // ---- 6) cleanup capture rig ----
        cam.targetTexture = null;
        Object.DestroyImmediate(camGO);
        rt.Release();
        Object.DestroyImmediate(readTex);
        SetLayerRecursive(arasTr.gameObject, savedLayerAras);
        foreach (var (r, e) in savedRendererStates) if (r != null) r.enabled = e;
        foreach (var (b, e) in savedGsStates) if (b != null) b.enabled = e;

        // ---- 7) finalize vertex colors (raw 누적값) ----
        int colored = 0;
        var rawCols = new Vector3[nVerts];
        var hasColor = new bool[nVerts];
        for (int i = 0; i < nVerts; i++)
        {
            if (accumWeight[i] > 0.0001f)
            {
                rawCols[i] = accumColor[i] / accumWeight[i];
                hasColor[i] = true;
                colored++;
            }
        }
        Debug.Log($"[3DGSBake] raw vertex colors: {colored:N0}/{nVerts:N0} ({(colored*100f/nVerts):F1}%) — diffusion 시작");

        // ---- 7b) 그래프 디퓨전 — 미채색 vertex 를 인접 채색 vertex 평균으로 메우기 ----
        // 메쉬 edge adjacency 빌드
        var adj = new List<int>[nVerts];
        for (int i = 0; i < nVerts; i++) adj[i] = new List<int>(8);
        for (int t = 0; t < triangles.Length; t += 3)
        {
            int a = triangles[t], b = triangles[t+1], c = triangles[t+2];
            adj[a].Add(b); adj[a].Add(c);
            adj[b].Add(a); adj[b].Add(c);
            adj[c].Add(a); adj[c].Add(b);
        }
        for (int iter = 0; iter < DIFFUSION_ITERATIONS; iter++)
        {
            int filled = 0;
            var newHas = (bool[])hasColor.Clone();
            var newCols = (Vector3[])rawCols.Clone();
            for (int i = 0; i < nVerts; i++)
            {
                if (hasColor[i]) continue;
                Vector3 sum = Vector3.zero; int cnt = 0;
                foreach (var n in adj[i])
                {
                    if (hasColor[n]) { sum += rawCols[n]; cnt++; }
                }
                if (cnt > 0)
                {
                    newCols[i] = sum / cnt;
                    newHas[i] = true;
                    filled++;
                }
            }
            hasColor = newHas; rawCols = newCols;
            colored += filled;
            Debug.Log($"[3DGSBake] diffusion iter {iter+1}: +{filled:N0} verts → total {colored:N0} ({(colored*100f/nVerts):F1}%)");
            if (filled == 0) break;
        }

        // 강제 100% — 아직 미채색 vertex 가 있으면 글로벌 평균색으로 메우기
        if (colored < nVerts)
        {
            Vector3 globalAvg = Vector3.zero; int cnt = 0;
            for (int i = 0; i < nVerts; i++) if (hasColor[i]) { globalAvg += rawCols[i]; cnt++; }
            if (cnt > 0) globalAvg /= cnt;
            for (int i = 0; i < nVerts; i++) if (!hasColor[i]) { rawCols[i] = globalAvg; hasColor[i] = true; }
            colored = nVerts;
            Debug.Log($"[3DGSBake] forced fill (global avg): +{nVerts - cnt:N0} verts → 100%");
        }

        // Linear → sRGB 다시 변환 후 vertex color 박기
        var finalCols = new Color[nVerts];
        for (int i = 0; i < nVerts; i++)
        {
            var lin = rawCols[i];
            finalCols[i] = new Color(LinearToSrgb(lin.x), LinearToSrgb(lin.y), LinearToSrgb(lin.z), 1f);
        }
        baked.colors = finalCols;
        Debug.Log($"[3DGSBake] FINAL vertex colors: {colored:N0}/{nVerts:N0} ({(colored*100f/nVerts):F1}%)");

        // ---- 8) save mesh + mat ----
        System.IO.Directory.CreateDirectory(OutDir);
        string meshPath = $"{OutDir}/{baked.name}.asset";
        if (AssetDatabase.LoadAssetAtPath<Mesh>(meshPath) != null) AssetDatabase.DeleteAsset(meshPath);
        AssetDatabase.CreateAsset(baked, meshPath);
        var shader = Shader.Find("Virnect/LccVertexColorUnlit") ?? Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/VertexColor");
        var mat = new Material(shader) { name = baked.name + "_Mat" };
        string matPath = $"{OutDir}/{baked.name}_Mat.mat";
        if (AssetDatabase.LoadAssetAtPath<Material>(matPath) != null) AssetDatabase.DeleteAsset(matPath);
        AssetDatabase.CreateAsset(mat, matPath);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        // ---- 9) spawn ----
        var parent = splat.transform.parent;
        var existing = parent != null ? parent.Find(SpawnName) : null;
        if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);

        var go = new GameObject(SpawnName);
        Undo.RegisterCreatedObjectUndo(go, "Spawn 3DGS-baked mesh");
        if (parent != null) go.transform.SetParent(parent, false);
        // 콜라이더 자식의 world transform 그대로 + Y 위로 Y_OFFSET 미터
        go.transform.position = colTr.position + Vector3.up * Y_OFFSET;
        go.transform.rotation = colTr.rotation;
        go.transform.localScale = colTr.lossyScale;
        go.AddComponent<MeshFilter>().sharedMesh = baked;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;

        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(splat.scene);
        Debug.Log($"[3DGSBake] DONE · '{SpawnName}' spawn 완료. 시각 비교해보세요.");
    }

    static void SetLayerRecursive(GameObject go, int layer)
    {
        go.layer = layer;
        foreach (Transform c in go.transform) SetLayerRecursive(c.gameObject, layer);
    }

    // sRGB↔Linear — 정확한 색 평균을 위해 Linear 공간에서 누적/평균/평균 후 sRGB 복귀
    static float SrgbToLinear(float c) => c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);
    static float LinearToSrgb(float c) => c <= 0.0031308f ? c * 12.92f : 1.055f * Mathf.Pow(c, 1f / 2.4f) - 0.055f;
}
