# Changelog

모든 사용자 대상 변경 사항은 이 파일에 기록합니다. 형식은 [Keep a Changelog] 기준.

## [Unreleased] — scaffold (2026-04-23)

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
