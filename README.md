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

## LOD 레벨 — 어디서 무엇이 쓰이나

LCC 는 5 단계 LOD (0=풀, 4=가장 가벼움) 를 동봉합니다. 각 LOD 별 점 개수는 데이터셋마다 다르지만, ShinWon 샘플 기준:

```
LOD 0:  5,149,480 splats   ← 풀 (원본)
LOD 1:  2,569,400          ← 절반
LOD 2:  1,284,217
LOD 3:    641,905
LOD 4:    320,706          ← 1/16 (메모리 안전)
```

코드 디폴트:

| 위치 | 변수 | 기본 LOD | 영향 |
|---|---|---|---|
| [LccImporterWindow.cs](Packages/com.virnect.lcc/Editor/LccImporterWindow.cs) Scenes 탭 | `_lodLevel` | **4** | ▶ Instantiate 시 splat 렌더러에 들어가는 LOD |
| [LccSplatRenderer.cs](Packages/com.virnect.lcc/Runtime/LccSplatRenderer.cs) | `lodLevel` | **4** | splat 빌보드 렌더 LOD (Inspector 디폴트) |
| [LccImporterWindow.cs](Packages/com.virnect.lcc/Editor/LccImporterWindow.cs) 콜라이더 탭 | `_bulkColLod` | **1** | 백엔드 콜라이더 일괄 재생성 입력 LOD |
| [LccImporterWindow.cs](Packages/com.virnect.lcc/Editor/LccImporterWindow.cs) Compare 탭 | `_cmpLod` | **2** | Hausdorff 비교 LOD |

### 디폴트가 LOD 4 인 이유 — splat 렌더링

LOD 0 (5M splats) 는 빌보드당 4 vertex × 5M = **20M verts 메쉬** → Unity 메모리 한계에 근접.
LOD 4 (320K splats) ≈ 1.28M verts ≈ 50 MB 메쉬로 안전. 자세한 트레이드오프는 [LccSplatRenderer.cs:18 코멘트](Packages/com.virnect.lcc/Runtime/LccSplatRenderer.cs#L18) 참고.

### 권장 설정

- **시각적 품질을 더 원하면**: `LccSplatRenderer.lodLevel = 1` 또는 `2` (씬당 메모리 여유 있을 때).
- **콜라이더가 본체 일부만 덮는 문제**: 콜라이더 탭에서 `LCC LOD = 0`, `최대 점 = 200K~1M` 로 변경 → 풀 해상도에서 무작위 다운샘플 → 분포 균일해서 Poisson 결과가 본체 전체 커버.
- **Compare 정확도 ↑**: `_cmpLod = 0` (시간은 더 걸림).

## 상태

현재 **스캐폴드만 구축된 상태** — 실제 LCC 디코더 로직 등 기능 구현은 차후 커밋에서 진행.
