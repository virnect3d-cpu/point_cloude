# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [2.7.0-dev] - 2026-04-27

### Added
- LCC Importer · 콜라이더 탭에 **🔁 그룹 일괄 재생성** 섹션. 대상 루트(예: `LccGroup`) 자식 순회 → 백엔드 `/api/mesh-collider` 호출 → 기존 `__LccCollider` 교체.
- **🧹 정리 · 캐시 비우기** 섹션 — `__LccCollider` GameObject 일괄 제거, `Assets/LCC_GeneratedColliders/*.asset` 캐시 삭제, 진행 중 작업 취소(run-id 기반).
- Backend `/api/mesh-collider` 에 `flat=true` 옵션 — Unity `JsonUtility` 친화 평탄 배열 응답 (`verts_flat`, `tris_flat`).
- Backend `/api/mesh-collider/{sid}/obj` 엔드포인트 — 콜라이더 메쉬를 OBJ 바이너리로 다운로드 (Blender/MeshLab 호환).
- Web UI `page2-collider` 에 노이즈 제거 토글 노출 — `density_trim`, `keep_fragments`, `max_edge_ratio` 슬라이더.
- 백엔드 단위 테스트 — `tests/test_mesh_collider_flat.py` (flat=true 응답 스키마 검증, OBJ 엔드포인트).
- LCC Importer · 콜라이더 탭에 **"Splat 본체 → 콜라이더"** 옵션 — XGrids proxy mesh 대신 `data.bin` 의 실제 splat 점 데이터를 PLY 로 추출해 백엔드 입력으로 사용. 작은 proxy 로 인한 부분 콜라이더 문제 해결.
- `LccCollisionLoader` 에 `collision.lci` AABB/cellSize 헤더 파서 — XGrids native 충돌 컨테이너 부분 역분석 진행분 반영.
- Compare 탭에 Hausdorff 결과 시각화 — 거리 히스토그램, 평균/95%/최대 표시.

### Changed
- `Packages/com.virnect.lcc/package.json` 버전 → `2.7.0-dev`.

## [1.0.0] - 2026-04-23

### Added
- Initial release.
