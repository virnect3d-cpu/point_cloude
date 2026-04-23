# PointCloudOptimizer v2 (파이썬 백엔드)

v1(v3.0_260422)의 기능을 그대로 승계 + LCC 연동 + 3D 스캔 비교 분석 추가 예정 폴더.

## 스코프

| 기능 | 출처 |
|------|------|
| 포인트 클라우드 → 메쉬 (Poisson/BPA/MC/Instant Meshes) | v1 백엔드 이식 |
| OBJ/GLB/FBX 내보내기 | v1 백엔드 이식 |
| Unity MeshCollider JSON 내보내기 | v1 백엔드 이식 |
| **3D 스캔 ↔ LCC 메쉬 비교 분석 (Hausdorff·법선·색상)** | v2 신규 |

## 현재 상태

**빈 스캐폴드** — 실제 코드 이식은 Unity 패키지 `LccSplatDecoder` 가 PLY 를 내보내는 단계까지 오면 시작.
지금 v1(`../../PointCloudOptimizer_v3.0_260422`) 에는 동결 상태로 모든 기능이 살아있으므로, 급한 작업은 v1 을 직접 쓰면 됨.
