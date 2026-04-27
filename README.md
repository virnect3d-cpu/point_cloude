# LCC → Unity 파이프라인 (3D Gaussian Splatting + 자동 콜라이더 + WASD 캐릭터)

XGrids **LCC (Lixel CyberColor) 3D Gaussian Splatting** 파일을 Unity에 **고품질로 import** 하고, **각 LCC에 자동 MeshCollider** 를 만들어 **로봇 캐릭터(V-Bot)로 환경 안을 WASD 로 돌아다닐 수 있는** 풀 파이프라인.

> **핵심 가치**:
> 1. `.lcc` 폴더 드롭 한 번으로 → 자동 변환 → 자동 spawn → 자동 콜라이더
> 2. Aras-P UnityGaussianSplatting 으로 **진짜 3DGS 화질** (view-dependent, SH bands, tile sorting)
> 3. proxy mesh 기반 자동 MeshCollider — 캐릭터/물리/raycast 즉시 동작

---

## 환경

| 항목 | 버전 / 값 |
|---|---|
| **Unity** | **6000.3.10f1** (Unity 6) |
| **Render Pipeline** | **URP 17.3.0** (Universal Render Pipeline) |
| **Color Space** | Linear |
| **HDR ColorGrading** | On (`PC_RPAsset.m_ColorGradingMode = 1`) |
| **Active Input Handler** | **Both** (Legacy + New Input System — Invector LITE 호환) |
| **Plaftorm** | Windows (DX11/12), Mac/Android/iOS/Pico4Ultra/Quest3 호환 |

## 사용 SDK / 패키지

| SDK | 용도 | 설치 위치 |
|---|---|---|
| **`org.nesnausk.gaussian-splatting`** (Aras-P UnityGaussianSplatting) | **진짜 3DGS 렌더러** (SH, tile sort, URP/HDRP 호환) | `Packages/manifest.json` (GitHub https) |
| **`com.virnect.lcc`** (Virnect LCC SDK) | `.lcc` ScriptedImporter, proxy mesh PLY loader, ICP point cloud registration | local file path (Packages/) |
| **`@playcanvas/splat-transform`** (npm CLI v1.10.2+) | `.lcc → .ply` 변환 (LOD 0, full SH bands) | npm 글로벌 (`npm i -g @playcanvas/splat-transform`) |
| **Invector 3rdPersonController LITE** | **V-Bot 로봇 prefab** + `vThirdPersonController` + `vThirdPersonCamera` | `Assets/Invector-3rdPersonController_LITE/` |
| Unity AI Navigation 2.0.10 | NavMesh (선택) | Packages |
| Unity Cinemachine 3.1.0 | 카메라 옵션 | Packages |

> ⚠ **`com.virnect.lcc` 는 로컬 파일 경로 의존**. clone 후 본인 환경의 패키지 위치로 `Packages/manifest.json` 에서 경로 수정 필요. (예: `"file:C:/Users/<USER>/path/to/com.virnect.lcc"`)

---

## ⚡ 가장 중요한 두 가지 (핵심 결과)

### 1) `.lcc` 자동 import + 고품질 렌더링
```
.lcc 폴더 드롭 (Assets/LCC_Drops/) 
  → LccDropAutoImporter (AssetPostprocessor 자동 발동)
  → LccConverter (splat-transform → .ply 임시)
  → GaussianAssetBuilder (Aras-P GaussianSplatAsset 빌드, Quality=High)
  → 활성 씬에 자동 spawn:
     Splat_<name>  (Z-up→Y-up 회전)
       ├─ __LccCollider     (MeshCollider, proxy mesh, Z-up→Y-up 변환된 mesh, world identity)
       └─ _ArasP            (GaussianSplatRenderer + asset, world identity)
  → Hierarchy 에서 자동 선택 + ping
```

**Quality = High** (PSNR 57.77 dB, 2.94× 압축, Norm16 pos/scale + Float16x4 color + Norm11 SH)

### 2) 자동 MeshCollider (proxy mesh 기반)
- 각 LCC 의 proxy mesh PLY (LCC native Z-up 좌표) 를 **Y-up 으로 변환** → MeshCollider 의 sharedMesh
- `__LccCollider` 자식 GameObject 에 부착 — `_ArasP` 와 동일한 world identity → splat 과 정확히 정렬
- 캐릭터 raycast / 물리 / Scene View 클릭 모두 즉시 동작

---

## Scene5_LccRotate — WASD 로 LCC 안 돌아다니기

5개 LCC 가 한 좌표계에 모여있는 walkable 씬 + V-Bot (Invector LITE 로봇) 캐릭터.

### 컨트롤
| 입력 | 동작 |
|---|---|
| **W / A / S / D** | 카메라 forward 기준 이동 |
| **Shift** | 달리기 |
| **Space** | 점프 |
| **마우스 이동** | 카메라 yaw / pitch 회전 (Invector vThirdPersonCamera) |

### Hierarchy
```
LccGroup                                 (transform identity, parent)
  ├─ Splat_ShinWon_1st_Cutter            (사용자 수동 정합 회전)
  │   ├─ __LccCollider                   (MeshCollider, proxy mesh — Y-up 변환, world identity)
  │   └─ _ArasP                          (GaussianSplatRenderer + Aras-P asset)
  ├─ Splat_ShinWon_Facility_01           ← 동일 패턴
  ├─ Splat_ShinWon_Facility_02
  ├─ Splat_ShinWon_Facility_03
  └─ Splat_ShinWon_Facility_Middle

Player_VBot                              (ThirdPersonController_LITE prefab)
  └─ Mesh_LOD                            (V-Bot LOD0~3 SkinnedMeshRenderer)

Cam_VBot                                 (vThirdPersonCamera, target=Player_VBot)
Directional Light                        (intensity 2.5)
```

### Spawn 위치
EditorPref 로 보존 (`LccDropForge.Scene5.PlayerSpawn{X,Y,Z}`). 사용자가 V-Bot 옮긴 후 `Tools/Lcc Drop Forge/Scene5 · Save current Player_VBot pos as default spawn` 메뉴로 명시 저장 가능.

---

## 메뉴 레퍼런스 (`Tools/Lcc Drop Forge/`)

### 핵심 자동화
| 메뉴 | 동작 |
|---|---|
| `Settings · Toggle auto-spawn on .lcc import` | 새 .lcc 드롭 시 자동 spawn 토글 (default ON) |
| `Reimport Selected .lcc` | Selection 의 .lcc 재import |
| `Process All LCC in LCC_Drops` | 폴더 내 모든 .lcc 일괄 처리 |

### 씬 빌드
| 메뉴 | 동작 |
|---|---|
| `Scene · Build Scene4_LccBrowse (...)` | 모든 LCC 자동 spawn + ICP 정합 (LCC-only viewer) |
| `Scene5 · Walkable v3 (per-Splat MeshColliders + Invector V-Bot robot)` | 5개 LCC + V-Bot + 카메라 + 콜라이더 fully automatic |
| `Scene5 · Walkable setup (LccGroup parent + single MeshCollider + WASD player)` | 단일 통합 collider 버전 (실험) |

### 콜라이더
| 메뉴 | 동작 |
|---|---|
| `Scene · Ensure MeshColliders on all Splat (clickable in Scene View)` | 누락 콜라이더 추가 |
| `Collider · DIAG · proxy mesh vs Aras-P bounds (per LCC)` | 정렬 진단 (read-only) |
| `Collider · FIX v2 · convert proxy mesh Z-up→Y-up + world identity (perfect align)` | **콜라이더 정확 정렬** ⭐ |

### 정합 (ICP)
| 메뉴 | 동작 |
|---|---|
| `Test ICP (Facility_01 ← 1st_Cutter · Coarse→Fine)` | 빠른 정합 |
| `Test ICP BRUTE (Facility_01 ← 1st_Cutter · 4-rotation search)` | 안전 정합 (Z 4방향 후보) |
| `Test ICP BRUTE (Facility_01 ← Middle · 4-rotation search)` | |
| `Test ICP (Facility_01 ← Middle · Coarse→Fine)` | |
| `NEW LCC pipeline · Facility_02 + 03 (refresh → build → spawn → ICP)` | 새 LCC 한 방 처리 |

### 회전 / 위치
| 메뉴 | 동작 |
|---|---|
| `Scene · Set ALL Splat rotation to (-180, 0, 0)` | 일괄 회전 + _ArasP child localRotation 자동 재계산 |
| `Scene · Auto-swap ALL Splat to Aras-P (current scene, any LCC)` | Virnect SDK → Aras-P 일괄 전환 |
| `Scene · Lighten current (disable all Mesh and ColoredMesh roots)` | 무거운 Mesh 측 OFF |

### V-Bot
| 메뉴 | 동작 |
|---|---|
| `Scene5 · Save current Player_VBot pos as default spawn (EditorPref)` | spawn 위치 명시 저장 |
| `Scene5 · Reset saved player spawn (use bounds top fallback)` | EditorPref 초기화 |
| `Scene5 · Brighten V-Bot materials (BaseColor=white) + capture` | 머티리얼 색감 fix |
| `Scene5 · Fix V-Bot lighting (light intensity + ambient + receiveShadows off)` | lighting fix |
| `Scene5 · DIAG · dump V-Bot Renderer materials (read-only)` | 머티리얼 진단 |
| `Scene5 · Force-reassign V-Bot materials in Player_VBot (fix textures)` | 머티리얼 강제 재할당 |
| `Scene5 · Convert V-Bot materials to URP/Lit (fix magenta)` | shader 변환 |

### URP
| 메뉴 | 동작 |
|---|---|
| `Aras-P · Register URP RendererFeature (PC_Renderer)` | `GaussianSplatURPFeature` 자동 등록 (한 번만 실행) |
| `Aras-P · Build GaussianSplatAssets (Quality=High) for Facility_01 + Middle` | 수동 asset 빌드 |
| `Aras-P · Swap Scene2 Splats (Virnect OFF → Aras-P child ON)` | Scene2 전용 swap |

### 캡처
| 메뉴 | 동작 |
|---|---|
| `Dev · Capture Scene View (top-down, 1920×1080)` | top-down PNG |
| `Dev · Capture Scene View (3/4 persp, 1920×1080)` | 3/4 perspective |
| `Dev · Capture Scene View (front, 1920×1080)` | 정면 |

---

## 작업 히스토리 (2026-04 ~)

### 발견 → 솔루션
1. **Virnect LCC SDK 자체 렌더러는 quad billboard prototype** (SH 미지원, RGBA8 색만, alpha blend 만)
   - xgrids.com 데모 같은 화질 절대 안 나옴 → Aras-P 도입 결정
2. **Aras-P UnityGaussianSplatting 도입** — Quality=High 로 모든 LCC 재import
3. **A/B 비교 (Scene4_AB_Compare)** — Virnect SDK 흰/회색 흐릿 vs Aras-P 베이지/금속 디테일 + 표면 텍스처
4. **Scene2 의 5개 Splat 모두 Aras-P 화** — `_ArasP` child 패턴 (parent rotation 보존, child world identity)

### 새 LCC 추가 (G:\)
- 기존 3개 (1st_Cutter, Facility_01, Middle) byte-perfect 같음 (G:\와 Assets 비교)
- 새 2개 (Facility_02, Facility_03) 추가 → splat-transform → Aras-P asset 빌드 → ICP 자동 정합

### 자동 콜라이더 + 자동 spawn
- `LccDropAutoImporter` 에 `_AutoSpawnInActiveScene` hook
- Splat_<name> + `__LccCollider` (proxy mesh) + `_ArasP` (GS renderer) 한 번에
- EditorPref toggle (`Settings · Toggle auto-spawn on .lcc import`)

### Scene5 — Walkable
- 1차: 단일 통합 MeshCollider — combined mesh (1.12M verts) — too heavy
- 2차: 각 Splat 자체 MeshCollider (proxy mesh) — V-Bot 떨어짐 동작
- 3차 (현재): proxy mesh **Z-up→Y-up 변환** + world identity → splat 과 정확 정렬

### V-Bot 캐릭터
- Invector LITE V-Bot prefab (`ThirdPersonController_LITE.prefab`)
- 머티리얼 BaseColor 흰색 + Lighting 강화 + receiveShadows off
- spawn 위치 EditorPref 보존

### 알고리즘 자료 정리
프로젝트와 별도로 `~/.claude/skills/point-cloud-algorithms/SKILL.md` 에 추가:
- Registration: ICP/GICP/NDT/RANSAC/TEASER++/GeoTransformer/FCGF/KISS-ICP
- LiDAR odometry: LOAM/LeGO-LOAM/CT-ICP/KISS-ICP/MULLS/LIO-SAM/FAST-LIO
- Tracking: PointPillars + SimpleTrack/AB3DMOT, RaTrack (class-agnostic), 4D-PLS
- Navigation: OctoMap/VDB/Voxblox/NVBlox, A*/RRT*/D* Lite, NavMesh + LCC 콜리전 통합

---

## 파이프라인 다이어그램

```
.lcc 폴더 (Assets/LCC_Drops/<name>/)
  data.bin + index.bin + attrs.lcp + collision.lci + ...
        │
        │ Virnect ScriptedImporter
        ▼
LccScene (Asset)
        │
        ├──→ proxy mesh PLY (LCC native, Z-up)
        │      │
        │      │ LccMeshPlyLoader.Load
        │      │ + _ConvertZupToYupMesh (x, z, -y)
        │      ▼
        │   MeshCollider (Y-up, world identity)
        │
        └──→ splat-transform CLI (-O 0, full SH)
               │
               ▼
            .ply (Library/lcc_to_ply/, ~hundreds MB, Inria 3DGS standard)
               │
               │ GaussianSplatAssetCreator (Quality=High)
               ▼
            GaussianSplatAsset (Assets/GaussianAssets/<name>.asset)
               │  + <name>_pos.bytes (Norm16)
               │  + <name>_col.bytes (Float16x4)
               │  + <name>_oth.bytes
               │  + <name>_shs.bytes (Norm11 SH)
               │  + <name>_chk.bytes (chunks)
               │
               ▼
            GaussianSplatRenderer (_ArasP child, world identity)
               │
               │ URP: GaussianSplatURPFeature (PC_Renderer 등록)
               ▼
            화면 출력 (proper 3DGS)
```

---

## 처음 클론한 분 (다른 사람 setup)

```bash
# 1. clone
git clone https://github.com/virnect3d-cpu/point_cloude.git
cd "point_cloude"

# 2. com.virnect.lcc 패키지 경로 수정 (본인 환경)
#    Packages/manifest.json 에서 "file:C:/...com.virnect.lcc" → 본인 경로로

# 3. splat-transform 설치
npm install -g @playcanvas/splat-transform

# 4. Unity 6 (6000.3.10f1) 로 프로젝트 열기 → 자동 import

# 5. URP RendererFeature 등록 (한 번만)
#    Tools > Lcc Drop Forge > Aras-P · Register URP RendererFeature

# 6. Scene5_LccRotate.unity 열고 ▶ Play → WASD 로 환경 안 돌아다니기
```

---

## 라이선스 / 출처

- **Aras-P UnityGaussianSplatting** (MIT) — https://github.com/aras-p/UnityGaussianSplatting
- **XGrids LCCWhitepaper / SDK** (MIT/Apache, 2025-11 open-source) — https://github.com/xgrids
- **PlayCanvas splat-transform** (MIT) — https://github.com/playcanvas/splat-transform
- **Invector 3rdPersonController LITE** — Asset Store free
- **com.virnect.lcc** — Virnect 내부 SDK
- **LCC Drops** (`Assets/LCC_Drops/ShinWon_*`) — Virnect 사내 자산

