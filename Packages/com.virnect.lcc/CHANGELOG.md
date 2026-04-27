# Changelog

모든 사용자 대상 변경 사항은 이 파일에 기록합니다. 형식은 [Keep a Changelog] 기준.

## [Unreleased] — mesh collider auto-bake (2026-04-27)

### Added
- `LccPlyTriMeshReader` — XGrids `mesh-files/<scene>.ply` (binary_le, x/y/z + uchar/uint face list) 파서. fan triangulation 지원, 254k+ vertices 처리.
- `LccProxyMeshBaker` — PLY → `Assets/LCC_Generated/<scene>_ProxyMesh.asset` Unity Mesh 자산 생성. 65535 초과 시 IndexFormat.UInt32 자동 전환, 기존 자산은 CopySerialized 로 덮어쓰기.
- `LccColliderBuilder` — 활성 씬의 `Splat_*` 오브젝트마다 자식 `__LccCollider` 를 보장하고 베이크된 Mesh 를 MeshCollider 에 자동 연결. 메뉴 3종 (`Virnect/LCC/Bake Mesh Colliders ...`).
- `LccScene5AutoHealer` — `Scene5_LccRotate` 가 열리면 미연결 콜라이더를 감지하고 베이크 다이얼로그 자동 띄움. 패키지 다운 직후 1-클릭 셋업.
- `LccImporterWindow.Collider` 탭 — Python 서버 없이 동작하는 In-Editor 베이크 버튼 추가 (기존 v1 백엔드 경로는 유지).

## [scaffold] — (2026-04-23)

### Added
- UPM 패키지 레이아웃 (`package.json`, Runtime/Editor asmdef)
- `LccManifest`, `LccSceneAttrs` — XGrids PortalCam JSON 매니페스트 스키마
- `LccScene` ScriptableObject + `LccScriptedImporter` (`.lcc` 드래그앤드롭 자동 임포트)
- `LccDataStream`, `LccSplatDecoder`, `LccCollisionLoader` 스텁 — 바이트 레이아웃 역분석 진행 중
- `LccSceneMerger` — 동일 EPSG 다중 씬 월드 배치 플래너
- `LccImporterWindow` — 수동 합치기 UI 스켈레톤

### Pending
- `data.bin` / `index.bin` / `collision.lci` 바이트 레이아웃 역분석 완료 → 실제 디코드 구현
- `LccPointCloudRenderer` 에 URP/BiRP 호환 포인트 셰이더 연결
- 기존 PointCloudOptimizer 파이썬 백엔드(v2)와의 PLY 연동
- 3D 스캔 vs LCC 메쉬 비교 분석 (v2 파이썬 모듈)
