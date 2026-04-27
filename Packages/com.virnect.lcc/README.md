# com.virnect.lcc — Virnect LCC Importer (Unity)

XGrids PortalCam `.lcc` 파일을 Unity 에서 바로 쓰기 위한 UPM 패키지.

## 설치

```
Window → Package Manager → + → Add package from git URL
```

```
https://github.com/virnect3d-cpu/point_cloude.git?path=/Packages/com.virnect.lcc#v2-main
```

## 사용 흐름

1. Unity Project 창에 `.lcc` 파일 드래그 → `LccScriptedImporter` 가 자동으로 읽어 `LccScene` 에셋 생성
2. 씬에 `LccPointCloudRenderer` 컴포넌트 추가 → LOD 단계 선택
3. 여러 스캔본을 합치려면 `Virnect → LCC Importer` 창에서 `LccScene` 여러 개 등록 → "월드로 인스턴스화"
4. (파이썬 백엔드 연동) 로드된 포인트 클라우드를 PLY 로 내보내 기존 PointCloud Optimizer 파이프라인(메쉬 변환·비교)에 투입

## 메쉬 콜라이더 (자동 베이크) ✨

XGrids 가 `.lcc` 와 함께 export 한 `mesh-files/<scene>.ply` (proxy 트라이앵글 메쉬) 를 그대로 활용해
Unity Mesh 자산을 만들고 씬의 MeshCollider 에 연결합니다. **Python 서버 불필요.**

- `Virnect → LCC → Bake Mesh Colliders (Active Scene)` — 활성 씬의 `Splat_*` 모두 자동 베이크
- `Virnect → LCC → Open Scene5 + Auto Bake` — Scene5_LccRotate 를 열고 한방에 베이크
- `Virnect → LCC Importer` 창의 **콜라이더 탭** 상단에서 1-클릭 실행

생성물: `Assets/LCC_Generated/<sceneName>_ProxyMesh.asset`
컨벤션: `Splat_<sceneName>` GameObject → 자식 `__LccCollider` (없으면 생성) → MeshCollider.sharedMesh 자동 연결

`Scene5_LccRotate` 를 처음 열면 **`LccScene5AutoHealer`** 가 미연결 콜라이더를 감지해 베이크 다이얼로그를 띄웁니다 — 패키지 받자마자 한 번 클릭으로 콜라이더가 동작.

## 현재 상태

- ✅ Mesh Collider 자동 베이크 (PLY → Unity Mesh asset → MeshCollider wiring)
- ⏳ 스플랫 디코드·렌더 (`LccSplatDecoder`, `LccPointCloudRenderer`) 구현 진행 중
- 자세한 설계는 `docs/pipeline.md` · `docs/lcc-format.md`

## 테스트 데이터

`C:/Users/jeongsomin/Desktop/LCC/LCC/lcc-result/ShinWon_1st_Cutter.lcc`
(9.97M splats · 5 LOD · 326 MB)
