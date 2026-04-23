// ═══════════════════════════════════════════════════════════════════════
//  Page 3 (Pipeline / Mesh Generation) 로컬 state
// ═══════════════════════════════════════════════════════════════════════
const P3 = {
  // Three.js
  threeReady: false, viewerRunning: false, dirty: true,
  renderer: null, scene: null, camera: null, raf: null,
  mesh: null, wire: null, wireOn: false,
  pCloud: null, mirrorPCloud: null,
  axisLines: null, grid: null, lights: null, gizmo: null,
  orb: {theta:-0.4, phi:1.15, radius:5, cx:0, cy:0, cz:0,
        dTheta:0, dPhi:0, dRadius:0, pdx:0, pdy:0,
        dragging:false, btn:-1, lx:0, ly:0},
  // Session / data
  final: null, pts: null, loadedFileName: null,
  hasNormals: false, sessionId: null, sse: null, colorMode: false,
};

//  PAGE 3 — STATE  (Python Backend 연동)
// ═══════════════════════════════════════════════════════════════════════
P3.threeReady = false;
P3.renderer = null;
P3.scene = null;
P3.camera = null;
P3.raf = null;
P3.viewerRunning = false;
P3.dirty = true;
P3.mesh = null;
P3.wire = null;
P3.wireOn = false;
P3.pCloud = null;  // PLY 로드 시 포인트 클라우드 프리뷰
P3.mirrorPCloud = null;  // 미러 ON 시 Y축 대칭 포인트 클라우드 프리뷰
P3.axisLines = null;  // X/Y/Z 축선 Group
P3.grid = null;
P3.lights = null;
P3.gizmo = null;
P3.orb = {theta:-0.4,phi:1.15,radius:5,cx:0,cy:0,cz:0,dTheta:0,dPhi:0,dRadius:0,pdx:0,pdy:0,dragging:false,btn:-1,lx:0,ly:0};

P3.final = null;  // {verts:Float32Array, indices:Uint32Array}
P3.pts = null;  // 업로드 후 메타 (확장용)
P3.loadedFileName = null;  // CLI 명령어 표시용 (원본 파일명)
P3.hasNormals = false;  // 업로드 응답 has_normals (PLY 법선)
P3.sessionId = null;  // Python backend session ID
P3.sse = null;  // EventSource for SSE
const P3_BACKEND = '/api';

/** 로컬 엔진에 포인트클라우드 업로드 → session_id 저장 */
async function loadP3File(file){
  const fd=new FormData();
  fd.append('file', file, file.name);
  const r=await fetch(`${P3_BACKEND}/upload`, { method:'POST', body:fd });
  const text=await r.text();
  let data=null;
  try{ data=JSON.parse(text); }catch(_){ /* ignore */ }
  if(!r.ok){
    let msg=`HTTP ${r.status}`;
    if(data&&data.detail!=null){
      const d=data.detail;
      msg=typeof d==='string'?d:(Array.isArray(d)?(d[0]&&d[0].msg)||JSON.stringify(d[0]):String(d));
    }else msg=text||msg;
    throw new Error(msg);
  }
  if(!data) throw new Error('응답이 올바른 JSON이 아닙니다');
  P3.sessionId=data.session_id||null;
  if(!P3.sessionId) throw new Error('세션 ID를 받지 못했습니다');
  P3.hasNormals=!!data.has_normals;
  return { count:+data.point_count||0, session_id:P3.sessionId };
}

// ─── 3페이지 프리셋 ──────────────────────────────────────────────────
// IM target ≈ 실제 결과 V × 1/4. 각 LOD의 "최대 V" 상한을 넘지 않도록 실측 튜닝.
const P3_PRESETS = {
  // LOD1 = 완전 로우 — 최대 V 5,000 (실측: im_faces=1200 → V=4,573)
  fast:     { algo:'poisson', denoise:true, sigma:'2', mc_res:'30', smooth:true,  smooth_iter:'1', icp:false, icp_iter:'0', poisson_depth:'7',
              smooth_normals:true, merge_verts:true, orient_normals:true, uniform_remesh:false,
              smooth_type:'taubin', prune:true,  prune_ratio:'4.0', remove_frag:true, target_tris:'0',
              voxel_remesh:false, voxel_res:'40',
              im:true, im_faces:'1200', im_pure_quad:true, im_crease:'0',
              fake_hole_fill:true, fake_hole_size:'15',
              color_groups_on:true, color_groups_k:'4' },
  // LOD2 = 중간 — 최대 V 15,000 (실측: im_faces=3700 → V=14,792)
  balanced: { algo:'poisson', denoise:true, sigma:'2', mc_res:'40', smooth:true,  smooth_iter:'2', icp:false, icp_iter:'0', poisson_depth:'7',
              smooth_normals:true, merge_verts:true, orient_normals:true, uniform_remesh:false,
              smooth_type:'taubin', prune:true,  prune_ratio:'4.0', remove_frag:true, target_tris:'0',
              voxel_remesh:false, voxel_res:'60',
              im:true, im_faces:'3700', im_pure_quad:true, im_crease:'0',
              fake_hole_fill:true, fake_hole_size:'15',
              color_groups_on:true, color_groups_k:'6' },
  // LOD3 = 고품질 (권장) — 최대 V 25,000 (실측: im_faces=6200 → V=24,378) + ICP + crease 30°
  quality:  { algo:'poisson', denoise:true, sigma:'2', mc_res:'50', smooth:true,  smooth_iter:'2', icp:true,  icp_iter:'3', poisson_depth:'8',
              smooth_normals:true, merge_verts:true, orient_normals:true, uniform_remesh:false,
              smooth_type:'taubin', prune:true,  prune_ratio:'4.0', remove_frag:true, target_tris:'0',
              voxel_remesh:false, voxel_res:'60',
              im:true, im_faces:'6200', im_pure_quad:true, im_crease:'30',
              fake_hole_fill:true, fake_hole_size:'15',
              color_groups_on:true, color_groups_k:'8' },
};

function _setRadio(id){ const el=$(id); if(el){ el.checked=true; } }

function p3SetPreset(name){
  const p = P3_PRESETS[name]; if(!p) return;
  // 카드 active 토글
  ['fast','balanced','quality'].forEach(n=>{
    const el=$('preset-'+n);
    if(el) el.classList.toggle('active', n===name);
  });
  // 알고리즘 라디오
  _setRadio('p3-algo-'+p.algo);
  const dn=$('p3-denoise'); if(dn) dn.checked=p.denoise;
  const dk=$('p3-denoise-k'); if(dk){ dk.value=p.sigma; const vv=$('p3-denoise-k-val'); if(vv) vv.textContent=p.sigma+'.0σ'; }
  const mr=$('p3-mc-res'); if(mr){ mr.value=p.mc_res; const vv=$('p3-mc-res-val'); if(vv) vv.textContent=p.mc_res; }
  const pd=$('p3-poisson-depth'); if(pd){ pd.value=p.poisson_depth; const vv=$('p3-poisson-depth-val'); if(vv) vv.textContent=p.poisson_depth; }
  const sm=$('p3-smooth'); if(sm) sm.checked=p.smooth;
  const si=$('p3-smooth-iter'); if(si){ si.value=p.smooth_iter; const vv=$('p3-smooth-iter-val'); if(vv) vv.textContent=p.smooth_iter; }
  const icp=$('p3-icp'); if(icp) icp.checked=!!p.icp;
  const ii=$('p3-icp-iter'); if(ii && p.icp_iter){ ii.value=p.icp_iter; const vv=$('p3-icp-iter-val'); if(vv) vv.textContent=p.icp_iter; }
  // 메쉬 품질 토글 (프리셋에 정의돼있으면 반영)
  const sn=$('p3-smooth-normals'); if(sn && 'smooth_normals' in p) sn.checked=!!p.smooth_normals;
  const mv=$('p3-merge-verts');    if(mv && 'merge_verts'    in p) mv.checked=!!p.merge_verts;
  const on=$('p3-orient-normals'); if(on && 'orient_normals' in p) on.checked=!!p.orient_normals;
  const ur=$('p3-uniform-remesh'); if(ur && 'uniform_remesh' in p) ur.checked=!!p.uniform_remesh;
  // 스무딩 타입 라디오
  if('smooth_type' in p){
    const rb = p.smooth_type==='laplacian' ? $('p3-smooth-laplacian') : $('p3-smooth-taubin');
    if(rb) rb.checked = true;
  }
  // 프루닝/파편 제거
  const pr=$('p3-prune'); if(pr && 'prune' in p) pr.checked=!!p.prune;
  const prRow=$('p3-prune-row'); if(prRow) prRow.style.display=(p.prune===false?'none':'flex');
  const prR=$('p3-prune-ratio'); if(prR && 'prune_ratio' in p){ prR.value=p.prune_ratio; const vv=$('p3-prune-ratio-val'); if(vv) vv.textContent=parseFloat(p.prune_ratio).toFixed(1)+'×'; }
  const rf=$('p3-remove-frag'); if(rf && 'remove_frag' in p) rf.checked=!!p.remove_frag;
  // 목표 삼각형 수
  const tt=$('p3-target-tris'); if(tt && 'target_tris' in p){
    tt.value=p.target_tris;
    const vv=$('p3-target-tris-val');
    if(vv){ const n=+p.target_tris; vv.textContent = n===0 ? '없음' : (n>=1000 ? (n/1000).toFixed(0)+'K' : String(n)); }
  }
  // 축-정렬 복셀 리메시
  const vr=$('p3-voxel-remesh'); if(vr && 'voxel_remesh' in p) vr.checked=!!p.voxel_remesh;
  const vrRow=$('p3-voxel-res-row'); if(vrRow) vrRow.style.display=(p.voxel_remesh?'flex':'none');
  const vrR=$('p3-voxel-res'); if(vrR && 'voxel_res' in p){ vrR.value=p.voxel_res; const vv=$('p3-voxel-res-val'); if(vv) vv.textContent=p.voxel_res; }
  // Instant Meshes
  const imCb=$('p3-instant-meshes'); if(imCb && 'im' in p) imCb.checked=!!p.im;
  const imOpts=$('p3-im-opts'); if(imOpts) imOpts.style.display=(p.im?'block':'none');
  const imF=$('p3-im-faces'); if(imF && 'im_faces' in p){ imF.value=p.im_faces; const vv=$('p3-im-faces-val'); if(vv){ const n=+p.im_faces; vv.textContent = n>=1000?(n/1000).toFixed(0)+'K':String(n); } }
  const imQ=$('p3-im-pure-quad'); if(imQ && 'im_pure_quad' in p) imQ.checked=!!p.im_pure_quad;
  const imC=$('p3-im-crease'); if(imC && 'im_crease' in p){ imC.value=p.im_crease; const vv=$('p3-im-crease-val'); if(vv){ const n=+p.im_crease; vv.textContent = n===0?'끔':n+'°'; } }
  // 공갈 구멍 메우기
  const hf=$('p3-fake-hole-fill'); if(hf && 'fake_hole_fill' in p) hf.checked=!!p.fake_hole_fill;
  const hfR=$('p3-fake-hole-size'); if(hfR && 'fake_hole_size' in p){ hfR.value=p.fake_hole_size; const vv=$('p3-fake-hole-size-val'); if(vv) vv.textContent=p.fake_hole_size+'%'; }
  // 색상 그룹 쉐이더
  const cg=$('p3-color-groups-on'); if(cg && 'color_groups_on' in p) cg.checked=!!p.color_groups_on;
  const cgK=$('p3-color-groups-k'); if(cgK && 'color_groups_k' in p){ cgK.value=p.color_groups_k; const vv=$('p3-color-groups-k-val'); if(vv) vv.textContent=p.color_groups_k; }
  const cgRow=$('p3-color-groups-row'); if(cgRow) cgRow.style.display=(p.color_groups_on===false?'none':'flex');
  // row 숨김 처리
  const sr=$('p3-smooth-row'); if(sr) sr.style.display=p.smooth?'':'none';
  const dr=$('p3-denoise-row'); if(dr) dr.style.display=p.denoise?'':'none';
  const ir=$('p3-icp-row'); if(ir) ir.style.display=p.icp?'':'none';
  p3OnAlgoChange();
}

/** 터미널용 process.py 명령 (앱 밖에서 CLI만 쓸 때) */
function p3BuildCliCmd(){
  const name=P3.loadedFileName||'input.ply';
  const safe=String(name).replace(/"/g,'');
  const dn=$('p3-denoise')?.checked??true;
  const sigma=$('p3-denoise-k')?.value??'2';
  const mc=$('p3-mc-res')?.value??'50';
  const sm=$('p3-smooth')?.checked??true;
  const it=$('p3-smooth-iter')?.value??'2';
  let cmd='cd PointCloudOptimizer && python process.py "'+safe+'"';
  if(!dn) cmd+=' --no-denoise';
  else cmd+=' --sigma '+sigma;
  cmd+=' --mc-res '+mc;
  if(!sm) cmd+=' --no-smooth';
  else cmd+=' --iter '+it;
  if($('p3-algo-bpa')?.checked){
    cmd+='\n# BPA(Ball-Pivoting)는 위 앱에서 ▶ 메쉬 변환 실행으로만 지원. CLI는 Marching Cubes 기준.';
  }
  return cmd;
}

function _p3CurrentAlgo(){
  if($('p3-algo-bpa')?.checked)     return 'bpa';
  if($('p3-algo-poisson')?.checked) return 'poisson';
  if($('p3-algo-sdf')?.checked)     return 'sdf';
  return 'mc';
}

// ─── 표면 모드 (스무스 | 하드) — 택1 필수 ──────────────────────────────
function _p3SurfaceMode(){
  if($('p3-mode-hard')?.checked)   return 'hard';
  if($('p3-mode-smooth')?.checked) return 'smooth';
  return null;  // 미선택 → 실행 불가
}

function p3OnSurfaceModeChange(mode){
  const s = $('p3-mode-smooth'), h = $('p3-mode-hard');
  const sL = $('p3-mode-smooth-lbl'), hL = $('p3-mode-hard-lbl');
  if(!s || !h) return;
  if(mode === 'smooth'){
    // 토글 — 이미 켜져있으면 끄기 (초기 "둘 다 OFF" 상태로 복귀 가능)
    s.checked = !s.checked;
    if(s.checked){ h.checked = false; }
  } else {
    h.checked = !h.checked;
    if(h.checked){ s.checked = false; }
  }
  if(sL) sL.classList.toggle('active', s.checked);
  if(hL) hL.classList.toggle('active', h.checked);
  _p3UpdateRunBtnEnabled();
}

function _p3UpdateRunBtnEnabled(){
  const run = $('p3-run-btn');
  const warn = $('p3-mode-warn');
  const hasMode = !!_p3SurfaceMode();
  const hasSession = !!P3.sessionId;
  if(run) run.disabled = !(hasMode && hasSession);
  if(warn) warn.style.display = hasMode ? 'none' : 'block';
}

function p3OnAlgoChange(){
  const algo = _p3CurrentAlgo();
  const mp=$('p3-mc-panel'),
        bp=$('p3-bpa-panel'),
        pp=$('p3-poisson-panel'),
        bHint=$('p3-bpa-hint'),
        pHint=$('p3-poisson-hint');
  // MC·SDF: grid_res 사용
  if(mp) mp.style.display=(algo==='mc'||algo==='sdf')?'block':'none';
  if(bp) bp.style.display=(algo==='bpa')?'block':'none';
  if(pp) pp.style.display=(algo==='poisson')?'block':'none';
  if(bHint) bHint.style.display=(algo==='bpa')?'block':'none';
  if(pHint) pHint.style.display=(algo==='poisson')?'block':'none';
  p3RefreshCmdBox();
}

function p3RefreshCmdBox(){
  const box=$('p3-cmd-box');
  if(!box) return;
  if(!P3.loadedFileName){
    box.textContent='파일을 먼저 로드하세요 (위 드롭존)';
    return;
  }
  box.textContent=p3BuildCliCmd();
}

function p3CopyCmd(){
  const box=$('p3-cmd-box');
  const t=(box&&box.textContent)?box.textContent.trim():'';
  if(!t||t.indexOf('먼저')>=0||t.indexOf('로드')>=0){
    appNotify('복사할 명령이 없습니다. 포인트 파일을 먼저 로드하세요.', '복사');
    return;
  }
  const done=()=>{
    const b=$('p3-copy-btn');
    if(b){ const o=b.textContent; b.textContent='복사됨'; setTimeout(()=>{b.textContent=o;},1200); }
  };
  if(navigator.clipboard&&navigator.clipboard.writeText){
    navigator.clipboard.writeText(t).then(done).catch(()=>appNotify('복사에 실패했습니다.', '복사'));
  }else{
    const ta=document.createElement('textarea'); ta.value=t; document.body.appendChild(ta); ta.select();
    try{ document.execCommand('copy'); done(); }catch(_){ appNotify('복사에 실패했습니다.', '복사'); }
    document.body.removeChild(ta);
  }
}

async function p3HandleObjFile(file){
  if(!file) return;
  if(!_checkBrowserUploadSize(file)) return;
  const low=file.name.toLowerCase();
  if(!low.endsWith('.obj')){ p3Log('OBJ 파일만 지원합니다','log-err'); return; }
  try{
    await ensureP3ViewerReady();
    const text=await file.text();
    P3.final=parseOBJToMesh(text);
    p3UpdateViewer(P3.final);
    p3Log('로컬 OBJ 로드: '+file.name,'log-ok');
    const v=P3.final.verts.length/3|0, f=P3.final.indices.length/3|0;
    $('p3-mesh-stats').style.display='block';
    $('ms-verts').textContent='V: '+v.toLocaleString();
    $('ms-faces').textContent='F: '+f.toLocaleString();
    $('ms-wire').textContent='E: ~'+((f*3/2)|0).toLocaleString();
    $('p3-export-smooth').disabled=!P3.sessionId;
  }catch(e){
    p3Log('OBJ 로드 오류: '+e.message,'log-err');
  }
}

// ─── Lightweight OBJ → mesh parser (quad + tri 지원) ──────────────────
function parseOBJToMesh(objText){
  const verts=[]; const norms=[]; const idxArr=[];
  const quadEdges=[];  // quad 와이어프레임용: [a,b, b,c, c,d, d,a] 형태 엣지
  let hasQuads=false;

  for(const line of objText.split('\n')){
    const p=line.trim().split(/\s+/);
    if(p[0]==='v'){
      verts.push(parseFloat(p[1]),parseFloat(p[2]),parseFloat(p[3]));
    } else if(p[0]==='vn'){
      norms.push(parseFloat(p[1]),parseFloat(p[2]),parseFloat(p[3]));
    } else if(p[0]==='f'){
      // f 라인 — v/vt/vn 형태 모두 지원, v 인덱스만 사용
      const vi=p.slice(1).map(s=>parseInt(s.split('/')[0])-1);
      if(vi.length===4){
        // Quad → 삼각형 2개 (렌더링용)
        idxArr.push(vi[0],vi[1],vi[2], vi[0],vi[2],vi[3]);
        // Quad 와이어 엣지 4개만 (대각선 제외)
        quadEdges.push(vi[0],vi[1], vi[1],vi[2], vi[2],vi[3], vi[3],vi[0]);
        hasQuads=true;
      } else if(vi.length>=3){
        idxArr.push(vi[0],vi[1],vi[2]);
      }
    }
  }
  // vn이 버텍스 수와 같으면 per-vertex normal로 사용 (Maya smooth)
  const nV = (verts.length/3)|0;
  const hasVN = (norms.length>0) && ((norms.length/3)|0) === nV;
  return {
    verts:new Float32Array(verts),
    normals: hasVN ? new Float32Array(norms) : null,
    indices: idxArr.length>65535 ? new Uint32Array(idxArr) : new Uint16Array(idxArr),
    quadEdges: hasQuads ? (quadEdges.length>65535 ? new Uint32Array(quadEdges) : new Uint16Array(quadEdges)) : null,
    hasQuads,
  };
}

// ─── Pipeline UI helpers ──────────────────────────────────────────────
function p3Log(msg, cls=''){
  const box=$('p3-log');
  if(!box) return;
  const span=document.createElement('span');
  if(cls) span.className=cls;
  span.textContent=msg+'\n';
  box.appendChild(span);
  box.scrollTop=box.scrollHeight;
}
function p3ClearLog(){ const b=$('p3-log'); if(b) b.innerHTML=''; }

/** X(빨강)/Y(초록)/Z(파랑) 축선 Group을 생성해 씬에 추가 */
function p3RebuildAxisLines(floorY, size){
  const THREE=window.THREE;
  if(!P3.scene||!THREE) return;
  if(P3.axisLines){ P3.scene.remove(P3.axisLines); P3.axisLines=null; }
  const grp=new THREE.Group();
  const mkLine=(pts,col)=>{
    const g=new THREE.BufferGeometry().setFromPoints(pts.map(p=>new THREE.Vector3(...p)));
    return new THREE.Line(g,new THREE.LineBasicMaterial({color:col,transparent:true,opacity:0.7}));
  };
  // X축(빨강) — 수평, 바닥 높이
  grp.add(mkLine([[-size,0,0],[size,0,0]], 0xE03030));
  // Y축(초록) — 수직, 바닥에서 위로
  grp.add(mkLine([[0,0,0],[0,size*1.5,0]], 0x30A830));
  // Z축(파랑) — 깊이, 바닥 높이
  grp.add(mkLine([[0,0,-size],[0,0,size]], 0x3060D0));
  grp.position.y = floorY;
  P3.scene.add(grp);
  P3.axisLines = grp;
}

// Pipeline step IDs — p3Reset, handleP3Event 등에서 공용. 순서 = UI 배치 순서.
const P3_ALL_STEPS = [
  'pipe-step-load', 'pipe-step-denoise', 'pipe-step-mc',
  'pipe-step-validate', 'pipe-step-repair', 'pipe-step-export',
];

function p3Reset(){
  // SSE 닫기
  if(P3.sse){ P3.sse.close(); P3.sse=null; }
  // 상태 초기화
  P3.loadedFileName=null; P3.hasNormals=false; P3.sessionId=null; P3.pts=null; P3.final=null;
  // 파일 입력 초기화
  const fi=$('p3-finput'); if(fi) fi.value='';
  const fn=$('p3-fname'); if(fn) fn.textContent='';
  // 버튼/UI 초기화
  const run=$('p3-run-btn'); if(run) run.disabled=true;
  const exp=$('p3-export-smooth'); if(exp) exp.disabled=true;
  const ms=$('p3-mesh-stats'); if(ms) ms.style.display='none';
  const vp=$('p3-val-panel'); if(vp) vp.style.display='none';
  p3SetProgress(0);
  // 파이프라인 스텝 리셋 — P3_ALL_STEPS 상수 재사용 (DRY)
  P3_ALL_STEPS.forEach(id => setPipeStep(id, '', '대기'));
  // 3D 뷰어 클리어 — core.js의 disposeFromScene helper 사용 (DRY)
  if(P3.threeReady && P3.scene){
    P3.mesh          = disposeFromScene(P3.scene, P3.mesh);
    P3.wire          = disposeFromScene(P3.scene, P3.wire);
    P3.pCloud        = disposeFromScene(P3.scene, P3.pCloud);
    P3.mirrorPCloud  = disposeFromScene(P3.scene, P3.mirrorPCloud);
    if(P3.axisLines){ P3.scene.remove(P3.axisLines); P3.axisLines = null; }
    P3.dirty = true;
  }
  P3.wireOn=false;
  const wb=$('p3-wire-btn'), wb2=$('p3-wire-btn2');
  if(wb){ wb.classList.add('off'); wb.textContent='◻ 와이어프레임'; }
  if(wb2){ wb2.classList.add('off'); wb2.textContent='◻ 와이어'; }
  const bd=$('p3-badge'); if(bd) bd.textContent='파일을 로드하세요';
  p3ClearLog();
  const box=$('p3-log'); if(box) box.innerHTML='— 파일을 로드하세요 —\n';
  p3RefreshCmdBox();
}

function setPipeStep(id, state, text){
  const el=$(id); if(!el) return;
  el.className='pipe-step'+(state?' '+state:'');
  const st=el.querySelector('.pipe-status');
  if(st) st.textContent=text;
}
function p3SetProgress(pct){
  const el=$('p3-prog'); if(!el) return;
  el.style.width=Math.min(100,Math.max(0,pct))+'%';
}

function p3ShowValidation(val){
  const panel=$('p3-val-panel'), badges=$('p3-val-badges');
  if(!panel||!badges) return;
  panel.style.display='block';
  const nc=(val.normal_consistency*100|0);
  const items=[
    {label:'Watertight', ok:val.watertight,           v:val.watertight?'✓':'✗ '+val.boundary_edges+'개'},
    {label:'Manifold',   ok:val.non_manifold_edges===0,v:val.non_manifold_edges===0?'✓':'✗ '+val.non_manifold_edges},
    {label:'Components', ok:val.components===1,        v:val.components+'개'},
    {label:'Normals',    ok:val.normal_consistency>.9, v:nc+'%'},
  ];
  badges.innerHTML=items.map(it=>`
    <div style="background:${it.ok?'#14532D':'#7F1D1D'};color:${it.ok?'#86EFAC':'#FCA5A5'};
         font-size:9px;padding:2px 6px;border-radius:3px;font-family:Consolas,monospace">
      ${it.label}: ${it.v}
    </div>`).join('');
}

// P3_ALL_STEPS — p3Reset 위쪽에서 선언됨 (DRY). 중복 제거.

// ─── Handle pipeline SSE event ────────────────────────────────────────
function handleP3Event(data){
  if(data.error){
    p3Log('오류: '+data.error,'log-err');
    P3_ALL_STEPS.forEach(id=>{if($(id)&&$(id).classList.contains('active'))setPipeStep(id,'error','오류 ✗');});
    _p3UpdateRunBtnEnabled();
    if(P3.sse){P3.sse.close();P3.sse=null;}
    p3HideLoading();
    return;
  }
  // 뷰어 로딩 오버레이 sub-메시지 실시간 업데이트
  if(data.msg){
    const sub = $('p3-loading-sub');
    if(sub) sub.textContent = data.msg;
  }
  const stepMap={denoise:'pipe-step-denoise',
                 mc:'pipe-step-mc', bpa:'pipe-step-mc',
                 poisson:'pipe-step-mc', sdf:'pipe-step-mc', alpha:'pipe-step-mc',
                 validate:'pipe-step-validate',repair:'pipe-step-repair',export:'pipe-step-export'};
  const stepId=stepMap[data.step];

  if(data.status==='active'){
    if(stepId) setPipeStep(stepId,'active',data.msg||'처리 중...');
  } else if(data.status==='done'){
    if(stepId){
      const label=data.V&&data.F?`${data.F.toLocaleString()} F ✓`:
                  data.count?`${data.count.toLocaleString()} pts ✓`:'완료 ✓';
      setPipeStep(stepId,'done',label);
    }
    if(data.val) p3ShowValidation(data.val);
    if(data.msg) p3Log(data.msg,'log-ok');
  }
  if(data.progress) p3SetProgress(data.progress);

  // Pipeline complete
  if(data.step==='export'&&data.status==='done'){
    if(P3.sse){P3.sse.close();P3.sse=null;}
    p3Log(`\n✅ 완료! V:${data.V?.toLocaleString()} F:${data.F?.toLocaleString()}`,'log-ok');
    p3Log('→ OBJ 저장 후 Instant Meshes (6k~9k V) → Blender Decimate (4k~10k F)','log-info');
    // Fetch OBJ and display
    const sub = $('p3-loading-sub'); if(sub) sub.textContent = '뷰어에 메쉬 표시 중...';
    _loadAndShowMesh().finally(() => p3HideLoading());
    $('p3-export-smooth').disabled=false;
    const fbxBtn = $('p3-export-fbx'); if(fbxBtn) fbxBtn.disabled = false;
    const glbBtn = $('p3-export-glb'); if(glbBtn) glbBtn.disabled = false;
    $('ms-verts').textContent=`V: ${data.V?.toLocaleString()??'-'}`;
    $('ms-faces').textContent=`F: ${data.F?.toLocaleString()??'-'}`;
    $('ms-wire').textContent =`E: ~${data.F?((data.F*3/2)|0).toLocaleString():'-'}`;
    $('p3-mesh-stats').style.display='block';
    _p3UpdateRunBtnEnabled();
  }
}

async function _loadAndShowMesh(){
  if(!P3.sessionId) return;
  try{
    await ensureP3ViewerReady();
    const res=await fetch(`${P3_BACKEND}/mesh/${P3.sessionId}`);
    if(!res.ok) throw new Error(`HTTP ${res.status}`);
    const objText=await res.text();
    P3.final=parseOBJToMesh(objText);
    p3UpdateViewer(P3.final);
  } catch(e){
    p3Log('뷰어 로드 오류: '+e.message,'log-err');
  }
}

// ─── Run pipeline (SSE) ───────────────────────────────────────────────
// ─── Page 3 뷰어 로딩 오버레이 헬퍼 ──────────────────────────────────
function p3ShowLoading(title, sub){
  const el = $('p3-viewer-loading');
  if(!el) return;
  if(title){ const t=$('p3-loading-title'); if(t) t.textContent = title; }
  if(sub !== undefined){ const s=$('p3-loading-sub'); if(s) s.textContent = sub; }
  el.classList.add('show');
  void el.offsetHeight;  // force paint
}
function p3HideLoading(){
  const el = $('p3-viewer-loading');
  if(el) el.classList.remove('show');
}

async function runP3Pipeline(){
  if(!P3.sessionId){ appNotify('포인트 클라우드 파일을 먼저 로드하세요.'); return; }
  const smode = _p3SurfaceMode();
  if(!smode){ appNotify('🟢 스무스 또는 🔷 하드 중 하나를 선택하세요.'); return; }
  if(P3.sse){ P3.sse.close(); P3.sse=null; }

  const modeLabel = smode === 'hard' ? '🔷 하드 모드' : '🟢 스무스 모드';
  p3ShowLoading(`${modeLabel} 메쉬 변환 중...`, '포인트 클라우드 업로드 및 재구성 준비');

  $('p3-run-btn').disabled=true;
  $('p3-export-smooth').disabled=true;
  $('p3-mesh-stats').style.display='none';
  const vp=$('p3-val-panel'); if(vp) vp.style.display='none';
  p3ClearLog();
  P3.final=null;

  P3_ALL_STEPS.forEach(id=>setPipeStep(id,'','대기'));
  setPipeStep('pipe-step-load','done','완료 ✓');
  p3SetProgress(5);

  const icpOn   = !!($('p3-icp')?.checked);
  const pruneOn = $('p3-prune')?.checked ?? true;
  const stype   = $('p3-smooth-laplacian')?.checked ? 'laplacian' : 'taubin';
  const body = {
    denoise:  $('p3-denoise')?.checked  ?? true,
    sigma:    parseFloat($('p3-denoise-k')?.value ?? '2'),
    mc_res:   parseInt($('p3-mc-res')?.value ?? '50', 10),
    smooth:   $('p3-smooth')?.checked   ?? true,
    smooth_iter: parseInt($('p3-smooth-iter')?.value ?? '2', 10),
    smooth_type: stype,
    algorithm: _p3CurrentAlgo(),
    surface_mode: smode,
    bpa_radii_scale: parseFloat($('p3-bpa-scale')?.value ?? '1'),
    poisson_depth:   parseInt($('p3-poisson-depth')?.value ?? '9', 10),
    icp_snap: icpOn ? parseInt($('p3-icp-iter')?.value || '3', 10) : 0,
    mirror_x:      !!($('p3-mirror')?.checked),
    mirror_axis:   $('p3-mirror-axis')?.value   || 'x',
    mirror_center: $('p3-mirror-center')?.value || 'centroid',
    quadify:  true,
    merge_verts:     $('p3-merge-verts')?.checked     ?? true,
    orient_normals:  $('p3-orient-normals')?.checked  ?? true,
    uniform_remesh:  !!($('p3-uniform-remesh')?.checked),
    smooth_normals:  $('p3-smooth-normals')?.checked  ?? true,
    prune_edges:     pruneOn ? parseFloat($('p3-prune-ratio')?.value || '4.0') : 0,
    remove_fragments:$('p3-remove-frag')?.checked     ?? true,
    target_tris:     parseInt($('p3-target-tris')?.value ?? '0', 10),
    voxel_remesh:    !!($('p3-voxel-remesh')?.checked),
    voxel_res:       parseInt($('p3-voxel-res')?.value ?? '60', 10),
    instant_meshes:  !!($('p3-instant-meshes')?.checked),
    im_target_faces: parseInt($('p3-im-faces')?.value ?? '8000', 10),
    im_pure_quad:    $('p3-im-pure-quad')?.checked    ?? true,
    im_crease:       parseFloat($('p3-im-crease')?.value ?? '0'),
    fake_hole_fill:  $('p3-fake-hole-fill')?.checked  ?? true,
    fake_hole_size:  parseFloat($('p3-fake-hole-size')?.value || '15') / 100,
    color_groups:    ($('p3-color-groups-on')?.checked ? parseInt($('p3-color-groups-k')?.value || '6', 10) : 0),
  };

  p3Log(`Python 백엔드 파이프라인 시작...`,'log-info');

  // POST JSON + SSE (EventSource 는 POST 미지원 → fetch/ReadableStream 사용).
  // P3.sse 는 AbortController 로 유지해 기존 .close() API 와 호환되게 둠.
  const ctrl = new AbortController();
  P3.sse = { close(){ try{ ctrl.abort(); }catch(_){} } };
  postSSE(`${P3_BACKEND}/process/${P3.sessionId}`, body, handleP3Event, {signal: ctrl.signal})
    .then(() => { P3.sse = null; })
    .catch(err => {
      if(err?.name === 'AbortError'){ P3.sse = null; return; }
      p3Log('변환 엔진 연결 끊김: ' + (err?.message || err),'log-err');
      P3.sse = null;
      _p3UpdateRunBtnEnabled();
      p3HideLoading();
    });
}

// ─── OBJ export ─────────────────────────────────────────────────────
async function p3ExportOBJ(){
  if(!P3.sessionId){ appNotify('먼저 메쉬 변환 파이프라인을 실행하세요.'); return; }
  const btn=$('p3-export-smooth');
  const origTxt=btn?btn.textContent:'';
  if(btn){ btn.disabled=true; btn.textContent='저장 중...'; }

  try{
    const resp=await fetch(`${P3_BACKEND}/mesh/${P3.sessionId}`);
    if(!resp.ok) throw new Error(`서버 오류 ${resp.status}`);
    const text=await resp.text();
    let mtlText = null;
    try{
      const mresp = await fetch(`${P3_BACKEND}/mtl/${P3.sessionId}`);
      if(mresp.ok) mtlText = await mresp.text();
    }catch(_){}

    const stem=(P3.loadedFileName||'mesh').replace(/\.[^.]+$/,'').replace(/[^\w\-]/g,'_');
    const fname=`${stem}_mesh.obj`;
    const mtlName=`${stem}_mesh.mtl`;

    // 항상 네이티브 다이얼로그 먼저 시도 (App.IS_PYWEBVIEW 무시)
    const api = await getPyApi();
    if(api){
      const r = await api.save_file_dialog(fname, text);
      if(!r || !r.ok){
        if(r && r.reason !== 'cancelled') appNotify('저장 실패: ' + (r.reason||'?'));
        return;
      }
      p3Log(`💾 OBJ 저장 완료 → ${r.path}`, 'log-ok');
      if(mtlText){
        const mtlPath = r.path.replace(/\.obj$/i, '.mtl');
        try{
          if(api.write_text_file){
            const wr = await api.write_text_file(mtlPath, mtlText);
            if(wr.ok) p3Log(`🎨 MTL 저장 → ${wr.path}`, 'log-ok');
          } else {
            const mr = await api.save_file_dialog(mtlName, mtlText);
            if(mr.ok) p3Log(`🎨 MTL 저장 → ${mr.path}`, 'log-ok');
          }
        }catch(e){ p3Log('⚠️ MTL 저장 실패: '+e.message, 'log-warn'); }
      }
      return;
    }

    // 폴백: 브라우저 다운로드 (PyWebView API 실패 시만)
    const blob=new Blob([text],{type:'text/plain'});
    await saveBlob(fname, blob);
    p3Log(`💾 OBJ 다운로드: ${fname}`, 'log-ok');
    if(mtlText){
      const mblob=new Blob([mtlText],{type:'text/plain'});
      await saveBlob(mtlName, mblob);
      p3Log(`🎨 MTL 다운로드: ${mtlName}`, 'log-ok');
    }
  }catch(e){
    appNotify('OBJ 저장 오류: '+e.message);
    p3Log('❌ OBJ 저장 실패: '+e.message, 'log-err');
  }finally{
    if(btn){ btn.disabled=false; btn.textContent=origTxt; }
  }
}

// ─── FBX 저장 (Binary FBX — Blender/Maya/Unity 전부 호환) ────────────
async function p3ExportFBX(){
  if(!P3.sessionId){ appNotify('먼저 메쉬 변환 파이프라인을 실행하세요.'); return; }
  const btn=$('p3-export-fbx');
  const origTxt=btn?btn.textContent:'';
  if(btn){ btn.disabled=true; btn.textContent='저장 중...'; }
  try{
    // fmt=binary (기본) — Binary FBX 7.4.0
    const resp=await fetch(`${P3_BACKEND}/mesh-fbx/${P3.sessionId}?fmt=binary`);
    if(!resp.ok) throw new Error(`서버 오류 ${resp.status}`);
    const ab=await resp.arrayBuffer();  // binary
    const bytes = new Uint8Array(ab);
    const stem=(P3.loadedFileName||'mesh').replace(/\.[^.]+$/,'').replace(/[^\w\-]/g,'_');
    const fname=`${stem}_mesh.fbx`;

    const api = await getPyApi();
    if(api && api.save_bytes_dialog){
      // Uint8Array → base64 (한 번에)
      let bin = '';
      for(let i=0; i<bytes.length; i++) bin += String.fromCharCode(bytes[i]);
      const b64 = btoa(bin);
      const r = await api.save_bytes_dialog(fname, b64);
      if(!r || !r.ok){
        if(r && r.reason !== 'cancelled') appNotify('저장 실패: '+(r.reason||'?'));
        return;
      }
      p3Log(`💾 FBX 저장 완료 → ${r.path}`, 'log-ok');
      appNotify(`✅ FBX (Binary) 저장 완료\n${r.path}\n\nBlender/Maya/Unity 모두 import 가능`);
      return;
    }
    // 폴백
    const blob=new Blob([ab],{type:'application/octet-stream'});
    await saveBlob(fname, blob);
    p3Log(`💾 FBX 다운로드: ${fname}`,'log-ok');
  }catch(e){
    appNotify('FBX 저장 오류: '+e.message);
    p3Log('❌ FBX 저장 실패: '+e.message,'log-err');
  }finally{
    if(btn){ btn.disabled=false; btn.textContent=origTxt; }
  }
}

// ─── GLB 저장 (binary glTF — Blender 호환성 좋음) ─────────────────────
async function p3ExportGLB(){
  if(!P3.sessionId){ appNotify('먼저 메쉬 변환 파이프라인을 실행하세요.'); return; }
  const btn=$('p3-export-glb');
  const origTxt=btn?btn.textContent:'';
  if(btn){ btn.disabled=true; btn.textContent='저장 중...'; }
  try{
    const resp=await fetch(`${P3_BACKEND}/mesh-glb/${P3.sessionId}`);
    if(!resp.ok) throw new Error(`서버 오류 ${resp.status}`);
    const ab=await resp.arrayBuffer();  // binary
    const bytes = new Uint8Array(ab);
    const stem=(P3.loadedFileName||'mesh').replace(/\.[^.]+$/,'').replace(/[^\w\-]/g,'_');
    const fname=`${stem}_mesh.glb`;

    const api = await getPyApi();
    if(api && api.save_bytes_dialog){
      // Uint8Array → base64
      let b64 = '';
      for(let i=0;i<bytes.length;i+=4096){
        b64 += btoa(String.fromCharCode.apply(null, bytes.subarray(i, i+4096)));
      }
      // 위 방식은 4096 단위 base64 concat이 맞지 않음 — 한 번에 base64
      let bin = '';
      for(let i=0; i<bytes.length; i++) bin += String.fromCharCode(bytes[i]);
      b64 = btoa(bin);
      const r = await api.save_bytes_dialog(fname, b64);
      if(!r || !r.ok){
        if(r && r.reason !== 'cancelled') appNotify('저장 실패: '+(r.reason||'?'));
        return;
      }
      p3Log(`💾 GLB 저장 완료 → ${r.path}`, 'log-ok');
      appNotify(`✅ GLB 저장 완료\n${r.path}\n\nBlender 드래그만 하면 열림`);
      return;
    }
    // 폴백
    const blob=new Blob([ab],{type:'model/gltf-binary'});
    await saveBlob(fname, blob);
    p3Log(`💾 GLB 다운로드: ${fname}`,'log-ok');
  }catch(e){
    appNotify('GLB 저장 오류: '+e.message);
    p3Log('❌ GLB 저장 실패: '+e.message,'log-err');
  }finally{
    if(btn){ btn.disabled=false; btn.textContent=origTxt; }
  }
}

// ─── 뷰어 색상 토글 (클러스터 색 적용) ──────────────────────────────
P3.colorMode = false;
async function p3ToggleColor(){
  if(!P3.sessionId || !P3.mesh){ appNotify('먼저 메쉬 변환을 실행하세요.'); return; }
  P3.colorMode = !P3.colorMode;
  const btn = $('p3-color-toggle');
  if(btn){ btn.classList.toggle('off', !P3.colorMode); btn.textContent = P3.colorMode ? '🎨 회색' : '🎨 색상'; }

  const THREE = window.THREE;
  if(P3.colorMode){
    try{
      const r = await fetch(`${P3_BACKEND}/mesh-colors/${P3.sessionId}`);
      if(!r.ok) throw new Error(`HTTP ${r.status}`);
      const data = await r.json();
      const cols = data.colors;  // [[r,g,b],...]
      const geo = P3.mesh.geometry;
      const posAttr = geo.getAttribute('position');
      const nVGeo = posAttr.count;
      const nCols = cols.length;
      // 인덱스 기반 draw면 원본 V 수와 cols 수가 일치. 불일치시 회색 폴백
      const carr = new Float32Array(nVGeo * 3);
      for(let i=0; i<nVGeo; i++){
        const src = (i < nCols) ? cols[i] : [0.6, 0.6, 0.6];
        carr[i*3]   = src[0];
        carr[i*3+1] = src[1];
        carr[i*3+2] = src[2];
      }
      geo.setAttribute('color', new THREE.BufferAttribute(carr, 3));
      P3.mesh.material.vertexColors = true;
      P3.mesh.material.color.setRGB(1, 1, 1);
      P3.mesh.material.needsUpdate = true;
      P3.dirty = true;
      p3Log(`🎨 색상 표시 ON — 클러스터 ${data.cluster_count || '?'}개`, 'log-info');
    }catch(e){
      appNotify('색상 로드 실패: ' + e.message);
      P3.colorMode = false;
      if(btn){ btn.classList.add('off'); btn.textContent='🎨 색상'; }
    }
  }else{
    // 회색으로 복귀
    P3.mesh.material.vertexColors = false;
    P3.mesh.material.color.setHex(0x8A8A8A);
    P3.mesh.material.needsUpdate = true;
    P3.dirty = true;
  }
}

// ─── Three.js loader (shared) ────────────────────────────────────────
// loadThreeJS — core.js로 이동 (page 2/3/4 공용)

// Returns a Promise that resolves when p3 viewer is fully ready
function ensureP3ViewerReady(){
  if(P3.threeReady) return Promise.resolve();
  return new Promise(async (res)=>{
    await loadThreeJS();
    initP3Viewer();
    res();
  });
}

// ─── Page 3 Three.js Viewer ──────────────────────────────────────────
function initP3Viewer(){
  if(P3.threeReady) return;
  if(!window.THREE){
    // Not loaded yet — will be called again after load
    loadThreeJS().then(()=>initP3Viewer());
    return;
  }

  const THREE = window.THREE;
  const cv    = $('p3-canvas');
  const wrap  = $('p3-viewer-wrap');
  if(!cv || !wrap) return;

  // ── Renderer ────────────────────────────────────────────────────
  P3.renderer = new THREE.WebGLRenderer({canvas:cv, antialias:true, alpha:false});
  P3.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  P3.renderer.setClearColor(0xF5F5F5, 1);

  // ── Scene ───────────────────────────────────────────────────────
  P3.scene = new THREE.Scene();

  // ── Camera ──────────────────────────────────────────────────────
  const W = Math.max(300, wrap.clientWidth  || 800);
  const H = Math.max(480, cv.getBoundingClientRect().height || window.innerHeight - 202);
  P3.renderer.setSize(W, H);
  P3.camera = new THREE.PerspectiveCamera(45, W/H, 0.001, 10000);

  // ── Lights ──────────────────────────────────────────────────────
  P3.scene.add(new THREE.AmbientLight(0xffffff, 0.55));
  const dL = new THREE.DirectionalLight(0xffffff, 0.85);
  dL.position.set(1.5, 2.5, 1.5);
  const dL2 = new THREE.DirectionalLight(0xffffff, 0.25);
  dL2.position.set(-1, -0.5, -1);
  P3.scene.add(dL, dL2);

  // ── Initial grid ────────────────────────────────────────────────
  P3.grid = new THREE.GridHelper(20, 20, 0xC8C8C8, 0xE0E0E0);
  P3.scene.add(P3.grid);

  // ── 축선 초기화 (실제 크기는 첫 파일 로드 시 결정) ─────────────
  P3.axisLines = null;

  // ── Mouse events (Maya 단축키 포함) ─────────────────────────────
  cv.addEventListener('contextmenu', e => e.preventDefault());
  let _p3AltDown = false;
  window.addEventListener('keydown', ke => { if(ke.key==='Alt') _p3AltDown=true; });
  window.addEventListener('keyup',   ke => { if(ke.key==='Alt') _p3AltDown=false; });

  cv.addEventListener('mousedown', e => {
    e.preventDefault();
    if(_p3AltDown){
      // Maya: Alt+LMB=회전, Alt+MMB=패닝, Alt+RMB=줌
      P3.orb.dragging=true;
      P3.orb.btn = e.button===0 ? 2 : e.button===1 ? 1 : 99;
    } else {
      P3.orb.dragging=true; P3.orb.btn=e.button;
    }
    P3.orb.lx=e.clientX; P3.orb.ly=e.clientY;
  });

  const p3MM = e => {
    if(!P3.orb.dragging) return;
    const dx=e.clientX-P3.orb.lx, dy=e.clientY-P3.orb.ly;
    P3.orb.lx=e.clientX; P3.orb.ly=e.clientY;
    if(P3.orb.btn===2){ P3.orb.dTheta-=dx*0.007; P3.orb.dPhi-=dy*0.007; }
    else if(P3.orb.btn===1){ const sc=P3.orb.radius*0.0015; P3.orb.pdx-=dx*sc; P3.orb.pdy+=dy*sc; }
    else if(P3.orb.btn===99){ P3.orb.dRadius+=dy*P3.orb.radius*0.006; }
    P3.dirty=true;
  };
  const p3MU = () => { P3.orb.dragging=false; P3.orb.btn=-1; };
  window.addEventListener('mousemove', p3MM);
  window.addEventListener('mouseup',   p3MU);

  // 'F' 키 = 전체보기 (Page 3)
  cv.addEventListener('keydown', ke => {
    if(ke.key==='f'||ke.key==='F'){ fitP3Camera(); ke.preventDefault(); }
  });
  cv.setAttribute('tabindex','0');

  // 줌 — magnitude 정규화 + direct mult + 상한 clamp (Page 2와 동일한 버그 수정)
  cv.addEventListener('wheel', e => {
    e.preventDefault();
    const f = Math.sign(e.deltaY) * Math.min(Math.abs(e.deltaY) / 120, 1);
    P3.orb.radius *= (1 + f * 0.12);
    P3.orb.dRadius = 0;
    P3.orb.radius = Math.max(0.001, Math.min(1e5, P3.orb.radius));
    P3.dirty = true;
  }, {passive:false});

  // ── Resize observer (캔버스 CSS height 기준으로 읽음) ───────────────
  const _p3Resize = () => {
    if(!P3.renderer || !P3.threeReady) return;
    const w = Math.max(300, wrap.clientWidth);
    const h = Math.max(480, cv.getBoundingClientRect().height || window.innerHeight - 202);
    P3.renderer.setSize(w, h);
    P3.camera.aspect = w / h;
    P3.camera.updateProjectionMatrix();
    P3.dirty = true;
  };
  new ResizeObserver(_p3Resize).observe(wrap);
  window.addEventListener('resize', _p3Resize);

  // ── Scene Gizmo ─────────────────────────────────────────────────
  P3.gizmo = new SceneGizmo('p3-gizmo',
    () => ({theta:P3.orb.theta, phi:P3.orb.phi}),
    (theta, phi) => {
      const dt = ((theta-P3.orb.theta+Math.PI*3)%(Math.PI*2))-Math.PI;
      P3.orb.dTheta += dt*0.4;
      P3.orb.dPhi   += (phi-P3.orb.phi)*0.4;
      P3.dirty = true;
    }
  );
  P3.gizmo.draw();

  P3.threeReady    = true;
  P3.viewerRunning = true;
  P3.dirty         = true;
  p3RenderLoop();
}

function p3ApplyOrbit(){
  if(!P3.camera) return;
  P3.orb.theta+=P3.orb.dTheta; P3.orb.dTheta*=0.86;
  P3.orb.phi  +=P3.orb.dPhi;   P3.orb.dPhi  *=0.86;
  P3.orb.radius+=P3.orb.dRadius;P3.orb.dRadius*=0.86;
  P3.orb.cx+=P3.orb.pdx;       P3.orb.pdx*=0.86;
  P3.orb.cy+=P3.orb.pdy;       P3.orb.pdy*=0.86;
  P3.orb.phi=Math.max(0.05,Math.min(Math.PI-0.05,P3.orb.phi));
  // radius 클램프: 한계 도달 시 dRadius 즉시 0 → 반대 방향 휠 즉시 반응
  if(P3.orb.radius<0.001){ P3.orb.radius=0.001; if(P3.orb.dRadius<0) P3.orb.dRadius=0; }
  if(P3.orb.radius>1e6)  { P3.orb.radius=1e6;   if(P3.orb.dRadius>0) P3.orb.dRadius=0; }
  P3.camera.position.set(
    P3.orb.cx+P3.orb.radius*Math.sin(P3.orb.phi)*Math.sin(P3.orb.theta),
    P3.orb.cy+P3.orb.radius*Math.cos(P3.orb.phi),
    P3.orb.cz+P3.orb.radius*Math.sin(P3.orb.phi)*Math.cos(P3.orb.theta)
  );
  P3.camera.lookAt(P3.orb.cx,P3.orb.cy,P3.orb.cz);
  if(P3.gizmo) P3.gizmo.draw();
}

function p3RenderLoop(){
  if(!P3.viewerRunning){P3.raf=null;return;}
  P3.raf=requestAnimationFrame(p3RenderLoop);
  const moving=Math.abs(P3.orb.dTheta)+Math.abs(P3.orb.dPhi)+Math.abs(P3.orb.dRadius)+Math.abs(P3.orb.pdx)+Math.abs(P3.orb.pdy)>2e-5;
  if(moving) P3.dirty=true;
  if(!P3.dirty&&!P3.orb.dragging) return;
  P3.dirty=false;
  p3ApplyOrbit();
  P3.renderer.render(P3.scene,P3.camera);
}

function p3UpdateViewer(mesh){
  if(!P3.threeReady || !P3.scene || !P3.camera) return;
  const THREE = window.THREE;

  // ── 포인트 클라우드 미리보기 제거 ─────────────────────────────────
  p3ClearPointPreview();

  // ── Dispose old mesh / wire ──────────────────────────────────────
  [P3.mesh, P3.wire].forEach(obj => {
    if(obj){ P3.scene.remove(obj); obj.geometry.dispose(); obj.material.dispose(); }
  });
  P3.mesh = P3.wire = null;

  // ── Build BufferGeometry ─────────────────────────────────────────
  const geo = new THREE.BufferGeometry();
  geo.setAttribute('position', new THREE.BufferAttribute(mesh.verts.slice(), 3));
  // mesh.indices is already correctly-typed Uint16Array or Uint32Array from parseOBJToMesh
  geo.setIndex(new THREE.BufferAttribute(mesh.indices, 1));
  // OBJ에 vn이 있으면 그걸 쓰고(백엔드가 이미 스무드 노멀 계산함),
  // 없으면 Three.js가 현장에서 계산 (area-weighted avg = smooth shading)
  if(mesh.normals && mesh.normals.length === mesh.verts.length){
    geo.setAttribute('normal', new THREE.BufferAttribute(mesh.normals.slice(), 3));
  } else {
    geo.computeVertexNormals();
  }

  // ── Solid mesh ───────────────────────────────────────────────────
  const mat = new THREE.MeshStandardMaterial({
    color: 0x8A8A8A, roughness: 0.65, metalness: 0.08, side: THREE.DoubleSide
  });
  P3.mesh = new THREE.Mesh(geo, mat);
  P3.scene.add(P3.mesh);

  // ── Wireframe overlay — quad이면 quad 엣지만, 아니면 일반 wireframe ─
  if(mesh.hasQuads && mesh.quadEdges && mesh.quadEdges.length > 0){
    // Quad 와이어: LineSegments로 4변만 표시 (대각선 제외)
    const wGeo = new THREE.BufferGeometry();
    wGeo.setAttribute('position', new THREE.BufferAttribute(mesh.verts.slice(), 3));
    // mesh.quadEdges is already correctly-typed from parseOBJToMesh
    wGeo.setIndex(new THREE.BufferAttribute(mesh.quadEdges, 1));
    const wMat = new THREE.LineBasicMaterial({color:0x222222, transparent:true, opacity:0.3});
    P3.wire = new THREE.LineSegments(wGeo, wMat);
  } else {
    const wGeo = geo.clone();
    const wMat = new THREE.MeshBasicMaterial({color:0x222222, wireframe:true, transparent:true, opacity:0.25});
    P3.wire = new THREE.Mesh(wGeo, wMat);
  }
  P3.wire.visible = P3.wireOn;
  P3.scene.add(P3.wire);

  // ── Fit camera ───────────────────────────────────────────────────
  const box3  = new THREE.Box3().setFromObject(P3.mesh);
  const ctr   = box3.getCenter(new THREE.Vector3());
  const sz    = box3.getSize(new THREE.Vector3());
  const maxDim = Math.max(sz.x, sz.y, sz.z) || 1;

  P3.orb.cx = ctr.x; P3.orb.cy = ctr.y; P3.orb.cz = ctr.z;
  P3.orb.radius  = maxDim * 1.8;
  P3.orb.theta   = -0.4; P3.orb.phi = 1.15;
  P3.orb.dTheta  = 0;    P3.orb.dPhi= 0; P3.orb.dRadius = 0;
  P3.orb.pdx     = 0;    P3.orb.pdy = 0;

  P3.camera.near = maxDim * 0.0001;
  P3.camera.far  = maxDim * 80;
  P3.camera.updateProjectionMatrix();

  // ── Rescale grid ─────────────────────────────────────────────────
  if(P3.grid) P3.scene.remove(P3.grid);
  const gs = Math.max(2, Math.ceil(maxDim * 2.5 / 2) * 2);
  P3.grid = new THREE.GridHelper(gs, Math.min(60, gs), 0xC8C8C8, 0xE0E0E0);
  P3.grid.position.y = box3.min.y;
  P3.scene.add(P3.grid);

  // ── X/Y/Z 축선 재생성 ────────────────────────────────────────────
  p3RebuildAxisLines(box3.min.y, Math.min(gs * 0.12, maxDim * 0.25));

  // ── Badge ────────────────────────────────────────────────────────
  const vC = mesh.verts.length/3|0, fC = mesh.indices.length/3|0;
  $('p3-badge').textContent = `${vC.toLocaleString()} V · ${fC.toLocaleString()} F`;

  P3.dirty = true;

  // Ensure render loop is running
  if(!P3.raf){ P3.viewerRunning=true; p3RenderLoop(); }
}

function toggleP3Wire(){
  P3.wireOn=!P3.wireOn;
  if(P3.wire) P3.wire.visible=P3.wireOn;
  const wb=$('p3-wire-btn'), wb2=$('p3-wire-btn2');
  if(wb){
    wb.classList.toggle('off',!P3.wireOn);
    wb.textContent=P3.wireOn?'◼ 와이어프레임':'◻ 와이어프레임';
  }
  if(wb2){
    wb2.classList.toggle('off',!P3.wireOn);
    wb2.textContent=P3.wireOn?'◼ 와이어':'◻ 와이어';
  }
  P3.dirty=true;
}

// ─── Page 3 — UI init & drop ─────────────────────────────────────────
(function initP3UI(){
  // Denoise sigma slider label
  const dnK = $('p3-denoise-k'), dnKVal = $('p3-denoise-k-val');
  if(dnK && dnKVal) dnK.addEventListener('input', () => { dnKVal.textContent = parseFloat(dnK.value).toFixed(1) + 'σ'; });

  // Denoise toggle → show/hide slider row
  const denoiseCb = $('p3-denoise'), denoiseRow = $('p3-denoise-row');
  if(denoiseCb && denoiseRow){
    denoiseCb.addEventListener('change', e => {
      denoiseRow.style.display = e.target.checked ? 'flex' : 'none';
    });
  }

  // MC res slider label
  const mcRes = $('p3-mc-res'), mcResVal = $('p3-mc-res-val');
  if(mcRes && mcResVal) mcRes.addEventListener('input', () => { mcResVal.textContent = mcRes.value; });

  // Smooth iter slider label
  const smIter = $('p3-smooth-iter'), smVal = $('p3-smooth-iter-val');
  if(smIter && smVal) smIter.addEventListener('input', () => { smVal.textContent = smIter.value; });

  // Smooth toggle → show/hide slider row
  const smoothCb = $('p3-smooth'), smoothRow = $('p3-smooth-row');
  if(smoothCb && smoothRow){
    smoothCb.addEventListener('change', e => {
      smoothRow.style.display = e.target.checked ? 'flex' : 'none';
    });
  }

  // ICP snap toggle → show/hide slider row
  const icpCb = $('p3-icp'), icpRow = $('p3-icp-row');
  if(icpCb && icpRow){
    icpCb.addEventListener('change', e => {
      icpRow.style.display = e.target.checked ? 'flex' : 'none';
    });
  }
  const icpIter = $('p3-icp-iter'), icpIterVal = $('p3-icp-iter-val');
  if(icpIter && icpIterVal) icpIter.addEventListener('input', () => { icpIterVal.textContent = icpIter.value; });

  // Poisson depth slider label
  const pd = $('p3-poisson-depth'), pdVal = $('p3-poisson-depth-val');
  if(pd && pdVal) pd.addEventListener('input', () => { pdVal.textContent = pd.value; });

  // Prune ratio slider label + toggle show/hide
  const pr = $('p3-prune-ratio'), prVal = $('p3-prune-ratio-val');
  if(pr && prVal) pr.addEventListener('input', () => { prVal.textContent = parseFloat(pr.value).toFixed(1) + '×'; });
  const pruneCb = $('p3-prune'), pruneRow = $('p3-prune-row');
  if(pruneCb && pruneRow){
    pruneCb.addEventListener('change', e => {
      pruneRow.style.display = e.target.checked ? 'flex' : 'none';
    });
  }

  // Target tris slider label
  const tt = $('p3-target-tris'), ttVal = $('p3-target-tris-val');
  if(tt && ttVal){
    const fmt = () => {
      const n = +tt.value;
      ttVal.textContent = n===0 ? '없음' : (n>=1000 ? (n/1000).toFixed(0)+'K' : String(n));
    };
    tt.addEventListener('input', fmt);
    fmt();
  }

  // Voxel remesh toggle + resolution slider
  const vrCb = $('p3-voxel-remesh'), vrRow = $('p3-voxel-res-row');
  if(vrCb && vrRow){
    vrCb.addEventListener('change', e => {
      vrRow.style.display = e.target.checked ? 'flex' : 'none';
    });
  }
  const vrR = $('p3-voxel-res'), vrVal = $('p3-voxel-res-val');
  if(vrR && vrVal) vrR.addEventListener('input', () => { vrVal.textContent = vrR.value; });

  // Instant Meshes toggle + options
  const imCb = $('p3-instant-meshes'), imOpts = $('p3-im-opts');
  if(imCb && imOpts){
    imCb.addEventListener('change', e => {
      imOpts.style.display = e.target.checked ? 'block' : 'none';
    });
  }
  const imF = $('p3-im-faces'), imFVal = $('p3-im-faces-val');
  if(imF && imFVal){
    const fmt = () => { const n = +imF.value; imFVal.textContent = n>=1000 ? (n/1000).toFixed(0)+'K' : String(n); };
    imF.addEventListener('input', fmt); fmt();
  }
  const imC = $('p3-im-crease'), imCVal = $('p3-im-crease-val');
  if(imC && imCVal){
    const fmt = () => { const n = +imC.value; imCVal.textContent = n===0 ? '끔' : n+'°'; };
    imC.addEventListener('input', fmt); fmt();
  }

  // Fake hole fill size slider
  const hf = $('p3-fake-hole-size'), hfVal = $('p3-fake-hole-size-val');
  if(hf && hfVal){
    const fmt = () => { hfVal.textContent = hf.value + '%'; };
    hf.addEventListener('input', fmt); fmt();
  }

  // Color groups K slider + toggle
  const cgCb = $('p3-color-groups-on'), cgRow = $('p3-color-groups-row');
  if(cgCb && cgRow){
    cgCb.addEventListener('change', e => {
      cgRow.style.display = e.target.checked ? 'flex' : 'none';
    });
  }
  const cgK = $('p3-color-groups-k'), cgKVal = $('p3-color-groups-k-val');
  if(cgK && cgKVal){
    const fmt = () => { cgKVal.textContent = cgK.value; };
    cgK.addEventListener('input', fmt); fmt();
  }

  // ★ 페이지 로드 시 기본 프리셋 자동 적용 (LOD3 = 권장, 고품질)
  // HTML 기본값(MC + IM off)이 아니라 프리셋 정의값이 쓰이도록 보장
  try{ p3SetPreset('quality'); }catch(_){}

  // ── Drop zone + file input ────────────────────────────────────────
  const drop   = $('p3-drop');
  const finput = $('p3-finput');
  if(drop && finput){
    drop.addEventListener('click', () => finput.click());
    drop.addEventListener('dragover',  e => { e.preventDefault(); drop.classList.add('drag'); });
    drop.addEventListener('dragleave', () => drop.classList.remove('drag'));
    drop.addEventListener('drop', e => {
      e.preventDefault(); drop.classList.remove('drag');
      p3HandleFile(e.dataTransfer.files[0]);
    });
    finput.addEventListener('change', e => p3HandleFile(e.target.files[0]));
  }

  const odrop=$('p3-obj-drop'), ofin=$('p3-obj-finput');
  if(odrop&&ofin){
    odrop.addEventListener('click',()=>ofin.click());
    odrop.addEventListener('dragover',e=>{ e.preventDefault(); odrop.style.borderColor='#60A5FA'; });
    odrop.addEventListener('dragleave',()=>{ odrop.style.borderColor='#334155'; });
    odrop.addEventListener('drop',e=>{
      e.preventDefault(); odrop.style.borderColor='#334155';
      const f=e.dataTransfer.files[0];
      if(f) p3HandleObjFile(f);
    });
    ofin.addEventListener('change',e=>p3HandleObjFile(e.target.files[0]));
  }

  const bpaSc = $('p3-bpa-scale'), bpaScVal = $('p3-bpa-scale-val');
  if(bpaSc && bpaScVal){
    bpaSc.addEventListener('input', () => {
      bpaScVal.textContent = parseFloat(bpaSc.value).toFixed(1);
      p3RefreshCmdBox();
    });
  }

  const cmdRefresh=()=>p3RefreshCmdBox();
  [dnK,mcRes,smIter,denoiseCb,smoothCb,bpaSc].forEach(el=>{ if(el) el.addEventListener('input',cmdRefresh); });
  [denoiseCb,smoothCb].forEach(el=>{ if(el) el.addEventListener('change',cmdRefresh); });
  p3OnAlgoChange();
})();

// ─── PLY 포인트 클라우드 프리뷰 (Page 3 뷰어에 표시) ────────────────
function p3ShowPointPreview(verts, colors, colorsLinear){
  if(!P3.threeReady||!P3.scene) return;
  const THREE=window.THREE;
  // 이전 프리뷰 제거
  if(P3.pCloud){ P3.scene.remove(P3.pCloud); P3.pCloud.geometry.dispose(); P3.pCloud.material.dispose(); P3.pCloud=null; }

  const n=verts.length/3|0;
  if(n<1) return;
  const stride=Math.max(1,Math.ceil(n/400000));
  const disp=Math.ceil(n/stride);

  const posArr=new Float32Array(disp*3);
  for(let i=0,j=0;i<n;i+=stride,j++){
    posArr[j*3]=verts[i*3]; posArr[j*3+1]=verts[i*3+1]; posArr[j*3+2]=verts[i*3+2];
  }
  const geo=new THREE.BufferGeometry();
  geo.setAttribute('position',new THREE.BufferAttribute(posArr,3));

  // 바운딩 박스 먼저 계산 (포인트 사이즈 결정용)
  const box3=new THREE.Box3().setFromBufferAttribute(geo.getAttribute('position'));
  const ctr=box3.getCenter(new THREE.Vector3());
  const sz=box3.getSize(new THREE.Vector3());
  const maxDim=Math.max(sz.x,sz.y,sz.z)||1;
  const ptSize=Math.max(0.003,Math.min(0.05,Math.pow(maxDim*maxDim*maxDim/disp,1/3)*0.5));

  let mat;
  if(colors&&colors.length>=n*3){
    const colArr=new Float32Array(disp*3);
    for(let i=0,j=0;i<n;i+=stride,j++){
      colArr[j*3  ]=colorsLinear?colors[i*3  ]:sRGB2Lin(colors[i*3  ]);
      colArr[j*3+1]=colorsLinear?colors[i*3+1]:sRGB2Lin(colors[i*3+1]);
      colArr[j*3+2]=colorsLinear?colors[i*3+2]:sRGB2Lin(colors[i*3+2]);
    }
    geo.setAttribute('color',new THREE.BufferAttribute(colArr,3));
    mat=new THREE.PointsMaterial({size:ptSize,vertexColors:true,sizeAttenuation:true});
  } else {
    mat=new THREE.PointsMaterial({size:ptSize,color:0x888888,sizeAttenuation:true});
  }

  P3.pCloud=new THREE.Points(geo,mat);
  P3.scene.add(P3.pCloud);

  // 카메라 / 그리드 씬 크기에 맞게
  P3.orb.cx=ctr.x; P3.orb.cy=ctr.y; P3.orb.cz=ctr.z;
  P3.orb.radius=maxDim*1.8; P3.orb.theta=-0.4; P3.orb.phi=1.15;
  P3.orb.dTheta=0; P3.orb.dPhi=0; P3.orb.dRadius=0; P3.orb.pdx=0; P3.orb.pdy=0;
  if(P3.camera){
    P3.camera.near=Math.max(0.0001,maxDim*0.0001);
    P3.camera.far=maxDim*80;
    P3.camera.updateProjectionMatrix();
  }
  if(P3.grid) P3.scene.remove(P3.grid);
  const gs=Math.max(2,Math.ceil(maxDim*2.5/2)*2);
  P3.grid=new THREE.GridHelper(gs,Math.min(60,gs),0xC8C8C8,0xE0E0E0);
  P3.grid.position.y=box3.min.y;
  P3.scene.add(P3.grid);

  // 포인트 클라우드 로드 시 X/Y/Z 축선 재생성
  const gsPC=Math.max(2,Math.ceil(maxDim*2.5/2)*2);
  p3RebuildAxisLines(box3.min.y, Math.min(gsPC*0.12, maxDim*0.25));

  $('p3-badge').textContent=`${n.toLocaleString()} pts`;
  P3.dirty=true;
  if(!P3.raf){ P3.viewerRunning=true; p3RenderLoop(); }

  // 미러 체크 상태이면 미리보기도 업데이트
  if($('p3-mirror')?.checked) p3UpdateMirrorPreview(verts, n);
}

function p3ClearPointPreview(){
  if(P3.pCloud&&P3.scene){
    P3.scene.remove(P3.pCloud);
    P3.pCloud.geometry.dispose();
    P3.pCloud.material.dispose();
    P3.pCloud=null;
    P3.dirty=true;
  }
  if(P3.mirrorPCloud&&P3.scene){
    P3.scene.remove(P3.mirrorPCloud);
    P3.mirrorPCloud.geometry.dispose();
    P3.mirrorPCloud.material.dispose();
    P3.mirrorPCloud=null;
    P3.dirty=true;
  }
}

// ─── 미러 미리보기 ───────────────────────────────────────────────────
function p3UpdateMirrorPreview(verts, n){
  if(!P3.threeReady||!P3.scene) return;
  const THREE=window.THREE;
  // 기존 미러 프리뷰 제거
  if(P3.mirrorPCloud){
    P3.scene.remove(P3.mirrorPCloud);
    P3.mirrorPCloud.geometry.dispose();
    P3.mirrorPCloud.material.dispose();
    P3.mirrorPCloud=null;
  }
  if(!verts||n<1) return;

  // Y축 기준 대칭(X축 반전) — 원본 cx 중심으로
  let minX=Infinity, maxX=-Infinity;
  for(let i=0;i<n;i++){ const x=verts[i*3]; if(x<minX)minX=x; if(x>maxX)maxX=x; }
  const cx=(minX+maxX)*0.5;

  const stride=Math.max(1,Math.ceil(n/400000));
  const disp=Math.ceil(n/stride);
  const posArr=new Float32Array(disp*3);
  for(let i=0,j=0;i<n;i+=stride,j++){
    posArr[j*3]  = 2*cx - verts[i*3];   // X 반전
    posArr[j*3+1]= verts[i*3+1];
    posArr[j*3+2]= verts[i*3+2];
  }
  const geo=new THREE.BufferGeometry();
  geo.setAttribute('position',new THREE.BufferAttribute(posArr,3));
  const mat=new THREE.PointsMaterial({color:0x60A5FA,size:0.02,sizeAttenuation:true});
  P3.mirrorPCloud=new THREE.Points(geo,mat);
  P3.scene.add(P3.mirrorPCloud);
  P3.dirty=true;
}

function p3OnMirrorChange(){
  const on=$('p3-mirror')?.checked;
  if(on){
    // 현재 로드된 포인트가 있으면 미러 미리보기 표시
    if(P3.pCloud&&P3.pCloud.geometry){
      const posAttr=P3.pCloud.geometry.getAttribute('position');
      if(posAttr) p3UpdateMirrorPreview(posAttr.array, posAttr.count);
    }
  } else {
    // 미러 끄면 프리뷰 제거
    if(P3.mirrorPCloud&&P3.scene){
      P3.scene.remove(P3.mirrorPCloud);
      P3.mirrorPCloud.geometry.dispose();
      P3.mirrorPCloud.material.dispose();
      P3.mirrorPCloud=null;
      P3.dirty=true;
    }
  }
}

async function p3HandleFile(f){
  if(!f) return;
  if(!_checkBrowserUploadSize(f)) return;
  const fname=$('p3-fname');
  if(fname) fname.textContent=f.name+' (로딩 중...)';
  $('p3-run-btn').disabled=true;
  p3ClearLog();

  // 내부 파이프라인 스텝 초기화
  ['pipe-step-load','pipe-step-denoise','pipe-step-mc','pipe-step-validate','pipe-step-repair','pipe-step-export']
    .forEach(id=>setPipeStep(id,'','대기'));
  const _vp=$('p3-val-panel'); if(_vp) _vp.style.display='none';
  setPipeStep('pipe-step-load','active','로딩...');
  p3SetProgress(5);

  // ── PLY면 클라이언트에서 파싱 → 뷰어 미리보기 (비동기 병렬) ──────
  const isPly=f.name.toLowerCase().endsWith('.ply');
  if(isPly){
    const bufProm=f.arrayBuffer();
    // 백엔드 업로드와 병렬로 미리보기 시작
    bufProm.then(async buf=>{
      try{
        await ensureP3ViewerReady();
        const parsed=parsePLY(buf);
        p3ShowPointPreview(parsed.verts, parsed.colors, parsed.colorsLinear);
        p3Log(`👁 포인트 클라우드 미리보기: ${(parsed.verts.length/3|0).toLocaleString()} pts`,'log-info');
      }catch(e){ console.warn('[P3 Preview]',e); }
    });
  }

  // ── 백엔드에 업로드 ──────────────────────────────────────────────
  try{
    const res=await loadP3File(f);
    if(!res||res.count<4) throw new Error('유효한 포인트가 4개 미만입니다');
    P3.pts=res;
    P3.loadedFileName=f.name;
    p3RefreshCmdBox();
    p3OnAlgoChange();
    if(fname) fname.textContent=f.name+(P3.hasNormals?' · 법선 포함':'');
    setPipeStep('pipe-step-load','done',`${res.count.toLocaleString()} pts ✓`);
    p3SetProgress(15);
    p3Log(`✅ ${f.name} 업로드 완료 (${res.count.toLocaleString()} pts)`,'log-ok');
    _p3UpdateRunBtnEnabled();
  }catch(err){
    P3.loadedFileName=null; P3.hasNormals=false; P3.sessionId=null;
    p3RefreshCmdBox();
    if(fname) fname.textContent='오류: '+err.message;
    setPipeStep('pipe-step-load','error','오류 ✗');
    // 에러 원인 구체화
    let hint='';
    if(err.message.includes('Failed to fetch')||err.message.includes('NetworkError')){
      hint='\n💡 백엔드 서버가 실행되지 않았습니다. 실행.bat을 다시 실행하세요.';
    } else if(err.message.includes('HTTP 4')){
      hint='\n💡 파일 형식 오류 또는 파일이 손상되었습니다.';
    } else if(err.message.includes('HTTP 5')){
      hint='\n💡 백엔드 처리 오류입니다. 로그를 확인하세요.';
    }
    p3Log('❌ 로드 오류: '+err.message+hint,'log-err');
    console.error('[P3 Load]',err);
    $('p3-run-btn').disabled=true;
  }
}

// ─── Page 2 Gizmo: poll until Three.js is ready ─────────────────────
(function waitForP2Gizmo(){
  const tryGizmo=()=>{
    if(P2.threeReady){ initP2Gizmo(); }
    else setTimeout(tryGizmo,300);
  };
  const origSwitch=window._p2GizmoHooked;
  if(!origSwitch){
    window._p2GizmoHooked=true;
    const checkInterval=setInterval(()=>{
      if($('page2').style.display!=='none' && P2.threeReady){
        initP2Gizmo();
        clearInterval(checkInterval);
      }
    },500);
  }
})();

// ═══════════════════════════════════════════════════════════════════════
