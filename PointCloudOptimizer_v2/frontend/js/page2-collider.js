// ═══════════════════════════════════════════════════════════════════════
//  Page 2 (Collider Editor) 로컬 state
// ═══════════════════════════════════════════════════════════════════════
const P2 = {
  // Three.js
  threeReady: false, viewerRunning: true,
  scene: null, camera: null, renderer: null, raf: null,
  pCloud: null, grid: null, dirty: true,
  // Orbit camera
  orb: {theta:-0.4, phi:1.15, radius:6, cx:0, cy:0, cz:0,
        dTheta:0, dPhi:0, dRadius:0, pdx:0, pdy:0,
        dragging:false, btn:-1, lx:0, ly:0},
  // Drag interaction
  placeDrag: {active:false, sx:0, sy:0, ex:0, ey:0},
  dragRectEl: null, lastOrbR: 0,
  // Gizmo / selection
  selectedId: -1, gizmoGroup: null, gizmoHandles: [], activeDrag: null,
  // Data
  points: null, sessionId: null, fileRef: null, zUp: false,
  // Convex geo lazy-load
  convexGeoLoaded: false, convexGeoLoading: false,
  // Collider data
  meshCols: [], boxes: [], boxIdSeed: 0, boxGroupSeed: 0,
  // Undo/redo
  history: [], redo: [], noHistory: false,
  // Mode
  placing: false,
  // Compass widget (SceneGizmo)
  sceneGizmo: null,
};

//  PAGE 2 — UNITY COLLIDER
// ═══════════════════════════════════════════════════════════════════════

// ─── Tab 전환 ───────────────────────────────────────────────────────
// 뷰어 공유 전략: 탭 비활성 시 renderer의 render loop만 중단.
// WebGL 컨텍스트 자체 dispose는 안 함 (재진입 시 지연 크고 상태 복원 복잡).
// 대신 RAF 완전 정지 + setAnimationLoop(null)로 GPU 유휴 상태.
function _p4StopLoop(){
  if(typeof P4.raf !== 'undefined' && P4.raf){ cancelAnimationFrame(P4.raf); P4.raf = null; }
}

function switchTab(n){
  $('page1').style.display = n===1 ? '' : 'none';
  $('page2').style.display = n===2 ? '' : 'none';
  $('page3').style.display = n===3 ? '' : 'none';
  const p4 = $('page4'); if(p4) p4.style.display = n===4 ? '' : 'none';
  const p5 = $('page5'); if(p5) p5.style.display = n===5 ? '' : 'none';
  $('tab-btn-1').classList.toggle('active', n===1);
  $('tab-btn-2').classList.toggle('active', n===2);
  $('tab-btn-3').classList.toggle('active', n===3);
  const tb4 = $('tab-btn-4'); if(tb4) tb4.classList.toggle('active', n===4);
  const tb5 = $('tab-btn-5'); if(tb5) tb5.classList.toggle('active', n===5);
  // 모든 뷰어 RAF 중단 (탭 진입 시 해당 뷰어만 다시 시작)
  P2.viewerRunning=false;
  if(P2.raf){ cancelAnimationFrame(P2.raf); P2.raf=null; }
  P3.viewerRunning=false;
  if(P3.raf){ cancelAnimationFrame(P3.raf); P3.raf=null; }
  _p4StopLoop();

  if(n===2){
    initViewer();
    if(P2.threeReady && !P2.raf){ P2.viewerRunning=true; P2.dirty=true; renderLoop(); }
  } else if(n===3){
    initP3Viewer();
    if(P3.threeReady && !P3.raf){ P3.viewerRunning=true; P3.dirty=true; p3RenderLoop(); }
  } else if(n===4){
    if(typeof initP4Viewer === 'function') initP4Viewer();
  }
  // 자동처리 결과 로드 (세션에 이미 처리된 내용 있으면 미리보기 바로 표시)
  if(typeof autoLoadPageData === 'function') autoLoadPageData(n);
}

// ─── Setup Modal (removed — Ollama/AI features removed) ─────────────

// ─── Three.js 뷰어 ──────────────────────────────────────────────────
P2.threeReady = false;
P2.viewerRunning = true;
P2.scene = null;
P2.camera = null;
P2.renderer = null;
P2.raf = null;
P2.pCloud = null;
P2.grid = null;
P2.dirty = true;  // Dirty render flag — 변화 있을 때만 렌더
function markDirty(){ P2.dirty=true; }

function initViewer(){
  if(P2.threeReady) return;
  if(typeof THREE !== 'undefined'){ setupThree(); return; }
  // 공용 loader (core.js) — 로컬 three.min.js 우선, 실패 시 CDN 폴백
  loadThreeJS().then(setupThree).catch(err => {
    console.warn('[P2] Three.js 로드 실패:', err);
    appNotify('Three.js 로드 실패 — 오프라인 환경이면 frontend/three.min.js 파일을 확인하세요.');
  });
}

function setupThree(){
  const canvas=$('three-canvas');
  P2.scene=new THREE.Scene();
  P2.scene.background=new THREE.Color(0xF0F0F0);

  const w=Math.max(300, canvas.clientWidth||800);
  const h=Math.max(480, canvas.getBoundingClientRect().height||window.innerHeight-202);
  P2.camera=new THREE.PerspectiveCamera(55, w/h, 0.01, 5000);
  P2.camera.position.set(0,3,6);

  P2.renderer=new THREE.WebGLRenderer({canvas, antialias:true});
  P2.renderer.setPixelRatio(Math.min(window.devicePixelRatio,2));
  P2.renderer.setSize(w, h, false);

  P2.grid=new THREE.GridHelper(30,30,0xC8C8C8,0xDDDDDD);
  P2.scene.add(P2.grid);
  P2.scene.add(new THREE.AmbientLight(0xffffff,1.3));
  const dir=new THREE.DirectionalLight(0xffffff,0.5);
  dir.position.set(5,10,5);
  P2.scene.add(dir);

  P2.threeReady=true;
  setupOrbit(canvas);
  window.addEventListener('resize', onP2Resize);
  new ResizeObserver(onP2Resize).observe(canvas.parentElement||canvas);
  renderLoop();
}

function onP2Resize(){
  if(!P2.renderer||!P2.camera) return;
  const canvas=$('three-canvas');
  const w=Math.max(300, canvas.clientWidth||800);
  const h=Math.max(480, canvas.getBoundingClientRect().height||window.innerHeight-202);
  P2.renderer.setSize(w, h, false);
  P2.camera.aspect=w/h;
  P2.camera.updateProjectionMatrix();
  P2.dirty=true;
}

function renderLoop(){
  if(!P2.viewerRunning){ P2.raf=null; return; }
  P2.raf=requestAnimationFrame(renderLoop);
  // Orbit 감쇠 중이면 dirty
  const orbMoving=
    Math.abs(P2.orb.dTheta)+Math.abs(P2.orb.dPhi)+Math.abs(P2.orb.dRadius)+
    Math.abs(P2.orb.pdx)+Math.abs(P2.orb.pdy) > 2e-5;
  if(orbMoving) P2.dirty=true;
  if(!P2.dirty && !P2.activeDrag && !P2.orb.dragging) return;  // 변화 없으면 스킵
  P2.dirty=false;
  applyOrbit();
  P2.renderer.render(P2.scene,P2.camera);
}

function toggleViewer(){
  P2.viewerRunning=!P2.viewerRunning;
  const btn=$('v-toggle');
  if(P2.viewerRunning){
    btn.textContent='⏸ ON'; btn.classList.remove('off');
    P2.dirty=true;
    if(P2.threeReady) renderLoop();
  } else {
    btn.textContent='▶ OFF'; btn.classList.add('off');
  }
}

// ─── Orbit Controls ─────────────────────────────────────────────────
P2.orb = {theta:-0.4, phi:1.15, radius:6, cx:0, cy:0, cz:0,
          dTheta:0, dPhi:0, dRadius:0, pdx:0, pdy:0,
          dragging:false, btn:-1, lx:0, ly:0};

// 드래그 배치 상태
P2.placeDrag = {active:false, sx:0, sy:0, ex:0, ey:0};
P2.dragRectEl = null;

function setupOrbit(canvas){
  canvas.addEventListener('contextmenu',e=>e.preventDefault());

  // rubber-band overlay 생성
  P2.dragRectEl=document.createElement('div');
  P2.dragRectEl.style.cssText=
    'position:absolute;border:2px solid #FF6600;background:rgba(255,102,0,0.10);'+
    'pointer-events:none;display:none;box-sizing:border-box;border-radius:2px;';
  canvas.parentElement.style.position='relative';
  canvas.parentElement.appendChild(P2.dragRectEl);

  // ── Maya 단축키 상태 ──────────────────────────────────────────────
  let _p2AltDown=false;
  window.addEventListener('keydown', ke=>{ if(ke.key==='Alt') _p2AltDown=true; });
  window.addEventListener('keyup',   ke=>{ if(ke.key==='Alt') _p2AltDown=false; });

  canvas.addEventListener('mousedown',e=>{
    e.preventDefault();

    // Maya: Alt+LMB = 회전, Alt+MMB = 패닝, Alt+RMB = 줌
    if(_p2AltDown){
      P2.orb.dragging=true;
      P2.orb.btn = e.button===0 ? 2 : e.button===1 ? 1 : 99; // 2=rotate,1=pan,99=zoom
      P2.orb.lx=e.clientX; P2.orb.ly=e.clientY;
      return;
    }
    if(e.button===0 && P2.placing){
      const r=canvas.getBoundingClientRect();
      P2.placeDrag={active:true, sx:e.clientX-r.left, sy:e.clientY-r.top,
                                ex:e.clientX-r.left, ey:e.clientY-r.top};
      updateDragRect();
      return;
    }
    if(e.button===0){
      onGizmoMouseDown(e, canvas);
      return;
    }
    // 기존 RMB/MMB 오빗
    P2.orb.dragging=true; P2.orb.btn=e.button;
    P2.orb.lx=e.clientX; P2.orb.ly=e.clientY;
  });

  window.addEventListener('mouseup',e=>{
    if(e.button===0 && P2.activeDrag){
      P2.activeDrag=null;
      updateColliderUI();
      markDirty();
      return;
    }
    if(e.button===0 && P2.placeDrag.active){
      P2.placeDrag.active=false;
      hideDragRect();
      finalizePlaceDrag(canvas);
      return;
    }
    P2.orb.dragging=false; P2.orb.btn=-1;
  });

  window.addEventListener('mousemove',e=>{
    if(P2.activeDrag){ onGizmoDrag(e); return; }
    if(P2.placeDrag.active){
      const r=canvas.getBoundingClientRect();
      P2.placeDrag.ex=e.clientX-r.left;
      P2.placeDrag.ey=e.clientY-r.top;
      updateDragRect();
      return;
    }
    if(!P2.orb.dragging) return;
    const dx=e.clientX-P2.orb.lx, dy=e.clientY-P2.orb.ly;
    P2.orb.lx=e.clientX; P2.orb.ly=e.clientY;
    if(P2.orb.btn===2){                         // 회전
      P2.orb.dTheta -= dx*0.007;
      P2.orb.dPhi   -= dy*0.007;
    } else if(P2.orb.btn===1){                  // 패닝
      const sc=P2.orb.radius*0.0015;
      P2.orb.pdx -= dx*sc; P2.orb.pdy += dy*sc;
    } else if(P2.orb.btn===99){                 // Maya Alt+RMB = 줌
      P2.orb.dRadius += dy * P2.orb.radius * 0.006;
    }
  });

  // 'F' 키 = 전체보기 (Page 2)
  canvas.addEventListener('keydown', ke=>{
    if(ke.key==='f'||ke.key==='F'){ fitP2Camera(); ke.preventDefault(); }
  });
  canvas.setAttribute('tabindex','0'); // 포커스 가능하게

  // 줌 — 트랙패드/휠 magnitude 정규화 (deltaY 값은 1~120+ 편차 심함, 정규화 없으면 폭발)
  // + 상한 clamp (누적 radius 폭주 방지)
  canvas.addEventListener('wheel',e=>{
    e.preventDefault();
    const f = Math.sign(e.deltaY) * Math.min(Math.abs(e.deltaY) / 120, 1);
    P2.orb.radius *= (1 + f * 0.12);
    P2.orb.dRadius = 0;   // 이전 누적 drift 제거
    P2.orb.radius = Math.max(0.001, Math.min(1e5, P2.orb.radius));
  },{passive:false});
}

function updateDragRect(){
  if(!P2.dragRectEl) return;
  const {sx,sy,ex,ey}=P2.placeDrag;
  P2.dragRectEl.style.display='block';
  P2.dragRectEl.style.left  =Math.min(sx,ex)+'px';
  P2.dragRectEl.style.top   =Math.min(sy,ey)+'px';
  P2.dragRectEl.style.width =Math.abs(ex-sx)+'px';
  P2.dragRectEl.style.height=Math.abs(ey-sy)+'px';
}

function hideDragRect(){
  if(P2.dragRectEl) P2.dragRectEl.style.display='none';
}

function floorRaycast(ndcX, ndcY){
  const ray=new THREE.Raycaster();
  ray.setFromCamera(new THREE.Vector2(ndcX,ndcY), P2.camera);
  const plane=new THREE.Plane(new THREE.Vector3(0,1,0), 0);
  const hit=new THREE.Vector3();
  return ray.ray.intersectPlane(plane, hit) ? hit : null;
}

function finalizePlaceDrag(canvas){
  if(!P2.threeReady||!P2.camera) return;
  const cw=canvas.clientWidth||800, ch=600;
  const {sx,sy,ex,ey}=P2.placeDrag;

  // 최소 드래그 거리 (5px) 미만은 무시
  if(Math.abs(ex-sx)<5 && Math.abs(ey-sy)<5) return;

  function toNDC(px,py){ return new THREE.Vector2((px/cw)*2-1, -(py/ch)*2+1); }

  const hA=floorRaycast(...toNDC(sx,sy).toArray());
  const hB=floorRaycast(...toNDC(ex,ey).toArray());

  if(!hA||!hB){
    appNotify('바닥면 교차 실패 — 카메라를 더 위에서 아래로 향하게 조정하세요.');
    return;
  }

  const rawX=Math.max(0.05, Math.abs(hB.x-hA.x));
  const rawZ=Math.max(0.05, Math.abs(hB.z-hA.z));
  const sq=Math.max(rawX, rawZ);                        // 정육면체: X=Y=Z 모두 동일
  const bcx=(hA.x+hB.x)/2, bcz=(hA.z+hB.z)/2;
  const name=$('bf-label').value.trim()||`Manual_${P2.boxIdSeed+1}`;

  addBox({name, center:{x:bcx, y:sq/2, z:bcz}, size:{x:sq, y:sq, z:sq}}, false);
  cancelBoxPlace();
}

P2.lastOrbR = 0;
function applyOrbit(){
  if(!P2.camera) return;
  P2.orb.theta  += P2.orb.dTheta;   P2.orb.dTheta  *= 0.86;
  P2.orb.phi    += P2.orb.dPhi;     P2.orb.dPhi    *= 0.86;
  P2.orb.radius += P2.orb.dRadius;  P2.orb.dRadius *= 0.86;
  P2.orb.cx     += P2.orb.pdx;      P2.orb.pdx     *= 0.86;
  P2.orb.cy     += P2.orb.pdy;      P2.orb.pdy     *= 0.86;

  P2.orb.phi    = Math.max(0.05, Math.min(Math.PI-0.05, P2.orb.phi));
  // radius 클램프: 한계 도달 시 dRadius 즉시 0 → 반대 방향 휠 즉시 반응
  if(P2.orb.radius < 0.001){ P2.orb.radius = 0.001; if(P2.orb.dRadius < 0) P2.orb.dRadius = 0; }
  if(P2.orb.radius > 1e6)  { P2.orb.radius = 1e6;   if(P2.orb.dRadius > 0) P2.orb.dRadius = 0; }

  P2.camera.position.set(
    P2.orb.cx + P2.orb.radius*Math.sin(P2.orb.phi)*Math.sin(P2.orb.theta),
    P2.orb.cy + P2.orb.radius*Math.cos(P2.orb.phi),
    P2.orb.cz + P2.orb.radius*Math.sin(P2.orb.phi)*Math.cos(P2.orb.theta)
  );
  P2.camera.lookAt(P2.orb.cx, P2.orb.cy, P2.orb.cz);

  // 줌 변화 시 핸들 scale만 갱신 (geometry 재생성 없이)
  if(P2.gizmoHandles.length>0 && Math.abs(P2.orb.radius-P2.lastOrbR)>P2.orb.radius*0.02){
    P2.lastOrbR=P2.orb.radius;
    const nr=Math.max(0.3, P2.orb.radius*0.018);
    P2.gizmoHandles.forEach(h=>h.mesh.scale.setScalar(nr));
  }
  // Scene Gizmo 업데이트
  if(P2.sceneGizmo) P2.sceneGizmo.draw();
}

// ─── Gizmo 선택 & 편집 ──────────────────────────────────────────────
P2.selectedId = -1;
P2.gizmoGroup = null;
P2.gizmoHandles = [];  // [{mesh, type:'move'|'scale', axis:null|'x'|'y'|'z', sign:0|+1|-1}]
P2.activeDrag = null;  // {type, axis, sign, dragPlane, startHit, startData}

// 그룹에 속한 박스들 전체 하이라이트 헬퍼
function _boxesInGroup(gid){ return P2.boxes.filter(x=>x.data.groupId===gid); }
function _boxColor(b){ return b.data.groupId!=null ? 0xFF8C00 : (b.data.fromAI?0x0088FF:0xFF6600); }

function selectBox(id){
  if(P2.selectedId===id) return;
  deselectBox();
  P2.selectedId=id;
  const b=P2.boxes.find(b=>b.id===id);
  if(!b) return;

  const gid=b.data.groupId;
  if(gid!=null){
    // 그룹 전체 노란 하이라이트 — gizmo 없음 (그룹은 통으로 삭제만 가능)
    _boxesInGroup(gid).forEach(x=>{
      x.wire.material.color.setHex(0xFFCC00);
      x.solid.material.color.setHex(0xFFCC00);
    });
  } else {
    b.wire.material.color.setHex(0xFFCC00);
    b.solid.material.color.setHex(0xFFCC00);
    createGizmo(b);
  }
  markDirty();
}

function deselectBox(){
  if(P2.selectedId<0) return;
  const b=P2.boxes.find(b=>b.id===P2.selectedId);
  if(b){
    const gid=b.data.groupId;
    if(gid!=null){
      _boxesInGroup(gid).forEach(x=>{
        x.wire.material.color.setHex(_boxColor(x));
        x.solid.material.color.setHex(_boxColor(x));
      });
    } else {
      const col=_boxColor(b);
      b.wire.material.color.setHex(col);
      b.solid.material.color.setHex(col);
    }
  }
  destroyGizmo();
  P2.selectedId=-1;
  markDirty();
}

function createGizmo(b){
  destroyGizmo();
  P2.gizmoGroup=new THREE.Group();
  P2.scene.add(P2.gizmoGroup);
  rebuildHandles(b);
}

function destroyGizmo(){
  if(!P2.gizmoGroup) return;
  P2.gizmoHandles.forEach(h=>{ h.mesh.geometry.dispose(); h.mesh.material.dispose(); });
  P2.gizmoHandles=[];
  P2.scene.remove(P2.gizmoGroup);
  P2.gizmoGroup=null;
}

function rebuildHandles(b){
  if(!b||!P2.gizmoGroup) return;
  P2.gizmoHandles.forEach(h=>{ P2.gizmoGroup.remove(h.mesh); h.mesh.geometry.dispose(); h.mesh.material.dispose(); });
  P2.gizmoHandles=[];

  const {center:c, size:s}=b.data;
  // 카메라 거리 기준으로 핸들 크기 결정 — 화면 픽셀 크기가 일정하게 유지
  const r=Math.max(0.3, P2.orb.radius * 0.018);
  const hx=s.x/2, hy=s.y/2, hz=s.z/2;

  // [위치, 색상, type, axis, sign]
  const defs=[
    [[c.x,      c.y,      c.z     ], 0xFFFFFF, 'move',  null, 0 ],  // 중심 (이동)
    [[c.x+hx,   c.y,      c.z     ], 0xFF4444, 'scale', 'x', +1],   // +X
    [[c.x-hx,   c.y,      c.z     ], 0xFF4444, 'scale', 'x', -1],   // -X
    [[c.x,      c.y+hy,   c.z     ], 0x33CC55, 'scale', 'y', +1],   // +Y
    [[c.x,      c.y-hy,   c.z     ], 0x33CC55, 'scale', 'y', -1],   // -Y
    [[c.x,      c.y,      c.z+hz  ], 0x4488FF, 'scale', 'z', +1],   // +Z
    [[c.x,      c.y,      c.z-hz  ], 0x4488FF, 'scale', 'z', -1],   // -Z
  ];

  // unit sphere(r=1) + scale — 줌 시 geometry 재생성 없이 scale만 변경
  const unitGeo=new THREE.SphereGeometry(1, 10, 10);
  defs.forEach(([pos, col, type, axis, sign])=>{
    const mat=new THREE.MeshBasicMaterial({color:col, depthTest:false});
    const mesh=new THREE.Mesh(unitGeo.clone(), mat);
    mesh.position.set(...pos);
    mesh.scale.setScalar(r);
    mesh.renderOrder=999;
    P2.gizmoGroup.add(mesh);
    P2.gizmoHandles.push({mesh, type, axis, sign});
  });
  P2.lastOrbR=P2.orb.radius;
}

function moveHandles(b){
  if(!b||P2.gizmoHandles.length===0) return;
  const {center:c, size:s}=b.data;
  const hx=s.x/2, hy=s.y/2, hz=s.z/2;
  const positions=[
    [c.x,    c.y,    c.z   ],
    [c.x+hx, c.y,    c.z   ], [c.x-hx, c.y,    c.z   ],
    [c.x,    c.y+hy, c.z   ], [c.x,    c.y-hy, c.z   ],
    [c.x,    c.y,    c.z+hz], [c.x,    c.y,    c.z-hz],
  ];
  P2.gizmoHandles.forEach((h,i)=>{ if(positions[i]) h.mesh.position.set(...positions[i]); });
}

function onGizmoMouseDown(e, canvas){
  if(!P2.threeReady||!P2.camera) return;
  P2.scene.updateMatrixWorld(true);
  const rect=canvas.getBoundingClientRect();
  const ndc=new THREE.Vector2(
    ((e.clientX-rect.left)/rect.width)*2-1,
    -((e.clientY-rect.top)/rect.height)*2+1
  );
  const ray=new THREE.Raycaster();
  ray.setFromCamera(ndc, P2.camera);

  // 핸들 먼저 피킹 — 각 핸들 구체를 Box3 구로 근사 (화면 크기에 맞게)
  if(P2.gizmoHandles.length>0){
    let bestH=null, bestD=Infinity;
    P2.gizmoHandles.forEach(h=>{
      // 핸들 위치에서 구 반지름으로 구성한 Box3
      const r=h.mesh.geometry.parameters?.radius ?? (P2.orb.radius*0.018);
      const wp=h.mesh.position.clone();
      const box3=new THREE.Box3(
        new THREE.Vector3(wp.x-r,wp.y-r,wp.z-r),
        new THREE.Vector3(wp.x+r,wp.y+r,wp.z+r)
      );
      const hit=new THREE.Vector3();
      if(ray.ray.intersectBox(box3, hit)){
        const d=P2.camera.position.distanceTo(hit);
        if(d<bestD){ bestD=d; bestH=h; }
      }
    });
    if(bestH){
      const b=P2.boxes.find(b=>b.id===P2.selectedId);
      if(b){
        const hitPos=bestH.mesh.position.clone();
        const camDir=new THREE.Vector3();
        P2.camera.getWorldDirection(camDir);
        const dragPlane=new THREE.Plane().setFromNormalAndCoplanarPoint(camDir.clone().negate(), hitPos);
        P2.activeDrag={
          type:bestH.type, axis:bestH.axis, sign:bestH.sign,
          dragPlane, startHit:hitPos.clone(),
          startData:JSON.parse(JSON.stringify(b.data))
        };
      }
      return;
    }
  }

  // 박스 선택 — Box3 AABB 방식 (대형 스케일 박스에서도 안정적)
  if(P2.boxes.length>0){
    let closest=null, closestD=Infinity;
    P2.boxes.forEach(b=>{
      const box3=new THREE.Box3().setFromObject(b.wire);
      const hit=new THREE.Vector3();
      if(ray.ray.intersectBox(box3, hit)){
        const d=P2.camera.position.distanceTo(hit);
        if(d<closestD){ closestD=d; closest=b; }
      }
    });
    if(closest){ selectBox(closest.id); return; }
  }
  deselectBox();
}

function onGizmoDrag(e){
  if(!P2.activeDrag||!P2.camera) return;
  const canvas=$('three-canvas');
  const rect=canvas.getBoundingClientRect();
  const ndc=new THREE.Vector2(
    ((e.clientX-rect.left)/rect.width)*2-1,
    -((e.clientY-rect.top)/rect.height)*2+1
  );
  const ray=new THREE.Raycaster();
  ray.setFromCamera(ndc, P2.camera);
  const curHit=new THREE.Vector3();
  if(!ray.ray.intersectPlane(P2.activeDrag.dragPlane, curHit)) return;

  const delta=new THREE.Vector3().subVectors(curHit, P2.activeDrag.startHit);
  const b=P2.boxes.find(b=>b.id===P2.selectedId);
  if(!b) return;
  const sd=P2.activeDrag.startData;

  if(P2.activeDrag.type==='move'){
    b.data.center.x=sd.center.x+delta.x;
    b.data.center.y=sd.center.y+delta.y;
    b.data.center.z=sd.center.z+delta.z;
  } else {
    const ax=P2.activeDrag.axis;
    const sign=P2.activeDrag.sign;
    const axVec=new THREE.Vector3(ax==='x'?1:0, ax==='y'?1:0, ax==='z'?1:0);
    const d=delta.dot(axVec)*sign;  // 해당 축 방향 성분만
    b.data.size[ax]=Math.max(0.05, sd.size[ax]+d);
    b.data.center[ax]=sd.center[ax]+delta.dot(axVec)*0.5;
  }
  updateBoxMesh(b);
  moveHandles(b);
}

function updateBoxMesh(b){
  b.wire.position.set(b.data.center.x, b.data.center.y, b.data.center.z);
  b.solid.position.set(b.data.center.x, b.data.center.y, b.data.center.z);
  b.wire.scale.set(b.data.size.x, b.data.size.y, b.data.size.z);
  b.solid.scale.set(b.data.size.x, b.data.size.y, b.data.size.z);
}

// ─── PLY 로드 (Page 2) ──────────────────────────────────────────────
P2.points = null;

P2.sessionId = null;  // 백엔드 세션 (메쉬 콜라이더 API 용)
P2.fileRef = null;  // 업로드 재시도용 File 참조

function loadP2PLY(file){
  P2.fileRef = file;       // 나중에 백엔드 업로드에 재사용
  P2.sessionId = null;
  const r=new FileReader();
  r.onload=e=>{
    try{
      // parsePLY는 ArrayBuffer 하나만 받고 {verts,normals,colors,hasNormals,hasColors,colorsLinear,formatNote} 반환
      const res=parsePLY(e.target.result);
      const cnt=res.verts.length/3|0;
      if(cnt<1) throw new Error('포인트 데이터가 없습니다');
      P2.points={verts:res.verts, colors:res.colors, count:cnt, colorsLinear:res.colorsLinear};
      buildP2Cloud();
      $('pts-badge').textContent=cnt.toLocaleString()+' pts';
      $('add-box-btn').disabled=false;
      const acb=$('auto-box-btn'); if(acb) acb.disabled=false;
      const amb=$('auto-mesh-btn'); if(amb) amb.disabled=false;
      // 백그라운드로 백엔드에도 업로드 (메쉬 콜라이더용 세션 준비)
      _p2EnsureSession().catch(()=>{/* 실패해도 박스 모드는 동작 */});
    } catch(err){
      appNotify('PLY 파싱 실패: '+err.message);
      console.error(err);
    }
  };
  r.readAsArrayBuffer(file);
}

async function _p2EnsureSession(){
  if(P2.sessionId) return P2.sessionId;
  if(!P2.fileRef) throw new Error('PLY 파일이 없습니다');
  const fd=new FormData();
  fd.append('file', P2.fileRef, P2.fileRef.name);
  const r=await fetch('/api/upload', {method:'POST', body:fd});
  if(!r.ok){
    let msg=`HTTP ${r.status}`;
    try{ const j=await r.json(); msg=j.detail||msg; }catch(_){}
    throw new Error('백엔드 업로드 실패: '+msg);
  }
  const d=await r.json();
  P2.sessionId=d.session_id||null;
  if(!P2.sessionId) throw new Error('세션 ID 수신 실패');
  return P2.sessionId;
}

const P2_MAX_PTS=500000;  // 뷰어 최대 표시 포인트 수

function autoPointSize(dispCnt, sizeVec){
  // 씬 볼륨 기반 자동 포인트 크기 계산
  const vol=Math.max(0.001, sizeVec.x * sizeVec.y * sizeVec.z);
  return Math.max(0.003, Math.min(0.06, Math.pow(vol/dispCnt, 1/3)*0.45));
}

function buildP2Cloud(){
  if(!P2.threeReady){ setTimeout(buildP2Cloud,200); return; }
  if(P2.pCloud){ P2.scene.remove(P2.pCloud); P2.pCloud.geometry.dispose(); P2.pCloud.material.dispose(); P2.pCloud=null; }

  const pts=P2.points;
  // ── 서브샘플링 (뷰어 부드러움 유지) ──────────────────
  const stride=Math.max(1, Math.ceil(pts.count/P2_MAX_PTS));
  const disp=Math.ceil(pts.count/stride);

  const posArr=new Float32Array(disp*3);
  for(let i=0,j=0; i<pts.count; i+=stride,j++){
    posArr[j*3]=pts.verts[i*3]; posArr[j*3+1]=pts.verts[i*3+1]; posArr[j*3+2]=pts.verts[i*3+2];
  }

  const geo=new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(posArr,3));

  // 바운딩 박스 먼저 계산
  const box3=new THREE.Box3().setFromBufferAttribute(geo.getAttribute('position'));
  const center=box3.getCenter(new THREE.Vector3());
  const size3=box3.getSize(new THREE.Vector3());
  const maxDim=Math.max(size3.x, size3.y, size3.z);
  const ptSize=autoPointSize(disp, size3);

  // ── 컬러 처리 ──────────────────────────────────────
  const hasCol=pts.colors && pts.colors.length>=pts.count*3;
  let mat;
  if(hasCol){
    const colArr=new Float32Array(disp*3);
    for(let i=0,j=0; i<pts.count; i+=stride,j++){
      colArr[j*3  ]=pts.colorsLinear?pts.colors[i*3  ]:sRGB2Lin(pts.colors[i*3  ]);
      colArr[j*3+1]=pts.colorsLinear?pts.colors[i*3+1]:sRGB2Lin(pts.colors[i*3+1]);
      colArr[j*3+2]=pts.colorsLinear?pts.colors[i*3+2]:sRGB2Lin(pts.colors[i*3+2]);
    }
    geo.setAttribute('color', new THREE.BufferAttribute(colArr,3));
    mat=new THREE.PointsMaterial({size:ptSize, vertexColors:true, sizeAttenuation:true});
  } else {
    mat=new THREE.PointsMaterial({size:ptSize, color:0x555555, sizeAttenuation:true});
  }

  P2.pCloud=new THREE.Points(geo,mat);
  P2.scene.add(P2.pCloud);

  // ── 카메라 near/far 씬 크기에 맞게 ──────────────────
  P2.camera.near=Math.max(0.001, maxDim*0.0001);
  P2.camera.far=maxDim*60;
  P2.camera.updateProjectionMatrix();

  // ── 그리드 씬 크기에 맞게 재생성 ────────────────────
  if(P2.grid){ P2.scene.remove(P2.grid); P2.grid.geometry.dispose(); P2.grid.material.dispose(); }
  const gs=Math.max(10, Math.ceil(maxDim*2.5/10)*10);
  P2.grid=new THREE.GridHelper(gs, Math.min(60,gs|0), 0xC8C8C8, 0xDDDDDD);
  P2.grid.position.y=box3.min.y;
  P2.scene.add(P2.grid);

  // ── 오빗 초기화 ──────────────────────────────────────
  P2.orb.cx=center.x; P2.orb.cy=center.y; P2.orb.cz=center.z;
  P2.orb.radius=maxDim*1.8; P2.orb.theta=-0.4; P2.orb.phi=1.15;
  P2.orb.dTheta=0; P2.orb.dPhi=0; P2.orb.dRadius=0;

  // ── 배지 업데이트 ────────────────────────────────────
  const badge=pts.count.toLocaleString()+' pts'+(stride>1?' (표시 1/'+stride+')':'');
  $('pts-badge').textContent=badge;

  markDirty();
  if(!P2.viewerRunning){ P2.viewerRunning=true; $('v-toggle').textContent='⏸ ON'; $('v-toggle').classList.remove('off'); renderLoop(); }
}

// ─── setP2Mode (legacy stub — AI mode removed) ─────────────────────
function setP2Mode(m){ /* no-op */ }

// ─── F키: 카메라 전체보기 (Page 2) ──────────────────────────────────
function fitP2Camera(){
  if(!P2.pCloud || !P2.camera) return;
  const box3=new THREE.Box3().setFromObject(P2.pCloud);
  const center=box3.getCenter(new THREE.Vector3());
  const sz=box3.getSize(new THREE.Vector3());
  const maxDim=Math.max(sz.x,sz.y,sz.z)||1;
  P2.orb.cx=center.x; P2.orb.cy=center.y; P2.orb.cz=center.z;
  P2.orb.radius=maxDim*1.8;
  P2.orb.dTheta=0; P2.orb.dPhi=0; P2.orb.dRadius=0;
  markDirty();
}

// ─── F키: 카메라 전체보기 (Page 3) ──────────────────────────────────
function fitP3Camera(){
  if(!P3.mesh || !P3.camera) return;
  const box3=new THREE.Box3().setFromObject(P3.mesh);
  const center=box3.getCenter(new THREE.Vector3());
  const sz=box3.getSize(new THREE.Vector3());
  const maxDim=Math.max(sz.x,sz.y,sz.z)||1;
  P3.orb.cx=center.x; P3.orb.cy=center.y; P3.orb.cz=center.z;
  P3.orb.radius=maxDim*1.8;
  P3.orb.dTheta=0; P3.orb.dPhi=0; P3.orb.dRadius=0;
  P3.dirty=true;
}

// ─── Z-up 보정 토글 (Page 2) ─────────────────────────────────────────
P2.zUp = false;
function applyP2ZUp(){
  const cb=$('p2-zup-toggle');
  P2.zUp = cb ? cb.checked : false;
  if(P2.pCloud){
    // Z-up: rotation.x = -π/2 → Y = -Z_old, Z = Y_old
    P2.pCloud.rotation.x = P2.zUp ? -Math.PI/2 : 0;
    // 그리드도 같이 업데이트
    if(P2.grid) P2.grid.rotation.x = P2.zUp ? -Math.PI/2 : 0;
    markDirty();
  }
}

// ─── 바운딩 박스 계산 공유 헬퍼 ─────────────────────────────────────
function _p2BBox(){
  if(!P2.points || !P2.points.verts) return null;
  const verts=P2.points.verts, n=P2.points.count;
  if(n<10) return null;
  let xMin=Infinity,xMax=-Infinity,yMin=Infinity,yMax=-Infinity,zMin=Infinity,zMax=-Infinity;
  for(let i=0;i<n;i++){
    let x=verts[i*3],y=verts[i*3+1],z=verts[i*3+2];
    if(P2.zUp){ const tmp=y; y=-z; z=tmp; }
    if(x<xMin)xMin=x; if(x>xMax)xMax=x;
    if(y<yMin)yMin=y; if(y>yMax)yMax=y;
    if(z<zMin)zMin=z; if(z>zMax)zMax=z;
  }
  return {xMin,xMax,yMin,yMax,zMin,zMax,
    W:xMax-xMin, H:yMax-yMin, D:zMax-zMin,
    cx:(xMin+xMax)/2, cy:(yMin+yMax)/2, cz:(zMin+zMax)/2};
}

// ─── Page 2 뷰어 로딩 오버레이 헬퍼 ─────────────────────────────────
// "응답 없음" 대신 "로딩중" 오버레이를 뷰어에 띄워서 사용자에게 진행 상태 표시
function p2ShowLoading(title, sub){
  const el = $('p2-viewer-loading');
  if(!el) return;
  if(title){ const t=$('p2-loading-title'); if(t) t.textContent = title; }
  if(sub !== undefined){ const s=$('p2-loading-sub'); if(s) s.textContent = sub; }
  el.classList.add('show');
  // 오버레이가 실제 페인트되도록 reflow 강제
  void el.offsetHeight;
}
function p2HideLoading(){
  const el = $('p2-viewer-loading');
  if(el) el.classList.remove('show');
}
// 브라우저에 페인트 기회를 주는 yield (sync 작업 전에 overlay 먼저 그려짐)
function _p2YieldPaint(){
  return new Promise(r => requestAnimationFrame(() => setTimeout(r, 0)));
}

// ─── 버튼1 : 박스 콜라이더 — 단일 오브젝트 AABB 1개 ─────────────────
// (Floor·Ceiling·벽 분리는 수동 드래그 배치로만 가능)
async function autoBoxCollider(){
  if(!P2.points || !P2.points.verts) { appNotify('PLY 파일을 먼저 로드하세요.'); return; }

  p2ShowLoading('⚡ 박스 콜라이더 생성 중...', 'AABB 계산 중');
  await _p2YieldPaint();

  try{
    const bb=_p2BBox();
    if(!bb){ appNotify('포인트가 너무 적습니다.'); return; }
    const {W, H, D, cx, cy, cz}=bb;

    clearBoxes();

    // 단일 오브젝트용 AABB 박스 1개 (그룹 없음 — 독립 박스)
    addBox({name:'BoxCollider', center:{x:cx, y:cy, z:cz}, size:{x:W, y:H, z:D}}, false);

    appNotify(`⚡ 박스 콜라이더 생성 (단일 AABB)\n${W.toFixed(2)} × ${H.toFixed(2)} × ${D.toFixed(2)} m`);
  } finally {
    p2HideLoading();
  }
}

// ─── 버튼2 : 메쉬 콜라이더 (Convex Hull) ────────────────────────────
// convexhull_local.js 는 THREE.js 가 완전히 로드된 뒤 동적으로 주입한다.
// (head 에 static script 로 넣으면 THREE 없이 IIFE 가 실행돼 window.THREE={} 로
//  오염시키고 뷰어 전체를 망가뜨림 — 이 방식으로 문제 해결)
P2.convexGeoLoaded = false;
P2.convexGeoLoading = false;

async function _loadConvexGeo(){
  if(window.THREE?.ConvexBufferGeometry){ P2.convexGeoLoaded=true; return true; }

  // 이미 로드 중이면 완료까지 폴링
  if(P2.convexGeoLoading){
    for(let i=0;i<50;i++){
      await new Promise(r=>setTimeout(r,100));
      if(window.THREE?.ConvexBufferGeometry){ P2.convexGeoLoaded=true; return true; }
    }
    return false;
  }

  // THREE.js 가 아직 없으면 먼저 로드 (initViewer 와 독립적 경로)
  if(!window.THREE || !window.THREE.BufferGeometry){
    try{ await loadThreeJS(); } catch(e){}
  }
  if(!window.THREE?.BufferGeometry) return false;  // THREE 없으면 포기

  P2.convexGeoLoading = true;
  return new Promise(resolve => {
    const s = document.createElement('script');
    s.src = 'convexhull_local.js';
    s.onload = () => {
      P2.convexGeoLoaded   = !!(window.THREE?.ConvexBufferGeometry);
      P2.convexGeoLoading  = false;
      resolve(P2.convexGeoLoaded);
    };
    s.onerror = () => { P2.convexGeoLoading = false; resolve(false); };
    document.head.appendChild(s);
  });
}

// 메쉬 콜라이더 오브젝트들 (박스 시스템과 별도)
P2.meshCols = [];

function _clearMeshCols(){
  while(P2.meshCols.length>0){
    const m=P2.meshCols.pop();
    if(P2.scene){ P2.scene.remove(m.wire); P2.scene.remove(m.solid); }
    m.wire.geometry.dispose(); m.wire.material.dispose();
    m.solid.geometry.dispose(); m.solid.material.dispose();
  }
}

// ─── 메쉬 콜라이더 — 백엔드 Poisson/BPA + ICP 스냅 기반 정밀 메쉬 ───
// 기존 "클러스터별 Convex Hull" 방식은 오목 영역을 전혀 못 따라갔음.
// 백엔드에서 실제 표면 메쉬를 생성 → 오목 영역까지 정합된 메쉬로 교체.
async function autoMeshCollider(){
  if(!P2.points || !P2.points.verts){ appNotify('PLY 파일을 먼저 로드하세요.'); return; }
  if(!P2.fileRef){ appNotify('PLY 파일 참조를 찾을 수 없습니다. 다시 로드하세요.'); return; }

  const btn = $('auto-mesh-btn');
  const origTxt = btn ? btn.innerHTML : '';
  if(btn){ btn.disabled = true; btn.innerHTML = '⏳ 생성 중...'; }

  // 뷰어에 "로딩중" 오버레이 — "응답 없음" 대신
  p2ShowLoading('🔶 메쉬 콜라이더 생성 중...', 'Poisson + ICP 정합 · 수 초 ~ 수십 초 소요');
  await _p2YieldPaint();

  try{
    // 1) 백엔드 세션 보장
    const updateSub = (msg) => {
      const s = $('p2-loading-sub'); if(s) s.textContent = msg;
    };
    updateSub('세션 준비 중...');
    const sid = await _p2EnsureSession();
    updateSub('백엔드에서 메쉬 재구성 중... (Poisson + ICP)');

    // 2) 옵션 결정 (UI 있으면 반영, 없으면 기본값)
    const method   = ($('mc-method')?.value)      || 'poisson';
    const depth    = +($('mc-depth')?.value || 8);
    const target   = +($('mc-target')?.value || 4000);
    const snap     = +($('mc-snap')?.value   || 3);
    const convex   = !!($('mc-convex')?.checked);
    const zup      = !!(P2.zUp);
    const prune    = parseFloat($('mc-prune')?.value || '4.0');
    const trimPct  = +($('mc-trim')?.value || 8);
    const keepFrag = !!($('mc-keep-frag')?.checked);
    const useIM    = !!($('mc-im')?.checked);
    const imFaces  = +($('mc-im-faces')?.value || 2000);

    // 3) API 호출
    const q = new URLSearchParams({
      method, depth, target_tris:target, snap,
      convex_parts: convex, zup_to_yup: zup,
      max_edge_ratio: prune.toFixed(2),
      density_trim:   (trimPct/100).toFixed(3),
      keep_fragments: keepFrag,
      instant_meshes: useIM,
      im_target_faces: imFaces,
      im_pure_quad: false,
    });
    const r = await fetch(`/api/mesh-collider/${sid}?${q}`);
    if(!r.ok){
      let msg=`HTTP ${r.status}`;
      try{ const j=await r.json(); msg=j.detail||msg; }catch(_){}
      throw new Error(msg);
    }
    updateSub('메쉬 데이터 수신 중...');
    const data = await r.json();
    if(!data.parts || !data.parts.length) throw new Error('빈 메쉬가 반환되었습니다');

    // 4) 렌더링
    updateSub('뷰어에 메쉬 업로드 중...');
    await _p2YieldPaint();
    if(!window.THREE){ await loadThreeJS(); }
    const THREE = window.THREE;
    // Undo 히스토리에 현재 상태 기록 (메쉬 생성 전) — 실행취소·초기화 지원
    p2Snapshot('autoMesh');
    P2.noHistory = true;
    _clearMeshCols();

    const palette = [0x00BFFF, 0xFF9944, 0x44CC88, 0xCC66CC, 0xFFDD44, 0x66AAFF];
    data.parts.forEach((p, idx)=>{
      const V = p.vertices, F = p.triangles;
      if(!V.length || !F.length) return;

      const posArr = new Float32Array(V.length*3);
      for(let i=0;i<V.length;i++){
        posArr[i*3  ]=V[i][0];
        posArr[i*3+1]=V[i][1];
        posArr[i*3+2]=V[i][2];
      }
      const idxArr = new Uint32Array(F.length*3);
      for(let i=0;i<F.length;i++){
        idxArr[i*3  ]=F[i][0];
        idxArr[i*3+1]=F[i][1];
        idxArr[i*3+2]=F[i][2];
      }
      const geo = new THREE.BufferGeometry();
      geo.setAttribute('position', new THREE.BufferAttribute(posArr,3));
      geo.setIndex(new THREE.BufferAttribute(idxArr,1));
      geo.computeVertexNormals();

      const col = palette[idx % palette.length];
      const wire = new THREE.Mesh(geo,
        new THREE.MeshBasicMaterial({color:col, wireframe:true, transparent:true, opacity:0.9}));
      const solid = new THREE.Mesh(geo.clone(),
        new THREE.MeshBasicMaterial({color:col, transparent:true, opacity:0.14,
                                     side:THREE.DoubleSide, depthWrite:false}));
      P2.scene.add(wire); P2.scene.add(solid);

      P2.meshCols.push({wire, solid, data:{
        name: data.parts.length>1
          ? `MeshCollider_${data.mode==='convex_parts'?'Part':'Seg'}_${idx+1}`
          : 'MeshCollider',
        // 익스포트용: 버텍스 + 삼각형 인덱스 둘 다 저장 → Unity에서 바로 Mesh 복원 가능
        vertices: V,
        triangles: F,
        mode: data.mode,
      }});
    });

    P2.noHistory = false;
    markDirty();
    updateColliderUI();

    const partCnt = P2.meshCols.length;
    const modeTxt = data.mode === 'convex_parts'
      ? `ACD ${partCnt}개 Convex 파트`
      : `단일 메쉬 (${partCnt}개 조각)`;
    appNotify(
      `🔶 메쉬 콜라이더 생성 완료 (${data.method.toUpperCase()})\n` +
      `${modeTxt}\n` +
      `버텍스: ${data.verts_total.toLocaleString()}  ·  삼각형: ${data.tris_total.toLocaleString()}\n` +
      `포인트 클라우드에 정합 · ICP 스냅 적용됨`
    );
  }catch(e){
    appNotify('메쉬 콜라이더 생성 실패\n'+(e.message||e));
    console.error(e);
  }finally{
    if(btn){ btn.disabled = false; btn.innerHTML = origTxt; }
    p2HideLoading();
  }
}

// ─── AI 기능 제거됨 ──────────────────────────────────────────────────
async function runAI(){
  appNotify('AI 콜라이더 기능은 제거되었습니다.');
}

// ─── 박스 콜라이더 관리 ─────────────────────────────────────────────
P2.boxes = [];
P2.boxIdSeed = 0;
P2.boxGroupSeed = 0;

// ─── Page 2 Undo/Redo 스택 (박스/메쉬 콜라이더 상태 스냅샷) ────────
P2.history = [];  // 과거 스냅샷 스택
P2.redo = [];  // 앞으로 가기 스택
P2.noHistory = false;  // 스냅샷 찍지 말아야 하는 경우 (undo/redo 복원 중)

function p2Snapshot(label){
  if(P2.noHistory) return;
  // 각 박스는 {data, id} 기록 — Three.js 객체 복원은 addBox로
  const boxesSnap = P2.boxes.map(b => ({
    id: b.id,
    data: JSON.parse(JSON.stringify(b.data)),
    fromAI: b.data.fromAI,
  }));
  const meshSnap = (P2.meshCols || []).map(m => ({
    data: JSON.parse(JSON.stringify(m.data)),
  }));
  P2.history.push({label: label||'', boxes: boxesSnap, mesh: meshSnap});
  if(P2.history.length > 50) P2.history.shift();  // 최대 50개
  P2.redo.length = 0;  // 새 액션 → redo 스택 비움
}

function p2Undo(){
  if(P2.history.length < 1){ return; }
  // 현재 상태를 redo 스택에 저장
  const cur = {
    boxes: P2.boxes.map(b => ({id: b.id, data: JSON.parse(JSON.stringify(b.data)), fromAI: b.data.fromAI})),
    mesh: (P2.meshCols || []).map(m => ({data: JSON.parse(JSON.stringify(m.data))})),
  };
  P2.redo.push(cur);
  const prev = P2.history.pop();
  _p2RestoreSnapshot(prev);
}

function p2Redo(){
  if(P2.redo.length < 1) return;
  const cur = {
    boxes: P2.boxes.map(b => ({id: b.id, data: JSON.parse(JSON.stringify(b.data)), fromAI: b.data.fromAI})),
    mesh: (P2.meshCols || []).map(m => ({data: JSON.parse(JSON.stringify(m.data))})),
  };
  P2.history.push(cur);
  const next = P2.redo.pop();
  _p2RestoreSnapshot(next);
}

function _p2RestoreSnapshot(snap){
  P2.noHistory = true;
  try{
    // 현재 박스 전부 제거
    while(P2.boxes.length > 0) removeBox(P2.boxes[0].id);
    // 메쉬 콜라이더도 제거
    if(typeof _clearMeshCols === 'function') _clearMeshCols();
    // 박스 복원
    for(const b of snap.boxes){
      addBox({
        name: b.data.name, center: b.data.center, size: b.data.size,
        groupId: b.data.groupId,
      }, b.fromAI);
    }
    // 메쉬 콜라이더 복원 — 저장된 vertices/triangles 로 Three.js geometry 재생성
    if(Array.isArray(snap.mesh) && snap.mesh.length > 0 && window.THREE && P2.scene){
      const THREE = window.THREE;
      const palette = [0x00BFFF, 0xFF9944, 0x44CC88, 0xCC66CC, 0xFFDD44, 0x66AAFF];
      snap.mesh.forEach((m, idx) => {
        const d = m.data || {};
        const V = d.vertices, F = d.triangles;
        if(!Array.isArray(V) || !Array.isArray(F) || !V.length || !F.length) return;
        const posArr = new Float32Array(V.length*3);
        for(let i=0;i<V.length;i++){
          posArr[i*3  ]=V[i][0]; posArr[i*3+1]=V[i][1]; posArr[i*3+2]=V[i][2];
        }
        const idxArr = new Uint32Array(F.length*3);
        for(let i=0;i<F.length;i++){
          idxArr[i*3  ]=F[i][0]; idxArr[i*3+1]=F[i][1]; idxArr[i*3+2]=F[i][2];
        }
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(posArr,3));
        geo.setIndex(new THREE.BufferAttribute(idxArr,1));
        geo.computeVertexNormals();
        const col = palette[idx % palette.length];
        const wire = new THREE.Mesh(geo,
          new THREE.MeshBasicMaterial({color:col, wireframe:true, transparent:true, opacity:0.9}));
        const solid = new THREE.Mesh(geo.clone(),
          new THREE.MeshBasicMaterial({color:col, transparent:true, opacity:0.14,
                                       side:THREE.DoubleSide, depthWrite:false}));
        P2.scene.add(wire); P2.scene.add(solid);
        P2.meshCols.push({wire, solid, data: JSON.parse(JSON.stringify(d))});
      });
    }
    updateColliderUI();
    markDirty();
  }finally{
    P2.noHistory = false;
  }
}

// 전역 Ctrl+Z / Ctrl+Y — Page 2 활성일 때만
window.addEventListener('keydown', e => {
  const p2 = document.getElementById('page2');
  if(!p2 || p2.style.display === 'none') return;
  if((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'z'){
    e.preventDefault(); p2Undo();
  } else if(((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'y') ||
            ((e.ctrlKey || e.metaKey) && e.shiftKey && e.key.toLowerCase() === 'z')){
    e.preventDefault(); p2Redo();
  }
});

function addBox(b, fromAI, _retry){
  _retry=_retry|0;
  if(!P2.threeReady){
    if(_retry<100) setTimeout(()=>addBox(b,fromAI,_retry+1),50);
    return;
  }
  // 스냅샷 (복원 중에는 스킵)
  p2Snapshot('addBox');
  const id=++P2.boxIdSeed;
  const cx=+(b.center?.x??0), cy=+(b.center?.y??0), cz=+(b.center?.z??0);
  const sx=Math.max(0.01,Math.abs(+(b.size?.x??1)));
  const sy=Math.max(0.01,Math.abs(+(b.size?.y??1)));
  const sz=Math.max(0.01,Math.abs(+(b.size?.z??1)));
  const name=b.name||`Box_${id}`;
  const groupId=b.groupId??null;  // null = 독립 박스, number = 그룹
  const col=groupId!=null ? 0xFF8C00 : (fromAI ? 0x0088FF : 0xFF6600);

  // BoxGeometry(1,1,1) + scale — gizmo 편집 시 geometry 재생성 불필요
  const geo=new THREE.BoxGeometry(1,1,1);

  const wire=new THREE.Mesh(geo, new THREE.MeshBasicMaterial({color:col, wireframe:true}));
  wire.position.set(cx,cy,cz);
  wire.scale.set(sx,sy,sz);

  const solid=new THREE.Mesh(geo.clone(), new THREE.MeshBasicMaterial({
    color:col, transparent:true, opacity:0.07, side:THREE.DoubleSide, depthWrite:false
  }));
  solid.position.set(cx,cy,cz);
  solid.scale.set(sx,sy,sz);

  P2.scene.add(wire);
  P2.scene.add(solid);
  P2.boxes.push({id, wire, solid, data:{name, center:{x:cx,y:cy,z:cz}, size:{x:sx,y:sy,z:sz}, fromAI, groupId}});
  updateColliderUI();
  markDirty();
}

function removeBox(id){
  if(P2.selectedId===id) deselectBox();  // 선택 해제 (내부에서 markDirty 호출)
  const idx=P2.boxes.findIndex(b=>b.id===id);
  if(idx<0) return;
  const b=P2.boxes[idx];
  P2.scene.remove(b.wire);  b.wire.geometry.dispose();  b.wire.material.dispose();
  P2.scene.remove(b.solid); b.solid.geometry.dispose(); b.solid.material.dispose();
  P2.boxes.splice(idx,1);
  updateColliderUI();
  markDirty();
}

function removeBoxGroup(gid){
  // 그룹 전체 삭제
  const ids=P2.boxes.filter(b=>b.data.groupId===gid).map(b=>b.id);
  ids.forEach(id=>removeBox(id));
}

// ─── 실행취소: 히스토리 스택이 있으면 p2Undo (박스+메쉬 복원), 없으면 최근 항목 pop ──
function undoBox(){
  // 히스토리가 있으면 정식 undo (박스/메쉬 모두 복원)
  if(P2.history && P2.history.length > 0){
    p2Undo();
    return;
  }
  // 폴백 — 히스토리가 비어도 최근 항목이라도 제거 (메쉬 > 박스 순)
  if(P2.meshCols && P2.meshCols.length > 0){
    removeMeshCol(P2.meshCols.length - 1);
    return;
  }
  if(P2.boxes.length>0) removeBox(P2.boxes[P2.boxes.length-1].id);
}

// ─── 초기화: 박스 + 메쉬 콜라이더 모두 제거 ─────────────────────────
function clearBoxes(){
  p2Snapshot('clearAll');
  P2.noHistory = true;
  try{
    while(P2.boxes.length>0) removeBox(P2.boxes[0].id);
    if(typeof _clearMeshCols === 'function') _clearMeshCols();
  }
  finally{
    P2.noHistory = false;
    updateColliderUI();
    markDirty();
  }
}

function updateColliderUI(){
  const totalCount=P2.boxes.length+(P2.meshCols?P2.meshCols.length:0);
  $('box-count').textContent=`(${totalCount}개)`;
  $('export-btn').disabled=totalCount===0;
  const upkgBtn=$('export-unitypkg-btn'); if(upkgBtn) upkgBtn.disabled = totalCount===0;
  if(P2.activeDrag) return;  // 드래그 중 DOM 재빌드 스킵 — mouseup 후 한 번 호출됨
  const list=$('col-list');
  if(P2.boxes.length===0 && (!P2.meshCols||P2.meshCols.length===0)){
    list.innerHTML='<div class="ai-hint">콜라이더가 없습니다</div>';
    return;
  }
  // ── 그룹 vs 독립 박스 분리 렌더링 ──────────────────────────────────
  const seenGid=new Set();
  const rows=[];
  P2.boxes.forEach(b=>{
    const gid=b.data.groupId;
    if(gid!=null){
      if(seenGid.has(gid)) return; // 그룹 대표는 첫 번째만
      seenGid.add(gid);
      const members=P2.boxes.filter(x=>x.data.groupId===gid);
      const label=b.data.groupName||'박스 콜라이더';
      rows.push(`
        <div style="display:flex;align-items:center;justify-content:space-between;
                    padding:7px 8px;border:1px solid #6A5020;border-radius:6px;
                    margin-bottom:5px;background:#2A2410;cursor:pointer"
             onclick="selectBox(${b.id})">
          <div>
            <span style="font-size:12px;font-weight:700;color:#FDE68A">📦 ${label}</span>
            <span style="font-size:11px;color:#B08040;margin-left:5px">⚡ 자동 (${members.length}개)</span>
            <div style="font-size:10px;color:#A07030;margin-top:1px">
              ${members.map(m=>m.data.name).join(' · ')}
            </div>
          </div>
          <button onclick="event.stopPropagation();removeBoxGroup(${gid})"
            style="background:none;border:none;color:#FCA5A5;cursor:pointer;font-size:15px;
                   padding:0 3px;line-height:1;flex-shrink:0">✕</button>
        </div>`);
    } else {
      rows.push(`
        <div style="display:flex;align-items:flex-start;justify-content:space-between;
                    padding:7px 8px;border:1px solid #333;border-radius:6px;
                    margin-bottom:5px;background:#1A1A1A;cursor:pointer"
             onclick="selectBox(${b.id})">
          <div>
            <span style="font-size:12px;font-weight:600;color:#E8E8E8">${b.data.name}</span>
            <span style="font-size:11px;color:#787878;margin-left:5px">${b.data.fromAI?'🤖 AI':'✏️ 수동'}</span>
            <div style="font-size:10px;color:#787878;font-family:Consolas,monospace;margin-top:2px">
              C(${b.data.center.x.toFixed(2)},${b.data.center.y.toFixed(2)},${b.data.center.z.toFixed(2)})
              &nbsp;S(${b.data.size.x.toFixed(2)},${b.data.size.y.toFixed(2)},${b.data.size.z.toFixed(2)})
            </div>
          </div>
          <button onclick="event.stopPropagation();removeBox(${b.id})"
            style="background:none;border:none;color:#FCA5A5;cursor:pointer;font-size:15px;
                   padding:0 3px;line-height:1;flex-shrink:0">✕</button>
        </div>`);
    }
  });

  // 메쉬 콜라이더 행 — 신·구 스키마 모두 지원
  if(P2.meshCols && P2.meshCols.length>0){
    P2.meshCols.forEach((m,i)=>{
      const d=m.data;
      let vCnt=0, fCnt=0, typeTxt='Convex Hull';
      if(Array.isArray(d.vertices)){
        vCnt=d.vertices.length;
        fCnt=Array.isArray(d.triangles)?d.triangles.length:0;
        typeTxt = d.mode==='convex_parts' ? 'Convex Part' : 'Mesh (Poisson/BPA)';
      } else if(Array.isArray(d.verts)){
        vCnt=d.verts.length; typeTxt='Convex Hull';
      }
      const detail = fCnt>0
        ? `${typeTxt} · ${vCnt.toLocaleString()} V · ${fCnt.toLocaleString()} F`
        : `${typeTxt} · ${vCnt.toLocaleString()} V`;
      rows.push(`
        <div style="display:flex;align-items:center;justify-content:space-between;
                    padding:7px 8px;border:1px solid #1E4FAA;border-radius:6px;
                    margin-bottom:5px;background:#101F33">
          <div>
            <span style="font-size:12px;font-weight:700;color:#93C5FD">🔶 ${d.name}</span>
            <div style="font-size:10px;color:#6D8FB8;margin-top:1px">
              ${detail}
            </div>
          </div>
          <button onclick="removeMeshCol(${i})"
            style="background:none;border:none;color:#FCA5A5;cursor:pointer;font-size:15px;
                   padding:0 3px;line-height:1;flex-shrink:0">✕</button>
        </div>`);
    });
  }

  list.innerHTML=rows.join('');
}

function removeMeshCol(i){
  if(!P2.meshCols || i>=P2.meshCols.length) return;
  const m=P2.meshCols[i];
  if(P2.scene){ P2.scene.remove(m.wire); P2.scene.remove(m.solid); }
  m.wire.geometry.dispose(); m.wire.material.dispose();
  m.solid.geometry.dispose(); m.solid.material.dispose();
  P2.meshCols.splice(i,1);
  updateColliderUI(); markDirty();
}

// ─── 수동 박스 배치 (드래그) ─────────────────────────────────────────
P2.placing = false;

function startBoxPlace(){
  if(!P2.points){ appNotify('PLY 파일을 먼저 로드해주세요.'); return; }
  P2.placing=true;
  $('add-box-btn').disabled=true;
  $('box-form').classList.add('show');
  $('three-canvas').style.cursor='crosshair';
}

function cancelBoxPlace(){
  P2.placing=false;
  P2.placeDrag.active=false;
  hideDragRect();
  $('add-box-btn').disabled=false;
  $('box-form').classList.remove('show');
  $('three-canvas').style.cursor='grab';
}

// ─── 공통: 콜라이더 JSON 페이로드 빌더 (JSON 내보내기·UnityPackage 공용) ──
function _buildCollidersPayload(){
  const boxList=P2.boxes.map(b=>({
    type:'box',
    name:b.data.name,
    groupId:b.data.groupId??null,
    center:{x:b.data.center.x, y:b.data.center.y, z:b.data.center.z},
    size:  {x:b.data.size.x,   y:b.data.size.y,   z:b.data.size.z},
    source:b.data.groupId!=null?'auto_box':(b.data.fromAI?'ai':'manual')
  }));
  const meshList=P2.meshCols.map(m=>{
    const d=m.data;
    if(Array.isArray(d.vertices) && Array.isArray(d.triangles)){
      return {
        type: d.mode==='convex_parts' ? 'convex_part' : 'mesh',
        name: d.name,
        vertexCount:  d.vertices.length,
        triangleCount:d.triangles.length,
        vertices:  d.vertices.map(v=>({x:+v[0].toFixed(4),y:+v[1].toFixed(4),z:+v[2].toFixed(4)})),
        triangles: d.triangles,
      };
    }
    const vs=(d.verts||[]).map(v=>({x:+v.x.toFixed(4),y:+v.y.toFixed(4),z:+v.z.toFixed(4)}));
    return {type:'convex_mesh', name:d.name, vertexCount:vs.length, vertices:vs};
  });
  const plyName = (P2.fileRef && P2.fileRef.name) ? P2.fileRef.name : 'scene.ply';
  const stem    = plyName.replace(/\.[^.]+$/, '') || 'scene';
  return {
    plyName, stem,
    payload: {
      version:'1.2',
      generated:new Date().toISOString(),
      plyFile: plyName,
      pointCount:P2.points?P2.points.count:0,
      colliders:[...boxList,...meshList]
    }
  };
}

// ─── Unity JSON 내보내기 ─────────────────────────────────────────────
// PyWebView: 폴더 선택 → PLY + collider JSON 직접 저장
// 브라우저:   JSON만 다운로드 (폴백)
async function exportColliders(){
  const {plyName, stem, payload} = _buildCollidersPayload();
  const jsonText = JSON.stringify(payload,null,2);
  const jsonName = `${stem}_colliders.json`;

  // 2) PyWebView 경로 — 폴더 선택 → PLY + JSON 직접 쓰기
  if(App.IS_PYWEBVIEW){
    const api = await getPyApi();
    if(api && api.pick_directory){
      const r = await api.pick_directory();
      if(!r || !r.ok){
        if(r && r.reason && r.reason !== 'cancelled') appNotify('폴더 선택 오류: '+r.reason);
        return;
      }
      const folder = r.path;
      const sep    = folder.includes('\\') ? '\\' : '/';
      const jsonPath = folder + sep + jsonName;
      let plyPath    = null;

      try{
        // JSON 저장
        const jr = await api.write_text_file(jsonPath, jsonText);
        if(!jr || !jr.ok) throw new Error((jr && jr.reason) || 'JSON 저장 실패');

        // PLY 저장 (원본 파일이 있을 때만)
        if(P2.fileRef && api.write_bytes_file){
          const buf = await P2.fileRef.arrayBuffer();
          // ArrayBuffer → Base64 (청크 단위로 인코딩 — 대용량 대비)
          const bytes = new Uint8Array(buf);
          let bin = '';
          const CHUNK = 0x8000;
          for(let i=0; i<bytes.length; i+=CHUNK){
            bin += String.fromCharCode.apply(null, bytes.subarray(i, i+CHUNK));
          }
          const b64 = btoa(bin);
          plyPath = folder + sep + plyName;
          const pr = await api.write_bytes_file(plyPath, b64);
          if(!pr || !pr.ok){
            plyPath = null;
            appNotify('⚠️ PLY 저장 실패 (JSON만 저장됨)\n' + ((pr && pr.reason) || ''));
          }
        }

        const files = [jsonName];
        if(plyPath) files.unshift(plyName);
        appNotify(`✅ 저장 완료\n${folder}\n\n${files.map(f=>'• '+f).join('\n')}`,
                  '내보내기 완료');
        if(api.reveal_in_explorer) api.reveal_in_explorer(jsonPath);
      }catch(e){
        appNotify('저장 실패: ' + (e.message||e));
      }
      return;
    }
  }

  // 3) 브라우저 폴백 — JSON만 다운로드
  const blob=new Blob([jsonText],{type:'application/json'});
  const a=document.createElement('a');
  a.style.display='none';
  document.body.appendChild(a);
  a.href=URL.createObjectURL(blob);
  a.download=jsonName;
  a.click();
  document.body.removeChild(a);
  setTimeout(()=>URL.revokeObjectURL(a.href),3000);
}

// ─── Unity Package (.unitypackage) 내보내기 ─────────────────────────
// 백엔드가 PLY + JSON + C# 스크립트 + 프리팹을 묶어 tar.gz (.unitypackage) 생성
async function exportUnityPackage(){
  if(!P2.fileRef){
    appNotify('원본 PLY 파일이 필요합니다. Page 2에서 PLY를 다시 로드하세요.');
    return;
  }
  const {plyName, stem, payload} = _buildCollidersPayload();
  if(!payload.colliders.length){
    appNotify('콜라이더가 없습니다. 먼저 생성하세요.');
    return;
  }

  const btn = $('export-unitypkg-btn');
  const orig = btn ? btn.textContent : '';
  if(btn){ btn.disabled = true; btn.textContent = '⏳ 패키지 생성 중...'; }

  try{
    // 1) 백엔드에 PLY + JSON 전송 → .unitypackage 바이트 수신
    const fd = new FormData();
    fd.append('ply', P2.fileRef, plyName);
    fd.append('colliders_json', JSON.stringify(payload));
    const r = await fetch('/api/unitypackage', {method:'POST', body:fd});
    if(!r.ok){
      let msg = `HTTP ${r.status}`;
      try{ const j = await r.json(); msg = j.detail || msg; }catch(_){}
      throw new Error(msg);
    }
    const pkgBlob = await r.blob();
    const pkgName = `${stem}_colliders.unitypackage`;

    // 2) 저장 — PyWebView 우선 (네이티브 다이얼로그), 없으면 브라우저 다운로드
    if(App.IS_PYWEBVIEW){
      const api = await getPyApi();
      if(api && api.save_bytes_dialog){
        const b64 = await blobToBase64(pkgBlob);
        const res = await api.save_bytes_dialog(pkgName, b64);
        if(res && res.ok){
          appNotify(`✅ Unity Package 저장 완료\n${res.path}\n\nUnity 프로젝트에 드래그하면 자동 임포트됩니다.`,
                    '내보내기 완료');
          if(api.reveal_in_explorer) api.reveal_in_explorer(res.path);
        } else if(res && res.reason && res.reason !== 'cancelled'){
          appNotify('저장 실패: ' + res.reason);
        }
        return;
      }
    }
    // 브라우저 폴백
    const a = document.createElement('a');
    a.style.display = 'none';
    document.body.appendChild(a);
    a.href = URL.createObjectURL(pkgBlob);
    a.download = pkgName;
    a.click();
    document.body.removeChild(a);
    setTimeout(()=>URL.revokeObjectURL(a.href),3000);
  }catch(e){
    appNotify('Unity Package 생성 실패\n'+(e.message||e));
    console.error(e);
  }finally{
    if(btn){ btn.disabled = false; btn.textContent = orig; }
  }
}

// ─── Page 2 파일 드롭/선택 ──────────────────────────────────────────
(function initP2Drop(){
  const drop=$('p2-drop');
  const finput=$('p2-finput');
  const fname=$('p2-fname');

  drop.addEventListener('click',()=>finput.click());

  finput.addEventListener('change',e=>{
    const f=e.target.files[0];
    if(!f) return;
    if(!_checkBrowserUploadSize(f)) return;
    fname.textContent=f.name;
    if(!P2.threeReady){ initViewer(); setTimeout(()=>loadP2PLY(f),1400); }
    else loadP2PLY(f);
  });

  drop.addEventListener('dragover',e=>{ e.preventDefault(); drop.classList.add('drag'); });
  drop.addEventListener('dragleave',()=>drop.classList.remove('drag'));
  drop.addEventListener('drop',e=>{
    e.preventDefault(); drop.classList.remove('drag');
    const f=e.dataTransfer.files[0];
    if(!f||!f.name.toLowerCase().endsWith('.ply')){
      appNotify('.ply 파일만 지원합니다'); return;
    }
    if(!_checkBrowserUploadSize(f)) return;
    fname.textContent=f.name;
    if(!P2.threeReady){ initViewer(); setTimeout(()=>loadP2PLY(f),1400); }
    else loadP2PLY(f);
  });
})();

// ═══════════════════════════════════════════════════════════════════════
//  SCENE GIZMO (View Gizmo) — Pages 2 & 3
// ═══════════════════════════════════════════════════════════════════════

class SceneGizmo {
  constructor(canvasId, getOrb, snapCam){
    this.cv=document.getElementById(canvasId);
    if(!this.cv) return;
    this.ctx=this.cv.getContext('2d');
    this.getOrb=getOrb;     // () => {theta, phi}
    this.snapCam=snapCam;   // (theta, phi) => void
    this._hover=null;
    this._size=90;
    const dpr=window.devicePixelRatio||1;
    this.cv.width=this._size*dpr;
    this.cv.height=this._size*dpr;
    this._dpr=dpr;
    this._setupEvents();
  }

  _projectAxis(ax){
    // Project unit vector ax=[x,y,z] onto screen using current camera orientation
    // Camera: position = (sin(phi)*sin(theta), cos(phi), sin(phi)*cos(theta)) * radius
    // Screen right  = (cos(theta), 0, -sin(theta))
    // Screen up     = (-sin(theta)*cos(phi), sin(phi), -cos(theta)*cos(phi))
    const {theta, phi} = this.getOrb();
    const sx = ax[0]*Math.cos(theta)         + ax[2]*(-Math.sin(theta));
    const sy = ax[0]*(-Math.sin(theta)*Math.cos(phi)) + ax[1]*Math.sin(phi) + ax[2]*(-Math.cos(theta)*Math.cos(phi));
    // Depth (positive = toward viewer)
    const depth = ax[0]*Math.sin(phi)*Math.sin(theta) + ax[1]*Math.cos(phi) + ax[2]*Math.sin(phi)*Math.cos(theta);
    return {sx, sy, depth};
  }

  draw(){
    if(!this.cv||!this.ctx) return;
    const ctx=this.ctx;
    const dpr=this._dpr;
    const S=this._size;
    ctx.clearRect(0,0,S*dpr,S*dpr);

    ctx.save();
    ctx.scale(dpr,dpr);

    const cx=S/2, cy=S/2;
    const R=S*0.42; // circle radius
    const L=S*0.33; // axis arm length

    // Background circle
    ctx.beginPath();
    ctx.arc(cx,cy,R,0,Math.PI*2);
    ctx.fillStyle='rgba(245,245,245,0.90)';
    ctx.fill();
    ctx.strokeStyle='#C8C8C8';
    ctx.lineWidth=1;
    ctx.stroke();

    const AXES=[
      {v:[1,0,0],  neg:[-1,0,0],  label:'X', nLabel:'-X', col:'#E03030', negCol:'rgba(180,60,60,0.38)'},
      {v:[0,1,0],  neg:[0,-1,0],  label:'Y', nLabel:'-Y', col:'#30A830', negCol:'rgba(60,150,60,0.38)'},
      {v:[0,0,1],  neg:[0,0,-1],  label:'Z', nLabel:'-Z', col:'#3060D0', negCol:'rgba(60,100,200,0.38)'},
    ];

    // Project all axes
    const projected=AXES.map(a=>{
      const p=this._projectAxis(a.v);
      const n=this._projectAxis(a.neg);
      return {...a, px:cx+p.sx*L, py:cy-p.sy*L, nx:cx+n.sx*L, ny:cy-n.sy*L, depth:p.depth};
    });

    // Draw negative arms first (behind)
    const back=[...projected].sort((a,b)=>b.depth-a.depth);
    back.forEach(a=>{
      ctx.beginPath();
      ctx.moveTo(cx,cy);
      ctx.lineTo(a.nx,a.ny);
      ctx.strokeStyle=a.negCol;
      ctx.lineWidth=2;
      ctx.setLineDash([3,2]);
      ctx.stroke();
      ctx.setLineDash([]);
      // Neg label dot
      const isHov=(this._hover===a.nLabel);
      ctx.beginPath();
      ctx.arc(a.nx,a.ny,isHov?7:5,0,Math.PI*2);
      ctx.fillStyle=isHov?a.col:a.negCol;
      ctx.fill();
    });

    // Draw positive arms front-to-back
    const front=[...projected].sort((a,b)=>b.depth-a.depth);
    front.reverse();
    front.forEach(a=>{
      // Arm line
      ctx.beginPath();
      ctx.moveTo(cx,cy);
      ctx.lineTo(a.px,a.py);
      ctx.strokeStyle=a.col;
      ctx.lineWidth=a.depth>0?3:2;
      ctx.stroke();

      // Tip circle
      const isHov=(this._hover===a.label);
      const tr=isHov?10:8;
      ctx.beginPath();
      ctx.arc(a.px,a.py,tr,0,Math.PI*2);
      ctx.fillStyle=isHov?'#FFFFFF':a.col;
      ctx.strokeStyle=a.col;
      ctx.lineWidth=isHov?2.5:0;
      ctx.fill();
      if(isHov) ctx.stroke();

      // Label
      ctx.fillStyle=isHov?a.col:'#FFFFFF';
      ctx.font=`bold ${8}px Segoe UI,Arial,sans-serif`;
      ctx.textAlign='center';
      ctx.textBaseline='middle';
      ctx.fillText(a.label, a.px, a.py);
    });

    ctx.restore();
  }

  _hitTest(mx,my){
    const S=this._size, cx=S/2, cy=S/2, L=S*0.33;
    const AXES=[
      {v:[1,0,0],label:'X'},{v:[-1,0,0],label:'-X'},
      {v:[0,1,0],label:'Y'},{v:[0,-1,0],label:'-Y'},
      {v:[0,0,1],label:'Z'},{v:[0,0,-1],label:'-Z'},
    ];
    for(const a of AXES){
      const p=this._projectAxis(a.v);
      const tx=cx+p.sx*L, ty=cy-p.sy*L;
      if(Math.hypot(mx-tx,my-ty)<10) return a.label;
    }
    return null;
  }

  _setupEvents(){
    const cv=this.cv;
    cv.addEventListener('mousemove',e=>{
      const r=cv.getBoundingClientRect();
      const hit=this._hitTest(e.clientX-r.left, e.clientY-r.top);
      if(hit!==this._hover){
        this._hover=hit;
        cv.style.cursor=hit?'pointer':'default';
        this.draw();
      }
    });
    cv.addEventListener('mouseleave',()=>{
      if(this._hover){this._hover=null; this.draw();}
    });
    cv.addEventListener('click',e=>{
      const r=cv.getBoundingClientRect();
      const hit=this._hitTest(e.clientX-r.left, e.clientY-r.top);
      if(!hit||!this.snapCam) return;
      // Snap camera view
      const snapMap={
        'X':  {theta: Math.PI*0.5,  phi: Math.PI*0.5},
        '-X': {theta: -Math.PI*0.5, phi: Math.PI*0.5},
        'Y':  {theta: 0,            phi: 0.01},
        '-Y': {theta: 0,            phi: Math.PI-0.01},
        'Z':  {theta: 0,            phi: Math.PI*0.5},
        '-Z': {theta: Math.PI,      phi: Math.PI*0.5},
      };
      const s=snapMap[hit];
      if(s) this.snapCam(s.theta, s.phi);
    });
  }
}

// ═══════════════════════════════════════════════════════════════════════
//  PAGE 2 — Scene Gizmo init (after Three.js is ready)
// ═══════════════════════════════════════════════════════════════════════
P2.sceneGizmo = null;

function initP2Gizmo(){
  if(P2.sceneGizmo) return;
  P2.sceneGizmo=new SceneGizmo('p2-gizmo',
    ()=>({theta:P2.orb.theta, phi:P2.orb.phi}),
    (theta,phi)=>{
      // Animate snap via dTheta/dPhi impulse
      const dt=((theta-P2.orb.theta + Math.PI*3) % (Math.PI*2)) - Math.PI;
      const dp=phi-P2.orb.phi;
      P2.orb.dTheta+=dt*0.4;
      P2.orb.dPhi  +=dp*0.4;
      markDirty();
    }
  );
  P2.sceneGizmo.draw();
}


// ═══════════════════════════════════════════════════════════════════════
