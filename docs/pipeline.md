# v2 파이프라인 아키텍처

## 1. 전체 데이터 흐름

```
 XGrids PortalCam 스캐너
          │ (촬영·처리)
          ▼
   *.lcc  (manifest JSON)
   data.bin, index.bin, environment.bin, collision.lci, attrs.lcp, assets/poses.json
   mesh-files/*.ply   ← XGrids 가 함께 내보내는 proxy 메쉬
          │
          ▼
 ┌──────────────────────────────┐
 │  Unity UPM Package           │
 │  com.virnect.lcc             │
 │                              │
 │  ① LccScriptedImporter       │ — Project 드롭 → LccScene 에셋
 │  ② LccSplatDecoder           │ — data.bin → Point[]
 │  ③ LccPointCloudRenderer     │ — 런타임 포인트 렌더
 │  ④ LccSceneMerger            │ — 다중 씬 월드 배치 (동일 EPSG)
 │  ⑤ Export → PLY / OBJ        │ — 파이썬 백엔드 연동 지점
 └──────────────┬───────────────┘
                │ PLY
                ▼
 ┌──────────────────────────────┐
 │  PointCloudOptimizer v2      │  (기존 기능 재사용)
 │  파이썬 백엔드                │
 │                              │
 │  ⑥ 점클라우드 → 메쉬          │ — Poisson/BPA/MC (v1 기능 그대로)
 │  ⑦ 3D 스캔 vs LCC 메쉬 비교  │ — v2 신규
 └──────────────────────────────┘
```

## 2. 책임 분할

| 영역 | 위치 | 이유 |
|------|------|------|
| LCC 디코드 | Unity C# (Runtime) | 최종 사용자는 Unity 개발자. 외부 CLI 의존 없이 패키지 설치만으로 즉시 사용. |
| 점클라우드 렌더 | Unity C# | Runtime 에서 바로 봐야 함. |
| 씬 합치기 | Unity C# (Editor + Runtime) | 동일 EPSG 전제 — 단순 오프셋 누적. Editor Window 로 수동 조합 UI 제공. |
| 메쉬화 | Python 백엔드 v2 | v1 의 Poisson·BPA·MC·Instant Meshes 파이프를 그대로 재사용. Unity 에서 PLY 로 익스포트해 전달. |
| 비교 분석 | Python 백엔드 v2 | Hausdorff 거리·법선 차이 등은 Open3D/trimesh 이미 갖춰져 있음. |

## 3. 사용자 여정

### 3-1. Unity 개발자가 LCC 를 씬에 올리는 흐름
1. `Window → Package Manager → Add from Git URL` → 위 URL 입력
2. 스캐너에서 받은 LCC 폴더를 `Assets/ScanData/ShinWon/` 로 복사
3. `.lcc` 더블클릭 → `LccScene` 인스펙터 확인
4. 씬에 빈 GameObject + `LccPointCloudRenderer` 컴포넌트 → Scene 필드에 방금 만든 에셋 드래그
5. Play → 포인트 클라우드 렌더 확인

### 3-2. 여러 공장동 합치기
1. 각 공장동 스캔을 별도 폴더로 임포트 → `LccScene` 에셋 N개
2. `Virnect → LCC Importer` 창 열기 → N개 슬롯에 에셋 드롭
3. "합치기 플랜 미리보기" → 콘솔에 각 씬의 월드 오프셋 확인
4. "월드로 인스턴스화" → 하나의 부모 GameObject 아래 모든 씬이 세팅됨

### 3-3. LCC → 메쉬 비교
1. Unity 에서 LCC 로드 → 우클릭 → `Export to PLY`
2. PLY 를 PointCloudOptimizer v2 에 드롭 → 기존 3페이지 파이프로 메쉬화
3. 원본 3D 스캔(별도 PLY)과 새 메쉬를 v2 의 비교 페이지에 동시 드롭 → Hausdorff·노멀·색상 차이 히트맵 출력

## 4. 향후 결정 필요

- **SH 차수·회전 압축 방식** — `data.bin` 역분석이 남아 있음. XGrids 문서/SDK 를 입수할 수 있는지.
- **런타임 LOD 스트리밍** — 9.97M 스플랫을 한번에 VRAM 에 올리면 Android/VR 에서 터짐. LOD 0~2 는 프리로드, 3~4 는 카메라 거리 기반 동적 로드.
- **LFS 정책** — 예제 LCC 파일(300+ MB)을 리포에 포함할지 여부. 권장: 리포에는 매니페스트만, 바이너리는 S3/HF.
