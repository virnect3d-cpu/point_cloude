# LccMeshColorizer — Splat RGB → Proxy Mesh Vertex Color

## 왜 이 방식인가

LCC 드롭은 두 레이어를 함께 제공한다:
1. **학습된 Gaussian Splat 클라우드** (`data.bin`, 컬러 있음, geometry 부정확)
2. **프록시 메쉬 `.ply`** (정확한 surface, 컬러 없음, physics collider 로 쓰기 좋음)

SuGaR/2DGS/GOF 등의 surface-reconstruction 은 재학습이 필요해 에디터-타임엔 과함.
이미 (1) 의 색과 (2) 의 surface 가 있으니 — **splat 색을 mesh vertex 로 projection**
하면 재학습 없이 색 있는 visualization mesh 를 얻을 수 있다. 같은 surface 를 쓰므로
MeshCollider (physics) 와 colored mesh (visualization) 가 정확히 일치.

## 알고리즘

**입력**
- `Mesh mesh` — 프록시 PLY 에서 로드한 position-only 메쉬 (N verts)
- `LccSplatDecoder.Point[] splats` — decode 된 splat 배열 (각 원소: position, color, scale, opacity)
- `Options { cellSize, k, maxRadius, fallbackColor, parallel }`

**출력**
- `Color32[]` (길이 N) — mesh.colors32 에도 바로 기록

**스텝**
1. **Sparse voxel hash-grid 빌드** — splat position 을 `cellSize` 단위 int3 key 로 버킷팅
   (O(M), M=splat count). 팩토리 규모는 `cellSize = 1m` 권장.
2. **Per-vertex k-NN 검색**
   - vertex 의 voxel cell 을 중심으로 3×3×3 = 27 개 이웃 cell 만 스캔
   - 각 후보 splat 과의 squared distance 계산, `maxRadius` 초과 시 skip
   - top-k heap 에 insertion-sort (k 작으니 flat array + bubble up 이 heap 보다 빠름)
3. **Inverse-distance blend**
   - `w_i = 1 / max(sqrt(d²_i), ε)` (ε=1e-4 로 수치 안정화)
   - `rgb = Σ w_i · color_i / Σ w_i` → clamp → `Color32`
4. **Fallback** — splat 을 하나도 못 찾으면 `opts.fallbackColor` (기본 회색)

**병렬화**
- vertex 수가 > 4096 이면 `Parallel.For` 로 `Environment.ProcessorCount` 개 청크로 분할
- thread-local top-k 배열만 할당 → false sharing / lock 없음

## 복잡도

- build: **O(M)**, M = splat 수
- query: **O(N · B̄)**, N = vertex 수, B̄ = 평균 버킷 후보 수 (= 3×3×3 voxel 내 splat 밀도)

실측: 145,801 verts × 641,905 splats (LOD3, cellSize=1m, k=6):
- decode: **90 ms**
- colorize: **1,852 ms** (병렬, 4-코어 추정)
- 총 **~2 초** 이내에 컬러 메쉬 생성.

## 왜 k-NN이고, 왜 opacity 가중치 안 쓰는가

k-NN 은:
- 지역적으로 일관됨 (nearest splat 의 색)
- 한 vertex 가 이웃 face 들의 평균이 아니라 surface 의 "점" 색을 받음 → detail 보존
- k>1 로 노이즈 완화

opacity 가중치는 후속 개선 포인트: 창/유리 등 opacity 낮은 splat 이 현재 k-NN 에 섞여
색이 흐려지는 문제가 있음. `w_i *= opacity_i` 한 줄 추가로 해결 가능.

## 대안 분석 (왜 채택 안 했나)

| 방법 | 장점 | 왜 기각 |
|------|------|---------|
| SuGaR / 2DGS / GOF / PGSR | 고품질 mesh + 색 | 재학습 단계, GPU 수십 분, 에디터-타임에 과함 |
| Marching Cubes on splat density | 외부 툴 없음 | blobby geometry, 우리는 XGrids proxy mesh 가 이미 있음 |
| Per-face texture bake | 선명도 높음 | UV unwrap 필요, proxy PLY 엔 UV 없음 |
| Unity Lightmapper 로 bake | 이미 있는 도구 | splat 을 emissive 로 올려야 하고 시간 수 분 |
| **k-NN projection (채택)** | 재학습 없음, 수 초, vertex color 그대로 사용 | opacity blending 미흡 (후속 개선) |

## 관련 파일

- `LccMeshColorizer.cs` — 알고리즘 본체
- `Shaders/LccVertexColorUnlit.shader` — URP 호환 vertex color unlit (gamma lift 포함)
- `LccSplatDecoder.cs` — 32-byte LCC splat record → Point 배열 디코드 (dependency)

## 사용 예

```csharp
using Virnect.Lcc;
using UnityEngine;

// 1) 프록시 mesh 로드 (LccMeshPlyLoader 또는 다른 PLY 리더)
Mesh mesh = LccMeshPlyLoader.Load(plyAbsPath);

// 2) splat 디코드 (원하는 LOD)
LccSplatDecoder.Point[] splats = LccSplatDecoder.DecodeLod(scene, lodLevel: 3);

// 3) colorize
var opts = LccMeshColorizer.Options.Default;
opts.cellSize = 1.0f;   // 팩토리 규모
opts.k = 6;
opts.maxRadius = 3.0f;
LccMeshColorizer.Colorize(mesh, splats, opts);

// 4) 씬에 붙이기
var go = new GameObject("ColoredMesh");
go.AddComponent<MeshFilter>().sharedMesh = mesh;
var mr = go.AddComponent<MeshRenderer>();
mr.sharedMaterial = new Material(Shader.Find("Virnect/LccVertexColorUnlit"));
```
