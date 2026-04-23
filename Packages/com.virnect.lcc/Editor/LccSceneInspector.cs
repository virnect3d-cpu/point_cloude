using UnityEditor;
using UnityEngine;

namespace Virnect.Lcc.Editor
{
    /// .lcc 파일 또는 LccScene 에셋 클릭 시 Inspector 에 그려지는 커스텀 뷰.
    /// Unity 기본 ScriptedImporterEditor 의 "Sequence contains no elements"
    /// LINQ 버그를 회피하기 위해 base.OnEnable 정상 호출.
    [CustomEditor(typeof(LccScene))]
    public sealed class LccSceneInspector : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            var s = (LccScene)target;
            if (s == null || s.manifest == null)
            {
                EditorGUILayout.HelpBox("LccScene 데이터가 비어 있습니다.", MessageType.Warning);
                return;
            }
            var m = s.manifest;

            // 헤더
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("🎯 LCC Scene", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("name", m.name ?? "-");
                EditorGUILayout.LabelField("source / dataType", $"{m.source} / {m.dataType}");
                EditorGUILayout.LabelField("version", m.version ?? "-");
                EditorGUILayout.LabelField("totalSplats", m.totalSplats.ToString("N0"));
                EditorGUILayout.LabelField("totalLevel", m.totalLevel.ToString());
                EditorGUILayout.LabelField("encoding", m.encoding ?? "-");
                EditorGUILayout.LabelField("epsg", m.epsg.ToString());
            }

            if (m.splats != null && m.splats.Length > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("LOD Breakdown", EditorStyles.boldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    long cum = 0;
                    for (int i = 0; i < m.splats.Length; i++)
                    {
                        long start = cum * 32;
                        cum += m.splats[i];
                        long end = cum * 32;
                        EditorGUILayout.LabelField(
                            $"LOD {i}",
                            $"{m.splats[i]:N0} splats  ·  bytes [{start:N0} .. {end:N0})");
                    }
                }
            }

            if (m.boundingBox != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("Bounding Box", EditorStyles.boldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField("min",
                        $"[{m.boundingBox.min[0]:F3}, {m.boundingBox.min[1]:F3}, {m.boundingBox.min[2]:F3}]");
                    EditorGUILayout.LabelField("max",
                        $"[{m.boundingBox.max[0]:F3}, {m.boundingBox.max[1]:F3}, {m.boundingBox.max[2]:F3}]");
                    var size = new Vector3(
                        m.boundingBox.max[0] - m.boundingBox.min[0],
                        m.boundingBox.max[1] - m.boundingBox.min[1],
                        m.boundingBox.max[2] - m.boundingBox.min[2]);
                    EditorGUILayout.LabelField("size", $"{size.x:F2} × {size.y:F2} × {size.z:F2} m");
                }
            }

            if (s.attrs != null && s.attrs.transform != null)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("attrs.lcp transform", EditorStyles.boldLabel);
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (s.attrs.transform.position != null && s.attrs.transform.position.Length >= 3)
                        EditorGUILayout.LabelField("position",
                            $"[{s.attrs.transform.position[0]}, {s.attrs.transform.position[1]}, {s.attrs.transform.position[2]}]");
                    if (s.attrs.transform.rotation != null && s.attrs.transform.rotation.Length >= 4)
                        EditorGUILayout.LabelField("rotation (quat)",
                            $"[{s.attrs.transform.rotation[0]}, {s.attrs.transform.rotation[1]}, {s.attrs.transform.rotation[2]}, {s.attrs.transform.rotation[3]}]");
                    if (s.attrs.transform.scale != null && s.attrs.transform.scale.Length >= 3)
                        EditorGUILayout.LabelField("scale",
                            $"[{s.attrs.transform.scale[0]}, {s.attrs.transform.scale[1]}, {s.attrs.transform.scale[2]}]");
                }
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Files", EditorStyles.boldLabel);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("rootPath", s.rootPath ?? "-", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("data.bin",
                    System.IO.File.Exists(s.DataBinPath) ? "✓ exists (" + _FormatBytes(new System.IO.FileInfo(s.DataBinPath).Length) + ")" : "✗ missing");
            }

            // 빠른 액션
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("LCC Importer 창 열기", GUILayout.Height(26)))
                    LccImporterWindow.Open();
                if (GUILayout.Button("씬에 바로 추가 (Splat LOD 4)", GUILayout.Height(26)))
                    _QuickInstantiate(s);
            }
        }

        void _QuickInstantiate(LccScene s)
        {
            var root = GameObject.Find("__LccRoot");
            if (root == null) root = new GameObject("__LccRoot");
            var go = new GameObject("__Lcc_" + s.name);
            go.transform.SetParent(root.transform, false);
            if (s.attrs?.transform?.position != null && s.attrs.transform.position.Length >= 3)
                go.transform.position = new Vector3(
                    s.attrs.transform.position[0],
                    s.attrs.transform.position[1],
                    s.attrs.transform.position[2]);
            var r = go.AddComponent<LccSplatRenderer>();
            r.scene = s;
            r.lodLevel = 4;
            r.scaleMultiplier = 1.5f;
            r.opacityBoost = 0.3f;
            r.enabled = false; r.enabled = true;
            Selection.activeGameObject = go;
            Undo.RegisterCreatedObjectUndo(go, "Instantiate LCC");
        }

        static string _FormatBytes(long b)
        {
            if (b < 1024) return b + " B";
            if (b < 1024 * 1024) return (b / 1024f).ToString("F1") + " KB";
            if (b < 1024L * 1024 * 1024) return (b / 1024f / 1024f).ToString("F1") + " MB";
            return (b / 1024f / 1024f / 1024f).ToString("F2") + " GB";
        }
    }
}
