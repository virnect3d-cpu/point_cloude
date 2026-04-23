// ═══════════════════════════════════════════════════════════════════════
//  Page 4 (Texture Bake + HSV) 로컬 state
// ═══════════════════════════════════════════════════════════════════════
const P4 = {
  // Files / session
  plyFile: null, objFile: null, sessionId: null,
  texSize: 2048, adjustTimer: null,
  // HSV baseline cache (JS 프리뷰용)
  baselineImageData: null, baselineVersion: 0,
  // Three.js
  renderer: null, scene: null, camera: null,
  mesh: null, orb: null, raf: null, threeReady: false,
  // 5-point lighting + HDRI
  ambLight: null, dirLight: null, hemiLight: null,
  fillLight: null, rimLight: null,
  envMap: null, hdrIntensity: 1.0, hdrOn: true, hdrName: '',
  textureObj: null,
  // Gizmo
  tMode: 'translate', gizmoGroup: null, gizmoDrag: null, axisGizmoInst: null,
};

//  PAGE 4 — 텍스처 베이크 (UV unwrap + PC color bake + HSV adjust)
// ═══════════════════════════════════════════════════════════════════════
P4.plyFile = null;
P4.objFile = null;
P4.sessionId = null;
P4.texSize = 2048;
P4.adjustTimer = null;
// HSV 프리뷰 — 서버의 apply_hsv_adjust 알고리즘과 동일한 JS 구현
P4.baselineImageData = null;  // 서버의 bake_base_tex 캐시 (HSV 기준)
P4.baselineVersion = 0;  // 캐시 무효화 체크용

// 3D 뷰어 (page 4 전용)
P4.renderer = null;
P4.scene = null;
P4.camera = null;
P4.mesh = null;
P4.orb = null;
P4.raf = null;
P4.threeReady = false;
// HDRI 환경광 상태
P4.ambLight = null;
P4.dirLight = null;
P4.hemiLight = null;
P4.fillLight = null;
P4.rimLight = null;
P4.envMap = null;  // PMREMGenerator 결과 (EquirectangularReflectionMapping)
P4.hdrIntensity = 1.0;  // envMapIntensity
P4.hdrOn = true;
P4.hdrName = '';  // UI 표시용
P4.textureObj = null;

function p4SetSize(sz){
  P4.texSize = sz;
  document.querySelectorAll('.p4-size-btn').forEach(b => {
    b.classList.toggle('p4-size-active', +b.dataset.size === sz);
  });
}

function p4UpdateStats(){
  const s = $('p4-stats');
  const t = $('p4-stats-text');
  if(!s || !t) return;
  if(P4.plyFile || P4.objFile){
    s.style.display = 'block';
    const parts = [];
    if(P4.plyFile) parts.push(`PLY: ${(P4.plyFile.size/1024/1024).toFixed(1)}MB`);
    if(P4.objFile) parts.push(`OBJ: ${(P4.objFile.size/1024/1024).toFixed(1)}MB`);
    t.textContent = parts.join('  ·  ');
  }
  // 둘 다 있으면 run 버튼 활성화
  const ok = !!(P4.plyFile && P4.objFile);
  $('p4-run-btn').disabled = !ok;
}

function p4BindDrop(dropId, inputId, nameId, kind){
  const d = $(dropId), i = $(inputId), n = $(nameId);
  if(!d || !i) return;
  const handle = (f) => {
    if(!f) return;
    if(!_checkBrowserUploadSize(f)) return;
    if(kind === 'ply' && !f.name.toLowerCase().endsWith('.ply')){ appNotify('.ply 파일만 지원'); return; }
    if(kind === 'obj'){
      const low = f.name.toLowerCase();
      if(!(low.endsWith('.obj') || low.endsWith('.fbx') || low.endsWith('.glb'))){
        appNotify('.obj / .fbx / .glb 만 지원'); return;
      }
    }
    if(kind === 'ply') P4.plyFile = f; else P4.objFile = f;
    if(n) n.textContent = f.name;
    p4UpdateStats();
  };
  d.addEventListener('click', () => i.click());
  d.addEventListener('dragover', e => { e.preventDefault(); d.classList.add('drag'); });
  d.addEventListener('dragleave', () => d.classList.remove('drag'));
  d.addEventListener('drop', e => {
    e.preventDefault(); d.classList.remove('drag');
    handle(e.dataTransfer.files[0]);
  });
  i.addEventListener('change', e => handle(e.target.files[0]));
}

async function p4RunBake(){
  if(!P4.plyFile || !P4.objFile){ appNotify('PLY와 OBJ 둘 다 올려주세요.'); return; }
  const runBtn = $('p4-run-btn'), regenBtn = $('p4-regen-btn');
  const prevText = runBtn.textContent;
  runBtn.disabled = true; runBtn.textContent = '🔄 업로드 중...';
  if(regenBtn) regenBtn.disabled = true;

  // 🧹 이전 베이크의 메쉬/텍스처/Transform 기즈모 정리 — 새 베이크로 완전 교체
  // (버그 수정: 재베이크 시 P4.mesh가 재사용되어 옛 geometry + 새 텍스처가 섞이던 문제)
  if(P4.gizmoGroup){ P4.scene.remove(P4.gizmoGroup); P4.gizmoGroup = null; }
  const _tmbar = $('p4-tmode-bar'); if(_tmbar) _tmbar.style.display = 'none';
  if(P4.mesh){
    try{
      P4.scene.remove(P4.mesh);
      P4.mesh.geometry?.dispose();
      if(P4.mesh.material){
        if(P4.mesh.material.map) P4.mesh.material.map.dispose();
        P4.mesh.material.dispose();
      }
    }catch(_){}
    P4.mesh = null;
  }
  if(P4.textureObj){ try{ P4.textureObj.dispose(); }catch(_){} P4.textureObj = null; }
  P4.baselineImageData = null;   // HSV 베이스라인도 무효화

  // 저장 버튼들도 비활성화 (베이크 끝나야 다시 활성)
  const _sbtn  = $('p4-save-btn');      if(_sbtn)  _sbtn.disabled  = true;
  const _fbtn  = $('p4-save-fbx-btn');  if(_fbtn)  _fbtn.disabled  = true;
  const _gbtn  = $('p4-save-glb-btn');  if(_gbtn)  _gbtn.disabled  = true;
  const _rgbtn = $('p4-regen-btn');     if(_rgbtn) _rgbtn.disabled = true;

  // 스피너 ON (2D 프리뷰 + 3D 뷰어 모두)
  p4ShowSpinner('tex', '업로드 중...');
  p4ShowSpinner('viewer', '베이크 준비 중...');

  try{
    // 1) 업로드
    const fd = new FormData();
    fd.append('ply', P4.plyFile, P4.plyFile.name);
    fd.append('obj', P4.objFile, P4.objFile.name);
    const up = await fetch('/api/bake/upload', {method:'POST', body:fd});
    if(!up.ok){ const j = await up.json().catch(()=>({detail:'?'})); throw new Error(j.detail || `HTTP ${up.status}`); }
    const upData = await up.json();
    P4.sessionId = upData.session_id;
    if(!upData.has_colors){
      p4Log(`⚠️ PLY에 색상 정보가 없어서 회색으로 베이크됩니다.`);
    }

    // 2) 베이크 실행 (SSE — 진행률 실시간)
    p4ShowSpinner('tex', 'UV 언랩 중... (0%)');
    p4ShowSpinner('viewer', '베이킹 중... (0%)');

    const aoStrength = parseFloat($('p4-ao').value);
    const lighting = $('p4-lighting').checked;
    const body = {
      tex_size: P4.texSize,
      ao_strength: aoStrength,
      lighting,
    };

    // POST JSON + SSE 스트리밍
    let bdata = null;
    await new Promise((resolve, reject) => {
      const ctrl = new AbortController();
      const onEvent = (d) => {
        if(d.error){ ctrl.abort(); reject(new Error(d.error)); return; }
        if(d.msg){
          const pct = d.progress || 0;
          const fullMsg = `${d.msg}\n(${pct}%)`;
          runBtn.textContent = `⚙️ ${d.msg.slice(0,30)} (${pct}%)`;
          const tm = $('p4-tex-spinner-msg'); if(tm) tm.textContent = fullMsg;
          const vm = $('p4-viewer-spinner-msg'); if(vm) vm.textContent = fullMsg;
        }
        if(d.step === 'done'){
          bdata = {stats: d.stats || {}};
          ctrl.abort();   // 서버가 곧 닫지만 명시적으로 연결 해제
          resolve();
        }
      };
      postSSE(`/api/bake/run-sse/${P4.sessionId}`, body, onEvent, {signal: ctrl.signal})
        .then(() => { if(!bdata) reject(new Error('스트림이 done 없이 종료')); })
        .catch(err => {
          if(err?.name === 'AbortError' && bdata){ /* 정상 완료 */ return; }
          reject(new Error('SSE 연결 끊김: ' + (err?.message || err)));
        });
    });

    // 3) 텍스처 이미지 새로고침 + 정보 표시
    const tVer = Date.now();
    const img = $('p4-tex-img');
    img.style.filter = ''; img.style.visibility = '';  // 새 베이크 → 원본
    img.src = `/api/bake/texture/${P4.sessionId}?v=${tVer}`;
    const texCv = $('p4-tex-canvas'); if(texCv) texCv.style.display = 'none';
    $('p4-tex-placeholder').style.display = 'none';
    $('p4-tex-info').textContent = `${bdata.stats.tex_size}×${bdata.stats.tex_size}  ·  ${(bdata.stats.filled_ratio*100).toFixed(0)}% 채움  ·  V=${bdata.stats.verts.toLocaleString()}`;

    // 4) 3D 뷰어에 적용
    await p4UpdateViewer(tVer);

    // 5) HSV 리셋 + 저장 버튼 활성 + JS 프리뷰용 베이스라인 캐시
    p4ResetHSV(/*apply=*/false);
    _p4CacheBaseline();  // fire-and-forget — 캐시되면 슬라이더 프리뷰가 서버와 동일해짐
    $('p4-save-btn').disabled = false;
    const fbxSave = $('p4-save-fbx-btn'); if(fbxSave) fbxSave.disabled = false;
    const glbSave = $('p4-save-glb-btn'); if(glbSave) glbSave.disabled = false;
    if(regenBtn) regenBtn.disabled = false;
  }catch(e){
    appNotify('베이크 실패: ' + e.message);
  }finally{
    runBtn.disabled = false; runBtn.textContent = prevText;
    p4HideSpinner('tex');
    p4HideSpinner('viewer');
  }
}

function p4Log(msg){ /* Page 4는 별도 로그 없음 — 필요시 확장 */ console.log('[p4]', msg); }

// 스피너 표시/숨김 헬퍼
function p4ShowSpinner(where, msg){
  const el = $(where==='viewer' ? 'p4-viewer-spinner' : 'p4-tex-spinner');
  const m  = $(where==='viewer' ? 'p4-viewer-spinner-msg' : 'p4-tex-spinner-msg');
  if(m && msg) m.textContent = msg;
  if(el) el.style.display = 'flex';
}
function p4HideSpinner(where){
  const el = $(where==='viewer' ? 'p4-viewer-spinner' : 'p4-tex-spinner');
  if(el) el.style.display = 'none';
}

// ── 베이스라인 이미지 캐시 (서버 bake_base_tex와 동일) ───────────────
// 512 이하로 다운샘플해서 JS HSV 프리뷰 속도 확보 (2K는 슬라이더 드래그엔 느림)
async function _p4CacheBaseline(){
  const sid = P4.sessionId;
  if(!sid) return;
  const ver = Date.now();
  P4.baselineVersion = ver;
  return new Promise((resolve) => {
    const img = new Image();
    img.crossOrigin = 'anonymous';
    img.onload = () => {
      if(P4.baselineVersion !== ver) { resolve(); return; }  // 취소됨
      try{
        const w = img.naturalWidth || 1024;
        const h = img.naturalHeight || 1024;
        const PV = 512;
        const scale = Math.min(1, PV / Math.max(w, h));
        const cw = Math.max(1, Math.round(w * scale));
        const ch = Math.max(1, Math.round(h * scale));
        const cv = document.createElement('canvas');
        cv.width = cw; cv.height = ch;
        const ctx = cv.getContext('2d', {willReadFrequently: true});
        ctx.drawImage(img, 0, 0, cw, ch);
        P4.baselineImageData = ctx.getImageData(0, 0, cw, ch);
      }catch(_){ P4.baselineImageData = null; }
      resolve();
    };
    img.onerror = () => { P4.baselineImageData = null; resolve(); };
    img.src = `/api/bake/texture/${sid}?v=${ver}`;
  });
}

// HSV 공식은 frontend/js/hsv.js (window.HSV) 에 canonical 구현.
// 서버 backend/core/uv_bake.py::apply_hsv_adjust 와 bit-identical 계약.
function _p4HSVAdjust(src, hueDeg, sat, bri){
  return window.HSV.adjustImageData(src, hueDeg, sat, bri);
}

// ── 실시간 HSV 프리뷰 — 서버와 동일한 알고리즘으로 즉시 반영 ──────────
function p4ApplyCssFilter(){
  const hue = parseFloat($('p4-hue').value);
  const sat = parseFloat($('p4-sat').value);
  const bright = parseFloat($('p4-bright').value);

  // 숫자 라벨
  $('p4-hue-val').textContent = hue.toFixed(0) + '°';
  $('p4-sat-val').textContent = sat.toFixed(2);
  $('p4-bright-val').textContent = bright.toFixed(2);

  const img = $('p4-tex-img');
  const cv  = $('p4-tex-canvas');

  // 베이스라인 캐시 있으면 JS HSV로 정확 프리뷰 (서버 결과와 동일 색)
  if(P4.baselineImageData && cv){
    const adjusted = _p4HSVAdjust(P4.baselineImageData, hue, sat, bright);
    cv.width  = adjusted.width;
    cv.height = adjusted.height;
    cv.getContext('2d').putImageData(adjusted, 0, 0);
    cv.style.display = 'block';
    if(img){ img.style.filter = ''; img.style.visibility = 'hidden'; }
  } else if(img){
    // 베이스라인 미준비 — 폴백 CSS filter (근사, 색상 다를 수 있음)
    img.style.filter = `hue-rotate(${hue}deg) saturate(${sat}) brightness(${bright})`;
    img.style.visibility = '';
  }

  // 3D 뷰어는 디바운스 후 서버 결과로 업데이트됨. 드래그 중엔 tint 미적용.
  if(P4.mesh && P4.mesh.material){
    P4.mesh.material.color.setRGB(1, 1, 1);
    P4.mesh.material.needsUpdate = true;
  }
}

// ── 실제 서버에 요청해 텍스처 재생성 — 3D 메쉬의 실질 텍스처 갱신용 ──
async function p4AdjustNow(){
  if(!P4.sessionId) return;
  const hue = parseFloat($('p4-hue').value);
  const sat = parseFloat($('p4-sat').value);
  const bright = parseFloat($('p4-bright').value);
  const q = new URLSearchParams({hue, saturation:sat, brightness:bright});

  p4ShowSpinner('viewer', '색상 적용 중...');
  try{
    const r = await fetch(`/api/bake/adjust/${P4.sessionId}?${q}`, {method:'POST'});
    if(!r.ok) return;
    const v = Date.now();
    // 서버가 실제 조정 적용 — JS 프리뷰 캔버스는 숨기고 img를 서버 결과로 교체
    const img = $('p4-tex-img');
    const cv  = $('p4-tex-canvas');
    if(img){
      img.src = `/api/bake/texture/${P4.sessionId}?v=${v}`;
      img.style.filter = '';
      img.style.visibility = '';
    }
    if(cv){ cv.style.display = 'none'; }
    if(P4.mesh && P4.mesh.material){
      P4.mesh.material.color.setRGB(1, 1, 1);
      P4.mesh.material.needsUpdate = true;
    }
    await p4UpdateViewer(v);
  }catch(_){
  }finally{
    p4HideSpinner('viewer');
  }
}

function p4AdjustDebounced(){
  // 1) 즉시 CSS filter로 프리뷰 반영 (0 latency)
  p4ApplyCssFilter();
  // 2) 디바운스해서 서버에 실제 베이크 적용 (300ms 정지 후)
  clearTimeout(P4.adjustTimer);
  P4.adjustTimer = setTimeout(p4AdjustNow, 300);
}

function p4ResetHSV(apply=true){
  $('p4-hue').value = 0; $('p4-sat').value = 1; $('p4-bright').value = 1;
  $('p4-hue-val').textContent = '0°';
  $('p4-sat-val').textContent = '1.00';
  $('p4-bright-val').textContent = '1.00';
  // CSS filter 해제 + JS 프리뷰 캔버스 숨기고 3D 리셋
  const img = $('p4-tex-img');
  if(img){ img.style.filter = ''; img.style.visibility = ''; }
  const cv = $('p4-tex-canvas');
  if(cv) cv.style.display = 'none';
  if(P4.mesh && P4.mesh.material){
    P4.mesh.material.color.setRGB(1, 1, 1);
    P4.mesh.material.needsUpdate = true;
  }
  if(apply && P4.sessionId) p4AdjustNow();
}

async function p4SaveAll(){
  if(!P4.sessionId){ appNotify('먼저 텍스처를 생성하세요.'); return; }
  const stem = (P4.objFile?.name || 'mesh').replace(/\.[^.]+$/, '').replace(/[^\w\-]/g, '_');
  try{
    const [objR, mtlR, pngR] = await Promise.all([
      fetch(`/api/bake/mesh/${P4.sessionId}`),
      fetch(`/api/bake/mtl/${P4.sessionId}`),
      fetch(`/api/bake/texture/${P4.sessionId}?v=${Date.now()}`),
    ]);
    const objText = await objR.text();
    const mtlText = await mtlR.text();
    const pngBlob = await pngR.blob();

    const api = await getPyApi();
    if(api){
      const objName = `${stem}_baked.obj`;
      const r = await api.save_file_dialog(objName, objText);
      if(!r || !r.ok){ if(r && r.reason !== 'cancelled') appNotify('저장 실패: '+(r.reason||'?')); return; }
      const mtlPath = r.path.replace(/\.obj$/i, '.mtl');
      if(api.write_text_file) await api.write_text_file(mtlPath, mtlText);
      const b64 = await blobToBase64(pngBlob);
      await api.save_bytes_dialog(`${stem}_baked.png`, b64);
      appNotify(`✅ OBJ+MTL+PNG 저장 완료!\n${r.path}\n\nUnity에 OBJ 드래그 → 텍스처 자동 연결`);
      return;
    }
    await saveBlob(`${stem}_baked.obj`, new Blob([objText], {type:'text/plain'}));
    await saveBlob(`${stem}_baked.mtl`, new Blob([mtlText], {type:'text/plain'}));
    await saveBlob(`${stem}_baked.png`, pngBlob);
  }catch(e){
    appNotify('저장 오류: ' + e.message);
  }
}

async function p4SaveFBX(){
  if(!P4.sessionId){ appNotify('먼저 텍스처를 생성하세요.'); return; }
  const stem = (P4.objFile?.name || 'mesh').replace(/\.[^.]+$/, '').replace(/[^\w\-]/g, '_');
  const btn = $('p4-save-fbx-btn');
  const prev = btn?.textContent;
  if(btn){ btn.disabled = true; btn.textContent = '저장 중...'; }
  try{
    const [fbxR, pngR] = await Promise.all([
      fetch(`/api/bake/mesh-fbx/${P4.sessionId}`),
      fetch(`/api/bake/texture/${P4.sessionId}?v=${Date.now()}`),
    ]);
    if(!fbxR.ok) throw new Error(`FBX 서버 오류 ${fbxR.status}`);
    const fbxBytes = new Uint8Array(await fbxR.arrayBuffer());
    const pngBlob = await pngR.blob();

    const api = await getPyApi();
    if(api && api.save_bytes_dialog){
      let bin=''; for(let i=0;i<fbxBytes.length;i++) bin += String.fromCharCode(fbxBytes[i]);
      const fbxB64 = btoa(bin);
      const r = await api.save_bytes_dialog(`${stem}_baked.fbx`, fbxB64);
      if(!r || !r.ok){ if(r && r.reason !== 'cancelled') appNotify('저장 실패: '+(r.reason||'?')); return; }
      // PNG를 같은 폴더에
      const pngB64 = await blobToBase64(pngBlob);
      // save_bytes_dialog 는 다이얼로그가 또 뜨니까, write 헬퍼로 옆에 직접 쓰기
      if(api.write_text_file){
        // base64 → binary로 저장하는 별도 헬퍼가 필요한데, save_bytes_dialog 한번 더 띄우는게 단순
      }
      await api.save_bytes_dialog(`${stem}_baked.png`, pngB64);
      appNotify(`✅ FBX+PNG 저장 완료\n${r.path}\n\nMaya/Blender/Unity 모두 바로 import 가능`);
      return;
    }
    await saveBlob(`${stem}_baked.fbx`, new Blob([fbxBytes], {type:'application/octet-stream'}));
    await saveBlob(`${stem}_baked.png`, pngBlob);
  }catch(e){
    appNotify('FBX 저장 오류: ' + e.message);
  }finally{
    if(btn){ btn.disabled = false; btn.textContent = prev; }
  }
}

async function p4SaveGLB(){
  if(!P4.sessionId){ appNotify('먼저 텍스처를 생성하세요.'); return; }
  const stem = (P4.objFile?.name || 'mesh').replace(/\.[^.]+$/, '').replace(/[^\w\-]/g, '_');
  const btn = $('p4-save-glb-btn');
  const prev = btn?.textContent;
  if(btn){ btn.disabled = true; btn.textContent = '저장 중...'; }
  try{
    const r2 = await fetch(`/api/bake/mesh-glb/${P4.sessionId}`);
    if(!r2.ok) throw new Error(`GLB 서버 오류 ${r2.status}`);
    const bytes = new Uint8Array(await r2.arrayBuffer());

    const api = await getPyApi();
    if(api && api.save_bytes_dialog){
      let bin=''; for(let i=0;i<bytes.length;i++) bin += String.fromCharCode(bytes[i]);
      const b64 = btoa(bin);
      const r = await api.save_bytes_dialog(`${stem}_baked.glb`, b64);
      if(!r || !r.ok){ if(r && r.reason !== 'cancelled') appNotify('저장 실패: '+(r.reason||'?')); return; }
      appNotify(`✅ GLB 저장 완료\n${r.path}\n\n단일 파일에 텍스처 임베드됨`);
      return;
    }
    await saveBlob(`${stem}_baked.glb`, new Blob([bytes], {type:'model/gltf-binary'}));
  }catch(e){
    appNotify('GLB 저장 오류: ' + e.message);
  }finally{
    if(btn){ btn.disabled = false; btn.textContent = prev; }
  }
}

function blobToBase64(blob){
  return new Promise((res, rej) => {
    const r = new FileReader();
    r.onload = () => {
      const s = r.result; const comma = s.indexOf(',');
      res(comma >= 0 ? s.slice(comma+1) : s);
    };
    r.onerror = rej;
    r.readAsDataURL(blob);
  });
}

// ─── Page 4 Three.js 뷰어 — Maya 네비 + Transform gizmo + 축 기즈모 ──
P4.tMode = 'translate';
P4.gizmoGroup = null;
P4.gizmoDrag = null;  // {mode, axis, startMouse, startObj}
P4.axisGizmoInst = null;

function initP4Viewer(){
  if(P4.threeReady) return;
  if(!window.THREE){ loadThreeJS().then(() => initP4Viewer()); return; }
  const THREE = window.THREE;
  const cv = $('p4-canvas'); if(!cv) return;

  P4.renderer = new THREE.WebGLRenderer({canvas:cv, antialias:true, alpha:false});
  P4.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  P4.renderer.setClearColor(0x121212, 1);
  // HDRI 환경광용 — 톤매핑 + 선형→sRGB 출력
  P4.renderer.toneMapping = THREE.ACESFilmicToneMapping;
  P4.renderer.toneMappingExposure = 1.0;
  // r155+ 는 outputColorSpace, r128 이하는 outputEncoding.
  // r155+ 부터 physically-correct lights 가 기본. 기존 r128 대비 빛이 다르게 보일 수 있으나
  // useLegacyLights 는 r160에서 deprecated이므로 새 기본을 쓰고 intensity 를 코드에서 조정.
  if('outputColorSpace' in P4.renderer){
    P4.renderer.outputColorSpace = THREE.SRGBColorSpace;
  } else {
    P4.renderer.outputEncoding = THREE.sRGBEncoding || 3001;
  }

  P4.scene = new THREE.Scene();
  // ── 폴백 3-point 조명 + 하늘/땅 하프돔 (HDRI 없을 때도 골고루 밝게) ──
  // Key (전상)·Fill (반대편 부드럽게)·Rim (뒤 하이라이트) + Hemisphere
  P4.ambLight  = new THREE.AmbientLight(0xffffff, 0.35);
  P4.hemiLight = new THREE.HemisphereLight(0xBFD6FF, 0x3A3026, 0.55);
  P4.dirLight  = new THREE.DirectionalLight(0xffffff, 0.85);  // Key
  P4.dirLight.position.set(2, 3, 2);
  P4.fillLight = new THREE.DirectionalLight(0xCBDBFF, 0.45);   // Fill (반대편)
  P4.fillLight.position.set(-2.5, 1.5, -1);
  P4.rimLight  = new THREE.DirectionalLight(0xFFE8C2, 0.35);   // Rim (뒤)
  P4.rimLight.position.set(0, 2, -3);
  P4.scene.add(P4.ambLight, P4.hemiLight, P4.dirLight, P4.fillLight, P4.rimLight);

  P4.camera = new THREE.PerspectiveCamera(45, cv.clientWidth/cv.clientHeight, 0.01, 10000);
  P4.camera.position.set(0, 0, 5);

  P4.orb = {cx:0, cy:0, cz:0, radius:5, theta:-0.4, phi:1.15};

  function resize(){
    const w = cv.clientWidth, h = cv.clientHeight;
    if(cv.width !== w || cv.height !== h){
      P4.renderer.setSize(w, h, false);
      P4.camera.aspect = w/h; P4.camera.updateProjectionMatrix();
    }
  }

  // ── Maya 스타일 네비 ─────────────────────────────────────────
  // Alt+LMB = 회전 / Alt+MMB = 패닝 / 스크롤·Alt+RMB = 줌
  let navMode = null, lx = 0, ly = 0;
  cv.addEventListener('contextmenu', e => e.preventDefault());
  cv.addEventListener('mousedown', e => {
    lx = e.clientX; ly = e.clientY;
    // Transform gizmo 드래그 체크 먼저
    if(P4.gizmoGroup && !e.altKey && e.button === 0){
      if(p4TryStartGizmoDrag(e)) return;
      // 아무 기즈모 핸들도 안 눌렀으면 메쉬 클릭 = 기즈모 활성화
      if(p4TryPickMesh(e)) return;
    }
    if(e.altKey){
      navMode = e.button === 0 ? 'rotate' : (e.button === 1 ? 'pan' : 'zoom');
      e.preventDefault();
    } else if(e.button === 0){
      // 일반 좌클릭 = 메쉬 픽킹
      if(p4TryPickMesh(e)) return;
      navMode = 'rotate';  // 빈 공간 클릭 = 회전 fallback
    }
  });
  window.addEventListener('mouseup', () => { navMode = null; P4.gizmoDrag = null; });
  window.addEventListener('mousemove', e => {
    const dx = e.clientX - lx, dy = e.clientY - ly;
    lx = e.clientX; ly = e.clientY;
    if(P4.gizmoDrag){
      p4UpdateGizmoDrag(e, dx, dy);
      return;
    }
    if(navMode === 'rotate'){
      P4.orb.theta += dx * 0.01;
      P4.orb.phi = Math.max(0.1, Math.min(Math.PI - 0.1, P4.orb.phi - dy * 0.01));
    } else if(navMode === 'pan'){
      const r = P4.orb.radius * 0.002;
      const ct = Math.cos(P4.orb.theta), st = Math.sin(P4.orb.theta);
      P4.orb.cx -= dx * r * ct;
      P4.orb.cz += dx * r * st;
      P4.orb.cy += dy * r;
    } else if(navMode === 'zoom'){
      P4.orb.radius *= (1 + dx * 0.01);
    }
  });
  cv.addEventListener('wheel', e => {
    e.preventDefault();
    const f = Math.sign(e.deltaY) * Math.min(Math.abs(e.deltaY) / 120, 1);
    P4.orb.radius *= (1 + f * 0.12);
    P4.orb.radius = Math.max(0.001, Math.min(1e5, P4.orb.radius));
  });

  window.addEventListener('keydown', e => {
    // Page 4 탭 아닐 때는 무시
    const pg4 = document.getElementById('page4');
    if(!pg4 || pg4.style.display === 'none') return;
    if(e.key === 'f' || e.key === 'F'){ if(P4.mesh) p4FitCamera(); }
    else if(e.key === 'w' || e.key === 'W'){ p4SetTMode('translate'); }
    else if(e.key === 'e' || e.key === 'E'){ p4SetTMode('rotate'); }
    else if(e.key === 'r' || e.key === 'R'){ p4SetTMode('scale'); }
    else if(e.key === 'Escape'){ p4DetachTransform(); }
  });

  function loop(){
    resize();
    const {cx,cy,cz,radius,theta,phi} = P4.orb;
    P4.camera.position.set(cx + radius*Math.sin(phi)*Math.sin(theta), cy + radius*Math.cos(phi), cz + radius*Math.sin(phi)*Math.cos(theta));
    P4.camera.lookAt(cx, cy, cz);
    // Transform 기즈모 — 줌에 따라 화면 크기 일정하게 유지
    if(P4.gizmoGroup){
      const baseR = P4.gizmoGroup.userData.baseRadius || radius;
      const k = radius / baseR;
      P4.gizmoGroup.scale.setScalar(k);
    }
    P4.renderer.render(P4.scene, P4.camera);
    // 축 기즈모 그리기
    if(P4.axisGizmoInst) P4.axisGizmoInst.draw();
    P4.raf = requestAnimationFrame(loop);
  }
  loop();

  // 축 기즈모 초기화 (우상단 XYZ 방향 표시)
  try{
    P4.axisGizmoInst = new SceneGizmo('p4-axis-gizmo',
      () => ({theta: P4.orb.theta, phi: P4.orb.phi}),
      (theta, phi) => { P4.orb.theta = theta; P4.orb.phi = phi; }
    );
  }catch(_){}

  // HDRI 파일 입력 바인딩
  const hdrInput = $('p4-hdri-input');
  if(hdrInput){
    hdrInput.addEventListener('change', async (e) => {
      const f = e.target.files[0]; if(!f) return;
      try{
        const buf = await f.arrayBuffer();
        p4ApplyHDR(buf, f.name);
      }catch(err){
        appNotify('HDRI 로드 실패: ' + (err.message||err));
      }
      hdrInput.value = '';
    });
  }

  P4.threeReady = true;

  // 기본 HDRI 자동 로드
  p4LoadDefaultHDR();
}

// ═══════════════════════════════════════════════════════════════════════
//  HDRI 환경광 — Radiance RGBE 파서 + PMREMGenerator 환경맵
// ═══════════════════════════════════════════════════════════════════════
/** Radiance .hdr (RGBE) → {width, height, rgba: Float32Array}
 *  Format:
 *    #?RADIANCE\n
 *    VAR=value\n    (FORMAT=, EXPOSURE=, ...)
 *    \n             ← end of variables
 *    -Y H +X W\n    ← resolution line (AFTER blank line)
 *    [binary pixels]
 */
function _parseRGBE(buf){
  const u8 = new Uint8Array(buf);
  // 1) 변수 섹션 파싱 (빈 줄로 종료)
  let p = 0, vars = '';
  while(p < u8.length - 1){
    const c = u8[p++];
    vars += String.fromCharCode(c);
    if(c === 0x0A && u8[p] === 0x0A){ p++; break; }   // blank line found
  }
  if(!/^#\?(RADIANCE|RGBE)/i.test(vars)) throw new Error('Not a Radiance HDR file');

  // 2) 해상도 라인 — 빈 줄 다음의 단일 라인
  let resLine = '';
  while(p < u8.length){
    const c = u8[p++];
    if(c === 0x0A) break;
    resLine += String.fromCharCode(c);
  }
  const m = resLine.match(/([+-][YX])\s+(\d+)\s+([+-][YX])\s+(\d+)/);
  if(!m) throw new Error('HDR resolution line not found');
  let H, W;
  if(m[1][1] === 'Y'){ H = +m[2]; W = +m[4]; } else { W = +m[2]; H = +m[4]; }

  const rgbe = new Uint8Array(W * H * 4);

  for(let y = 0; y < H; y++){
    if(p + 4 > u8.length) throw new Error('HDR data truncated');
    const b0 = u8[p], b1 = u8[p+1], b2 = u8[p+2], b3 = u8[p+3];
    if(b0 === 2 && b1 === 2 && (b2 & 0x80) === 0){
      // New RLE: 채널별 RLE 압축
      const scanW = (b2 << 8) | b3;
      if(scanW !== W) throw new Error('HDR scanline width mismatch');
      p += 4;
      for(let ch = 0; ch < 4; ch++){
        let x = 0;
        while(x < W){
          const c = u8[p++];
          if(c > 128){
            const cnt = c - 128, val = u8[p++];
            for(let k = 0; k < cnt; k++) rgbe[(y*W + x + k)*4 + ch] = val;
            x += cnt;
          } else {
            for(let k = 0; k < c; k++) rgbe[(y*W + x + k)*4 + ch] = u8[p++];
            x += c;
          }
        }
      }
    } else {
      // 구형 RLE / raw — 단순 raw 4바이트 픽셀 가정
      let x = 0;
      while(x < W){
        rgbe[(y*W + x)*4    ] = u8[p++];
        rgbe[(y*W + x)*4 + 1] = u8[p++];
        rgbe[(y*W + x)*4 + 2] = u8[p++];
        rgbe[(y*W + x)*4 + 3] = u8[p++];
        x++;
      }
    }
  }

  // RGBE → Float RGBA
  const out = new Float32Array(W * H * 4);
  for(let i = 0; i < W*H; i++){
    const r = rgbe[i*4], g = rgbe[i*4+1], b = rgbe[i*4+2], e = rgbe[i*4+3];
    if(e === 0){
      out[i*4] = 0; out[i*4+1] = 0; out[i*4+2] = 0;
    } else {
      const f = Math.pow(2, e - 128) / 255;
      out[i*4]   = r * f;
      out[i*4+1] = g * f;
      out[i*4+2] = b * f;
    }
    out[i*4+3] = 1.0;
  }
  return {width: W, height: H, rgba: out};
}

/** HDR 버퍼 → 환경맵 생성 + 씬 적용 */
function p4ApplyHDR(buf, label){
  const THREE = window.THREE;
  if(!P4.threeReady || !P4.renderer) return;
  try{
    const {width, height, rgba} = _parseRGBE(buf);
    const dataTex = new THREE.DataTexture(rgba, width, height, THREE.RGBAFormat, THREE.FloatType);
    dataTex.mapping = THREE.EquirectangularReflectionMapping;
    dataTex.needsUpdate = true;

    const pmrem = new THREE.PMREMGenerator(P4.renderer);
    pmrem.compileEquirectangularShader();
    const env = pmrem.fromEquirectangular(dataTex).texture;
    pmrem.dispose();
    dataTex.dispose();

    // 기존 envMap 해제
    if(P4.envMap){ P4.envMap.dispose(); P4.envMap = null; }
    P4.envMap = env;
    P4.hdrName = label || 'HDRI';

    p4ToggleHDR(P4.hdrOn);  // 현재 on/off 상태대로 반영
    const info = $('p4-hdri-info');
    if(info) info.textContent = `✅ ${P4.hdrName}  (${width}×${height})`;
  }catch(e){
    const info = $('p4-hdri-info');
    if(info) info.textContent = `⚠️ HDRI 파싱 실패: ${e.message||e}`;
    console.error('HDR parse error:', e);
  }
}

/** 서버 기본 HDRI 로드 */
async function p4LoadDefaultHDR(){
  const info = $('p4-hdri-info');
  if(info) info.textContent = '⏳ 기본 HDRI 로드 중...';
  try{
    const r = await fetch('/api/hdri/default');
    if(!r.ok) throw new Error(`HTTP ${r.status}`);
    const name = r.headers.get('X-HDRI-Name') || 'default.hdr';
    const buf = await r.arrayBuffer();
    p4ApplyHDR(buf, name);
  }catch(e){
    if(info) info.textContent = `⚠️ 기본 HDRI 없음 (${e.message||e})`;
  }
}

/** HDRI 켜기/끄기 */
function p4ToggleHDR(on){
  P4.hdrOn = !!on;
  if(!P4.scene) return;
  if(P4.hdrOn && P4.envMap){
    P4.scene.environment = P4.envMap;
    // HDRI가 lighting을 담당하면 폴백 조명은 약한 보강만
    if(P4.ambLight)  P4.ambLight.intensity  = 0.08;
    if(P4.hemiLight) P4.hemiLight.intensity = 0.10;
    if(P4.dirLight)  P4.dirLight.intensity  = 0.15;
    if(P4.fillLight) P4.fillLight.intensity = 0.08;
    if(P4.rimLight)  P4.rimLight.intensity  = 0.08;
  } else {
    P4.scene.environment = null;
    // HDRI 없을 땐 3-point 풀 강도 — 어느 각도에서 봐도 어두운 면 없게
    if(P4.ambLight)  P4.ambLight.intensity  = 0.35;
    if(P4.hemiLight) P4.hemiLight.intensity = 0.55;
    if(P4.dirLight)  P4.dirLight.intensity  = 0.85;
    if(P4.fillLight) P4.fillLight.intensity = 0.45;
    if(P4.rimLight)  P4.rimLight.intensity  = 0.35;
  }
  if(P4.mesh && P4.mesh.material){
    P4.mesh.material.envMapIntensity = P4.hdrIntensity;
    P4.mesh.material.needsUpdate = true;
  }
}

/** HDRI 강도 슬라이더 */
function p4SetHDRIntensity(v){
  P4.hdrIntensity = Math.max(0, v);
  if(P4.mesh && P4.mesh.material){
    P4.mesh.material.envMapIntensity = P4.hdrIntensity;
  }
}

// ─── Transform Gizmo (클릭시 이동/회전/스케일) ─────────────────────
function p4BuildGizmoGroup(){
  const THREE = window.THREE;
  const grp = new THREE.Group();
  grp.name = 'p4GizmoGroup';
  grp.renderOrder = 1000;
  return grp;
}

function p4TryPickMesh(e){
  if(!P4.mesh) return false;
  const THREE = window.THREE;
  const cv = $('p4-canvas');
  const rect = cv.getBoundingClientRect();
  const mx = ((e.clientX - rect.left) / rect.width) * 2 - 1;
  const my = -((e.clientY - rect.top) / rect.height) * 2 + 1;
  const raycaster = new THREE.Raycaster();
  raycaster.setFromCamera(new THREE.Vector2(mx, my), P4.camera);
  const hits = raycaster.intersectObject(P4.mesh, false);
  if(hits.length > 0){
    p4AttachTransform(P4.mesh);
    return true;
  }
  return false;
}

function p4AttachTransform(obj){
  const THREE = window.THREE;
  if(P4.gizmoGroup){ P4.scene.remove(P4.gizmoGroup); P4.gizmoGroup = null; }
  P4.gizmoGroup = p4BuildGizmoGroup();
  P4.gizmoGroup.userData.target = obj;
  // 핸들 월드 크기 — 카메라 거리의 15% (화면 상 일정한 크기로 보이도록)
  // baseRadius 저장 → 줌에 따라 render loop에서 그룹 스케일 조절
  P4.gizmoGroup.userData.scale = P4.orb.radius * 0.15;
  P4.gizmoGroup.userData.baseRadius = P4.orb.radius;
  p4RebuildGizmoHandles();
  P4.scene.add(P4.gizmoGroup);
  // Transform 모드 바 표시
  const bar = $('p4-tmode-bar'); if(bar) bar.style.display = 'flex';
}

function p4DetachTransform(){
  if(P4.gizmoGroup){ P4.scene.remove(P4.gizmoGroup); P4.gizmoGroup = null; }
  const bar = $('p4-tmode-bar'); if(bar) bar.style.display = 'none';
}

function p4SetTMode(mode){
  P4.tMode = mode;
  // 버튼 상태 갱신
  document.querySelectorAll('.p4-tmode[data-mode]').forEach(b => {
    const active = b.dataset.mode === mode;
    b.style.background = active ? '#1E4FAA' : '#2A2A2A';
    b.style.color = active ? '#fff' : '#C0C0C0';
  });
  // 핸들 재구성
  if(P4.gizmoGroup) p4RebuildGizmoHandles();
}

function p4RebuildGizmoHandles(){
  const THREE = window.THREE;
  if(!P4.gizmoGroup) return;
  const target = P4.gizmoGroup.userData.target;
  const s = P4.gizmoGroup.userData.scale || 1;
  // 기존 자식 제거
  while(P4.gizmoGroup.children.length) P4.gizmoGroup.remove(P4.gizmoGroup.children[0]);
  // 타겟 위치에 배치
  P4.gizmoGroup.position.copy(target.position);

  const axes = [
    {name:'x', color:0xFF4040, dir:new THREE.Vector3(1,0,0)},
    {name:'y', color:0x40FF40, dir:new THREE.Vector3(0,1,0)},
    {name:'z', color:0x4080FF, dir:new THREE.Vector3(0,0,1)},
  ];
  axes.forEach(ax => {
    let handle;
    if(P4.tMode === 'translate'){
      // 화살표 (실린더 + 콘)
      const geo = new THREE.ConeGeometry(s*0.08, s*0.3, 8);
      const mat = new THREE.MeshBasicMaterial({color: ax.color, depthTest: false, transparent: true, opacity: 0.9});
      handle = new THREE.Mesh(geo, mat);
      handle.position.copy(ax.dir.clone().multiplyScalar(s));
      const lineGeo = new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(0,0,0), ax.dir.clone().multiplyScalar(s*0.9)
      ]);
      const line = new THREE.Line(lineGeo, new THREE.LineBasicMaterial({color: ax.color, depthTest: false, linewidth: 3}));
      P4.gizmoGroup.add(line);
      if(ax.name === 'x') handle.rotateZ(-Math.PI/2);
      else if(ax.name === 'z') handle.rotateX(Math.PI/2);
    } else if(P4.tMode === 'rotate'){
      const geo = new THREE.TorusGeometry(s, s*0.02, 8, 48);
      const mat = new THREE.MeshBasicMaterial({color: ax.color, depthTest: false, transparent: true, opacity: 0.9, side: THREE.DoubleSide});
      handle = new THREE.Mesh(geo, mat);
      if(ax.name === 'x') handle.rotateY(Math.PI/2);
      else if(ax.name === 'y') handle.rotateX(Math.PI/2);
    } else { // scale
      const geo = new THREE.BoxGeometry(s*0.15, s*0.15, s*0.15);
      const mat = new THREE.MeshBasicMaterial({color: ax.color, depthTest: false, transparent: true, opacity: 0.9});
      handle = new THREE.Mesh(geo, mat);
      handle.position.copy(ax.dir.clone().multiplyScalar(s));
      const lineGeo = new THREE.BufferGeometry().setFromPoints([
        new THREE.Vector3(0,0,0), ax.dir.clone().multiplyScalar(s*0.9)
      ]);
      const line = new THREE.Line(lineGeo, new THREE.LineBasicMaterial({color: ax.color, depthTest: false}));
      P4.gizmoGroup.add(line);
    }
    handle.userData = {axis: ax.name, color: ax.color};
    handle.renderOrder = 1001;
    P4.gizmoGroup.add(handle);
  });
}

function p4TryStartGizmoDrag(e){
  if(!P4.gizmoGroup) return false;
  const THREE = window.THREE;
  const cv = $('p4-canvas');
  const rect = cv.getBoundingClientRect();
  const mx = ((e.clientX - rect.left) / rect.width) * 2 - 1;
  const my = -((e.clientY - rect.top) / rect.height) * 2 + 1;
  const raycaster = new THREE.Raycaster();
  raycaster.linePrecision = 0.1;
  raycaster.setFromCamera(new THREE.Vector2(mx, my), P4.camera);
  // 핸들 메쉬만 체크 (Line 제외)
  const handles = P4.gizmoGroup.children.filter(c => c.type === 'Mesh');
  const hits = raycaster.intersectObjects(handles, false);
  if(hits.length > 0){
    const h = hits[0].object;
    const target = P4.gizmoGroup.userData.target;
    P4.gizmoDrag = {
      mode: P4.tMode, axis: h.userData.axis,
      startPos: target.position.clone(),
      startRot: target.rotation.clone(),
      startScale: target.scale.clone(),
      startMX: e.clientX, startMY: e.clientY,
    };
    e.preventDefault();
    return true;
  }
  return false;
}

function p4UpdateGizmoDrag(e, dx, dy){
  const d = P4.gizmoDrag;
  if(!d || !P4.gizmoGroup) return;
  const target = P4.gizmoGroup.userData.target;
  const s = P4.gizmoGroup.userData.scale || 1;
  // 스크린 상에서의 누적 이동량
  const mx = e.clientX - d.startMX;
  const my = e.clientY - d.startMY;
  const axIdx = {x:0, y:1, z:2}[d.axis];

  if(d.mode === 'translate'){
    // 축 방향 단위 벡터 × 이동량 스칼라
    const moveAmt = (mx * 0.01 - my * 0.01) * (s * 0.3);
    target.position.copy(d.startPos);
    target.position.getComponent(axIdx);
    const add = [0,0,0]; add[axIdx] = moveAmt;
    target.position.x = d.startPos.x + add[0];
    target.position.y = d.startPos.y + add[1];
    target.position.z = d.startPos.z + add[2];
  } else if(d.mode === 'rotate'){
    const ang = (mx + my) * 0.01;
    target.rotation.copy(d.startRot);
    if(axIdx === 0) target.rotation.x = d.startRot.x + ang;
    else if(axIdx === 1) target.rotation.y = d.startRot.y + ang;
    else target.rotation.z = d.startRot.z + ang;
  } else { // scale
    const factor = 1 + (mx - my) * 0.005;
    target.scale.copy(d.startScale);
    if(axIdx === 0) target.scale.x = d.startScale.x * factor;
    else if(axIdx === 1) target.scale.y = d.startScale.y * factor;
    else target.scale.z = d.startScale.z * factor;
  }
  // 기즈모 위치 갱신 (타겟 따라다님)
  P4.gizmoGroup.position.copy(target.position);
}

function p4FitCamera(){
  if(!P4.mesh) return;
  const THREE = window.THREE;
  const box = new THREE.Box3().setFromObject(P4.mesh);
  const center = box.getCenter(new THREE.Vector3());
  const size = box.getSize(new THREE.Vector3());
  const maxDim = Math.max(size.x, size.y, size.z) || 1;
  P4.orb.cx = center.x; P4.orb.cy = center.y; P4.orb.cz = center.z;
  // FOV 기반 정확한 fit 거리 + 1.3배 여유 (상·하 모두 안 잘리도록)
  const fovRad = (P4.camera.fov * Math.PI) / 180;
  const fitDist = (maxDim * 0.5) / Math.tan(fovRad * 0.5);
  P4.orb.radius = fitDist * 1.3;
  // near/far도 씬 크기에 맞게
  P4.camera.near = Math.max(0.001, maxDim * 0.0005);
  P4.camera.far  = maxDim * 80;
  P4.camera.updateProjectionMatrix();
}

async function p4UpdateViewer(texVer){
  if(!P4.threeReady) initP4Viewer();
  if(!P4.threeReady || !P4.sessionId) return;
  const THREE = window.THREE;

  // 1) 메쉬 OBJ 받아서 파싱 (V, F, UV)
  if(!P4.mesh){
    const objR = await fetch(`/api/bake/mesh/${P4.sessionId}`);
    const objText = await objR.text();
    const V = [], UV = [], F = [];
    for(const ln of objText.split('\n')){
      const p = ln.trim().split(/\s+/);
      if(p[0]==='v') V.push(+p[1], +p[2], +p[3]);
      else if(p[0]==='vt') UV.push(+p[1], +p[2]);
      else if(p[0]==='f'){
        const idx = p.slice(1).map(s => +s.split('/')[0]-1);
        if(idx.length >= 3){
          for(let i=1; i<idx.length-1; i++) F.push(idx[0], idx[i], idx[i+1]);
        }
      }
    }
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(V), 3));
    if(UV.length === (V.length/3)*2){
      geo.setAttribute('uv', new THREE.BufferAttribute(new Float32Array(UV), 2));
    }
    geo.setIndex(new THREE.BufferAttribute(F.length > 65535 ? new Uint32Array(F) : new Uint16Array(F), 1));
    geo.computeVertexNormals();
    const mat = new THREE.MeshStandardMaterial({
      color:0xffffff, roughness:0.8, metalness:0.0, side:THREE.DoubleSide,
      envMapIntensity: P4.hdrIntensity,
    });
    P4.mesh = new THREE.Mesh(geo, mat);
    P4.scene.add(P4.mesh);
    p4FitCamera();
  }

  // 2) 텍스처 로드 → 머티리얼에 바인드
  const loader = new THREE.TextureLoader();
  loader.load(`/api/bake/texture/${P4.sessionId}?v=${texVer||Date.now()}`, (tex) => {
    // r152+ colorSpace, 이전은 encoding. r160에서 encoding은 제거됨.
    if('colorSpace' in tex) tex.colorSpace = THREE.SRGBColorSpace;
    else tex.encoding = THREE.sRGBEncoding || 3001;
    if(P4.textureObj){ P4.textureObj.dispose(); }
    P4.textureObj = tex;
    if(P4.mesh){
      P4.mesh.material.map = tex;
      P4.mesh.material.needsUpdate = true;
    }
  });
}

// 드롭존 바인딩 — DOMContentLoaded 후
(function initP4(){
  const check = setInterval(() => {
    if($('p4-ply-drop') && $('p4-obj-drop')){
      p4BindDrop('p4-ply-drop', 'p4-ply-input', 'p4-ply-name', 'ply');
      p4BindDrop('p4-obj-drop', 'p4-obj-input', 'p4-obj-name', 'obj');
      clearInterval(check);
    }
  }, 200);
})();

// ═══════════════════════════════════════════════════════════════════════
//  🚀 전체 자동 처리 — Page 1~4 순차 실행 + 세션 공유
// ═══════════════════════════════════════════════════════════════════════
App.autoSessionId = null;
App.autoSSE = null;

async function runAutoPipeline(){
  if(!App.file){ appNotify('먼저 파일을 선택하세요.'); return; }
  const btn = $('auto-run-btn');
  if(btn){ btn.disabled = true; btn.textContent = '🔄 업로드 중...'; }
  const prog = $('auto-prog-wrap'); if(prog) prog.classList.add('show');
  $('auto-prog-fill').style.width = '2%';
  $('auto-prog-msg').textContent = '파일 업로드 중...';

  try{
    // 1) 파일 업로드
    const fd = new FormData();
    fd.append('file', App.file, App.file.name);
    const up = await fetch('/api/upload', {method:'POST', body:fd});
    if(!up.ok){ throw new Error(`업로드 실패 ${up.status}`); }
    const upData = await up.json();
    App.autoSessionId = upData.session_id;
    // 이 세션을 다른 페이지들도 공유
    P3.sessionId = App.autoSessionId;
    P2.sessionId = App.autoSessionId;
    P4.sessionId = App.autoSessionId;
    P3.loadedFileName = upData.filename;

    if(btn) btn.textContent = '⚙️ 처리 중...';

    // 2) 자동 파이프라인 SSE 시작 (POST JSON)
    const body = {
      lod: $('auto-lod').value,
      tex_size: +$('auto-tex').value,
    };
    const ctrl = new AbortController();
    App.autoSSE = { close(){ try{ ctrl.abort(); }catch(_){} } };

    const handleEvent = (d) => {
      if(d.error){
        appNotify('자동처리 오류: ' + d.error);
        if(App.autoSSE){ App.autoSSE.close(); App.autoSSE = null; }
        if(btn){ btn.disabled = false; btn.textContent = '🚀 전체 자동 처리'; }
        return;
      }
      if(typeof d.progress === 'number'){
        $('auto-prog-fill').style.width = d.progress + '%';
      }
      if(d.msg){
        $('auto-prog-msg').textContent = d.msg;
      }
      if(d.phase === 'complete'){
        if(App.autoSSE){ App.autoSSE.close(); App.autoSSE = null; }
        $('auto-prog-fill').style.width = '100%';
        if(btn){ btn.disabled = false; btn.textContent = '🔁 다시 실행'; }
      }
    };

    postSSE(`/api/automate/${App.autoSessionId}`, body, handleEvent, {signal: ctrl.signal})
      .catch(err => {
        if(err?.name === 'AbortError') return;
        if(App.autoSSE){ App.autoSSE.close(); App.autoSSE = null; }
        $('auto-prog-msg').textContent = '⚠️ 연결 끊김: ' + (err?.message || err);
        if(btn){ btn.disabled = false; btn.textContent = '🚀 전체 자동 처리'; }
      });
  }catch(e){
    appNotify('자동처리 실패: ' + e.message);
    if(btn){ btn.disabled = false; btn.textContent = '🚀 전체 자동 처리'; }
  }
}

// ── 단축키 오버레이 ? ──────────────────────────────────────────────
function toggleShortcutsHelp(show){
  const o = $('kbd-overlay');
  if(!o) return;
  if(show === undefined) show = !o.classList.contains('show');
  o.classList.toggle('show', show);
}
window.addEventListener('keydown', e => {
  // 입력창 포커스 시엔 무시
  const t = e.target;
  if(t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.isContentEditable)) return;
  if(e.key === '?' || (e.shiftKey && e.key === '/')){
    e.preventDefault(); toggleShortcutsHelp();
  } else if(e.key === 'Escape'){
    const o = $('kbd-overlay');
    if(o && o.classList.contains('show')){ toggleShortcutsHelp(false); }
  }
});

// Page 2에서 자동 콜라이더 JSON 다운로드
async function p2DownloadAutoCollider(){
  if(!App.autoSessionId){ appNotify('자동 파이프라인 결과가 없습니다.'); return; }
  try{
    const r = await fetch(`/api/auto/collider/${App.autoSessionId}`);
    if(!r.ok) throw new Error(`서버 오류 ${r.status}`);
    const text = await r.text();
    const stem = (P3.loadedFileName || 'mesh').replace(/\.[^.]+$/,'').replace(/[^\w\-]/g,'_');
    const fname = `${stem}_colliders.json`;

    const api = await getPyApi();
    if(api){
      const rr = await api.save_file_dialog(fname, text);
      if(rr && rr.ok){ appNotify('✅ 자동 콜라이더 JSON 저장:\n' + rr.path); }
      else if(rr && rr.reason !== 'cancelled'){ appNotify('저장 실패: '+rr.reason); }
      return;
    }
    const blob = new Blob([text], {type:'application/json'});
    await saveBlob(fname, blob);
  }catch(e){
    appNotify('다운로드 실패: ' + e.message);
  }
}

// 탭 전환 시 자동처리 결과를 각 페이지가 로드
async function autoLoadPageData(tabN){
  if(!App.autoSessionId) return;
  try{
    const st = await (await fetch(`/api/auto/status/${App.autoSessionId}`)).json();
    if(!st.auto_done) return;

    if(tabN === 3 && st.has_mesh && !P3.final){
      // Page 3 뷰어에 이미 베이크된 메쉬 표시
      const r = await fetch(`${P3_BACKEND}/mesh/${P3.sessionId}`);
      if(r.ok){
        const objText = await r.text();
        P3.final = parseOBJToMesh(objText);
        await ensureP3ViewerReady();
        p3UpdateViewer(P3.final);
        $('p3-export-smooth').disabled = false;
        const fbxBtn = $('p3-export-fbx'); if(fbxBtn) fbxBtn.disabled = false;
        const glbBtn = $('p3-export-glb'); if(glbBtn) glbBtn.disabled = false;
      }
    } else if(tabN === 4 && st.has_texture){
      // Page 4: 이미 세션에 베이크 결과 있음 → 이미지/뷰어 갱신
      const tVer = Date.now();
      const img = $('p4-tex-img');
      if(img){ img.style.filter=''; img.src = `/api/bake/texture/${P4.sessionId}?v=${tVer}`; }
      const ph = $('p4-tex-placeholder'); if(ph) ph.style.display = 'none';
      await p4UpdateViewer(tVer);
      $('p4-save-btn').disabled = false;
      const fbxSave=$('p4-save-fbx-btn'); if(fbxSave) fbxSave.disabled=false;
      const glbSave=$('p4-save-glb-btn'); if(glbSave) glbSave.disabled=false;
    } else if(tabN === 2 && st.has_collider){
      // Page 2 자동 콜라이더 결과 다운로드 안내 배너 표시
      // (기존 P2.meshCols와 별개 — 자동 결과는 별도 다운로드)
      const info = $('p2-auto-info');
      if(info){ info.style.display = 'block'; }
    }
  }catch(e){ /* 세션 만료 등 무시 */ }
}


// ═══════════════════════════════════════════════════════════════════════
