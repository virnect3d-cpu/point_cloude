using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEditor;
using Virnect.Lcc;

// 가설 검증용 — 1st_Cutter 의 콜라이더 메쉬를 source 로 fresh colorize 후 spawn.
// 콜라이더 mesh = 이미 Y-up 변환된 정합 mesh. splat transform 도 동일하게 적용.
// 컬러는 현재 LCC scene 에서 fresh decode → 가능한 모든 LccMeshColorizer.Options preset 시도.
public static class ColoredMeshHypothesis
{
    const string SplatName = "Splat_ShinWon_1st_Cutter";
    const string LccPath = "Assets/LCC_Drops/ShinWon_1st_Cutter/ShinWon_1st_Cutter.lcc";
    const string OutDir = "Assets/LCC_Generated";

    [MenuItem("Tools/Lcc Drop Forge/Hypothesis · Bake mesh from collider + recolor (1st_Cutter)")]
    public static void BakeAndRecolor()
    {
        // 1) 씬에서 splat + collider mesh 가져오기
        var splat = GameObject.Find(SplatName);
        if (splat == null) { Debug.LogError($"[Hypothesis] '{SplatName}' 없음"); return; }
        var colTr = splat.transform.Find("__LccCollider");
        var mc = colTr != null ? colTr.GetComponent<MeshCollider>() : null;
        if (mc == null || mc.sharedMesh == null) { Debug.LogError("[Hypothesis] __LccCollider mesh 없음"); return; }

        var src = mc.sharedMesh;
        Debug.Log($"[Hypothesis] source = {src.name} ({src.vertexCount:N0} verts) — 콜라이더에서 그대로 가져옴");

        // 2) src mesh 복제 (수정 안전)
        var baked = new Mesh
        {
            name = SplatName + "_FromCollider_Colored",
            indexFormat = src.indexFormat
        };
        baked.SetVertices(src.vertices);
        baked.SetTriangles(src.triangles, 0, calculateBounds: true);
        if (src.normals != null && src.normals.Length == src.vertexCount) baked.SetNormals(src.normals);
        if (src.uv != null && src.uv.Length == src.vertexCount) baked.SetUVs(0, src.uv);

        // 3) LCC scene 로드 + splats fresh decode
        var scene = AssetDatabase.LoadAssetAtPath<LccScene>(LccPath);
        if (scene == null) { Debug.LogError($"[Hypothesis] LccScene 못 찾음: {LccPath}"); return; }
        var splats = LccSplatDecoder.DecodeLod(scene, 0);
        Debug.Log($"[Hypothesis] LCC splats decoded · {scene.name}");

        // 4) LccMeshColorizer.Options 의 모든 정적 멤버 검출 (field + property + method) — PhotoReal 외 옵션 찾기
        var optsType = typeof(LccMeshColorizer).GetNestedType("Options");
        if (optsType == null) optsType = AppDomainSearchType("Virnect.Lcc.LccMeshColorizer+Options")
                                          ?? AppDomainSearchType("Virnect.Lcc.LccMeshColorizerOptions");
        if (optsType == null) { Debug.LogError("[Hypothesis] LccMeshColorizer.Options 타입 못 찾음"); return; }
        Debug.Log($"[Hypothesis] Options 타입 = {optsType.FullName}");

        var presets = new List<(string name, object value)>();
        const BindingFlags AllStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
        foreach (var f in optsType.GetFields(AllStatic))
            if (f.FieldType == optsType) presets.Add((f.Name, f.GetValue(null)));
        foreach (var p in optsType.GetProperties(AllStatic))
            if (p.PropertyType == optsType && p.CanRead) presets.Add((p.Name, p.GetValue(null)));
        foreach (var m in optsType.GetMethods(AllStatic))
            if (m.ReturnType == optsType && m.GetParameters().Length == 0) presets.Add((m.Name + "()", m.Invoke(null, null)));

        if (presets.Count == 0)
        {
            // fallback — instance 직접 생성
            var defaultOpts = System.Activator.CreateInstance(optsType);
            presets.Add(("(default-instance)", defaultOpts));
        }
        Debug.Log($"[Hypothesis] presets 발견 ({presets.Count}): {string.Join(", ", presets.ConvertAll(x => x.name))}");

        // 5) PhotoReal 외의 다른 preset 우선 시도 (PhotoReal 결과가 구려서 — 다른 시도)
        (string name, object value) chosen = presets[0];
        foreach (var p in presets)
        {
            if (!p.name.Contains("PhotoReal")) { chosen = p; break; }
        }
        Debug.Log($"[Hypothesis] preset 선택: {chosen.name}");

        // 6) Colorize 호출 — 시그니처 (Mesh, splats, Options) 정확히 매칭
        MethodInfo colorizeMethod = null;
        foreach (var m in typeof(LccMeshColorizer).GetMethods(BindingFlags.Public | BindingFlags.Static))
        {
            if (m.Name != "Colorize") continue;
            var ps = m.GetParameters();
            if (ps.Length == 3 && ps[0].ParameterType == typeof(Mesh) && ps[2].ParameterType == optsType)
            { colorizeMethod = m; break; }
        }
        if (colorizeMethod == null) { Debug.LogError("[Hypothesis] LccMeshColorizer.Colorize(Mesh, splats, Options) overload 못 찾음"); return; }
        Debug.Log($"[Hypothesis] Colorize signature = {colorizeMethod}");

        double t0 = EditorApplication.timeSinceStartup;
        colorizeMethod.Invoke(null, new[] { (object)baked, splats, chosen.value });
        double t1 = EditorApplication.timeSinceStartup;
        Debug.Log($"[Hypothesis] Colorize ({chosen.name}) 완료 · {(t1-t0)*1000:F0}ms");

        // 7) 자산 저장
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

        // 8) Spawn — 콜라이더 자식의 transform 그대로 (이미 정합 맞춰진 transform)
        const string SpawnName = "Test_ColoredMesh_FromCollider_1st_Cutter";
        var parent = splat.transform.parent;
        var existing = parent != null ? parent.Find(SpawnName) : null;
        if (existing != null) Undo.DestroyObjectImmediate(existing.gameObject);

        var go = new GameObject(SpawnName);
        Undo.RegisterCreatedObjectUndo(go, "Spawn baked ColoredMesh");
        if (parent != null) go.transform.SetParent(parent, false);
        // splat 의 transform 복사 — collider mesh 가 splat 의 자식이었으므로 splat transform 적용 시 collider 자식이 보였던 자리 그대로
        go.transform.localPosition = splat.transform.localPosition;
        go.transform.localRotation = splat.transform.localRotation;
        go.transform.localScale    = splat.transform.localScale;
        // 콜라이더 자식의 localPos/Rot 도 적용 (collider가 부모와 다른 local 가졌으면 보정)
        go.transform.localPosition += splat.transform.localRotation * Vector3.Scale(splat.transform.localScale, colTr.localPosition);
        // localRotation 까지는 콜라이더와 splat 차이 누적 적용 (단순화 — 필요시 추후 미세조정)

        go.AddComponent<MeshFilter>().sharedMesh = baked;
        go.AddComponent<MeshRenderer>().sharedMaterial = mat;

        Selection.activeGameObject = go;
        EditorGUIUtility.PingObject(go);
        UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(splat.scene);
        Debug.Log($"[Hypothesis] {SpawnName} spawn 완료 · mesh '{baked.name}' · mat '{mat.name}'");
    }

    static System.Type AppDomainSearchType(string fullName)
    {
        foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
        {
            var t = asm.GetType(fullName);
            if (t != null) return t;
        }
        return null;
    }
}
