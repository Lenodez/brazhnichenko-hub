let wikiData = { appleSilicon: [], nvidia: [] };
let lastStatus = null;
let simMode = 'auto';
let selectedAppleGen = 'M4';
let selectedAppleRam = 48;
let catalogCategory = 'text';
let cachedModels = [];
let sessions = [];
let personas = [];
let activeSessionId = null;
let visionImageBase64 = null;
let chatAbortController = null;
let assistantHistory = [];
let currentGalleryFilter = 'all';
let hadRunningMediaJobs = false;

function showToast(msg) {
  const el = document.getElementById('toast');
  el.textContent = msg;
  el.classList.add('show');
  clearTimeout(window.__toastTimer);
  window.__toastTimer = setTimeout(() => el.classList.remove('show'), 3200);
}

function esc(str) {
  return String(str ?? '').replace(/[&<>"']/g, s => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[s]));
}

function switchTab(name) {
  document.querySelectorAll('.nav-tab').forEach(b => b.classList.toggle('active', b.dataset.tab === name));
  document.querySelectorAll('.tab-view').forEach(v => v.classList.toggle('active', v.id === 'tab-' + name));
  if (name === 'models' && cachedModels.length === 0) loadCatalog();
  if (name === 'gallery') loadGallery(currentGalleryFilter);
  if (name === 'system') loadSettings();
  if (name === 'photo') refreshStudioPreviews();
  if (name === 'video') { loadVoices(); refreshStudioPreviews(); }
}

function openAssistantTab() {
  switchTab('chat');
  switchChatSub('assistant');
}

function switchChatSub(sub) {
  document.getElementById('subChatLlmBtn').classList.toggle('active', sub === 'llm');
  document.getElementById('subChatAsstBtn').classList.toggle('active', sub === 'assistant');
  document.getElementById('chatSubLlm').style.display = sub === 'llm' ? 'flex' : 'none';
  document.getElementById('chatSubAssistant').style.display = sub === 'assistant' ? 'flex' : 'none';
}

function renderSparkline(svgId, values, strokeColor) {
  const svg = document.getElementById(svgId);
  if (!svg || !values || values.length < 2) { if (svg) svg.innerHTML = ''; return; }
  const w = 72, h = 18;
  const max = Math.max(100, ...values);
  const pts = values.map((v, i) => {
    const x = (i / (values.length - 1)) * w;
    const y = h - Math.max(1, Math.min(h - 1, (v / max) * (h - 2)));
    return `${x.toFixed(1)},${y.toFixed(1)}`;
  }).join(' ');
  svg.innerHTML = `<polyline fill="none" stroke="${strokeColor}" stroke-width="1.5" points="${pts}"/>`;
}

// ==================== STATUS POLLING ====================
async function pollStatus() {
  try {
    const res = await fetch('/api/status');
    const st = await res.json();
    lastStatus = st;

    document.getElementById('versionPill').textContent = 'v' + st.version;
    document.getElementById('platformPill').textContent = `${st.platform} · ${st.arch}`;

    const hw = st.hardware;
    const m = st.metrics;
    const gpu = m.gpu;
    const gpuBar = document.getElementById('gpuBar');
    let vramPct = 0;

    if (hw.isAppleSilicon) {
      document.getElementById('gpuNameText').textContent = `${hw.detectedGpuName} (Metal)`;
      const usedGB = gpu ? (gpu.vramUsedMiB / 1024).toFixed(1) : '0.0';
      const budGB = hw.detectedVramGB.toFixed(1);
      vramPct = hw.detectedVramGB > 0 && gpu ? Math.min(100, (gpu.vramUsedMiB / 1024) * 100 / hw.detectedVramGB) : 0;
      document.getElementById('gpuValText').textContent = `${usedGB} / ~${budGB} ГБ`;
    } else if (gpu) {
      const usedGB = (gpu.vramUsedMiB / 1024).toFixed(1);
      const totGB = (gpu.vramTotalMiB / 1024).toFixed(1);
      vramPct = gpu.vramTotalMiB > 0 ? Math.min(100, gpu.vramUsedMiB * 100 / gpu.vramTotalMiB) : 0;
      document.getElementById('gpuNameText').textContent = gpu.name;
      document.getElementById('gpuValText').textContent = `${usedGB}/${totGB} ГБ · ${gpu.utilPct}% · ${gpu.tempC}°C`;
    } else {
      document.getElementById('gpuNameText').textContent = hw.detectedGpuName;
      document.getElementById('gpuValText').textContent = 'CPU / Metal';
    }

    gpuBar.style.width = vramPct.toFixed(0) + '%';
    gpuBar.className = 'bar-fill ' + (vramPct > 85 ? 'danger' : vramPct > 65 ? 'warning' : 'signal');

    const ramPct = m.ramTotalGB > 0 ? Math.min(100, m.ramUsedGB * 100 / m.ramTotalGB) : 0;
    document.getElementById('ramLabelText').textContent = hw.isAppleSilicon ? 'Unified Memory' : 'ОЗУ';
    document.getElementById('ramValText').textContent = `${m.ramUsedGB.toFixed(1)}/${m.ramTotalGB.toFixed(1)} ГБ (${ramPct.toFixed(0)}%)`;
    document.getElementById('ramBar').style.width = ramPct.toFixed(0) + '%';

    const diskUsedPct = st.diskTotalGB > 0 ? Math.min(100, (st.diskTotalGB - m.diskFreeDGB) * 100 / st.diskTotalGB) : 0;
    document.getElementById('diskLabelText').textContent = `Диск ${st.driveLabel}`;
    document.getElementById('diskValText').textContent = `${m.diskFreeDGB.toFixed(1)} ГБ своб.`;
    document.getElementById('diskBar').style.width = diskUsedPct.toFixed(0) + '%';

    renderSparkline('gpuSpark', st.history.vramPct, vramPct > 85 ? '#EF4444' : '#FF521B');
    renderSparkline('ramSpark', st.history.ramPct, '#A1A1AA');

    const llmUp = !!st.services.qwen;
    const qPill = document.getElementById('llmQuickStatus');
    qPill.textContent = llmUp ? '● LLM :18080' : '○ LLM ВЫКЛ';
    qPill.className = 'pill mono ' + (llmUp ? 'success' : '');

    const lPill = document.getElementById('llmStatusPill');
    lPill.textContent = llmUp ? '● :18080' : '○ СТОП';
    lPill.className = 'pill mono ' + (llmUp ? 'success' : '');

    const vBadge = document.getElementById('visionBadge');
    vBadge.textContent = st.activeLlmHasVision ? 'VISION' : 'TEXT ONLY';
    vBadge.className = 'pill mono ' + (st.activeLlmHasVision ? 'success' : '');

    // Populate LLM select only if changed
    const sel = document.getElementById('llmModelSelect');
    const curFiles = (st.availableLlms || []).map(x => x.fileName).join('|');
    if (sel.dataset.sig !== curFiles) {
      sel.dataset.sig = curFiles;
      sel.innerHTML = (st.availableLlms || []).length
        ? st.availableLlms.map(x => `<option value="${esc(x.fileName)}" ${x.fileName === st.activeLlm ? 'selected' : ''}>${esc(x.displayName)}</option>`).join('')
        : '<option value="">Нет скачанных GGUF — откройте «Модели и память»</option>';
    }

    // Runtime statuses
    document.getElementById('rtLlamaStatus').textContent = st.runtime.llamaInstalled ? `● ${st.runtime.llamaExePath}` : '▲ Не установлен';
    document.getElementById('rtComfyStatus').textContent = st.runtime.comfyInstalled ? `● ${st.runtime.comfyRootDir}` : '▲ Не установлен';
    document.getElementById('rtVoiceStatus').textContent = `Whisper: ${st.voice.whisperReady ? 'OK' : '—'} · Piper: ${st.voice.piperReady ? 'OK' : '—'} · FFmpeg: ${st.voice.ffmpegReady ? 'OK' : '—'}`;

    const jobs = st.jobs || [];
    const runningMedia = jobs.some(j => (j.kind === 'photo' || j.kind === 'video') && j.status === 'running');
    if (hadRunningMediaJobs && !runningMedia) {
      refreshStudioPreviews();
    }
    hadRunningMediaJobs = runningMedia;

    renderJobs(jobs);
  } catch (e) { /* transient */ }
}

function renderJobs(jobs) {
  const bar = document.getElementById('jobsBar');
  const active = jobs.filter(j => j.status === 'running' || j.status === 'error');
  if (!active.length) { bar.classList.remove('visible'); bar.innerHTML = ''; return; }
  bar.classList.add('visible');
  bar.innerHTML = active.map(j => `
    <div class="row" style="justify-content:space-between;">
      <div><strong>${esc(j.title)}</strong> <span class="muted mono">${esc(j.stage)}</span></div>
      <div class="row">
        <span class="mono">${j.percent.toFixed(0)}%${j.speedMBs > 0 ? ' · ' + j.speedMBs.toFixed(1) + ' МБ/с' : ''}</span>
        ${j.status === 'running' ? `<button class="btn danger sm" onclick="cancelJob('${j.id}')">Отмена</button>` : ''}
      </div>
    </div>
    <div class="bar-track"><div class="bar-fill ${j.status === 'error' ? 'danger' : 'signal'}" style="width:${j.percent.toFixed(1)}%"></div></div>
  `).join('');
}

async function cancelJob(jobId) {
  await fetch('/api/jobs/cancel', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ jobId }) });
  pollStatus();
}

// ==================== HARDWARE WIKI & APPLE SILICON M1-M6 SIMULATOR ====================
async function initHardwareWiki() {
  const res = await fetch('/api/hardware/wiki');
  wikiData = await res.json();

  const gens = ['M1', 'M2', 'M3', 'M4', 'M5', 'M6'];
  document.getElementById('appleGenChips').innerHTML = gens.map(g =>
    `<button class="ram-chip ${g === selectedAppleGen ? 'active' : ''}" data-gen="${g}" onclick="selectAppleGen('${g}')">${g}</button>`
  ).join('');

  document.getElementById('nvidiaGpuSelect').innerHTML = wikiData.nvidia.map(g =>
    `<option value="${esc(g.modelName)}" ${g.modelName.includes('4080') ? 'selected' : ''}>${esc(g.modelName)} (${g.vramGB} ГБ · ${g.bandwidthGBs} ГБ/с)</option>`
  ).join('');

  populateAppleChipsForGen(selectedAppleGen);
}

function selectAppleGen(gen) {
  selectedAppleGen = gen;
  document.querySelectorAll('#appleGenChips .ram-chip').forEach(b => b.classList.toggle('active', b.dataset.gen === gen));
  populateAppleChipsForGen(gen);
  runHardwareSimulation();
}

function populateAppleChipsForGen(gen) {
  const chips = wikiData.appleSilicon.filter(c => c.generation === gen);
  const sel = document.getElementById('appleChipSelect');
  sel.innerHTML = chips.map(c =>
    `<option value="${esc(c.chipName)}">${esc(c.chipName)} (${c.gpuCores} · ${c.bandwidthGBs} ГБ/с)</option>`
  ).join('');
  if (chips.length > 1) sel.selectedIndex = 1;
  onAppleChipChanged(false);
}

function onAppleChipChanged(triggerSim = true) {
  const chipName = document.getElementById('appleChipSelect').value;
  const chip = wikiData.appleSilicon.find(c => c.chipName === chipName) || wikiData.appleSilicon[0];
  if (!chip) return;
  if (!chip.ramOptionsGB.includes(selectedAppleRam))
    selectedAppleRam = chip.ramOptionsGB[Math.min(1, chip.ramOptionsGB.length - 1)];

  document.getElementById('appleRamChips').innerHTML = chip.ramOptionsGB.map(r =>
    `<button class="ram-chip ${r === selectedAppleRam ? 'active' : ''}" onclick="selectAppleRam(${r})">${r} ГБ</button>`
  ).join('');

  if (triggerSim) runHardwareSimulation();
}

function selectAppleRam(ram) {
  selectedAppleRam = ram;
  document.querySelectorAll('#appleRamChips .ram-chip').forEach(b => b.classList.toggle('active', b.textContent === ram + ' ГБ'));
  runHardwareSimulation();
}

function setSimMode(mode) {
  simMode = mode;
  document.getElementById('simModeAuto').classList.toggle('primary', mode === 'auto');
  document.getElementById('simModeApple').classList.toggle('primary', mode === 'apple');
  document.getElementById('simModeNvidia').classList.toggle('primary', mode === 'nvidia');
  document.getElementById('appleSelectorRow').style.display = mode === 'apple' ? 'flex' : 'none';
  document.getElementById('nvidiaSelectorRow').style.display = mode === 'nvidia' ? 'flex' : 'none';
  runHardwareSimulation();
}

async function runHardwareSimulation() {
  const payload = { platform: simMode, chipName: null, ramGB: 0 };
  if (simMode === 'apple') {
    payload.chipName = document.getElementById('appleChipSelect').value;
    payload.ramGB = selectedAppleRam;
  } else if (simMode === 'nvidia') {
    payload.chipName = document.getElementById('nvidiaGpuSelect').value;
    payload.ramGB = Number(document.getElementById('nvidiaRamSelect').value);
  }

  const res = await fetch('/api/hardware/simulate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload)
  });
  const data = await res.json();
  cachedModels = data.models || [];
  renderHardwareSummary(data.hardware);
  renderCatalogGrid();
}

async function loadCatalog() {
  await runHardwareSimulation();
}

function renderHardwareSummary(hw) {
  document.getElementById('simChipLabel').textContent = hw.detectedGpuName;
  const vramSeg = document.getElementById('simPoolVramSeg');
  const ramSeg = document.getElementById('simPoolRamSeg');
  const poolLegend = document.getElementById('simPoolLegend');

  if (hw.isAppleSilicon && hw.matchedAppleChip) {
    const c = hw.matchedAppleChip;
    document.getElementById('simVramLabel').textContent = `~${hw.detectedVramGB.toFixed(1)} ГБ Metal (из ${hw.detectedRamGB} ГБ)`;
    const flags = [c.hasDynamicCaching ? 'Dynamic Caching' : null, c.hasNeuralGpuAccel ? 'Neural Accel' : null].filter(Boolean).join(' · ');
    document.getElementById('simBwLabel').textContent = `${c.bandwidthGBs} ГБ/с${flags ? ' · ' + flags : ''}`;
    document.getElementById('simFormatLabel').textContent = c.bestFormatHint;
    document.getElementById('simSummaryText').textContent = c.summaryRu;

    const metalPct = Math.min(85, (hw.detectedVramGB / Math.max(1, hw.detectedRamGB)) * 100);
    vramSeg.style.width = metalPct.toFixed(0) + '%';
    ramSeg.style.width = (100 - metalPct).toFixed(0) + '%';
    poolLegend.textContent = `Metal GPU ~${hw.detectedVramGB.toFixed(1)} ГБ · macOS резерв ~${(hw.detectedRamGB - hw.detectedVramGB).toFixed(1)} ГБ`;
  } else if (hw.matchedWiki) {
    const g = hw.matchedWiki;
    document.getElementById('simVramLabel').textContent = `${hw.detectedVramGB.toFixed(0)} ГБ VRAM + ${hw.detectedRamGB.toFixed(0)} ГБ RAM`;
    document.getElementById('simBwLabel').textContent = `${g.bandwidthGBs} ГБ/с · ${g.architecture} · FP8:${g.nativeFp8 ? 'да' : 'нет'}`;
    document.getElementById('simFormatLabel').textContent = g.bestFormatHint;
    document.getElementById('simSummaryText').textContent = g.summaryRu;

    const total = Math.max(1, hw.detectedVramGB + Math.max(0, hw.detectedRamGB - 8));
    const vPct = Math.min(85, (hw.detectedVramGB / total) * 100);
    vramSeg.style.width = vPct.toFixed(0) + '%';
    ramSeg.style.width = Math.max(10, 100 - vPct).toFixed(0) + '%';
    poolLegend.textContent = `VRAM ${hw.detectedVramGB.toFixed(0)} ГБ · Оффлоад ОЗУ до ~${Math.max(4, hw.detectedRamGB - 10).toFixed(0)} ГБ`;
  } else {
    document.getElementById('simVramLabel').textContent = `${hw.detectedVramGB.toFixed(1)} ГБ / ${hw.detectedRamGB.toFixed(0)} ГБ RAM`;
    document.getElementById('simBwLabel').textContent = hw.ramTierLabel;
    document.getElementById('simFormatLabel').textContent = 'GGUF Q4_K_M';
    document.getElementById('simSummaryText').textContent = hw.overallTierSummaryRu;
    vramSeg.style.width = '35%';
    ramSeg.style.width = '65%';
    poolLegend.textContent = `${hw.detectedVramGB.toFixed(1)} ГБ GPU · ${hw.detectedRamGB.toFixed(0)} ГБ RAM`;
  }
}

function setCatalogCategory(cat, btn) {
  catalogCategory = cat;
  document.querySelectorAll('#tab-models .subtab').forEach(b => b.classList.toggle('active', b === btn));
  const isHf = cat === 'hf';
  document.getElementById('catalogGrid').style.display = isHf ? 'none' : 'grid';
  document.getElementById('hfSearchPane').style.display = isHf ? 'flex' : 'none';
  if (!isHf) renderCatalogGrid();
}

function renderCatalogGrid() {
  const grid = document.getElementById('catalogGrid');
  const order = { ideal: 0, goodWithOffload: 1, tight: 2, unsupported: 3 };
  const items = cachedModels
    .filter(m => m.category === catalogCategory)
    .sort((a, b) => (order[a.fit.level] ?? 9) - (order[b.fit.level] ?? 9));

  grid.innerHTML = items.map(m => {
    const lvl = m.fit.level;
    const cls = lvl === 'ideal' ? 'ideal' : lvl === 'goodWithOffload' ? 'offload' : lvl === 'tight' ? 'tight' : 'unsupported';
    const badgeCls = lvl === 'ideal' ? 'success' : lvl === 'goodWithOffload' ? 'warning' : lvl === 'tight' ? 'signal' : 'danger';
    return `
      <div class="model-card ${cls}">
        <div class="row" style="justify-content:space-between;align-items:flex-start;">
          <div class="model-title">${esc(m.name)}</div>
          <span class="pill mono ${badgeCls}">${esc(m.fit.badgeText)}</span>
        </div>
        <div class="row">
          <span class="pill mono">${m.totalSizeGB.toFixed(2)} ГБ</span>
          <span class="pill info mono">${esc(m.fit.estimatedSpeedHint)}</span>
          ${m.isGated ? '<span class="pill warning mono">GATED</span>' : ''}
          ${m.installed ? '<span class="pill success mono">✓ УСТАНОВЛЕНО</span>' : ''}
        </div>
        <div class="model-desc">${esc(m.summaryRu)}</div>
        <div class="model-fit-line">${esc(m.fit.reasonRu)}</div>
        <div class="row" style="margin-top:auto;padding-top:4px;justify-content:space-between;">
          <span class="muted mono" style="font-size:11px;">${esc(m.shortTag)}</span>
          <div class="row">
            ${m.installed
              ? `<button class="btn danger sm" onclick="deleteModel('${m.id}')">Удалить</button>`
              : `<button class="btn ${lvl === 'ideal' || lvl === 'goodWithOffload' ? 'signal' : 'primary'} sm" onclick="installModel('${m.id}')">Скачать (${m.missingGB.toFixed(1)} ГБ)</button>`
            }
          </div>
        </div>
      </div>
    `;
  }).join('');
}

async function installModel(modelId) {
  const res = await fetch('/api/catalog/install', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelId })
  });
  const data = await res.json();
  if (!res.ok) return showToast(data.error || 'Ошибка скачивания');
  showToast('Загрузка модели запущена — прогресс отображается сверху');
  pollStatus();
}

async function deleteModel(modelId) {
  if (!confirm('Удалить файлы этой модели с диска? Общие компоненты (VAE/энкодеры) сохранятся.')) return;
  const res = await fetch('/api/catalog/delete', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelId })
  });
  const data = await res.json();
  showToast(`Удалено файлов: ${data.deleted}, освобождено ${data.freedGB.toFixed(2)} ГБ`);
  loadCatalog();
}

// ==================== HUGGING FACE SEARCH ====================
async function searchHuggingFace() {
  const q = document.getElementById('hfQueryInput').value.trim();
  const cat = document.getElementById('hfCatSelect').value;
  if (!q) return;
  const box = document.getElementById('hfResultsList');
  box.innerHTML = '<div class="muted mono">Поиск репозиториев на Hugging Face...</div>';
  const res = await fetch(`/api/hf/search?q=${encodeURIComponent(q)}&category=${encodeURIComponent(cat)}`);
  const items = await res.json();
  if (!items.length) { box.innerHTML = '<div class="muted">Ничего не найдено.</div>'; return; }
  box.innerHTML = items.map(r => `
    <div class="panel row" style="justify-content:space-between;">
      <div>
        <strong>${esc(r.repoId)}</strong>
        <div class="muted mono">↓ ${r.downloads} · ★ ${r.likes}${r.isGated ? ' · GATED' : ''}</div>
      </div>
      <button class="btn sm primary" onclick="loadHfRepoFiles('${esc(r.repoId)}', this)">Файлы</button>
    </div>
    <div id="hf-files-${esc(r.repoId).replace(/[^a-zA-Z0-9]/g, '_')}" class="stack" style="display:none;padding-left:14px;"></div>
  `).join('');
}

async function loadHfRepoFiles(repoId, btn) {
  const target = document.getElementById('hf-files-' + repoId.replace(/[^a-zA-Z0-9]/g, '_'));
  target.style.display = 'flex';
  target.innerHTML = '<div class="muted mono">Чтение списка файлов...</div>';
  const res = await fetch(`/api/hf/files?repo=${encodeURIComponent(repoId)}`);
  const files = await res.json();
  if (!files.length) { target.innerHTML = '<div class="muted">Файлы .gguf / .safetensors не найдены.</div>'; return; }
  target.innerHTML = files.slice(0, 14).map(f => `
    <div class="row" style="justify-content:space-between;background:var(--bg-canvas);padding:6px 10px;border-radius:3px;border:1px solid var(--border-subtle);">
      <span class="mono" style="font-size:12px;">${esc(f.filePath)}</span>
      <div class="row">
        <span class="pill mono">${(f.sizeBytes / 1073741824).toFixed(2)} ГБ</span>
        <button class="btn signal sm" onclick="downloadHfFile('${esc(repoId)}', '${esc(f.filePath)}', ${f.sizeBytes})">Скачать</button>
      </div>
    </div>
  `).join('');
}

async function downloadHfFile(repoId, filePath, expectedSizeBytes) {
  await fetch('/api/hf/download', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ repoId, filePath, expectedSizeBytes })
  });
  showToast('Скачивание файла с Hugging Face запущено');
  pollStatus();
}

// ==================== CHAT 2.0 (SSE STREAM + VISION) ====================
async function loadSessions() {
  const res = await fetch('/api/chat/sessions');
  const data = await res.json();
  personas = data.personas || [];
  sessions = data.sessions || [];
  if (!sessions.length) {
    await createNewSession();
    return;
  }
  document.getElementById('personaSelect').innerHTML = personas.map(p =>
    `<option value="${esc(p.id)}">${esc(p.name)}</option>`
  ).join('');
  if (!activeSessionId || !sessions.some(s => s.id === activeSessionId))
    activeSessionId = sessions[0].id;
  renderSessions();
  renderActiveSession();
}

async function createNewSession() {
  const res = await fetch('/api/chat/sessions', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({}) });
  const s = await res.json();
  sessions.unshift(s);
  activeSessionId = s.id;
  renderSessions();
  renderActiveSession();
}

function renderSessions() {
  const q = (document.getElementById('sessionSearch').value || '').toLowerCase();
  const list = document.getElementById('sessionList');
  list.innerHTML = sessions
    .filter(s => !q || s.title.toLowerCase().includes(q))
    .map(s => `
      <div class="session-item ${s.id === activeSessionId ? 'active' : ''}" onclick="selectSession('${s.id}')">
        <div class="session-title">${esc(s.title)}</div>
        <div class="session-meta">${(s.messages || []).length} сообщ.</div>
      </div>
    `).join('');
}

function selectSession(id) {
  activeSessionId = id;
  renderSessions();
  renderActiveSession();
}

function renderActiveSession() {
  const s = sessions.find(x => x.id === activeSessionId);
  const box = document.getElementById('chatMessages');
  if (!s) { box.innerHTML = ''; return; }
  document.getElementById('personaSelect').value = s.personaId || 'default';
  document.getElementById('tempRange').value = s.temperature ?? 0.7;
  document.getElementById('tempVal').textContent = Number(s.temperature ?? 0.7).toFixed(2);

  if (!s.messages || !s.messages.length) {
    box.innerHTML = `<div class="msg assistant"><div class="msg-head">ЛОКАЛЬНАЯ LLM · ГОТОВА К РАБОТЕ</div>Введите сообщение или вставьте изображение (Ctrl+V) для Vision. Сервер выбранной модели запустится автоматически.</div>`;
    return;
  }
  box.innerHTML = s.messages.map(m => `
    <div class="msg ${m.role === 'user' ? 'user' : 'assistant'}">
      <div class="msg-head">${m.role === 'user' ? 'ВЫ' : 'ЛОКАЛЬНАЯ LLM'}</div>
      ${m.thinking ? `<details class="think-box"><summary>РАЗМЫШЛЕНИЯ · THINK</summary><div>${esc(m.thinking)}</div></details>` : ''}
      <div>${esc(m.content)}</div>
    </div>
  `).join('');
  box.scrollTop = box.scrollHeight;
}

async function updateCurrentSessionMeta() {
  if (!activeSessionId) return;
  const personaId = document.getElementById('personaSelect').value;
  const temperature = Number(document.getElementById('tempRange').value);
  await fetch(`/api/chat/sessions/${activeSessionId}`, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ personaId, temperature })
  });
}

async function clearCurrentSession() {
  if (!activeSessionId) return;
  await fetch(`/api/chat/sessions/${activeSessionId}`, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ clearMessages: true })
  });
  await loadSessions();
}

async function deleteCurrentSession() {
  if (!activeSessionId) return;
  await fetch(`/api/chat/sessions/${activeSessionId}`, { method: 'DELETE' });
  activeSessionId = null;
  await loadSessions();
}

function handleVisionFile(file) {
  if (!file) return;
  const reader = new FileReader();
  reader.onload = () => {
    visionImageBase64 = reader.result;
    document.getElementById('visionPreviewImg').src = visionImageBase64;
    document.getElementById('visionPreviewRow').style.display = 'flex';
  };
  reader.readAsDataURL(file);
}

function clearVisionAttachment() {
  visionImageBase64 = null;
  document.getElementById('visionFileInput').value = '';
  document.getElementById('visionPreviewRow').style.display = 'none';
}

document.addEventListener('paste', e => {
  const item = [...(e.clipboardData?.items || [])].find(i => i.type.startsWith('image/'));
  if (item) handleVisionFile(item.getAsFile());
});

function onChatKeyDown(e) {
  if (e.key === 'Enter' && !e.shiftKey) {
    e.preventDefault();
    sendChatMessage();
  }
}

async function sendChatMessage() {
  const input = document.getElementById('chatInput');
  const text = input.value.trim();
  if (!text && !visionImageBase64) return;
  input.value = '';

  const imgToSend = visionImageBase64;
  clearVisionAttachment();

  const box = document.getElementById('chatMessages');
  const uEl = document.createElement('div');
  uEl.className = 'msg user';
  uEl.innerHTML = `<div class="msg-head">ВЫ</div>${imgToSend ? `<img src="${imgToSend}" class="vision-thumb" style="margin-bottom:6px;display:block;"/>` : ''}<div>${esc(text || 'Что изображено на фото?')}</div>`;
  box.appendChild(uEl);

  const aEl = document.createElement('div');
  aEl.className = 'msg assistant';
  aEl.innerHTML = `<div class="msg-head"><span>ЛОКАЛЬНАЯ LLM</span><span class="mono" id="liveTok">...</span></div><details class="think-box" style="display:none;"><summary>РАЗМЫШЛЕНИЯ · THINK</summary><div class="think-text"></div></details><div class="ans-text">Подключение...</div>`;
  box.appendChild(aEl);
  box.scrollTop = box.scrollHeight;

  const ansNode = aEl.querySelector('.ans-text');
  const thinkBox = aEl.querySelector('.think-box');
  const thinkNode = aEl.querySelector('.think-text');

  document.getElementById('btnSendChat').style.display = 'none';
  document.getElementById('btnStopChat').style.display = 'inline-flex';
  chatAbortController = new AbortController();

  try {
    const res = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      signal: chatAbortController.signal,
      body: JSON.stringify({
        sessionId: activeSessionId,
        message: text,
        personaId: document.getElementById('personaSelect').value,
        temperature: Number(document.getElementById('tempRange').value),
        modelFile: document.getElementById('llmModelSelect').value || null,
        imageBase64: imgToSend
      })
    });

    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '';
    let started = false;

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      const parts = buf.split('\n\n');
      buf = parts.pop();
      for (const part of parts) {
        if (!part.startsWith('data:')) continue;
        const ev = JSON.parse(part.slice(5).trim());
        if (ev.type === 'status') {
          ansNode.textContent = ev.data;
        } else if (ev.type === 'think') {
          thinkBox.style.display = 'block';
          thinkNode.textContent += ev.data;
        } else if (ev.type === 'token') {
          if (!started) { ansNode.textContent = ''; started = true; }
          ansNode.textContent += ev.data;
        } else if (ev.type === 'vision_missing') {
          aEl.className = 'msg warn';
          aEl.innerHTML = `<div class="msg-head">VISION (MMPROJ) НЕ АКТИВИРОВАН</div><div>${esc(ev.data.message)}</div>
            <div class="row" style="margin-top:8px;">
              ${ev.data.canDownloadQwen36Mmproj ? `<button class="btn signal sm" onclick="installModel('llm-qwen36-35b-hauhau-vision-mmproj')">Скачать mmproj для Qwen 3.6 (0.84 ГБ)</button>` : ''}
              <button class="btn primary sm" onclick="installModel('llm-qwen25-vl-7b-vision')">Скачать Qwen 2.5 VL 7B Vision (5.16 ГБ)</button>
              <button class="btn sm" onclick="switchTab('models')">Открыть каталог</button>
            </div>`;
        } else if (ev.type === 'error') {
          aEl.className = 'msg warn';
          ansNode.textContent = 'Ошибка: ' + ev.data;
        } else if (ev.type === 'done') {
          const spd = document.getElementById('chatSpeedBadge');
          if (ev.data.tokPerSec > 0) {
            spd.textContent = `${ev.data.tokPerSec} tok/s`;
            spd.style.display = 'inline-flex';
          }
          await loadSessions();
        }
        box.scrollTop = box.scrollHeight;
      }
    }
  } catch (err) {
    if (err.name !== 'AbortError') ansNode.textContent = 'Ошибка потока: ' + err.message;
  } finally {
    document.getElementById('btnSendChat').style.display = 'inline-flex';
    document.getElementById('btnStopChat').style.display = 'none';
    chatAbortController = null;
  }
}

function abortChatStream() {
  if (chatAbortController) chatAbortController.abort();
}

async function onLlmModelSelectChanged() {
  const modelFile = document.getElementById('llmModelSelect').value;
  if (!modelFile) return;
  if (lastStatus?.services?.qwen) {
    showToast(`Переключение активной LLM на ${modelFile}...`);
    await fetch('/api/services/stop', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: 'qwen' }) });
    await startSelectedLlm();
  } else {
    showToast(`Выбрана модель: ${modelFile}`);
  }
}

async function startSelectedLlm() {
  const modelFile = document.getElementById('llmModelSelect').value;
  showToast('Запуск локальной LLM...');
  const res = await fetch('/api/services/start', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id: 'qwen', modelFile })
  });
  const data = await res.json();
  if (!res.ok) showToast(data.error || 'Не удалось запустить LLM');
  else showToast('Локальная LLM активна');
  pollStatus();
}

async function stopService(id) {
  await fetch('/api/services/stop', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ id }) });
  showToast('Сервис остановлен');
  pollStatus();
}

async function stopAllGpu() {
  await fetch('/api/services/stop-all', { method: 'POST' });
  showToast('Все GPU-сервисы остановлены, видеопамять освобождена');
  pollStatus();
}

// ==================== AI ASSISTANT (RAG) ====================
function askAssistantQuick(q) {
  document.getElementById('assistantInput').value = q;
  askAssistant();
}

async function askAssistant() {
  const inp = document.getElementById('assistantInput');
  const msg = inp.value.trim();
  if (!msg) return;
  inp.value = '';
  const box = document.getElementById('assistantMessages');
  box.innerHTML += `<div class="msg user"><div class="msg-head">ВЫ</div>${esc(msg)}</div>`;
  const waitId = 'asst-' + Date.now();
  box.innerHTML += `<div id="${waitId}" class="msg assistant"><div class="msg-head">СПРАВКА · RAG</div>Поиск по базе знаний Apple Silicon M1–M6 + NVIDIA...</div>`;
  box.scrollTop = box.scrollHeight;

  const payload = {
    message: msg,
    history: assistantHistory.slice(-6),
    simulatedPlatform: simMode === 'apple' ? 'apple' : null,
    simulatedChip: simMode === 'apple' ? document.getElementById('appleChipSelect').value : null,
    simulatedRamGB: simMode === 'apple' ? selectedAppleRam : 0
  };
  const res = await fetch('/api/assistant/ask', {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(payload)
  });
  const rep = await res.json();
  assistantHistory.push({ role: 'user', content: msg }, { role: 'assistant', content: rep.text });

  const acts = (rep.actions || []).map(a =>
    `<button class="btn primary sm" onclick="runAssistantAction('${esc(a.type)}','${esc(a.param)}')">${esc(a.label)}</button>`
  ).join('');

  document.getElementById(waitId).innerHTML = `
    <div class="msg-head"><span>СПРАВКА · ${esc(rep.engineUsed)}</span><span>RAG: ${esc((rep.ragSources || []).join(', '))}</span></div>
    <div>${esc(rep.text)}</div>
    ${acts ? `<div class="row" style="margin-top:8px;">${acts}</div>` : ''}
  `;
  box.scrollTop = box.scrollHeight;
}

function runAssistantAction(type, param) {
  if (type === 'install_model') installModel(param);
  else if (type === 'install_runtime') installRuntime(param);
  else if (type === 'apply_antiloop') applyAntiLoop();
  else if (type === 'open_tab') {
    if (param.includes('catalog') || param === 'models' || param === 'hf_search') switchTab('models');
    else if (param === 'settings' || param === 'services') switchTab('system');
    else if (param === 'gallery') switchTab('gallery');
    else if (param === 'studio') switchTab('photo');
    else switchTab('chat');
  } else if (type === 'open_folder') openFolder(param);
  else if (type === 'open_url') window.open(param, '_blank');
}

function clearAssistant() {
  assistantHistory = [];
  document.getElementById('assistantMessages').innerHTML = '';
}

// ==================== STUDIO, VOICE, GALLERY & SYSTEM ====================
async function refreshStudioPreviews() {
  try {
    const [pRes, vRes] = await Promise.all([
      fetch('/api/gallery?filter=photo'),
      fetch('/api/gallery?filter=video')
    ]);
    const photos = await pRes.json();
    const videos = await vRes.json();

    const pBox = document.getElementById('photoStageBox');
    if (pBox && photos.length > 0) {
      const it = photos[0];
      pBox.innerHTML = `<img src="${it.url}" onclick="window.open('${it.url}','_blank')" style="cursor:pointer" title="Открыть оригинал" /><div class="muted mono" style="font-size:11px;">${esc(it.name)}</div>`;
    }
    const vBox = document.getElementById('videoStageBox');
    if (vBox && videos.length > 0) {
      const it = videos[0];
      vBox.innerHTML = `<video src="${it.url}" controls preload="metadata"></video><div class="muted mono" style="font-size:11px;">${esc(it.name)}</div>`;
    }
  } catch (e) { /* ignore */ }
}

async function generatePhoto() {
  const [w, h] = document.getElementById('photoSize').value.split('x').map(Number);
  const res = await fetch('/api/studio/photo', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      engine: document.getElementById('photoEngine').value,
      prompt: document.getElementById('photoPrompt').value,
      width: w, height: h, seed: Number(document.getElementById('photoSeed').value)
    })
  });
  const data = await res.json();
  if (!res.ok) return showToast(data.error || 'Ошибка');
  showToast('Генерация фото запущена');
  pollStatus();
}

async function generateVideo() {
  const [w, h] = document.getElementById('videoSize').value.split('x').map(Number);
  const res = await fetch('/api/studio/video', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      engine: document.getElementById('videoEngine').value,
      prompt: document.getElementById('videoPrompt').value,
      width: w, height: h, durationSec: Number(document.getElementById('videoDur').value)
    })
  });
  const data = await res.json();
  if (!res.ok) return showToast(data.error || 'Ошибка');
  showToast('Генерация видео запущена');
  pollStatus();
}

async function loadVoices() {
  const res = await fetch('/api/voice/voices');
  const data = await res.json();
  document.getElementById('ttsVoiceSelect').innerHTML = (data.voices || []).map(v =>
    `<option value="${esc(v.id)}">${esc(v.displayName)}</option>`
  ).join('');
}

async function synthesizeTts() {
  const res = await fetch('/api/voice/tts', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ text: document.getElementById('ttsText').value, voiceId: document.getElementById('ttsVoiceSelect').value })
  });
  const data = await res.json();
  if (!res.ok) return showToast(data.error || 'Ошибка синтеза');
  const audio = document.getElementById('ttsAudio');
  audio.src = data.url;
  audio.style.display = 'block';
  audio.play();
  showToast('Озвучка готова');
}

async function loadGallery(filter = 'all', btn = null) {
  currentGalleryFilter = filter;
  if (btn) document.querySelectorAll('#tab-gallery .subtab').forEach(b => b.classList.toggle('active', b === btn));
  const res = await fetch(`/api/gallery?filter=${encodeURIComponent(filter)}`);
  const items = await res.json();
  const grid = document.getElementById('galleryGrid');
  if (!items.length) {
    grid.innerHTML = '<div class="panel muted">В галерее пока нет файлов. Сгенерируйте фото или видео.</div>';
    return;
  }
  grid.innerHTML = items.map(it => `
    <div class="gal-card">
      ${it.isVideo
        ? `<video class="gal-media" src="${it.url}" controls muted preload="metadata"></video>`
        : `<img class="gal-media" src="${it.url}" loading="lazy" onclick="window.open('${it.url}','_blank')" />`
      }
      <div class="gal-foot">
        <span class="mono" style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;" title="${esc(it.name)}">${esc(it.name)}</span>
        <button class="btn sm ${it.isFavorite ? 'signal' : ''}" onclick="toggleFav('${esc(it.fullPath.replace(/\\/g, '\\\\'))}')">★</button>
      </div>
    </div>
  `).join('');
}

async function toggleFav(fullPath) {
  await fetch('/api/gallery/favorite', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ fullPath }) });
  loadGallery(currentGalleryFilter);
}

async function installRuntime(runtime) {
  await fetch('/api/runtime/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ runtime }) });
  showToast(`Установка ${runtime} запущена`);
  pollStatus();
}

async function loadSettings() {
  const res = await fetch('/api/settings');
  const data = await res.json();
  const c = data.config;
  document.getElementById('cfgCtx').value = c.llmCtxSize;
  document.getElementById('cfgIdle').value = c.llmIdleUnloadMinutes;
  document.getElementById('cfgTemp').value = c.llmTemperature;
  document.getElementById('cfgPres').value = c.llmPresencePenalty;
  document.getElementById('mcpJsonBox').textContent = data.mcpJson;
}

async function saveSettings() {
  await fetch('/api/settings', {
    method: 'POST', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      llmCtxSize: Number(document.getElementById('cfgCtx').value),
      llmIdleUnloadMinutes: Number(document.getElementById('cfgIdle').value),
      llmTemperature: Number(document.getElementById('cfgTemp').value),
      llmPresencePenalty: Number(document.getElementById('cfgPres').value),
      llmDryMultiplier: 0.5, llmReasoningBudget: 4096
    })
  });
  showToast('Настройки сохранены');
}

async function applyAntiLoop() {
  await fetch('/api/settings/antiloop', { method: 'POST' });
  showToast('Применён профиль Qwen Анти-луп (перезапустите LLM)');
}

async function saveHfToken() {
  const token = document.getElementById('hfTokenInput').value.trim();
  const res = await fetch('/api/hf/token', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ token }) });
  const data = await res.json();
  if (!res.ok) return showToast(data.error || 'Ошибка HF токена');
  showToast(data.connected ? `Hugging Face подключён (@${data.username})` : 'Токен HF отключён');
  pollStatus();
}

function copyMcpJson() {
  navigator.clipboard.writeText(document.getElementById('mcpJsonBox').textContent);
  showToast('MCP JSON скопирован в буфер обмена');
}

async function loadLogs(id) {
  const res = await fetch(`/api/services/logs?id=${encodeURIComponent(id)}`);
  const data = await res.json();
  document.getElementById('logsBox').textContent = data.logs || data.error || '(пусто)';
}

async function openFolder(target) {
  await fetch('/api/system/open-folder', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ target }) });
}

// ==================== BOOT ====================
(async function boot() {
  await initHardwareWiki();
  await pollStatus();
  await loadSessions();
  await loadCatalog();
  refreshStudioPreviews();
  setInterval(pollStatus, 2500);
})();
