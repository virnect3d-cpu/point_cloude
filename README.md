# PointCloud Optimizer v2 — LCC Edition

XGrids PortalCam `.lcc` (Gaussian Splats) 전용 Unity 임포터 + 기존 포인트 클라우드 파이프라인 확장.

## 구성

| 폴더 | 역할 |
|------|------|
| `Packages/com.virnect.lcc` | Unity UPM 패키지 — LCC 디코더·렌더러·임포터 (모든 LCC 처리 로직) |
| `PointCloudOptimizer_v2` | 파이썬 백엔드 v2 — 점클라우드→메쉬, 3D 스캔 비교 분석 |
| `docs/` | 아키텍처·포맷 역분석 문서 |

## v1 (v3.0_260422) 는 그대로 유지

`C:\Users\jeongsomin\Desktop\PointCloudOptimizer_v3.0_260422` 는 읽기 전용으로 동결.
v2 는 완전히 별도 폴더/브랜치에서 독립 개발.

## Unity 패키지 설치 (사용자 입장)

Unity → `Window → Package Manager → + → Add package from git URL`:

```
https://github.com/virnect3d-cpu/point_cloude.git?path=/Packages/com.virnect.lcc#v2-main
```

## 파이프라인 흐름

1. **LCC → Point Cloud** — Unity 패키지 내부 C# 디코더 (런타임 or 에디터)
2. **Point Cloud → Mesh** — 기존 v1 파이프라인 재사용 (PLY 경유)
3. **3D Scan vs LCC Mesh 비교** — v2 파이썬 백엔드 신규 기능

자세한 설계는 [docs/pipeline.md](docs/pipeline.md) 참고.

## 상태

현재 **스캐폴드만 구축된 상태** — 실제 LCC 디코더 로직 등 기능 구현은 차후 커밋에서 진행.
