using System;
using UnityEngine;

namespace Virnect.Lcc
{
    // attrs.lcp — 씬 레벨 설정 (XGrids 생성기가 함께 내보냄).
    // 여러 LCC 를 합칠 때 각 씬의 transform 을 월드 원점에 맞추는 소스.
    [Serializable]
    public sealed class LccSceneAttrs
    {
        public string version;
        public string name;
        public string guid;
        public string description;
        public string createTime;
        public string modifyTime;
        public string thumbnail;
        public bool   sceneModified;
        public LccSpawnPoint spawnPoint;
        public LccTransform  transform;
        public LccPosesRef   poses;
        public LccCollider   collider;

        public static LccSceneAttrs Parse(string json) => JsonUtility.FromJson<LccSceneAttrs>(json);
    }

    [Serializable] public sealed class LccSpawnPoint { public float[] position; public float[] rotation; }
    [Serializable] public sealed class LccTransform  { public float[] position; public float[] rotation; public float[] scale; }
    [Serializable] public sealed class LccPosesRef   { public string path; }
    [Serializable] public sealed class LccCollider   { public LccSimpleMesh simpleMesh; }
    [Serializable] public sealed class LccSimpleMesh { public string type; public string path; }
}
