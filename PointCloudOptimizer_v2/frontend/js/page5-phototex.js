// ═══════════════════════════════════════════════════════════════════════
//  Page 5 (Photo Texture / SfM) 로컬 state
// ═══════════════════════════════════════════════════════════════════════
const P5 = {
  meshFile: null, photos: [], sessionId: null, texSize: 2048,
  renderer: null, scene: null, camera: null,
  mesh: null, orb: null, raf: null, threeReady: false,
};

//  PAGE 5 — 사진 텍스처 투영 (SfM + projective texturing)
// ═══════════════════════════════════════════════════════════════════════
P5.meshFile = null;
P5.photos = [];  // File[]
P5.sessionId = null;
P5.texSize = 2048;

function p5SetSize(n){
  P5.texSize = n;
  document.querySelectorAll('.p5-size-btn').forEach(b => {
    b.classList.toggle('p5-size-active', +b.dataset.p5size === n);
  });
}

function p5UpdateStats(){
  const cnt = P5.photos.length;
  $('p5-photo-count').textContent = `(${cnt}/10)`;
  const validPhotos = cnt >= 4 && cnt <= 10;
  const ok = P5.meshFile && validPhotos;
  $('p5-run-btn').disabled = !ok;
  // Thumbs
  const tw = $('p5-photo-thumbs');
  tw.innerHTML = '';
  P5.photos.forEach((f, i) => {
    const thumb = document.createElement('div');
    thumb.style.cssText = 'width:44px;height:44px;border:1px solid #333;border-radius:4px;'
      + 'background-size:cover;background-position:center;position:relative;background-color:#1a1a1a';
    const url = URL.createObjectURL(f);
    thumb.style.backgroundImage = `url("${url}")`;
    thumb.title = f.name;
    const x = document.createElement('div');
    x.textContent = '✕';
    x.style.cssText = 'position:absolute;top:-4px;right:-4px;background:#d32f2f;color:#fff;'
      + 'border-radius:50%;width:14px;height:14px;text-align:center;line-height:14px;font-size:9px;cursor:pointer';
    x.onclick = () => { P5.photos.splice(i, 1); p5UpdateStats(); };
    thumb.appendChild(x);
    tw.appendChild(thumb);
  });
}

function p5ClearPhotos(){
  P5.photos = [];
  p5UpdateStats();
}

function _p5AddPhotos(fileList){
  for(const f of fileList){
    if(P5.photos.length >= 10){ appNotify('최대 10장까지만 가능합니다.'); break; }
    if(!/\.(jpe?g|png|webp)$/i.test(f.name)){
      appNotify(`지원하지 않는 이미지: ${f.name}`); continue;
    }
    if(!_checkBrowserUploadSize(f)) return;
    P5.photos.push(f);
  }
  p5UpdateStats();
}

// Drop bindings
(function initP5Drop(){
  const bindMesh = () => {
    const d = $('p5-mesh-drop'), i = $('p5-mesh-input'), n = $('p5-mesh-name');
    if(!d || !i) return;
    d.addEventListener('click', () => i.click());
    d.addEventListener('dragover', e => { e.preventDefault(); d.classList.add('drag'); });
    d.addEventListener('dragleave', () => d.classList.remove('drag'));
    const set = (f) => {
      if(!f) return;
      if(!_checkBrowserUploadSize(f)) return;
      const low = f.name.toLowerCase();
      if(!(low.endsWith('.obj') || low.endsWith('.fbx') || low.endsWith('.glb') || low.endsWith('.ply'))){
        appNotify('메쉬는 .obj / .fbx / .glb / .ply 만 지원'); return;
      }
      P5.meshFile = f;
      if(n) n.textContent = f.name;
      p5UpdateStats();
    };
    d.addEventListener('drop', e => { e.preventDefault(); d.classList.remove('drag'); set(e.dataTransfer.files[0]); });
    i.addEventListener('change', e => set(e.target.files[0]));
  };
  const bindPhotos = () => {
    const d = $('p5-photo-drop'), i = $('p5-photo-input');
    if(!d || !i) return;
    d.addEventListener('click', () => i.click());
    d.addEventListener('dragover', e => { e.preventDefault(); d.classList.add('drag'); });
    d.addEventListener('dragleave', () => d.classList.remove('drag'));
    d.addEventListener('drop', e => { e.preventDefault(); d.classList.remove('drag'); _p5AddPhotos(e.dataTransfer.files); });
    i.addEventListener('change', e => _p5AddPhotos(e.target.files));
  };
  // Wait for DOM
  if(document.readyState === 'loading'){
    document.addEventListener('DOMContentLoaded', () => { bindMesh(); bindPhotos(); });
  } else {
    bindMesh(); bindPhotos();
  }
})();

// ─── Page 5 Three.js viewer (간소화 — p4 스타일과 동일) ─────────────────
P5.renderer = null;
P5.scene = null;
P5.camera = null;
P5.mesh = null;
P5.orb = null;
P5.raf = null;
P5.threeReady = false;

function initP5Viewer(){
  if(P5.threeReady) return;
  if(!window.THREE){ loadThreeJS().then(() => initP5Viewer()); return; }
  const THREE = window.THREE;
  const cv = $('p5-canvas'); if(!cv) return;

  P5.renderer = new THREE.WebGLRenderer({canvas:cv, antialias:true, alpha:false});
  P5.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
  P5.renderer.setClearColor(0x121212, 1);
  // r155+ 는 outputColorSpace, r128 이하는 outputEncoding.
  if('outputColorSpace' in P5.renderer){
    P5.renderer.outputColorSpace = THREE.SRGBColorSpace;
  } else {
    P5.renderer.outputEncoding = THREE.sRGBEncoding || 3001;
  }

  P5.scene = new THREE.Scene();
  P5.scene.add(new THREE.AmbientLight(0xffffff, 0.6));
  const dL = new THREE.DirectionalLight(0xffffff, 0.8);
  dL.position.set(1, 2, 1);
  P5.scene.add(dL);

  P5.camera = new THREE.PerspectiveCamera(45, cv.clientWidth / cv.clientHeight, 0.01, 10000);
  P5.orb = {cx:0, cy:0, cz:0, radius:5, theta:-0.4, phi:1.15};

  function resize(){
    const w = cv.clientWidth, h = cv.clientHeight;
    if(cv.width !== w || cv.height !== h){
      P5.renderer.setSize(w, h, false);
      P5.camera.aspect = w/h; P5.camera.updateProjectionMatrix();
    }
  }

  // Maya navigation
  let navMode = null, lx = 0, ly = 0;
  cv.addEventListener('contextmenu', e => e.preventDefault());
  cv.addEventListener('mousedown', e => {
    lx = e.clientX; ly = e.clientY;
    if(e.altKey) navMode = e.button === 0 ? 'rotate' : (e.button === 1 ? 'pan' : 'zoom');
    else if(e.button === 0) navMode = 'rotate';
    if(navMode) e.preventDefault();
  });
  window.addEventListener('mouseup', () => { navMode = null; });
  window.addEventListener('mousemove', e => {
    if(!navMode) return;
    const dx = e.clientX - lx, dy = e.clientY - ly;
    lx = e.clientX; ly = e.clientY;
    if(navMode === 'rotate'){
      P5.orb.theta += dx * 0.01;
      P5.orb.phi = Math.max(0.1, Math.min(Math.PI - 0.1, P5.orb.phi - dy * 0.01));
    } else if(navMode === 'pan'){
      const r = P5.orb.radius * 0.002;
      const ct = Math.cos(P5.orb.theta), st = Math.sin(P5.orb.theta);
      P5.orb.cx -= dx * r * ct; P5.orb.cz += dx * r * st;
      P5.orb.cy += dy * r;
    } else if(navMode === 'zoom'){
      P5.orb.radius *= (1 + dx * 0.01);
    }
  });
  // 휠 줌 — magnitude 정규화 + 상한 clamp
  cv.addEventListener('wheel', e => {
    e.preventDefault();
    const f = Math.sign(e.deltaY) * Math.min(Math.abs(e.deltaY) / 120, 1);
    P5.orb.radius *= (1 + f * 0.12);
    P5.orb.radius = Math.max(0.001, Math.min(1e5, P5.orb.radius));
  });
  window.addEventListener('keydown', e => {
    const pg5 = document.getElementById('page5');
    if(!pg5 || pg5.style.display === 'none') return;
    if(e.key === 'f' || e.key === 'F'){ if(P5.mesh) p5FitCamera(); }
  });

  function loop(){
    resize();
    const {cx,cy,cz,radius,theta,phi} = P5.orb;
    P5.camera.position.set(
      cx + radius*Math.sin(phi)*Math.sin(theta),
      cy + radius*Math.cos(phi),
      cz + radius*Math.sin(phi)*Math.cos(theta));
    P5.camera.lookAt(cx, cy, cz);
    P5.renderer.render(P5.scene, P5.camera);
    P5.raf = requestAnimationFrame(loop);
  }
  loop();

  P5.threeReady = true;
}

function p5FitCamera(){
  if(!P5.mesh || !P5.camera) return;
  const THREE = window.THREE;
  const box = new THREE.Box3().setFromObject(P5.mesh);
  const center = box.getCenter(new THREE.Vector3());
  const size = box.getSize(new THREE.Vector3());
  const maxDim = Math.max(size.x, size.y, size.z) || 1;
  P5.orb.cx = center.x; P5.orb.cy = center.y; P5.orb.cz = center.z;
  const fovRad = (P5.camera.fov * Math.PI) / 180;
  P5.orb.radius = (maxDim * 0.5) / Math.tan(fovRad * 0.5) * 1.4;
  P5.camera.near = Math.max(0.001, maxDim * 0.0005);
  P5.camera.far = maxDim * 80;
  P5.camera.updateProjectionMatrix();
}

async function p5UpdateViewer(){
  // THREE.js 로드 + viewer 초기화 대기 (최대 5s)
  if(!window.THREE){ await loadThreeJS(); }
  if(!P5.threeReady) initP5Viewer();
  for(let i=0; i<50 && !P5.threeReady; i++){ await new Promise(r=>setTimeout(r,100)); }
  if(!P5.threeReady || !P5.sessionId){
    console.warn('p5UpdateViewer: not ready', {ready:P5.threeReady, sid:P5.sessionId});
    return;
  }
  const THREE = window.THREE;
  // Clean old mesh
  if(P5.mesh){
    try{
      P5.scene.remove(P5.mesh);
      P5.mesh.geometry?.dispose();
      if(P5.mesh.material?.map) P5.mesh.material.map.dispose();
      P5.mesh.material?.dispose();
    }catch(_){}
    P5.mesh = null;
  }
  // Fetch mesh OBJ
  const objR = await fetch(`/api/phototex/mesh/${P5.sessionId}`);
  if(!objR.ok) throw new Error(`mesh fetch ${objR.status}`);
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

  const loader = new THREE.TextureLoader();
  const tex = await new Promise((res, rej) => {
    loader.load(`/api/phototex/texture/${P5.sessionId}?v=${Date.now()}`, res, undefined, rej);
  });
  if('colorSpace' in tex) tex.colorSpace = THREE.SRGBColorSpace;
  else tex.encoding = THREE.sRGBEncoding || 3001;
  const mat = new THREE.MeshStandardMaterial({
    map: tex, color: 0xffffff, roughness:0.85, metalness:0.0,
    side: THREE.DoubleSide,
  });
  P5.mesh = new THREE.Mesh(geo, mat);
  P5.scene.add(P5.mesh);
  p5FitCamera();
}

function p5ShowLoading(title, sub){
  const el = $('p5-viewer-loading');
  if(title){ const t=$('p5-loading-title'); if(t) t.textContent = title; }
  if(sub !== undefined){ const s=$('p5-loading-sub'); if(s) s.textContent = sub; }
  if(el){ el.classList.add('show'); void el.offsetHeight; }
}
function p5HideLoading(){
  const el = $('p5-viewer-loading'); if(el) el.classList.remove('show');
}

async function p5Run(){
  if(!P5.meshFile){ appNotify('메쉬 파일을 올려주세요.'); return; }
  if(P5.photos.length < 4 || P5.photos.length > 10){
    appNotify(`사진 4~10장이 필요합니다 (현재 ${P5.photos.length}장)`); return;
  }
  const runBtn = $('p5-run-btn');
  runBtn.disabled = true;
  const origTxt = runBtn.textContent;
  runBtn.textContent = '🔄 업로드 중...';
  p5ShowLoading('사진 텍스처 투영 중...', '업로드 중');

  // 재시도 시 이전 fallback 배지 초기화
  const _prevBadge = $('p5-fallback-badge');
  if(_prevBadge) _prevBadge.style.display = 'none';

  try{
    // Upload
    const fd = new FormData();
    fd.append('mesh', P5.meshFile, P5.meshFile.name);
    P5.photos.forEach((f, i) => fd.append(`photo_${i}`, f, f.name));
    const up = await fetch('/api/phototex/upload', {method:'POST', body:fd});
    if(!up.ok){ const j = await up.json().catch(()=>({detail:'?'})); throw new Error(j.detail || `HTTP ${up.status}`); }
    const upData = await up.json();
    P5.sessionId = upData.session_id;

    runBtn.textContent = '⚙️ 처리 중...';
    p5ShowLoading('사진 텍스처 투영 중...', 'SfM 카메라 포즈 복원 중');

    // POST JSON SSE — 최종 stats 수신용 holder
    let _p5DoneStats = null;
    await new Promise((res, rej) => {
      const ctrl = new AbortController();
      const onEvent = (d) => {
        if(d.error){ ctrl.abort(); rej(new Error(d.error)); return; }
        if(d.msg){
          const sub = $('p5-loading-sub'); if(sub) sub.textContent = d.msg;
        }
        if(d.progress){
          runBtn.textContent = `⚙️ ${d.progress}%`;
        }
        if(d.step === 'done'){
          _p5DoneStats = d.stats || null;
          ctrl.abort();
          res(d);
        }
      };
      postSSE(`/api/phototex/run-sse/${P5.sessionId}`, {tex_size: P5.texSize}, onEvent, {signal: ctrl.signal})
        .then(() => { if(!_p5DoneStats) rej(new Error('스트림이 done 없이 종료')); })
        .catch(err => {
          if(err?.name === 'AbortError' && _p5DoneStats) return;
          rej(new Error('SSE 끊김: ' + (err?.message || err)));
        });
    });

    // Update 2D texture
    const tVer = Date.now();
    const img = $('p5-tex-img');
    img.src = `/api/phototex/texture/${P5.sessionId}?v=${tVer}`;
    img.style.display = 'block';
    const ph = $('p5-tex-placeholder'); if(ph) ph.style.display = 'none';

    // Update 3D viewer
    p5ShowLoading('사진 텍스처 투영 중...', '뷰어에 메쉬 표시 중');
    await p5UpdateViewer();

    // Show save panel
    $('p5-save-panel').style.display = '';

    // ── stats.fallback 표시 ────────────────────────────────────────
    const badge = $('p5-fallback-badge');
    const reasonEl = $('p5-fallback-reason');
    if(_p5DoneStats && _p5DoneStats.fallback){
      const filled = Math.round((_p5DoneStats.filled_ratio || 0) * 100);
      $('p5-tex-info').innerHTML = `${P5.texSize}×${P5.texSize} · <span style="color:#E8C458">Fallback</span> · 채움률 ${filled}%`;
      if(reasonEl) reasonEl.textContent = _p5DoneStats.fallback_reason || 'SfM 재구성 실패';
      if(badge)    badge.style.display = 'block';
    } else {
      const ncam = _p5DoneStats ? (_p5DoneStats.n_cameras || 0) : 0;
      const filled = _p5DoneStats ? Math.round((_p5DoneStats.filled_ratio || 0) * 100) : 100;
      $('p5-tex-info').textContent = `${P5.texSize}×${P5.texSize} · 카메라 ${ncam}대 · 채움률 ${filled}%`;
      if(badge) badge.style.display = 'none';
    }
  }catch(e){
    appNotify('사진 텍스처 투영 실패\n' + (e.message || e));
  }finally{
    runBtn.disabled = false;
    runBtn.textContent = origTxt;
    p5HideLoading();
  }
}

async function p5SaveOBJ(){
  if(!P5.sessionId){ appNotify('먼저 텍스처 투영을 실행하세요.'); return; }
  const stem = (P5.meshFile?.name || 'mesh').replace(/\.[^.]+$/, '').replace(/[^\w\-]/g, '_');
  try{
    const [objR, mtlR, pngR] = await Promise.all([
      fetch(`/api/phototex/mesh/${P5.sessionId}`),
      fetch(`/api/phototex/mtl/${P5.sessionId}`),
      fetch(`/api/phototex/texture/${P5.sessionId}?v=${Date.now()}`),
    ]);
    const objText = await objR.text();
    const mtlText = await mtlR.text();
    const pngBlob = await pngR.blob();

    const api = await getPyApi();
    if(api){
      const objName = `${stem}_photo.obj`;
      const r = await api.save_file_dialog(objName, objText);
      if(!r || !r.ok){ if(r && r.reason !== 'cancelled') appNotify('저장 실패'); return; }
      const mtlPath = r.path.replace(/\.obj$/i, '.mtl');
      if(api.write_text_file) await api.write_text_file(mtlPath, mtlText);
      const b64 = await blobToBase64(pngBlob);
      await api.save_bytes_dialog(`${stem}_photo.png`, b64);
      appNotify(`✅ 저장 완료\n${r.path}`);
    } else {
      // browser fallback — 3 downloads
      for(const [name, data] of [
        [`${stem}_photo.obj`, new Blob([objText], {type:'text/plain'})],
        [`${stem}_photo.mtl`, new Blob([mtlText], {type:'text/plain'})],
        [`${stem}_photo.png`, pngBlob],
      ]){
        const a = document.createElement('a');
        a.href = URL.createObjectURL(data); a.download = name;
        a.click();
      }
    }
  }catch(e){
    appNotify('저장 오류: ' + e.message);
  }
}

