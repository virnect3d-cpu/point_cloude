'use strict';
// ═══════════════════════════════════════════════════════════════════════
//  전역 App 상태 — 이전의 _file, _blobs, _running 등 전역을 여기에 캡슐화.
//  여러 파일에서 공유하는 UI/세션 state (page local 은 P2/P3/P4/P5).
// ═══════════════════════════════════════════════════════════════════════
const App = {
  // ── Mutable state ──
  file: null,               // 현재 업로드된 File (Page 1)
  blobs: {},                // 처리된 blob 캐시 {format: Blob}
  running: false,           // 파이프라인 실행 중 플래그
  dirHandle: null,          // FSA 폴더 핸들 (브라우저 전용)
  nativeSaveDir: null,      // PyWebView 네이티브 저장 경로
  logText: '',              // 누적 로그 문자열 (#log-box 렌더링)
  autoSessionId: null,      // 🚀 전체 자동 처리 세션
  autoSSE: null,            // 자동 처리 EventSource
  // ── Constants (런타임 감지) ──
  // PyWebView 감지 — DOM 로드 후 약간의 딜레이 후에 window.pywebview 가 주입됨.
  IS_PYWEBVIEW: !!window.pywebview ||
                navigator.userAgent.includes('pywebview') ||
                !window.chrome,    // Edge WebView2는 chrome 객체 없음
  HAS_FSA:      false,             // 아래 init 에서 계산 (IS_PYWEBVIEW 필요)
  // ── Constants (파라미터 테이블) ──
  PART_RETENTION: {floor:1.10, wall:1.05, ceiling:0.90, object:1.00, props:0.80},
  PARAMS: {floorN:.85, ceilN:.85, wallN:.30, floorPct:15, ceilPct:85,
           dbscanEps:.15, dbscanMin:20, objThresh:.5},
};
App.HAS_FSA = ('showDirectoryPicker' in window) && !App.IS_PYWEBVIEW;

/** pywebview.api 를 가져옵니다. 준비될 때까지 최대 2초 대기. */
async function getPyApi(timeout=2000){
  if(window.pywebview?.api) return window.pywebview.api;
  const t0=Date.now();
  while(Date.now()-t0<timeout){
    await new Promise(r=>setTimeout(r,50));
    if(window.pywebview?.api) return window.pywebview.api;
  }
  return null;
}

// ═══════════════════════════════════════════════════════════════════════
//  SHARED LIBRARY LOADERS  (page 2/3/4 공용)
// ═══════════════════════════════════════════════════════════════════════
/**
 * Three.js 오브젝트 안전 제거 — scene에서 remove + geometry/material dispose.
 * Page 2/3/4에서 메쉬/와이어/포인트클라우드 정리할 때 매번 4-5줄 복붙하던 걸 하나로.
 *
 * Usage:
 *   P3.mesh = disposeFromScene(P3.scene, P3.mesh);   // null 반환 — 할당 편의
 *
 * @param {THREE.Scene} scene  — 해당 scene
 * @param {THREE.Object3D?} obj — mesh/line/points 등 Object3D (null 허용)
 * @returns {null} always null (재할당 편의)
 */
function disposeFromScene(scene, obj){
  if(!obj) return null;
  try{
    if(scene) scene.remove(obj);
    // Mesh/Line/Points 등 공통
    if(obj.geometry && typeof obj.geometry.dispose === 'function') obj.geometry.dispose();
    // material은 배열일 수도 있음 (multi-material)
    const mats = Array.isArray(obj.material) ? obj.material : [obj.material];
    mats.forEach(m => {
      if(!m) return;
      // 텍스처도 dispose (texture leak 방지)
      ['map','normalMap','aoMap','emissiveMap','roughnessMap','metalnessMap',
       'alphaMap','bumpMap','displacementMap','envMap','lightMap']
        .forEach(k => { if(m[k] && typeof m[k].dispose === 'function') m[k].dispose(); });
      if(typeof m.dispose === 'function') m.dispose();
    });
  }catch(e){ console.warn('disposeFromScene:', e); }
  return null;
}

/**
 * Three.js 로드 — 로컬 파일 우선(데스크톱/오프라인), 실패 시 CDN 폴백.
 * Returns a Promise that resolves when window.THREE is available.
 * 동시 호출 안전: script tag 중복 방지.
 */
function loadThreeJS(){
  if(window.THREE) return Promise.resolve();
  return new Promise((res, rej) => {
    // 이미 로드 중이면 polling으로 공유
    const existing = document.getElementById('three-script-tag');
    if(existing){
      const poll = setInterval(() => {
        if(window.THREE){ clearInterval(poll); res(); }
      }, 50);
      return;
    }
    const s = document.createElement('script');
    s.id = 'three-script-tag';
    s.src = 'three.min.js';      // 로컬 우선
    s.onload = res;
    s.onerror = () => {
      // 폴백: CDN (오프라인 데스크톱에서는 이 경로 거의 안 탐)
      const s2 = document.createElement('script');
      s2.src = 'https://cdnjs.cloudflare.com/ajax/libs/three.js/0.160.0/three.min.js';
      s2.onload = res;
      s2.onerror = () => rej(new Error('Three.js 로드 실패 (로컬·CDN 모두 실패)'));
      document.head.appendChild(s2);
    };
    document.head.appendChild(s);
  });
}

// ═══════════════════════════════════════════════════════════════════════
//  SSE via POST (EventSource 는 POST body 미지원 — fetch + ReadableStream)
// ═══════════════════════════════════════════════════════════════════════
/**
 * POST JSON 바디로 SSE 스트림을 소비.
 * @param {string} url
 * @param {Object} body - JSON 직렬화될 객체
 * @param {(ev: any) => void} onEvent - `data:` 라인에서 파싱된 JSON 이벤트
 * @param {Object} [opt]
 * @param {AbortSignal} [opt.signal]
 * @param {() => void} [opt.onError] - 네트워크/파싱 에러 시 호출 (이벤트 payload 에 error 필드가 있으면 호출 안 함, onEvent가 받음)
 * @returns {Promise<void>} 스트림 종료 시 resolve; signal abort / 네트워크 끊김 시 reject
 *
 * SSE 포맷: 각 이벤트는 `data: <json>\n\n` — 여러 라인 올 수 있으므로 \n\n 경계로 분할.
 */
async function postSSE(url, body, onEvent, opt = {}){
  const resp = await fetch(url, {
    method: 'POST',
    headers: {'Content-Type': 'application/json', 'Accept': 'text/event-stream'},
    body: JSON.stringify(body),
    signal: opt.signal,
  });
  if(!resp.ok){
    let detail = '';
    try{ detail = (await resp.json())?.detail || ''; }catch(_){}
    throw new Error(`HTTP ${resp.status}${detail ? ' · ' + detail : ''}`);
  }
  if(!resp.body) throw new Error('no response body (streaming unsupported)');

  const reader  = resp.body.getReader();
  const decoder = new TextDecoder('utf-8');
  let buf = '';
  while(true){
    const {value, done} = await reader.read();
    if(done) break;
    buf += decoder.decode(value, {stream: true});
    // SSE 이벤트 경계 \n\n
    let idx;
    while((idx = buf.indexOf('\n\n')) !== -1){
      const chunk = buf.slice(0, idx);
      buf = buf.slice(idx + 2);
      // 각 chunk 내 data: 로 시작하는 라인들을 합침 (스펙상 멀티라인 허용)
      const lines = chunk.split('\n').filter(l => l.startsWith('data:'));
      if(lines.length === 0) continue;
      const dataText = lines.map(l => l.slice(5).replace(/^ /, '')).join('\n');
      try{
        onEvent(JSON.parse(dataText));
      }catch(err){
        if(opt.onError) opt.onError(err); else console.warn('SSE parse:', err);
      }
    }
  }
}

// ═══════════════════════════════════════════════════════════════════════
//  UI HELPERS
// ═══════════════════════════════════════════════════════════════════════
const $  = id => document.getElementById(id);
App.logText = '';
function log(m){ App.logText+=m+'\n'; $('log-box').textContent=App.logText; $('log-box').scrollTop=9e9; }

/** PyWebView·데스크톱용 인앱 알림 (window.alert 대체) */
function appNotify(msg, title){
  const bg=$('app-dlg'), m=$('app-dlg-msg'), t=$('app-dlg-title');
  if(!bg||!m){ window.alert(msg); return; }
  if(t) t.textContent=title||'알림';
  m.textContent=msg;
  bg.style.display='flex';
}
function closeAppDlg(){
  const bg=$('app-dlg');
  if(bg) bg.style.display='none';
}

// 권장 사양 안내 팝업 — 첫 실행 시 (또는 "다시 보지 않기" 해제 시) 표시
function openSpecDlg(){
  const bg = $('spec-dlg');
  if(bg) bg.classList.add('show');
}
function closeSpecDlg(){
  const bg = $('spec-dlg');
  if(!bg) return;
  const hide = $('spec-hide');
  if(hide && hide.checked){
    try{ localStorage.setItem('pco.specDlgHide', '1'); }catch(_){}
  }
  bg.classList.remove('show');
}
document.addEventListener('DOMContentLoaded', () => {
  let hide = '0';
  try{ hide = localStorage.getItem('pco.specDlgHide') || '0'; }catch(_){}
  if(hide !== '1'){
    // 초기 렌더 후 살짝 뒤에 띄우기 (UI 로딩 튐 방지)
    setTimeout(openSpecDlg, 350);
  }
});
document.addEventListener('keydown', e=>{
  if(e.key!=='Escape') return;
  const bg=$('app-dlg');
  if(bg && bg.style.display==='flex'){ e.preventDefault(); closeAppDlg(); return; }
  const sp=$('spec-dlg');
  if(sp && sp.classList.contains('show')){ e.preventDefault(); closeSpecDlg(); }
});
function setProgress(p,m){ $('prog-bar').style.width=p+'%'; $('prog-text').textContent=m||p+'% 완료'; }

// ═══════════════════════════════════════════════════════════════════════
//  FORMAT METADATA
// ═══════════════════════════════════════════════════════════════════════
const FORMAT_INFO = {
  ply    :{ label:'PLY',    category:'PointCloud / Gaussian Splatting', color:'#B0B0B0' },
  xyz    :{ label:'XYZ',    category:'PointCloud — Plain Text',          color:'#B0B0B0' },
  pts    :{ label:'PTS',    category:'PointCloud — Leica Scanner',       color:'#B0B0B0' },
  pcd    :{ label:'PCD',    category:'PointCloud — PCL / ROS',           color:'#B0B0B0' },
  las    :{ label:'LAS',    category:'PointCloud — LiDAR Exchange',      color:'#B0B0B0' },
  laz    :{ label:'LAZ',    category:'PointCloud — Compressed LAS',      color:'#888888' },
  obj    :{ label:'OBJ',    category:'PointCloud — Wavefront Vertex',    color:'#B0B0B0' },
  ptx    :{ label:'PTX',    category:'PointCloud — Leica PTX Grid',      color:'#B0B0B0' },
  csv    :{ label:'CSV',    category:'PointCloud — Spreadsheet',         color:'#B0B0B0' },
  txt    :{ label:'TXT',    category:'PointCloud — Plain Text',          color:'#B0B0B0' },
  splat  :{ label:'SPLAT',  category:'Gaussian Splatting — Antimatter15',color:'#D0D0D0' },
  ksplat :{ label:'KSPLAT', category:'Gaussian Splatting — Kevin Kwok',  color:'#D0D0D0' },
};

// ═══════════════════════════════════════════════════════════════════════
//  OUTPUT FOLDER  (PyWebView 네이티브 다이얼로그 우선, FSA 폴백)
// ═══════════════════════════════════════════════════════════════════════
(function initOutputFolder(){
  const ofBtn  = $('of-btn');
  const ofWarn = $('of-warn');
  const ofClr  = $('of-clear-btn');

  if(App.IS_PYWEBVIEW){
    // PyWebView: 네이티브 폴더 선택 다이얼로그 사용
    if(ofWarn) ofWarn.style.display='none';
    if(ofBtn){
      ofBtn.textContent='📂 폴더 선택';
      ofBtn.style.display='';
      ofBtn.addEventListener('click', async()=>{
        const api=await getPyApi(); if(!api){ appNotify('PyWebView API 초기화 중...'); return; }
        const r=await api.pick_directory();
        if(!r.ok){ if(r.reason!=='cancelled') appNotify('폴더 선택 오류: '+r.reason); return; }
        App.nativeSaveDir=r.path;
        $('of-path').textContent=r.name;
        $('of-path').className='of-path set';
        $('of-badge').textContent='직접 저장';
        $('of-badge').className='of-badge pick';
        if(ofClr) ofClr.style.display='';
        log(`📁 출력 폴더 설정: ${r.path}`);
      });
    }
  } else if(App.HAS_FSA){
    // 브라우저 FSA API
    if(ofBtn) ofBtn.addEventListener('click', async()=>{
      try{
        const h=await window.showDirectoryPicker({mode:'readwrite'});
        App.dirHandle=h;
        $('of-path').textContent=h.name;
        $('of-path').className='of-path set';
        $('of-badge').textContent='직접 저장';
        $('of-badge').className='of-badge pick';
        if(ofClr) ofClr.style.display='';
        log(`📁 출력 폴더 설정: ${h.name}`);
      }catch(e){
        if(e.name!=='AbortError') appNotify('폴더 선택 오류: '+e.message);
      }
    });
  } else {
    if(ofWarn) ofWarn.style.display='block';
    if(ofBtn)  ofBtn.style.display='none';
  }

  if(ofClr) ofClr.addEventListener('click',()=>{
    App.dirHandle=null; App.nativeSaveDir=null;
    $('of-path').textContent='기본 다운로드 폴더';
    $('of-path').className='of-path fallback';
    $('of-badge').textContent='기본';
    $('of-badge').className='of-badge dl';
    if(ofClr) ofClr.style.display='none';
    log('📁 출력 폴더 해제');
  });
})();

// ─── 파일 저장 (PyWebView 네이티브 > FSA > 브라우저 다운로드) ──────────────
async function saveBlob(name, blob){
  // 1. PyWebView 네이티브 폴더 직접 쓰기
  if(App.IS_PYWEBVIEW && App.nativeSaveDir){
    const api=await getPyApi();
    if(api){
      try{
        const text=await blob.text();
        const r=await api.save_file_dialog(name, text);
        if(r.ok){
          log(`   💾 저장 완료 → ${r.path}`);
          const revBtn=$('st-open-btn');
          if(revBtn){ revBtn.style.display=''; revBtn.onclick=async()=>{ const a2=await getPyApi(); if(a2) a2.reveal_in_explorer(r.path); }; }
          return true;
        }
        if(r.reason==='cancelled') return false;
        log(`   ⚠️ 네이티브 저장 실패: ${r.reason}`);
      }catch(e){ log(`   ⚠️ PyWebView 저장 오류: ${e.message}`); }
    }
  }
  // 2. FSA (브라우저)
  if(App.dirHandle){
    try{
      const fh=await App.dirHandle.getFileHandle(name,{create:true});
      const wr=await fh.createWritable();
      await wr.write(blob); await wr.close();
      log(`   💾 저장 완료 → ${App.dirHandle.name}/${name}`);
      return true;
    }catch(e){ log(`   ⚠️ 직접 저장 실패 (${e.message})`); }
  }
  // 3. 브라우저 <a download> 폴백
  const a=document.createElement('a');
  a.style.display='none'; document.body.appendChild(a);
  a.href=URL.createObjectURL(blob); a.download=name; a.click();
  document.body.removeChild(a);
  setTimeout(()=>URL.revokeObjectURL(a.href),3000);
  return false;
}

// ═══════════════════════════════════════════════════════════════════════
//  DROP / FILE INPUT
// ═══════════════════════════════════════════════════════════════════════
const dz=$('drop-zone');
dz.addEventListener('click',()=>$('file-input').click());
dz.addEventListener('dragover',e=>{e.preventDefault();dz.classList.add('drag');});
dz.addEventListener('dragleave',()=>dz.classList.remove('drag'));
dz.addEventListener('drop',e=>{
  e.preventDefault();dz.classList.remove('drag');
  const f=e.dataTransfer.files[0];
  if(f) setFile(f); else appNotify('지원하지 않는 파일입니다.');
});
$('file-input').addEventListener('change',e=>{ if(e.target.files[0]) setFile(e.target.files[0]); });

// 파일 확장자 기반 자동 라우팅 — 메쉬 파일이면 다른 페이지로 안내
function _autoRouteByExtension(f){
  if(!f) return false;
  const nm = f.name.toLowerCase();
  const meshExts = ['.fbx','.glb','.gltf'];
  for(const e of meshExts){
    if(nm.endsWith(e)){
      // OBJ는 포인트 클라우드로도 쓰이니 예외 — FBX/GLB만 확실히 메쉬
      const go = confirm(
        `이 파일은 메쉬 파일(${e.toUpperCase()})입니다.\n\n` +
        `• 메쉬 변환은 포인트 클라우드(PLY/XYZ/PCD/LAS)만 지원\n` +
        `• 이 파일을 [🖼 텍스처 베이크] 페이지에서 쓰시겠어요?\n\n` +
        `확인: Page 4로 이동 / 취소: 그대로 Page 1`
      );
      if(go){
        switchTab(4);
        // Page 4의 메쉬 드롭에 파일 세팅
        setTimeout(() => {
          P4.objFile = f;
          const nameEl = $('p4-obj-name'); if(nameEl) nameEl.textContent = f.name;
          if(typeof p4UpdateStats === 'function') p4UpdateStats();
        }, 100);
        return true;
      }
    }
  }
  return false;
}

// 브라우저/WebView 업로드 2GB 안전 한도 체크 — 넘으면 경고 후 차단
const BROWSER_UPLOAD_LIMIT = 2 * 1024 * 1024 * 1024;
function _checkBrowserUploadSize(f){
  if(!f) return true;
  if(f.size > BROWSER_UPLOAD_LIMIT){
    const gb = (f.size/1024/1024/1024).toFixed(2);
    appNotify(
      `⚠️ 파일이 너무 큽니다 (${gb} GB)\n\n` +
      `브라우저/WebView 업로드는 2 GB까지만 안정적입니다.\n` +
      `더 큰 파일은 메모리 부족(OOM)으로 앱이 멈출 수 있습니다.\n\n` +
      `해결 방법:\n` +
      `• 2 GB 이하로 자르거나 다운샘플해 주세요\n` +
      `• 또는 32 GB 이상 RAM의 PC에서 사용`,
      '파일 크기 초과 (2 GB)'
    );
    return false;
  }
  return true;
}

function setFile(f){
  // 메쉬 파일이면 다른 페이지로 라우팅
  if(_autoRouteByExtension(f)) return;
  // 브라우저 업로드 2GB 한도 체크
  if(!_checkBrowserUploadSize(f)) return;
  App.file=f;
  const ext=f.name.split('.').pop().toLowerCase();
  const info=FORMAT_INFO[ext]||{label:ext.toUpperCase(),category:'Unknown',color:'#94A3B8'};
  $('file-name').textContent=`✅  ${f.name}  (${(f.size/1024/1024).toFixed(1)} MB)`;
  $('fmt-bar').style.display='flex';
  $('fmt-name').textContent=info.label;
  $('fmt-type').textContent=info.category;
  $('fmt-type').style.cssText=`background:${info.color}22;color:${info.color};border:1px solid ${info.color}44;
    padding:2px 7px;border-radius:4px;font-size:10px;font-weight:700`;
  $('fmt-pts').textContent='';
  $('run-btn').disabled=false;
  const ab = $('auto-run-btn'); if(ab){ ab.disabled = false; ab.textContent = '🚀 전체 자동 처리'; }
  // 자동처리 진행바 리셋
  const prog = $('auto-prog-wrap'); if(prog) prog.classList.remove('show');
}

document.querySelectorAll('.q-card').forEach(c=>c.addEventListener('click',()=>c.classList.toggle('active')));

// ═══════════════════════════════════════════════════════════════════════
//  CLEAR
// ═══════════════════════════════════════════════════════════════════════
$('clear-btn').addEventListener('click',()=>{
  App.file=null;App.blobs={};App.logText='';
  $('file-name').textContent='';$('file-input').value='';
  $('log-box').textContent='대기 중...\n';$('run-btn').disabled=true;
  $('prog-section').style.display='none';$('result-section').style.display='none';
  $('fmt-bar').style.display='none';$('save-toast').classList.remove('show');
  setProgress(0,'');
});

// ═══════════════════════════════════════════════════════════════════════
//  RUN
// ═══════════════════════════════════════════════════════════════════════
$('run-btn').addEventListener('click',async()=>{
  if(App.running||!App.file) return;
  const levels=[...document.querySelectorAll('.q-card.active')]
    .map(c=>+c.dataset.q).filter(Boolean);
  if(!levels.length){appNotify('품질 레벨을 선택하세요.');return;}
  App.running=true;App.blobs={};App.logText='';
  $('prog-section').style.display='';$('result-section').style.display='none';
  $('file-list').innerHTML='';$('run-btn').disabled=true;$('run-btn').textContent='⏳ 처리 중...';
  try {
    await runPipeline(App.file, levels.sort((a,b)=>b-a));
    $('result-section').style.display='';
  } catch(e){
    log('\n❌ 오류: '+e.message+'\n'+e.stack);
    console.error(e);
  }
  App.running=false;$('run-btn').disabled=false;$('run-btn').textContent='🚀 변환 시작';
});

// ═══════════════════════════════════════════════════════════════════════
//  PIPELINE
// ═══════════════════════════════════════════════════════════════════════
async function runPipeline(file, levels){
  const t0=performance.now();
  log('📂 파일 읽는 중...');
  setProgress(2,'파일 읽는 중...');
  const buf=await file.arrayBuffer();
  const ext=file.name.split('.').pop().toLowerCase();

  let parsed;
  switch(ext){
    case 'ply':   parsed=parsePLY(buf);   break;
    case 'xyz':
    case 'pts':
    case 'txt':   parsed=parseXYZ(buf);   break;
    case 'csv':   parsed=parseCSV(buf);   break;
    case 'pcd':   parsed=parsePCD(buf);   break;
    case 'las':   parsed=parseLAS(buf);   break;
    case 'laz':   parsed=await _decodeViaBackend(file, 'LAZ (lazrs)'); break;
    case 'obj':   parsed=parseOBJ(buf);   break;
    case 'ptx':   parsed=parsePTX(buf);   break;
    case 'splat': parsed=parseSPLAT(buf); break;
    case 'ksplat':parsed=parseKSPLAT(buf);break;
    default: throw new Error(`지원하지 않는 확장자: .${ext}`);
  }

  const {verts,normals,colors,hasNormals,hasColors,colorsLinear,formatNote}=parsed;
  const n=verts.length/3;
  if(n<10) throw new Error('포인트가 너무 적습니다. 파일을 확인하세요.');
  log(`   ✅ ${n.toLocaleString()} 포인트 로드${formatNote?' ('+formatNote+')':''}`);
  $('fmt-pts').textContent=n.toLocaleString()+' pts';

  if(!hasNormals) log('   ⚠️  Normal 없음 → Z-up 기본값 적용');

  log('🔪 파츠 분리 중...'); setProgress(32,'파츠 분리 중...');
  const masks=separateParts(verts,normals,n,App.PARAMS);
  Object.entries(masks).forEach(([k,m])=>{
    const c=m.filter(Boolean).length;
    log(`   ${k.padEnd(8)}: ${c.toLocaleString().padStart(9)} pts (${(c/n*100).toFixed(1)}%)`);
  });

  log('📋 meta.json 생성 중...'); setProgress(55,'meta.json...');
  const meta=buildMeta(verts,masks,n);
  const metaStr=JSON.stringify(meta,null,2);
  App.blobs['meta.json']=new Blob([metaStr],{type:'application/json'});
  log(`   ✅ 벽:${meta.walls.length}개  오브젝트:${meta.objects.length}개`);

  const base=58, perQ=(95-base)/Math.max(levels.length,1);
  for(let i=0;i<levels.length;i++){
    const q=levels[i],ratio=q/100;
    log(`\n⚙️  scene_opt_${q}.ply 생성 중 (${q}%)...`);
    setProgress(base+perQ*i,`opt_${q}.ply...`);
    const {sv,sn,sc,cnt,voxelSizeAvg}=samplePointcloud(verts,normals,colors,masks,n,ratio);
    const plyBuf=writePLY(sv,sn,sc,cnt,colorsLinear);
    const fname=`scene_opt_${q}.ply`;
    App.blobs[fname]=new Blob([plyBuf],{type:'application/octet-stream'});
    const vsInfo=voxelSizeAvg?` · 간격≈${voxelSizeAvg.toFixed(3)}m`:'';
    const cInfo=hasColors?` · sRGB→Linear`:'· 색상 없음(흰색)';
    log(`   ✅ ${cnt.toLocaleString()} pts / ${(plyBuf.byteLength/1024/1024).toFixed(1)} MB${vsInfo}${cInfo}`);
    addResultFile(fname,plyBuf.byteLength,q);
  }
  addResultFile('meta.json',new TextEncoder().encode(metaStr).byteLength,null);
  const elapsed=((performance.now()-t0)/1000).toFixed(1);
  setProgress(100,`✅ 완료! (${elapsed}초)`);
  log(`\n🎉 완료! (${elapsed}초)`);

  // 폴더 지정 시 자동 저장
  if(App.dirHandle){
    log(`\n💾 지정 폴더에 자동 저장 중 → ${App.dirHandle.name}/`);
    for(const name of Object.keys(App.blobs)) await saveBlob(name,App.blobs[name]);
    log('   ✅ 전체 자동 저장 완료');
    setProgress(100,`✅ 완료 + 저장 (${elapsed}초) → ${App.dirHandle.name}/`);
    showSaveToast(App.dirHandle.name, false);
  }
}

// ═══════════════════════════════════════════════════════════════════════
//  ① PLY PARSER  (Point Cloud + Gaussian Splatting 자동 감지)
// ═══════════════════════════════════════════════════════════════════════
function parsePLY(buf){
  const u8=new Uint8Array(buf);
  const END=new TextEncoder().encode('end_header\n');
  let hEnd=0;
  for(let i=0;i<u8.length-END.length;i++){
    let ok=true; for(let j=0;j<END.length;j++) if(u8[i+j]!==END[j]){ok=false;break;}
    if(ok){hEnd=i+END.length;break;}
  }
  const header=new TextDecoder().decode(u8.slice(0,hEnd));
  let nV=0,isBinLE=false; const props=[];let inV=false;
  header.split('\n').forEach(l=>{
    l=l.trim();
    if(l.startsWith('format binary_little_endian')) isBinLE=true;
    else if(l.startsWith('element vertex')){nV=+l.split(' ')[2];inV=true;}
    else if(l.startsWith('element ')&&!l.startsWith('element vertex')) inV=false;
    else if(l.startsWith('property ')&&inV){const[,t,n]=l.split(' ');props.push({t,n});}
  });
  if(!isBinLE) throw new Error('ASCII PLY 미지원 — Binary LE PLY만 가능');

  // Gaussian Splatting PLY 감지
  const pNames=props.map(p=>p.n);
  const isGS=pNames.includes('f_dc_0')&&pNames.includes('opacity');
  if(isGS) return parsePLY_GS(buf,hEnd,nV,props);

  // 일반 PLY
  const SIZES={float:4,float32:4,int:4,uint:4,int32:4,uint32:4,
               double:8,float64:8,short:2,int16:2,uint16:2,ushort:2,
               char:1,uchar:1,int8:1,uint8:1};
  const offs=[]; let stride=0;
  props.forEach(p=>{offs.push(stride);stride+=(SIZES[p.t]||4);});
  const idx={};props.forEach((p,i)=>idx[p.n]=i);
  const hasN='nx' in idx&&'ny' in idx&&'nz' in idx;
  const hasC='red' in idx&&'green' in idx&&'blue' in idx;
  // float 타입 컬러 vs uchar 컬러 — 타입 체크로 자동 분기
  const colorIsFloat=hasC&&(props[idx.red].t==='float'||props[idx.red].t==='float32');
  const verts=new Float32Array(nV*3),normals=new Float32Array(nV*3);
  const colors=hasC?new Float32Array(nV*3):null;
  const dv=new DataView(buf,hEnd);
  for(let i=0;i<nV;i++){
    const b=i*stride;
    verts[i*3]=dv.getFloat32(b+offs[idx.x],true);
    verts[i*3+1]=dv.getFloat32(b+offs[idx.y],true);
    verts[i*3+2]=dv.getFloat32(b+offs[idx.z],true);
    if(hasN){normals[i*3]=dv.getFloat32(b+offs[idx.nx],true);
             normals[i*3+1]=dv.getFloat32(b+offs[idx.ny],true);
             normals[i*3+2]=dv.getFloat32(b+offs[idx.nz],true);}
    if(hasC){
      if(colorIsFloat){
        colors[i*3]  =Math.max(0,Math.min(1,dv.getFloat32(b+offs[idx.red],  true)));
        colors[i*3+1]=Math.max(0,Math.min(1,dv.getFloat32(b+offs[idx.green],true)));
        colors[i*3+2]=Math.max(0,Math.min(1,dv.getFloat32(b+offs[idx.blue], true)));
      } else {
        colors[i*3]=dv.getUint8(b+offs[idx.red])/255;
        colors[i*3+1]=dv.getUint8(b+offs[idx.green])/255;
        colors[i*3+2]=dv.getUint8(b+offs[idx.blue])/255;
      }
    } else if(normals[i*3+2]===0&&!hasN) normals[i*3+2]=1;
  }
  if(!hasN) for(let i=0;i<nV;i++) normals[i*3+2]=1;
  // float 컬러는 이미 linear 공간 (Blender Base Color)
  return {verts,normals,colors,hasNormals:hasN,hasColors:hasC,
          colorsLinear:colorIsFloat,formatNote:colorIsFloat?'PLY (float linear)':'PLY'};
}

// Gaussian Splatting PLY — 3DGS 형식
function parsePLY_GS(buf,hEnd,nV,props){
  const SIZES={float:4,float32:4,int:4,uint:4,double:8,
               short:2,ushort:2,char:1,uchar:1,int8:1,uint8:1};
  const offs=[]; let stride=0;
  props.forEach(p=>{offs.push(stride);stride+=(SIZES[p.t]||4);});
  const idx={};props.forEach((p,i)=>idx[p.n]=i);
  const SH_C0=0.28209479177387814;
  const verts=new Float32Array(nV*3),normals=new Float32Array(nV*3);
  const colors=new Float32Array(nV*3);
  const dv=new DataView(buf,hEnd);
  for(let i=0;i<nV;i++){
    const b=i*stride;
    verts[i*3]  =dv.getFloat32(b+offs[idx.x],true);
    verts[i*3+1]=dv.getFloat32(b+offs[idx.y],true);
    verts[i*3+2]=dv.getFloat32(b+offs[idx.z],true);
    // SH DC → linear RGB  (f_dc * SH_C0 + 0.5, clamped)
    colors[i*3]  =Math.max(0,Math.min(1,dv.getFloat32(b+offs[idx.f_dc_0],true)*SH_C0+.5));
    colors[i*3+1]=Math.max(0,Math.min(1,dv.getFloat32(b+offs[idx.f_dc_1],true)*SH_C0+.5));
    colors[i*3+2]=Math.max(0,Math.min(1,dv.getFloat32(b+offs[idx.f_dc_2],true)*SH_C0+.5));
    // 회전(quaternion) → 앞방향 벡터 → normal
    if('rot_0' in idx){
      const w=dv.getFloat32(b+offs[idx.rot_0],true),x=dv.getFloat32(b+offs[idx.rot_1],true),
            y=dv.getFloat32(b+offs[idx.rot_2],true),z=dv.getFloat32(b+offs[idx.rot_3],true);
      normals[i*3]  =2*(x*z+w*y);
      normals[i*3+1]=2*(y*z-w*x);
      normals[i*3+2]=1-2*(x*x+y*y);
    } else { normals[i*3+2]=1; }
  }
  // GS SH계수 → 선형 방사 휘도이므로 colorsLinear:true
  return {verts,normals,colors,hasNormals:true,hasColors:true,colorsLinear:true,formatNote:'Gaussian Splatting PLY'};
}

// ═══════════════════════════════════════════════════════════════════════
//  ② XYZ / PTS / TXT
//  지원 형식:
//    x y z
//    x y z r g b
//    x y z intensity nx ny nz
//    x y z intensity r g b  (Leica PTS)
// ═══════════════════════════════════════════════════════════════════════
function parseXYZ(buf){
  const text=new TextDecoder().decode(buf);
  const lines=text.split('\n');
  const vA=[],nA=[],cA=[];
  let hasN=false,hasC=false;
  for(let li=0;li<lines.length;li++){
    const l=lines[li].trim();
    if(!l||l.startsWith('#')||l.startsWith('//')) continue;
    const p=l.split(/[\s,;]+/).map(Number);
    if(p.length<3||isNaN(p[0])) continue;
    vA.push(p[0],p[1],p[2]);
    if(p.length>=6){
      // x y z [intensity] r g b  or  x y z nx ny nz
      const v4=p[3],v5=p[4],v6=p[5];
      if(p.length===6){
        // x y z r g b (0-255 or 0-1)
        const sc = (v4>1||v5>1||v6>1)?255:1;
        cA.push(v4/sc,v5/sc,v6/sc); hasC=true;
        nA.push(0,0,1);
      } else if(p.length>=7){
        // x y z intensity r g b  (PTS)
        const sc=(p[4]>1||p[5]>1||p[6]>1)?255:1;
        cA.push(p[4]/sc,p[5]/sc,p[6]/sc); hasC=true; nA.push(0,0,1);
      }
    } else { nA.push(0,0,1); }
  }
  const n=vA.length/3;
  const verts=new Float32Array(vA),normals=new Float32Array(nA);
  const colors=hasC?new Float32Array(cA):null;
  if(!hasC) for(let i=0;i<n;i++) normals[i*3+2]=1;
  return {verts,normals,colors,hasNormals:false,hasColors:hasC,colorsLinear:false,formatNote:'XYZ/PTS'};
}

// ═══════════════════════════════════════════════════════════════════════
//  ③ CSV  (헤더 자동 감지)
// ═══════════════════════════════════════════════════════════════════════
function parseCSV(buf){
  const text=new TextDecoder().decode(buf);
  const lines=text.split('\n').filter(l=>l.trim());
  let startLine=0;
  // 헤더 행 감지 (첫 행 숫자가 아니면 헤더)
  const first=lines[0].split(/,|\t/).map(v=>v.trim().toLowerCase());
  if(isNaN(parseFloat(first[0]))) startLine=1;
  // 컬럼 매핑
  const xi=first.indexOf('x'), yi=first.indexOf('y'), zi=first.indexOf('z');
  const ri=first.indexOf('r')>=0?first.indexOf('r'):first.indexOf('red');
  const gi=first.indexOf('g')>=0?first.indexOf('g'):first.indexOf('green');
  const bi=first.indexOf('b')>=0?first.indexOf('b'):first.indexOf('blue');
  const hasC2=ri>=0&&gi>=0&&bi>=0;
  const xIdx=xi>=0?xi:0,yIdx=yi>=0?yi:1,zIdx=zi>=0?zi:2;
  const vA=[],cA=[];
  for(let li=startLine;li<lines.length;li++){
    const p=lines[li].split(/,|\t/).map(v=>parseFloat(v.trim()));
    if(p.length<3||isNaN(p[xIdx])) continue;
    vA.push(p[xIdx],p[yIdx],p[zIdx]);
    if(hasC2){ const sc=(p[ri]>1||p[gi]>1||p[bi]>1)?255:1;cA.push(p[ri]/sc,p[gi]/sc,p[bi]/sc);}
  }
  const n=vA.length/3;
  const verts=new Float32Array(vA),normals=new Float32Array(n*3);
  for(let i=0;i<n;i++) normals[i*3+2]=1;
  return {verts,normals,colors:hasC2?new Float32Array(cA):null,
          hasNormals:false,hasColors:hasC2,colorsLinear:false,formatNote:'CSV'};
}

// ═══════════════════════════════════════════════════════════════════════
//  ④ PCD  (PCL — ASCII + Binary)
// ═══════════════════════════════════════════════════════════════════════
function parsePCD(buf){
  const u8=new Uint8Array(buf);
  // 헤더 끝 위치 탐색
  const DATA_ASCII=new TextEncoder().encode('DATA ascii');
  const DATA_BIN  =new TextEncoder().encode('DATA binary\n');
  let hEnd=0, isBin=false;
  for(let i=0;i<u8.length-12;i++){
    let okA=true,okB=true;
    for(let j=0;j<DATA_ASCII.length;j++) if(u8[i+j]!==DATA_ASCII[j]){okA=false;break;}
    for(let j=0;j<DATA_BIN.length;j++)   if(u8[i+j]!==DATA_BIN[j]){okB=false;break;}
    if(okA){hEnd=i+DATA_ASCII.length; while(hEnd<u8.length&&u8[hEnd]!==10)hEnd++; hEnd++;break;}
    if(okB){hEnd=i+DATA_BIN.length; isBin=true; break;}
  }
  const header=new TextDecoder().decode(u8.slice(0,hEnd));
  const hLines={};
  header.split('\n').forEach(l=>{
    const p=l.trim().split(/\s+/);
    if(p.length>=2) hLines[p[0].toLowerCase()]=p.slice(1);
  });
  const fields=(hLines.fields||[]).map(f=>f.toLowerCase());
  const sizes=(hLines.size||[]).map(Number);
  const types=(hLines.type||[]);
  const counts=(hLines.count||[]).map(Number);
  const nPts=+(hLines.points||[0])[0];

  const xi=fields.indexOf('x'),yi=fields.indexOf('y'),zi=fields.indexOf('z');
  const nxi=fields.indexOf('normal_x'),nyi=fields.indexOf('normal_y'),nzi=fields.indexOf('normal_z');
  const ri=fields.indexOf('r')>=0?fields.indexOf('r'):
           fields.indexOf('rgb')>=0?fields.indexOf('rgb'):-1;
  const gi=fields.indexOf('g'),bi=fields.indexOf('b');
  const hasN2=nxi>=0&&nyi>=0&&nzi>=0;
  const hasC2=ri>=0&&gi>=0&&bi>=0;

  // 오프셋 계산
  const offs2=[]; let stride2=0;
  for(let i=0;i<fields.length;i++){
    offs2.push(stride2); stride2+=sizes[i]*(counts[i]||1);
  }

  const verts=new Float32Array(nPts*3),normals=new Float32Array(nPts*3);
  const colors=hasC2?new Float32Array(nPts*3):null;

  const readF=(dv,off,t,s)=>{
    if(t==='F'&&s===4) return dv.getFloat32(off,true);
    if(t==='F'&&s===8) return dv.getFloat64(off,true);
    if(t==='U'&&s===1) return dv.getUint8(off);
    if(t==='U'&&s===2) return dv.getUint16(off,true);
    if(t==='U'&&s===4) return dv.getUint32(off,true);
    if(t==='I'&&s===4) return dv.getInt32(off,true);
    return 0;
  };

  if(isBin){
    const dv=new DataView(buf,hEnd);
    for(let i=0;i<nPts;i++){
      const b=i*stride2;
      verts[i*3]  =readF(dv,b+offs2[xi],types[xi],sizes[xi]);
      verts[i*3+1]=readF(dv,b+offs2[yi],types[yi],sizes[yi]);
      verts[i*3+2]=readF(dv,b+offs2[zi],types[zi],sizes[zi]);
      if(hasN2){normals[i*3]=readF(dv,b+offs2[nxi],types[nxi],sizes[nxi]);
                normals[i*3+1]=readF(dv,b+offs2[nyi],types[nyi],sizes[nyi]);
                normals[i*3+2]=readF(dv,b+offs2[nzi],types[nzi],sizes[nzi]);}
      if(hasC2){colors[i*3]=readF(dv,b+offs2[ri],types[ri],sizes[ri])/255;
                colors[i*3+1]=readF(dv,b+offs2[gi],types[gi],sizes[gi])/255;
                colors[i*3+2]=readF(dv,b+offs2[bi],types[bi],sizes[bi])/255;}
    }
  } else {
    const text=new TextDecoder().decode(u8.slice(hEnd));
    const lines=text.split('\n');
    let pi=0;
    for(let li=0;li<lines.length&&pi<nPts;li++){
      const p=lines[li].trim().split(/\s+/).map(Number);
      if(p.length<3||isNaN(p[0])) continue;
      verts[pi*3]=p[xi];verts[pi*3+1]=p[yi];verts[pi*3+2]=p[zi];
      if(hasN2){normals[pi*3]=p[nxi];normals[pi*3+1]=p[nyi];normals[pi*3+2]=p[nzi];}
      if(hasC2){colors[pi*3]=p[ri]/255;colors[pi*3+1]=p[gi]/255;colors[pi*3+2]=p[bi]/255;}
      pi++;
    }
  }
  if(!hasN2) for(let i=0;i<nPts;i++) normals[i*3+2]=1;
  return {verts,normals,colors,hasNormals:hasN2,hasColors:hasC2,colorsLinear:false,
          formatNote:`PCD ${isBin?'Binary':'ASCII'}`};
}

// ═══════════════════════════════════════════════════════════════════════
//  ⑤ LAS 1.2 / 1.4  (Binary)
// ═══════════════════════════════════════════════════════════════════════
// ───────────────────────────────────────────────────────────────────────
// LAZ(압축 LAS) 백엔드 디코드 경로
//   - laspy+lazrs 로 파싱 → /api/points-binary/{sid} 로 Float32Array 수신
//   - parseLAS 와 동일한 스키마({verts, normals, colors, ...}) 반환
// ───────────────────────────────────────────────────────────────────────
async function _decodeViaBackend(file, label){
  log(`🔗 ${label||'backend'} 디코드 요청 중 (laspy 서버)...`);
  setProgress(6, `${label||'backend'} 디코드 중...`);
  const fd = new FormData();
  fd.append('file', file, file.name);
  let up;
  try{
    const r = await fetch('/api/upload', {method:'POST', body:fd});
    if(!r.ok){
      let msg = `HTTP ${r.status}`;
      try{ const j = await r.json(); msg = j.detail || msg; }catch(_){}
      throw new Error(msg);
    }
    up = await r.json();
  }catch(e){
    throw new Error(
      `${label||'LAZ'} 백엔드 디코드 실패\n` +
      `(백엔드 laspy+lazrs 설치가 필요합니다)\n\n원본 오류: ${e.message||e}`
    );
  }
  const sid = up.session_id;
  log(`   ✅ 백엔드 세션 ${sid} · ${up.point_count.toLocaleString()} pts`);
  setProgress(12, '포인트 데이터 수신 중...');
  const rb = await fetch(`/api/points-binary/${sid}`);
  if(!rb.ok) throw new Error(`포인트 바이너리 수신 실패 (HTTP ${rb.status})`);
  const ab = await rb.arrayBuffer();
  const dv = new DataView(ab);
  const n      = dv.getInt32(0, true);
  const hasN   = dv.getInt32(4, true) === 1;
  const hasC   = dv.getInt32(8, true) === 1;
  const offV   = 12;
  const bytesV = n*3*4;
  const verts  = new Float32Array(ab.slice(offV, offV + bytesV));
  let cursor   = offV + bytesV;
  let normals  = new Float32Array(n*3);
  if(hasN){
    normals = new Float32Array(ab.slice(cursor, cursor + bytesV));
    cursor += bytesV;
  } else {
    for(let i=0;i<n;i++) normals[i*3+2] = 1; // fallback Z-up
  }
  let colors = null;
  if(hasC){
    colors = new Float32Array(ab.slice(cursor, cursor + bytesV));
  }
  return {verts, normals, colors,
          hasNormals: hasN, hasColors: hasC, colorsLinear: false,
          formatNote: `${label||'backend'} · ${n.toLocaleString()} pts`};
}

function parseLAS(buf){
  const dv=new DataView(buf);
  const sig=String.fromCharCode(dv.getUint8(0),dv.getUint8(1),dv.getUint8(2),dv.getUint8(3));
  if(sig!=='LASF') throw new Error('LAS 파일 서명 오류 (LASF가 아님)');
  const vMaj=dv.getUint8(24), vMin=dv.getUint8(25);
  const ptOffset=dv.getUint32(96,true);
  const ptFormat=dv.getUint8(104);
  const ptLen=dv.getUint16(105,true);
  // LAS 1.4: nPts = getUint64 at 247; LAS 1.2: getUint32 at 107
  const nPts = vMaj>=1&&vMin>=4
    ? Number(dv.getBigUint64(247,true))
    : dv.getUint32(107,true);
  const scaleX=dv.getFloat64(131,true),scaleY=dv.getFloat64(139,true),scaleZ=dv.getFloat64(147,true);
  const offX=dv.getFloat64(155,true),offY=dv.getFloat64(163,true),offZ=dv.getFloat64(171,true);
  // 포맷 2,3,7,8 → RGB 포함
  const hasC2=[2,3,5,7,8,10].includes(ptFormat);
  const verts=new Float32Array(nPts*3),normals=new Float32Array(nPts*3);
  const colors=hasC2?new Float32Array(nPts*3):null;
  for(let i=0;i<nPts;i++){
    const b=ptOffset+i*ptLen;
    verts[i*3]  =(dv.getInt32(b,true)*scaleX+offX);
    verts[i*3+1]=(dv.getInt32(b+4,true)*scaleY+offY);
    verts[i*3+2]=(dv.getInt32(b+8,true)*scaleZ+offZ);
    normals[i*3+2]=1;
    if(hasC2){
      // RGB color: 16bit unsigned → 0-1
      let rOff=0;
      if(ptFormat===2)  rOff=20;
      else if(ptFormat===3||ptFormat===5) rOff=28;
      else if(ptFormat===7) rOff=30;
      else if(ptFormat===8) rOff=30;
      else rOff=20;
      colors[i*3]  =dv.getUint16(b+rOff,true)/65535;
      colors[i*3+1]=dv.getUint16(b+rOff+2,true)/65535;
      colors[i*3+2]=dv.getUint16(b+rOff+4,true)/65535;
    }
  }
  return {verts,normals,colors,hasNormals:false,hasColors:hasC2,colorsLinear:false,
          formatNote:`LAS v${vMaj}.${vMin} fmt${ptFormat}`};
}

// ═══════════════════════════════════════════════════════════════════════
//  ⑥ OBJ  (vertex 추출)
// ═══════════════════════════════════════════════════════════════════════
function parseOBJ(buf){
  const text=new TextDecoder().decode(buf);
  const vA=[],nA=[],cA=[];let hasC2=false,hasN2=false;
  text.split('\n').forEach(l=>{
    l=l.trim();
    if(l.startsWith('v ')){
      const p=l.slice(2).trim().split(/\s+/).map(Number);
      vA.push(p[0],p[1],p[2]);
      if(p.length>=6){cA.push(p[3],p[4],p[5]);hasC2=true;}else cA.push(1,1,1);
      nA.push(0,0,1);
    } else if(l.startsWith('vn ')){
      // 이미 vn 있으면 나중에 매핑 복잡하므로 스킵 (인덱스 미지원)
    }
  });
  const n=vA.length/3;
  return {verts:new Float32Array(vA),normals:new Float32Array(nA),
          colors:hasC2?new Float32Array(cA):null,
          hasNormals:false,hasColors:hasC2,colorsLinear:false,formatNote:'OBJ Vertices'};
}

// ═══════════════════════════════════════════════════════════════════════
//  ⑦ PTX  (Leica 그리드 포맷)
//  형식: 각 행 = x y z intensity  or  x y z intensity r g b
// ═══════════════════════════════════════════════════════════════════════
function parsePTX(buf){
  const text=new TextDecoder().decode(buf);
  const lines=text.split('\n');
  const vA=[],cA=[];let hasC2=false;
  let state='header',headerLines=0;
  for(let li=0;li<lines.length;li++){
    const l=lines[li].trim();
    if(!l) continue;
    // PTX 헤더: cols rows, 3 transform lines, 4x4 matrix
    if(state==='header'){ headerLines++; if(headerLines>=10) state='data'; continue; }
    const p=l.split(/\s+/).map(Number);
    if(p.length<4||isNaN(p[0])||p[0]===0&&p[1]===0&&p[2]===0) continue;
    vA.push(p[0],p[1],p[2]);
    if(p.length>=7){
      const sc=p[4]>1||p[5]>1||p[6]>1?255:1;
      cA.push(p[4]/sc,p[5]/sc,p[6]/sc);hasC2=true;
    } else { cA.push(1,1,1); }
  }
  const n=vA.length/3;
  const normals=new Float32Array(n*3);
  for(let i=0;i<n;i++) normals[i*3+2]=1;
  return {verts:new Float32Array(vA),normals,
          colors:hasC2?new Float32Array(cA):null,
          hasNormals:false,hasColors:hasC2,colorsLinear:false,formatNote:'PTX'};
}

// ═══════════════════════════════════════════════════════════════════════
//  ⑧ SPLAT  (Antimatter15 — 32 bytes/gaussian)
//  [0-11]  xyz float32   [12-23] scale float32
//  [24-27] rgba uint8    [28-31] rot uint8 (w,x,y,z → [-1,1])
// ═══════════════════════════════════════════════════════════════════════
function parseSPLAT(buf){
  const STRIDE=32;
  const nG=Math.floor(buf.byteLength/STRIDE);
  if(nG<1) throw new Error('SPLAT 파일이 비어 있거나 형식 오류');
  const verts=new Float32Array(nG*3),normals=new Float32Array(nG*3);
  const colors=new Float32Array(nG*3);
  const dv=new DataView(buf);
  for(let i=0;i<nG;i++){
    const b=i*STRIDE;
    verts[i*3]  =dv.getFloat32(b,   true);
    verts[i*3+1]=dv.getFloat32(b+4, true);
    verts[i*3+2]=dv.getFloat32(b+8, true);
    colors[i*3]  =dv.getUint8(b+24)/255;
    colors[i*3+1]=dv.getUint8(b+25)/255;
    colors[i*3+2]=dv.getUint8(b+26)/255;
    // 회전 → 앞 방향 (Z축)
    const ow=dv.getUint8(b+28)/128-1, ox=dv.getUint8(b+29)/128-1,
          oy=dv.getUint8(b+30)/128-1, oz=dv.getUint8(b+31)/128-1;
    normals[i*3]  =2*(ox*oz+ow*oy);
    normals[i*3+1]=2*(oy*oz-ow*ox);
    normals[i*3+2]=1-2*(ox*ox+oy*oy);
  }
  // SPLAT rgba는 uchar sRGB
  return {verts,normals,colors,hasNormals:true,hasColors:true,colorsLinear:false,
          formatNote:`SPLAT (${nG.toLocaleString()} gaussians)`};
}

// ═══════════════════════════════════════════════════════════════════════
//  ⑨ KSPLAT  (Kevin Kwok — 압축 헤더 + 청크)
//  헤더: 1424 bytes, 이후 32bytes/splat (동일 구조)
// ═══════════════════════════════════════════════════════════════════════
function parseKSPLAT(buf){
  const HEADER=1424, STRIDE=32;
  const data=buf.slice(HEADER);
  return parseSPLAT(data);  // 이후 구조 동일
}

// ═══════════════════════════════════════════════════════════════════════
//  PARTS SEPARATION
// ═══════════════════════════════════════════════════════════════════════
function percentile(arr,p){
  const s=[...arr].sort((a,b)=>a-b);return s[Math.floor(s.length*p/100)];
}
function separateParts(verts,normals,n,P){
  const zArr=new Float32Array(n);
  for(let i=0;i<n;i++) zArr[i]=verts[i*3+2];
  const zLo=percentile(zArr,P.floorPct),zHi=percentile(zArr,P.ceilPct);
  const isFloor=new Array(n).fill(false),isWall=new Array(n).fill(false),
        isCeiling=new Array(n).fill(false),isObj=new Array(n).fill(false),
        isProps=new Array(n).fill(false);
  const rIdx=[];
  for(let i=0;i<n;i++){
    const nz=normals[i*3+2],nx=normals[i*3],ny=normals[i*3+1];
    const nxy=Math.sqrt(nx*nx+ny*ny),z=verts[i*3+2];
    if(nz>P.floorN&&z<zLo+.1)       isFloor[i]=true;
    else if(nz<-P.ceilN&&z>zHi-.1)  isCeiling[i]=true;
    else if(nxy>P.wallN)             isWall[i]=true;
    else                              rIdx.push(i);
  }
  if(rIdx.length>P.dbscanMin){
    const pts=rIdx.map(i=>[verts[i*3],verts[i*3+1]]);
    const lbl=gridDbscan(pts,P.dbscanEps,P.dbscanMin);
    const clMap={};
    lbl.forEach((l,j)=>{if(l>=0)(clMap[l]=clMap[l]||[]).push(j);});
    lbl.forEach((l,j)=>{
      const vi=rIdx[j];
      if(l<0){isProps[vi]=true;return;}
      const cj=clMap[l];
      let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity;
      cj.forEach(jj=>{const ii=rIdx[jj];
        minX=Math.min(minX,verts[ii*3]);maxX=Math.max(maxX,verts[ii*3]);
        minY=Math.min(minY,verts[ii*3+1]);maxY=Math.max(maxY,verts[ii*3+1]);});
      (Math.max(maxX-minX,maxY-minY)>=P.objThresh?isObj:isProps)[vi]=true;
    });
  } else rIdx.forEach(i=>isProps[i]=true);
  return{floor:isFloor,wall:isWall,ceiling:isCeiling,object:isObj,props:isProps};
}

// ═══════════════════════════════════════════════════════════════════════
//  GRID DBSCAN
// ═══════════════════════════════════════════════════════════════════════
function gridDbscan(pts,eps,minPts){
  const n=pts.length,labels=new Int32Array(n).fill(-1),visited=new Uint8Array(n);
  const grid=new Map();
  pts.forEach(([x,y],i)=>{
    const k=`${Math.floor(x/eps)},${Math.floor(y/eps)}`;
    if(!grid.has(k))grid.set(k,[]);grid.get(k).push(i);
  });
  function nb(i){
    const[x,y]=pts[i],cx=Math.floor(x/eps),cy=Math.floor(y/eps),res=[];
    for(let dx=-1;dx<=1;dx++)for(let dy=-1;dy<=1;dy++){
      const c=grid.get(`${cx+dx},${cy+dy}`);
      if(c)c.forEach(j=>{const d0=pts[j][0]-x,d1=pts[j][1]-y;
        if(d0*d0+d1*d1<=eps*eps)res.push(j);});
    }
    return res;
  }
  let cid=0;
  for(let i=0;i<n;i++){
    if(visited[i])continue;visited[i]=1;
    const n0=nb(i);if(n0.length<minPts)continue;
    labels[i]=cid;const q=[...n0];
    for(let qi=0;qi<q.length;qi++){
      const v=q[qi];if(!visited[v]){visited[v]=1;const vn=nb(v);if(vn.length>=minPts)vn.forEach(x=>q.push(x));}
      if(labels[v]<0)labels[v]=cid;
    }
    cid++;
  }
  return labels;
}

// ═══════════════════════════════════════════════════════════════════════
//  SAMPLING  —  Voxel Grid Downsampling
//  원리: 3D 공간을 균등 크기 복셀로 분할 → 복셀당 1점 유지
//        → 출력 점 간격이 일정해짐 (밀도 편향 제거)
// ═══════════════════════════════════════════════════════════════════════
function samplePointcloud(verts,normals,colors,masks,n,baseRatio){
  const keep=new Uint8Array(n);
  const _vsSamples=[];   // 파트별 복셀 크기 기록 (로그용)

  ['floor','wall','ceiling','object','props'].forEach(part=>{
    const m=masks[part];
    const adj=Math.min(1,Math.max(0.05,baseRatio*App.PART_RETENTION[part]));

    // 해당 파트 인덱스 수집
    const indices=[];
    for(let i=0;i<n;i++) if(m[i]) indices.push(i);
    if(!indices.length) return;

    const pn=indices.length;
    const target=Math.max(1,Math.round(pn*adj));

    // 바운딩 박스
    let mnX=Infinity,mxX=-Infinity,mnY=Infinity,mxY=-Infinity,mnZ=Infinity,mxZ=-Infinity;
    for(const i of indices){
      const x=verts[i*3],y=verts[i*3+1],z=verts[i*3+2];
      if(x<mnX)mnX=x;if(x>mxX)mxX=x;
      if(y<mnY)mnY=y;if(y>mxY)mxY=y;
      if(z<mnZ)mnZ=z;if(z>mxZ)mxZ=z;
    }
    const volX=(mxX-mnX)||0.001,volY=(mxY-mnY)||0.001,volZ=(mxZ-mnZ)||0.001;

    // 복셀 크기 = cbrt(부피 / 목표점수)  →  목표점수 ≈ 셀 수
    let vs=Math.cbrt((volX*volY*volZ)/target);
    if(vs<=0||!isFinite(vs)) vs=0.01;

    // 그리드 분할 수 (overflow 방지 캡)
    const NX=Math.min(Math.ceil(volX/vs)+1,65535);
    const NY=Math.min(Math.ceil(volY/vs)+1,65535);
    const NZ=Math.min(Math.ceil(volZ/vs)+1,65535);
    const useNum=(NX*NY*NZ)<Number.MAX_SAFE_INTEGER;

    // 복셀 맵: 키 → 대표 점 인덱스 (첫 번째 점 = 원본 순서 기준)
    const voxelMap=new Map();
    for(const i of indices){
      const gx=Math.floor((verts[i*3]  -mnX)/vs);
      const gy=Math.floor((verts[i*3+1]-mnY)/vs);
      const gz=Math.floor((verts[i*3+2]-mnZ)/vs);
      const key=useNum?(gx*NY+gy)*NZ+gz:`${gx},${gy},${gz}`;
      if(!voxelMap.has(key)) voxelMap.set(key,i);
    }
    for(const i of voxelMap.values()) keep[i]=1;
    _vsSamples.push(vs);
  });

  const voxelSizeAvg=_vsSamples.length
    ? _vsSamples.reduce((a,v)=>a+v,0)/_vsSamples.length : 0;

  // 출력 배열 구성
  const cnt=keep.reduce((a,v)=>a+v,0);
  const sv=new Float32Array(cnt*3),sn=new Float32Array(cnt*3);
  // 색상: 항상 출력 — 원본 없으면 흰색(1,1,1) 기본값
  const sc=new Float32Array(cnt*3);
  let j=0;
  for(let i=0;i<n;i++){
    if(!keep[i])continue;
    sv[j*3]=verts[i*3];sv[j*3+1]=verts[i*3+1];sv[j*3+2]=verts[i*3+2];
    sn[j*3]=normals[i*3];sn[j*3+1]=normals[i*3+1];sn[j*3+2]=normals[i*3+2];
    if(colors){
      sc[j*3]=colors[i*3];sc[j*3+1]=colors[i*3+1];sc[j*3+2]=colors[i*3+2];
    }else{
      sc[j*3]=1;sc[j*3+1]=1;sc[j*3+2]=1; // 흰색 기본
    }
    j++;
  }
  return{sv,sn,sc,cnt,voxelSizeAvg};
}

// ═══════════════════════════════════════════════════════════════════════
//  PLY WRITER
//  ─ 색상: 항상 property float red/green/blue (Linear, Blender Base Color 호환)
//  ─ sRGB 입력은 IEC 61966-2-1 공식으로 Linear 변환 후 저장
// ═══════════════════════════════════════════════════════════════════════
function sRGB2Lin(c){
  // IEC 61966-2-1 (정확한 sRGB → Linear 변환)
  return c<=0.04045 ? c/12.92 : Math.pow((c+0.055)/1.055, 2.4);
}

function writePLY(verts,normals,colors,n,colorsLinear){
  // 색상은 항상 float32 Linear로 출력
  const lines=['ply','format binary_little_endian 1.0',
    'comment PointCloud Optimizer v2.0',
    'comment color: float32 linear (Blender Base Color compatible)',
    `element vertex ${n}`,
    'property float x','property float y','property float z',
    'property float nx','property float ny','property float nz',
    'property float red','property float green','property float blue'];
  lines.push('end_header');
  const hBytes=new TextEncoder().encode(lines.join('\n')+'\n');
  const stride=36; // xyz(12) + normal(12) + rgb float(12) = 36 bytes
  const out=new ArrayBuffer(hBytes.byteLength+n*stride);
  new Uint8Array(out).set(hBytes,0);
  const dv=new DataView(out,hBytes.byteLength);
  for(let i=0;i<n;i++){
    const b=i*stride;
    dv.setFloat32(b,    verts[i*3],   true);
    dv.setFloat32(b+4,  verts[i*3+1], true);
    dv.setFloat32(b+8,  verts[i*3+2], true);
    dv.setFloat32(b+12, normals[i*3],  true);
    dv.setFloat32(b+16, normals[i*3+1],true);
    dv.setFloat32(b+20, normals[i*3+2],true);
    // 색상: sRGB → Linear 변환 (이미 Linear면 그대로)
    let r=colors[i*3], g=colors[i*3+1], bl=colors[i*3+2];
    if(!colorsLinear){ r=sRGB2Lin(r); g=sRGB2Lin(g); bl=sRGB2Lin(bl); }
    dv.setFloat32(b+24, Math.max(0,Math.min(1,r)), true);
    dv.setFloat32(b+28, Math.max(0,Math.min(1,g)), true);
    dv.setFloat32(b+32, Math.max(0,Math.min(1,bl)),true);
  }
  return out;
}

// ═══════════════════════════════════════════════════════════════════════
//  META.JSON
// ═══════════════════════════════════════════════════════════════════════
function buildMeta(verts,masks,n){
  let minX=Infinity,maxX=-Infinity,minY=Infinity,maxY=-Infinity,minZ=Infinity,maxZ=-Infinity;
  for(let i=0;i<n;i++){
    const x=verts[i*3],y=verts[i*3+1],z=verts[i*3+2];
    if(x<minX)minX=x;if(x>maxX)maxX=x;if(y<minY)minY=y;
    if(y>maxY)maxY=y;if(z<minZ)minZ=z;if(z>maxZ)maxZ=z;
  }
  const r4=v=>Math.round(v*1e4)/1e4;
  const med=arr=>{const s=[...arr].sort((a,b)=>a-b);return s[Math.floor(s.length/2)];};
  const meta={version:'2.0',total_points:n,
    bounds:{min:[r4(minX),r4(minY),r4(minZ)],max:[r4(maxX),r4(maxY),r4(maxZ)]},
    floor_height:null,ceiling_height:null,walls:[],objects:[]};
  const fz=[]; for(let i=0;i<n;i++) if(masks.floor[i]) fz.push(verts[i*3+2]);
  if(fz.length) meta.floor_height=r4(med(fz));
  const cz=[]; for(let i=0;i<n;i++) if(masks.ceiling[i]) cz.push(verts[i*3+2]);
  if(cz.length) meta.ceiling_height=r4(med(cz));
  function clP(mk,key){
    const idx2=[]; for(let i=0;i<n;i++) if(masks[mk][i]) idx2.push(i);
    if(idx2.length<5)return;
    const pts=idx2.map(i=>[verts[i*3],verts[i*3+1]]);
    const lbl=gridDbscan(pts,.3,5);
    const byCl={};
    lbl.forEach((l,j)=>{if(l>=0)(byCl[l]=byCl[l]||[]).push(idx2[j]);});
    Object.entries(byCl).forEach(([cid,ia])=>{
      let ax=Infinity,bx=-Infinity,ay=Infinity,by=-Infinity,az=Infinity,bz=-Infinity;
      ia.forEach(i=>{ax=Math.min(ax,verts[i*3]);bx=Math.max(bx,verts[i*3]);
        ay=Math.min(ay,verts[i*3+1]);by=Math.max(by,verts[i*3+1]);
        az=Math.min(az,verts[i*3+2]);bz=Math.max(bz,verts[i*3+2]);});
      meta[key].push({id:+cid,
        center:[r4((ax+bx)/2),r4((ay+by)/2),r4((az+bz)/2)],
        size:[r4(bx-ax),r4(by-ay),r4(bz-az)],point_count:ia.length});
    });
  }
  clP('wall','walls'); clP('object','objects');
  return meta;
}

// ═══════════════════════════════════════════════════════════════════════
//  RESULT UI
// ═══════════════════════════════════════════════════════════════════════
function addResultFile(name,bytes,q){
  const mb=(bytes/1024/1024).toFixed(1);
  const tag=q===60?'<span class="tag green">High</span>'
    :q===40?'<span class="tag yellow">Med</span>'
    :q===20?'<span class="tag" style="background:#252525;color:#A0A0A0">Low</span>'
    :'<span class="tag" style="background:#1E1E1E;color:#787878">JSON</span>';
  const div=document.createElement('div');
  div.className='file-item';
  div.innerHTML=`<div class="fi-icon">${name.endsWith('.json')?'📋':'☁️'}</div>
    <div class="fi-name">${name} ${tag}</div>
    <div class="fi-size">${mb} MB</div>
    <button onclick="dlFile('${name}')">⬇ 다운로드</button>`;
  $('file-list').appendChild(div);
}
function dlFile(name){
  const b=App.blobs[name];if(!b)return;
  saveBlob(name,b);
}
// ═══════════════════════════════════════════════════════════════════════
//  CRC-32  (ZIP 생성용)
// ═══════════════════════════════════════════════════════════════════════
const _CRC_TBL=(()=>{
  const t=new Uint32Array(256);
  for(let n=0;n<256;n++){
    let c=n;
    for(let k=0;k<8;k++) c=c&1?0xedb88320^(c>>>1):c>>>1;
    t[n]=c;
  }
  return t;
})();
function crc32(u8){
  let c=0xffffffff;
  for(let i=0;i<u8.length;i++) c=_CRC_TBL[(c^u8[i])&0xff]^(c>>>8);
  return (c^0xffffffff)>>>0;
}

// ═══════════════════════════════════════════════════════════════════════
//  ZIP 빌더  (Store 무압축, 단일 볼륨)
// ═══════════════════════════════════════════════════════════════════════
async function buildZip(blobs){
  const enc=new TextEncoder();
  const parts=[]; // ArrayBuffer 조각들
  const cd=[];    // central directory 레코드
  let offset=0;

  for(const [name,blob] of Object.entries(blobs)){
    const data=new Uint8Array(await blob.arrayBuffer());
    const fn=enc.encode(name);
    const crc=crc32(data);
    const size=data.byteLength;

    // Local file header (30 + fn.length)
    const lh=new DataView(new ArrayBuffer(30+fn.length));
    lh.setUint32(0,0x04034b50,true); // sig
    lh.setUint16(4,20,true);         // version
    lh.setUint16(6,0,true);          // flags
    lh.setUint16(8,0,true);          // store (no compression)
    lh.setUint16(10,0,true);         // mod time
    lh.setUint16(12,0,true);         // mod date
    lh.setUint32(14,crc,true);
    lh.setUint32(18,size,true);
    lh.setUint32(22,size,true);
    lh.setUint16(26,fn.length,true);
    lh.setUint16(28,0,true);
    new Uint8Array(lh.buffer,30).set(fn);

    cd.push({fn,crc,size,offset});
    parts.push(lh.buffer, data.buffer);
    offset+=30+fn.length+size;
  }

  // Central directory
  const cdStart=offset;
  for(const {fn,crc,size,offset:fo} of cd){
    const rec=new DataView(new ArrayBuffer(46+fn.length));
    rec.setUint32(0,0x02014b50,true); // sig
    rec.setUint16(4,20,true);
    rec.setUint16(6,20,true);
    rec.setUint16(8,0,true);
    rec.setUint16(10,0,true);
    rec.setUint16(12,0,true);
    rec.setUint16(14,0,true);
    rec.setUint32(16,crc,true);
    rec.setUint32(20,size,true);
    rec.setUint32(24,size,true);
    rec.setUint16(28,fn.length,true);
    rec.setUint16(30,0,true);
    rec.setUint16(32,0,true);
    rec.setUint16(34,0,true);
    rec.setUint16(36,0,true);
    rec.setUint32(38,0,true);
    rec.setUint32(42,fo,true);
    new Uint8Array(rec.buffer,46).set(fn);
    parts.push(rec.buffer);
    offset+=46+fn.length;
  }
  const cdSize=offset-cdStart;

  // End of central directory
  const eocd=new DataView(new ArrayBuffer(22));
  eocd.setUint32(0,0x06054b50,true);
  eocd.setUint16(4,0,true);
  eocd.setUint16(6,0,true);
  eocd.setUint16(8,cd.length,true);
  eocd.setUint16(10,cd.length,true);
  eocd.setUint32(12,cdSize,true);
  eocd.setUint32(16,cdStart,true);
  eocd.setUint16(20,0,true);
  parts.push(eocd.buffer);

  return new Blob(parts,{type:'application/zip'});
}

// 저장 완료 토스트 표시
function showSaveToast(folderName, isBrowser){
  const toast=$('save-toast');
  $('st-icon').textContent = isBrowser ? '📥' : '✅';
  $('st-title').textContent = isBrowser ? '브라우저 다운로드 폴더에 저장됨' : '폴더에 저장 완료';
  $('st-path').textContent  = isBrowser
    ? '브라우저 기본 다운로드 폴더 (Downloads)'
    : `📁 ${folderName}`;
  // "폴더 열기" 버튼 — FSA 환경 + showOpenFilePicker 지원 시만 노출
  const openBtn=$('st-open-btn');
  if(!isBrowser && App.dirHandle && 'showDirectoryPicker' in window){
    openBtn.style.display='';
    openBtn.onclick=async()=>{
      // 폴더 핸들 재확인 알림 (브라우저 보안 정책상 직접 열기 불가, 경로 복사로 대체)
      try{
        const text=folderName;
        await navigator.clipboard.writeText(text);
        openBtn.textContent='📋 이름 복사됨';
        setTimeout(()=>openBtn.textContent='📂 폴더 열기',2000);
      }catch(_){openBtn.textContent='📂 파일 탐색기에서 확인'}
    };
  } else {
    openBtn.style.display='none';
  }
  // 애니메이션 재트리거를 위해 클래스 리셋
  toast.classList.remove('show');
  void toast.offsetWidth; // reflow
  toast.classList.add('show');
}

$('dl-all-btn').addEventListener('click', async()=>{
  const names=Object.keys(App.blobs);
  if(!names.length){appNotify('다운로드할 파일이 없습니다.');return;}
  const btn=$('dl-all-btn');
  btn.disabled=true; btn.textContent='⏳ 준비 중...';
  try{
    if(App.dirHandle){
      // FSA: 각 파일 직접 저장
      log(`\n📦 전체 파일 폴더에 저장 중 → ${App.dirHandle.name}/`);
      for(const name of names) await saveBlob(name,App.blobs[name]);
      log('   ✅ 전체 저장 완료');
      showSaveToast(App.dirHandle.name,false);
    } else {
      // 팝업 차단 우회: 전체를 ZIP 한 파일로 묶어 단일 다운로드
      log('\n📦 ZIP 파일 생성 중...');
      const zip=await buildZip(App.blobs);
      const a=document.createElement('a');
      a.style.display='none';
      document.body.appendChild(a);
      a.href=URL.createObjectURL(zip);
      a.download='pointcloud_optimizer_output.zip';
      a.click();
      document.body.removeChild(a);
      setTimeout(()=>URL.revokeObjectURL(a.href),3000);
      log(`   ✅ ZIP 다운로드 시작 (${(zip.size/1024/1024).toFixed(1)} MB)`);
      showSaveToast(null,true);
    }
  }catch(e){
    log('   ❌ 다운로드 오류: '+e.message);
    console.error(e);
  }finally{
    btn.disabled=false; btn.textContent='📦 전체 다운로드';
  }
});

// (q-card 클릭 이벤트는 line 337에서 단일 등록됨)

// ═══════════════════════════════════════════════════════════════════════