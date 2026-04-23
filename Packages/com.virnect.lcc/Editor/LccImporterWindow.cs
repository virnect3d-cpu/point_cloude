using System.IO;
using UnityEditor;
using UnityEngine;

namespace Virnect.Lcc.Editor
{
    // 수동 임포트/합치기 UI.
    // Scaffold 단계 — 드롭 필드 + 합치기 플랜 미리보기만.
    public sealed class LccImporterWindow : EditorWindow
    {
        [MenuItem("Virnect/LCC Importer")]
        public static void Open() => GetWindow<LccImporterWindow>("LCC Importer");

        LccScene[] _scenes = System.Array.Empty<LccScene>();

        void OnGUI()
        {
            EditorGUILayout.LabelField("여러 LCC 씬 합치기 (동일 EPSG 전제)", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            int newSize = EditorGUILayout.IntField("씬 개수", _scenes.Length);
            if (newSize != _scenes.Length) System.Array.Resize(ref _scenes, Mathf.Max(0, newSize));

            for (int i = 0; i < _scenes.Length; i++)
                _scenes[i] = (LccScene)EditorGUILayout.ObjectField($"Scene #{i+1}", _scenes[i], typeof(LccScene), false);

            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(_scenes.Length == 0))
            {
                if (GUILayout.Button("합치기 플랜 미리보기"))
                {
                    var placed = LccSceneMerger.Plan(_scenes);
                    foreach (var p in placed)
                        Debug.Log($"[LCC] {p.scene?.name}  @ world={p.worldOffset}  rot={p.rotation}  scl={p.scale}");
                }
                if (GUILayout.Button("월드로 인스턴스화 (TODO)"))
                {
                    EditorUtility.DisplayDialog("LCC",
                        "합치기 인스턴스화는 LccSplatDecoder 완성 후 구현됩니다.", "OK");
                }
            }
        }
    }
}
