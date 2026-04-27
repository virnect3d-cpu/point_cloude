using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Virnect.Lcc.Editor
{
    /// 사용자가 패키지를 받자마자 Scene5_LccRotate 를 열었을 때
    /// 메쉬콜라이더 슬롯이 비어 있으면 한 번에 베이크 + 와이어업.
    ///
    /// 동작:
    ///   · Scene5 가 열리면 한 프레임 뒤에 콜라이더 미연결 갯수 측정
    ///   · 1 개라도 비어 있으면 1 회 다이얼로그 → 베이크
    ///   · "이번 세션엔 묻지 마" 옵션 (EditorPrefs)
    [InitializeOnLoad]
    public static class LccScene5AutoHealer
    {
        const string PrefSkipKey = "Virnect.Lcc.AutoHealer.SkipUntilSessionEnds";

        static LccScene5AutoHealer()
        {
            EditorSceneManager.sceneOpened -= _OnSceneOpened;
            EditorSceneManager.sceneOpened += _OnSceneOpened;
        }

        static void _OnSceneOpened(Scene scene, OpenSceneMode mode)
        {
            // 우리가 관심 있는 씬만
            if (!scene.name.StartsWith("Scene5_LccRotate")) return;
            if (SessionState.GetBool(PrefSkipKey, false)) return;

            // 한 프레임 뒤에 처리 (씬 객체가 다 hydrate 된 후)
            EditorApplication.delayCall += () =>
            {
                int unwired = LccColliderBuilder.CountUnwiredColliders(scene);
                if (unwired <= 0) return;

                int choice = EditorUtility.DisplayDialogComplex(
                    "LCC · Scene5 메쉬 콜라이더 누락",
                    $"{scene.name} 의 Splat 오브젝트 중 {unwired} 개가 콜라이더 메쉬가 비어 있습니다.\n\n" +
                    "지금 베이크 + 자동 연결을 실행할까요?\n" +
                    "  · LCC_Drops/<sceneName>/(mesh-files/)<sceneName>.ply 를 읽어서\n" +
                    "  · LCC_Generated/<sceneName>_ProxyMesh.asset 를 만들고\n" +
                    "  · __LccCollider 자식의 MeshCollider.sharedMesh 에 연결합니다.",
                    "지금 베이크",
                    "이번 세션 동안 묻지 않기",
                    "나중에");

                switch (choice)
                {
                    case 0:
                        var rep = LccColliderBuilder.BakeScene(scene, forceRebuild: false);
                        EditorSceneManager.MarkSceneDirty(scene);
                        EditorSceneManager.SaveScene(scene);
                        EditorUtility.DisplayDialog("LCC",
                            $"완료 — baked {rep.baked}, reused {rep.reused}, wired {rep.wired}, " +
                            $"alreadyOk {rep.alreadyOk}, missingPly {rep.missingPly}.\n\n" +
                            "Console 에 상세 로그가 남았습니다.",
                            "OK");
                        break;
                    case 1:
                        SessionState.SetBool(PrefSkipKey, true);
                        break;
                    case 2:
                    default:
                        break;
                }
            };
        }
    }
}
