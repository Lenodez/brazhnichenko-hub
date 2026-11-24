let sessions = [];
let activeSessionId = null;
let attachedImg = null;
let attachedDoc = null; // { name, text }
let abortCtrl = null;
let lastStatus = null;
let maxViewportH = window.innerHeight || 700;
let assistSettings = { assistAutoVoice: true, assistAutoTts: true, assistAttachScreen: false, assistWebSearch: true, assistModel: '' };
let pendingManualAssistModel = '';
let catalogModelsCache = [];
let activeModelFilter = 'all';
let latestUpdateInfo = null;
let updatePollTimer = null;
let modalResolver = null;
const codeBlockStore = [];

function haptic(kind) {
  try {
    if (window.AndroidHub && window.AndroidHub.haptic) window.AndroidHub.haptic(kind || 'tick');
  } catch (e) { }
}

function syncOverlayBack() {
  try {
    const anyOpen =
      document.getElementById('m3Modal')?.classList.contains('show') ||
      document.getElementById('chatSheet')?.classList.contains('show') ||
      document.getElementById('chatSide')?.classList.contains('open');
    if (window.AndroidHub && window.AndroidHub.setOverlayOpen) {
      window.AndroidHub.setOverlayOpen(!!anyOpen);
    }
  } catch (e) { }
}

// ==================== MATERIAL 3 MODAL DIALOGS (NO BROWSER confirm/prompt) ====================
function openM3Modal({ title, message, isPrompt, defaultValue, okText, isDanger }) {
  return new Promise((resolve) => {
    modalResolver = resolve;
    document.getElementById('m3ModalTitle').textContent = title || 'Подтверждение';
    document.getElementById('m3ModalMsg').textContent = message || '';
    const inp = document.getElementById('m3ModalInput');
    if (isPrompt) {
      inp.style.display = 'block';
      inp.value = defaultValue || '';
      setTimeout(() => { inp.focus(); inp.select(); }, 60);
    } else {
      inp.style.display = 'none';
    }
    const okBtn = document.getElementById('m3ModalOk');
    okBtn.textContent = okText || 'ОК';
    okBtn.className = 'btn sm ' + (isDanger ? 'danger' : 'primary');
    document.getElementById('modalBackdrop').classList.add('show');
    document.getElementById('m3Modal').classList.add('show');
    syncOverlayBack();
  });
}

function closeM3Modal(val) {
  document.getElementById('modalBackdrop').classList.remove('show');
  document.getElementById('m3Modal').classList.remove('show');
  syncOverlayBack();
  const res = modalResolver;
  modalResolver = null;
  if (res) res(val);
}

function confirmM3Modal() {
  haptic('confirm');
  const inp = document.getElementById('m3ModalInput');
  const isPrompt = inp.style.display !== 'none';
  closeM3Modal(isPrompt ? inp.value : true);
}

function m3Confirm(title, message, okText, isDanger) {
  return openM3Modal({ title, message, isPrompt: false, okText, isDanger }).then(v => v === true);
}

function m3Prompt(title, message, defaultValue) {
  return openM3Modal({ title, message, isPrompt: true, defaultValue, okText: 'Сохранить', isDanger: false });
}

function onWebSearchToggle() {
  const chk = document.getElementById('chkWebSearch');
  const lbl = document.getElementById('webToggleLabel');
  const inp = document.getElementById('chatInput');
  const on = !!(chk && chk.checked);
  if (lbl) lbl.classList.toggle('active', on);
  if (inp) inp.placeholder = on ? 'Спроси AI + поиск в интернете...' : 'Спроси локальную нейросеть офлайн...';
  haptic('tick');
  try { localStorage.setItem('hub_web_search', on ? '1' : '0'); } catch (e) { }
}

function initWebSearchToggle() {
  const chk = document.getElementById('chkWebSearch');
  if (!chk) return;
  try { chk.checked = localStorage.getItem('hub_web_search') === '1'; } catch (e) { }
  onWebSearchToggle();
}

function renderSourcesHtml(items, provider) {
  if (!items || !items.length) return '';
  const rows = items.map((it, idx) => {
    let host = '';
    try { host = new URL(it.url).hostname.replace(/^www\./, ''); } catch (e) { }
    return `<a class="source-link" href="${esc(it.url)}" target="_blank" rel="noopener"><span class="src-idx mono">[${idx + 1}]</span><span class="src-title">${esc(it.title || it.url)}</span>${host ? `<span class="src-host mono">${esc(host)}</span>` : ''}</a>`;
  }).join('');
  const prov = provider ? ` · ${esc(provider)}` : '';
  return `<details class="sources-box"><summary>Источники (${items.length})${prov}</summary><div class="sources-list">${rows}</div></details>`;
}

async function freeRamNow(btn) {
  const prev = btn ? btn.innerHTML : '';
  if (btn) { btn.disabled = true; btn.textContent = 'Очистка...'; }
  try {
    if (window.AndroidHub && window.AndroidHub.clearWebViewCache) {
      window.AndroidHub.clearWebViewCache();
    }
    const res = await fetch('/api/system/free-ram', { method: 'POST' });
    const d = await res.json();
    await pollStatus();
    if (d.freedMB > 10) {
      toast(`Освобождено +${d.freedMB} МБ RAM · свободно ${d.afterAvailGB} ГБ`);
    } else {
      toast(`Фоновые процессы очищены · свободно ${d.afterAvailGB} ГБ${d.llmRunning ? ' (LLM активна)' : ''}`);
    }
  } catch (e) {
    toast('Не удалось очистить RAM: ' + e.message);
  } finally {
    if (btn) { btn.disabled = false; btn.innerHTML = prev; }
  }
}

function toast(msg) {
  const t = document.getElementById('toast');
  if (!t) return;
  t.textContent = msg;
  t.classList.add('show');
  clearTimeout(window.__t);
  window.__t = setTimeout(() => t.classList.remove('show'), 3000);
}

function esc(s) {
  return String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ==================== DRAWER, BOTTOM SHEET & KEYBOARD UX ====================
function toggleChatDrawer(force) {
  const side = document.getElementById('chatSide');
  const bd = document.getElementById('chatDrawerBackdrop');
  if (!side || !bd) return;
  const open = typeof force === 'boolean' ? force : !side.classList.contains('open');
  side.classList.toggle('open', open);
  bd.classList.toggle('show', open);
  if (open) haptic('tick');
  syncOverlayBack();
}

function toggleChatSheet(force) {
  const sheet = document.getElementById('chatSheet');
  const bd = document.getElementById('chatSheetBackdrop');
  if (!sheet || !bd) return;
  const show = typeof force === 'boolean' ? force : !sheet.classList.contains('show');
  sheet.classList.toggle('show', show);
  bd.classList.toggle('show', show);
  if (show) haptic('tick');
  const s = sessions.find(x => x.id === activeSessionId);
  if (show && s) renderContextBar(s);
  syncOverlayBack();
}

function toggleMemoryEditor(force) {
  const el = document.getElementById('ctxDrawer');
  if (!el) return;
  const show = typeof force === 'boolean' ? force : !el.classList.contains('show');
  el.classList.toggle('show', show);
  if (show) {
    setTimeout(() => el.scrollIntoView({ behavior: 'smooth', block: 'nearest' }), 60);
  }
}

function toggleCtxDrawer(force) {
  if (force !== false) toggleChatSheet(true);
  toggleMemoryEditor(force);
}

window.handleAndroidBack = function() {
  const modal = document.getElementById('m3Modal');
  if (modal && modal.classList.contains('show')) {
    closeM3Modal(null);
    return true;
  }
  const sheet = document.getElementById('chatSheet');
  if (sheet && sheet.classList.contains('show')) {
    toggleChatSheet(false);
    return true;
  }
  const side = document.getElementById('chatSide');
  if (side && side.classList.contains('open')) {
    toggleChatDrawer(false);
    return true;
  }
  return false;
};

function initSwipeGestures() {
  // Swipe down to close Bottom Sheet
  const sheet = document.getElementById('chatSheet');
  if (sheet) {
    let sy = 0, tracking = false;
    sheet.addEventListener('touchstart', (e) => {
      const body = sheet.querySelector('.sheet-body');
      if (!body || body.scrollTop <= 2) {
        sy = e.touches[0].clientY;
        tracking = true;
      }
    }, { passive: true });
    sheet.addEventListener('touchend', (e) => {
      if (!tracking) return;
      tracking = false;
      const dy = (e.changedTouches[0]?.clientY || 0) - sy;
      if (dy > 65) toggleChatSheet(false);
    }, { passive: true });
  }
  // Swipe left to close Chats Drawer
  const side = document.getElementById('chatSide');
  if (side) {
    let sx = 0;
    side.addEventListener('touchstart', (e) => { sx = e.touches[0].clientX; }, { passive: true });
    side.addEventListener('touchend', (e) => {
      const dx = (e.changedTouches[0]?.clientX || 0) - sx;
      if (dx < -65 && side.classList.contains('open')) toggleChatDrawer(false);
    }, { passive: true });
  }
  // Scroll-to-bottom FAB visibility
  const box = document.getElementById('chatMsgs');
  const fab = document.getElementById('btnScrollBottom');
  if (box && fab) {
    box.addEventListener('scroll', () => {
      const distFromBottom = box.scrollHeight - box.scrollTop - box.clientHeight;
      fab.classList.toggle('show', distFromBottom > 180);
    }, { passive: true });
  }
  // Enter submits M3 prompt modal
  const mInp = document.getElementById('m3ModalInput');
  if (mInp) {
    mInp.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { e.preventDefault(); confirmM3Modal(); }
    });
  }
}

function scrollToBottom(smooth) {
  const box = document.getElementById('chatMsgs');
  if (!box) return;
  box.scrollTo({ top: box.scrollHeight, behavior: smooth ? 'smooth' : 'auto' });
}

function autoGrowInput() {
  const inp = document.getElementById('chatInput');
  if (!inp) return;
  inp.style.height = 'auto';
  inp.style.height = Math.min(112, Math.max(26, inp.scrollHeight)) + 'px';
}

function initKeyboardWatcher() {
  const updateKb = () => {
    const h = window.visualViewport ? window.visualViewport.height : window.innerHeight;
    if (h > maxViewportH) maxViewportH = h;
    const isNarrow = window.innerWidth < 600;
    const isKbOpen = isNarrow && (maxViewportH - h > 150);
    document.body.classList.toggle('kb-open', isKbOpen);
    if (isKbOpen) {
      const box = document.getElementById('chatMsgs');
      if (box) box.scrollTop = box.scrollHeight;
    }
  };
  window.addEventListener('resize', updateKb);
  if (window.visualViewport) window.visualViewport.addEventListener('resize', updateKb);

  const inp = document.getElementById('chatInput');
  if (inp) {
    inp.addEventListener('input', autoGrowInput);
    inp.addEventListener('keydown', (e) => {
      if (e.key === 'Enter' && !e.shiftKey && window.innerWidth >= 840) {
        e.preventDefault();
        sendChat();
      }
    });
  }
  document.addEventListener('visibilitychange', () => {
    if (!document.hidden) refreshAssistantStatus();
  });
  window.addEventListener('focus', refreshAssistantStatus);
}

// ==================== RICH ZERO-DEPENDENCY MARKDOWN + SYNTAX HIGHLIGHTER ====================
function copyCodeBlock(idx) {
  const code = codeBlockStore[idx] ?? '';
  if (window.AndroidHub) window.AndroidHub.copyText(code);
  else if (navigator.clipboard) navigator.clipboard.writeText(code);
  toast('Код скопирован');
}

function toggleCodeWrap(btn) {
  const wrap = btn.closest('.code-wrap');
  if (!wrap) return;
  const wrapped = wrap.classList.toggle('nowrap');
  btn.textContent = wrapped ? 'Перенос' : 'В 1 строку';
}

const CODE_KEYWORDS = new Set([
  'def','class','return','if','elif','else','for','while','in','not','and','or','is','import','from','as',
  'try','except','finally','raise','with','yield','lambda','pass','break','continue','async','await','global','nonlocal','del','assert',
  'function','const','let','var','new','switch','case','default','do','typeof','instanceof','export','extends','implements',
  'public','private','protected','static','final','abstract','interface','package','void','throws','throw','catch',
  'int','long','float','double','bool','boolean','char','byte','short','string','auto','struct','enum','namespace','using','override','virtual',
  'fn','mut','pub','match','impl','trait','mod','use','crate','func','defer','go','chan','select','type','map','val','fun','when','object',
  'SELECT','FROM','WHERE','JOIN','LEFT','RIGHT','INNER','OUTER','ON','GROUP','BY','ORDER','LIMIT','INSERT','INTO','VALUES','UPDATE','SET','DELETE','CREATE','TABLE','DROP','ALTER','AS','AND','OR','NOT','NULL','DISTINCT','COUNT','SUM','AVG',
  'echo','export','fi','then','done','esac','local','exit','sudo'
]);

const CODE_CONSTANTS = new Set([
  'True','False','None','true','false','null','undefined','NaN','Infinity','self','this','super','cls','nullptr','nil'
]);

const CODE_BUILTINS = new Set([
  'print','len','range','str','int','float','list','dict','set','tuple','open','sum','min','max','abs','round','enumerate','zip','sorted','isinstance','input','format',
  'console','Math','JSON','Promise','Array','Object','String','Number','Boolean','Date','RegExp','Error','fetch','document','window','System','printf','println','fmt'
]);

function highlightCode(code, lang) {
  const l = (lang || '').toLowerCase();
  const isHashLang = ['py','python','sh','bash','zsh','shell','yaml','yml','toml','ruby','rb','r','dockerfile','makefile','perl',''].includes(l);
  const isSql = ['sql','postgres','postgresql','mysql','sqlite'].includes(l);
  const isJson = l === 'json';

  const tokenRe = /([furbFURB]?"""[\s\S]*?"""|[furbFURB]?'''[\s\S]*?'''|[furbFURB]?"(?:\\.|[^"\\\n])*"|[furbFURB]?'(?:\\.|[^'\\\n])*'|`(?:\\.|[^`\\])*`|\/\*[\s\S]*?\*\/|\/\/[^\n]*|#[^\n]*|--[^\n]*|@[A-Za-z_][A-Za-z0-9_]*|\b0x[0-9a-fA-F]+\b|\b\d+(?:\.\d+)?\b|\b[A-Za-z_][A-Za-z0-9_]*\b)/g;

  let out = '';
  let last = 0;
  let m;
  while ((m = tokenRe.exec(code)) !== null) {
    if (m.index > last) out += esc(code.slice(last, m.index));
    const tok = m[0];
    const nextChar = code[m.index + tok.length] || '';

    if (/^[furbFURB]?(?:"""|''')/.test(tok)) {
      out += `<span class="hl-str">${esc(tok)}</span>`;
    } else if (tok.startsWith('//') || tok.startsWith('/*') || (tok.startsWith('#') && isHashLang) || (tok.startsWith('--') && isSql)) {
      out += `<span class="hl-com">${esc(tok)}</span>`;
    } else if (/^[furbFURB]?["'`]/.test(tok)) {
      if (isJson && /^\s*:/.test(code.slice(m.index + tok.length))) {
        out += `<span class="hl-key">${esc(tok)}</span>`;
      } else {
        out += `<span class="hl-str">${esc(tok)}</span>`;
      }
    } else if (tok.startsWith('@')) {
      out += `<span class="hl-dec">${esc(tok)}</span>`;
    } else if (/^(?:0x[0-9a-fA-F]+|\d+(?:\.\d+)?)$/.test(tok)) {
      out += `<span class="hl-num">${esc(tok)}</span>`;
    } else if (CODE_KEYWORDS.has(tok) || (isSql && CODE_KEYWORDS.has(tok.toUpperCase()))) {
      out += `<span class="hl-kw">${esc(tok)}</span>`;
    } else if (CODE_CONSTANTS.has(tok)) {
      out += `<span class="hl-con">${esc(tok)}</span>`;
    } else if (CODE_BUILTINS.has(tok) || nextChar === '(') {
      out += `<span class="hl-fn">${esc(tok)}</span>`;
    } else if (tok.startsWith('#')) {
      out += `<span class="hl-dec">${esc(tok)}</span>`;
    } else {
      out += esc(tok);
    }
    last = m.index + tok.length;
  }
  if (last < code.length) out += esc(code.slice(last));
  return out;
}

function buildCodeBlockHtml(lang, code) {
  const cleanCode = code.replace(/^\n/, '').replace(/\n$/, '');
  const storeIdx = codeBlockStore.length;
  codeBlockStore.push(cleanCode);
  const label = lang ? esc(lang) : 'code';
  const highlighted = highlightCode(cleanCode, lang);
  return `<div class="code-wrap"><div class="code-bar"><span class="mono code-lang">${label}</span><div class="row"><button class="btn sm" onclick="toggleCodeWrap(this)">В 1 строку</button><button class="btn sm" onclick="copyCodeBlock(${storeIdx})">Копия</button></div></div><pre><code>${highlighted}</code></pre></div>`;
}

function formatInlineMd(text) {
  let s = esc(text);
  const inlines = [];
  s = s.replace(/`([^`\n]+)`/g, (_, c) => {
    const idx = inlines.length;
    inlines.push(`<code class="inline-code">${c}</code>`);
    return `@@INL_${idx}@@`;
  });
  s = s.replace(/\*\*([^*\n]+)\*\*/g, '<strong>$1</strong>');
  s = s.replace(/~~([^~\n]+)~~/g, '<del>$1</del>');
  s = s.replace(/(^|[\s(])\*([^*\n]+)\*(?=[\s).,!?:;]|$)/g, '$1<em>$2</em>');
  s = s.replace(/@@INL_(\d+)@@/g, (_, i) => inlines[Number(i)]);
  return s;
}

function renderMarkdown(raw) {
  if (!raw) return '';
  let src = String(raw).replace(/\r\n/g, '\n');

  const fenceCount = (src.match(/^```/gm) || []).length;
  if (fenceCount % 2 === 1) src += '\n```';

  const fenced = [];
  const withPlaceholders = src.replace(/```([a-zA-Z0-9_+#.-]*)[ \t]*\n?([\s\S]*?)```/g, (_, lang, code) => {
    const html = buildCodeBlockHtml(lang, code);
    const ph = `@@CODEBLOCK_${fenced.length}@@`;
    fenced.push(html);
    return `\n${ph}\n`;
  });

  const lines = withPlaceholders.split('\n');
  const out = [];
  let i = 0;

  while (i < lines.length) {
    const line = lines[i];
    const trimmed = line.trim();
    if (!trimmed) { i++; continue; }

    const cbMatch = trimmed.match(/^@@CODEBLOCK_(\d+)@@$/);
    if (cbMatch) { out.push(fenced[Number(cbMatch[1])]); i++; continue; }

    if (/^(-{3,}|\*{3,}|_{3,})$/.test(trimmed)) { out.push('<hr/>'); i++; continue; }

    const hMatch = trimmed.match(/^(#{1,3})\s+(.+)$/);
    if (hMatch) {
      out.push(`<h${hMatch[1].length}>${formatInlineMd(hMatch[2])}</h${hMatch[1].length}>`);
      i++; continue;
    }

    if (trimmed.includes('|') && i + 1 < lines.length && /^\s*\|?[\s:-]+\|[\s|:-]*$/.test(lines[i + 1].trim())) {
      const parseRow = r => r.trim().replace(/^\|/, '').replace(/\|$/, '').split('|').map(c => formatInlineMd(c.trim()));
      const headers = parseRow(trimmed);
      i += 2;
      const rows = [];
      while (i < lines.length && lines[i].trim().includes('|')) { rows.push(parseRow(lines[i])); i++; }
      const thead = '<thead><tr>' + headers.map(h => `<th>${h}</th>`).join('') + '</tr></thead>';
      const tbody = '<tbody>' + rows.map(r => '<tr>' + r.map(c => `<td>${c}</td>`).join('') + '</tr>').join('') + '</tbody>';
      out.push(`<div class="tbl-wrap"><table>${thead}${tbody}</table></div>`);
      continue;
    }

    if (trimmed.startsWith('>')) {
      const qLines = [];
      while (i < lines.length && lines[i].trim().startsWith('>')) {
        qLines.push(formatInlineMd(lines[i].trim().replace(/^>\s?/, '')));
        i++;
      }
      out.push(`<blockquote>${qLines.join('<br/>')}</blockquote>`);
      continue;
    }

    if (/^[-*•]\s+/.test(trimmed)) {
      const items = [];
      while (i < lines.length && /^[-*•]\s+/.test(lines[i].trim())) {
        items.push(`<li>${formatInlineMd(lines[i].trim().replace(/^[-*•]\s+/, ''))}</li>`);
        i++;
      }
      out.push(`<ul>${items.join('')}</ul>`);
      continue;
    }

    if (/^\d+[.)]\s+/.test(trimmed)) {
      const items = [];
      while (i < lines.length && /^\d+[.)]\s+/.test(lines[i].trim())) {
        items.push(`<li>${formatInlineMd(lines[i].trim().replace(/^\d+[.)]\s+/, ''))}</li>`);
        i++;
      }
      out.push(`<ol>${items.join('')}</ol>`);
      continue;
    }

    const pLines = [];
    while (i < lines.length) {
      const t = lines[i].trim();
      if (!t || /^@@CODEBLOCK_\d+@@$/.test(t) || /^(#{1,3})\s+/.test(t) || /^[-*•]\s+/.test(t) || /^\d+[.)]\s+/.test(t) || t.startsWith('>') || /^(-{3,}|\*{3,})$/.test(t)) break;
      if (t.includes('|') && i + 1 < lines.length && /^\s*\|?[\s:-]+\|[\s|:-]*$/.test(lines[i + 1].trim())) break;
      pLines.push(formatInlineMd(lines[i]));
      i++;
    }
    if (pLines.length) out.push(`<p>${pLines.join('<br/>')}</p>`);
  }

  return `<div class="md">${out.join('')}</div>`;
}

function stripMarkdownForTts(raw) {
  if (!raw) return '';
  return String(raw)
    .replace(/```[\s\S]*?```/g, ' блок кода ')
    .replace(/`([^`]+)`/g, '$1')
    .replace(/!\[[^\]]*\]\([^)]*\)/g, '')
    .replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')
    .replace(/^#{1,6}\s+/gm, '')
    .replace(/[*_~>]+/g, '')
    .replace(/\|/g, ', ')
    .replace(/\s+/g, ' ')
    .trim();
}

// ==================== NAVIGATION & TELEMETRY ====================
function switchView(name) {
  toggleChatDrawer(false);
  toggleChatSheet(false);
  document.querySelectorAll('.nav-btn').forEach(b => b.classList.toggle('active', b.dataset.view === name));
  document.querySelectorAll('.view').forEach(v => v.classList.toggle('active', v.id === 'view-' + name));
  const isChat = name === 'chat';
  const btnOpen = document.getElementById('btnOpenChats');
  const btnNew = document.getElementById('btnNewChatTop');
  if (btnOpen) btnOpen.style.visibility = isChat ? 'visible' : 'hidden';
  if (btnNew) btnNew.style.visibility = isChat ? 'visible' : 'hidden';
  if (name === 'models') loadCatalog();
  if (name === 'system') { loadSettings(); loadLogs(); refreshAssistantStatus(); loadSttStatus(); }
}

function shortModelLabel(fileName, sizeGB) {
  const clean = String(fileName || '')
    .replace(/\.gguf$/i, '')
    .replace(/-Instruct-\d+/i, '')
    .replace(/-it-/i, '-')
    .replace(/-Q4_K_M$/i, ' Q4')
    .replace(/-Q4_K_S$/i, ' Q4')
    .replace(/-Q5_K_M$/i, ' Q5');
  return sizeGB ? `${clean} (${sizeGB} ГБ)` : clean;
}

function updateVisionBadge(hasVision) {
  const vp = document.getElementById('visionPill');
  if (!vp) return;
  if (hasVision) {
    vp.style.display = 'inline-flex';
    vp.textContent = 'Vision';
    vp.className = 'pill info vision-badge';
  } else {
    vp.style.display = 'none';
  }
}

function onModelSelectChanged() {
  if (!lastStatus || !lastStatus.availableLlms) return;
  const sel = document.getElementById('modelSelect').value;
  const item = lastStatus.availableLlms.find(x => x.fileName === sel);
  if (item) {
    const isVis = /gemma-3|vl|vision|llava|minicpm/i.test(item.fileName);
    updateVisionBadge(item.fileName === lastStatus.activeLlm ? lastStatus.activeLlmHasVision : isVis);
  }
}

async function pollStatus() {
  try {
    const res = await fetch('/api/status');
    const st = await res.json();
    lastStatus = st;

    // Desktop header telemetry
    const dSoc = document.getElementById('devSoc');
    const dRam = document.getElementById('devRam');
    const dDisk = document.getElementById('devDisk');
    if (dSoc) dSoc.textContent = st.device;
    if (dRam) dRam.textContent = `${st.ramUsedGB}/${st.ramTotalGB} ГБ (своб. ${st.ramAvailGB})`;
    if (dDisk) dDisk.textContent = `${st.diskFreeGB}/${st.diskTotalGB} ГБ · ${st.batteryPct}% · ${st.batteryTempC}°C`;

    // Full System tab telemetry (never truncated on folded screen)
    const sSoc = document.getElementById('sysDevSoc');
    const sRam = document.getElementById('sysDevRam');
    const sDisk = document.getElementById('sysDevDisk');
    const sBat = document.getElementById('sysDevBat');
    if (sSoc) sSoc.textContent = st.device;
    if (sRam) sRam.textContent = `${st.ramUsedGB} / ${st.ramTotalGB} ГБ (своб. ${st.ramAvailGB} ГБ)`;
    if (sDisk) sDisk.textContent = `Своб. ${st.diskFreeGB} из ${st.diskTotalGB} ГБ`;
    if (sBat) sBat.textContent = `${st.batteryPct}% · ${st.batteryTempC}°C`;

    const mp = document.getElementById('modelsPathText');
    if (mp) mp.textContent = 'Папка моделей: ' + st.modelsDir;

    // Unified LLM Power Toggle Pill
    const btn = document.getElementById('llmToggleBtn');
    const txt = document.getElementById('llmTopText');
    if (btn && txt) {
      btn.classList.toggle('running', !!st.llmRunning);
      txt.textContent = st.llmRunning ? `${String(st.activeBackend || 'NPU').toUpperCase()} · Стоп` : 'Старт LLM';
    }

    updateVisionBadge(!!st.activeLlmHasVision);

    const sel = document.getElementById('modelSelect');
    const sig = (st.availableLlms || []).map(x => x.fileName).join('|');
    if (sel && sel.dataset.sig !== sig) {
      sel.dataset.sig = sig;
      sel.innerHTML = st.availableLlms.length
        ? st.availableLlms.map(x => `<option value="${esc(x.fileName)}" ${x.fileName === st.activeLlm ? 'selected' : ''}>${esc(shortModelLabel(x.fileName, x.sizeGB))}${x.hasVision ? ' · Vision' : ''}</option>`).join('')
        : '<option value="">Скачай модель во вкладке «Модели»</option>';
      populateAssistModelSelect(st.availableLlms || []);
    }

    const bar = document.getElementById('jobsBar');
    const act = (st.jobs || []).filter(j => j.status === 'running' || j.status === 'error');
    if (!act.length) {
      bar.classList.remove('show');
      bar.innerHTML = '';
    } else {
      bar.classList.add('show');
      bar.innerHTML = act.map(j => `
        <div class="row" style="justify-content:space-between;">
          <div style="font-size:12px;"><strong>${esc(j.title)}</strong> · ${esc(j.stage)}</div>
          <div class="row">
            <span class="mono" style="font-size:12px;">${j.percent}%${j.speedMBs > 0 ? ' · ' + j.speedMBs + ' МБ/с' : ''}</span>
            ${j.status === 'error' && j.retryModelId ? `<button class="btn ok sm" onclick="retryJob('${j.id}','${esc(j.retryModelId)}')">Докачать</button>` : ''}
            <button class="btn danger sm" onclick="cancelJob('${j.id}')">×</button>
          </div>
        </div>
        <div class="bar-track"><div class="bar-fill" style="width:${j.percent}%;background:${j.status === 'error' ? 'var(--red)' : 'var(--green)'}"></div></div>
      `).join('');
    }
  } catch (e) { }
}

async function toggleLlmPower() {
  if (lastStatus && lastStatus.llmRunning) {
    await stopLlm();
  } else {
    await startLlm();
  }
}

async function cancelJob(jobId) {
  await fetch('/api/jobs/cancel', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ jobId }) });
  toast('Загрузка отменена');
  pollStatus();
}

async function retryJob(oldJobId, modelId) {
  await fetch('/api/jobs/cancel', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ jobId: oldJobId }) });
  await installModel(modelId);
}

// ==================== MODELS & HF SEARCH ====================
function setModelFilter(filter, btn) {
  activeModelFilter = filter;
  document.querySelectorAll('.model-filters .chip').forEach(c => c.classList.toggle('active', c === btn));
  haptic('tick');
  renderCatalogGrid();
}

function renderCatalogGrid() {
  const filtered = (catalogModelsCache || []).filter(m => {
    if (activeModelFilter === 'installed') return !!m.installed;
    if (activeModelFilter === 'vision') return !!m.isVision;
    return true;
  });
  const grid = document.getElementById('catalogGrid');
  if (!grid) return;
  if (!filtered.length) {
    grid.innerHTML = '<div class="card"><div class="muted">По этому фильтру моделей пока нет.</div></div>';
    return;
  }
  grid.innerHTML = filtered.map(m => `
    <div class="card ${m.fitLevel}">
      <div class="row" style="justify-content:space-between;">
        <strong>${esc(m.name)}</strong>
        <span class="pill ${m.fitLevel === 'ideal' ? 'ok' : m.fitLevel === 'tight' ? 'warn' : 'err'}">${esc(m.fitBadge)}</span>
      </div>
      <div class="row">
        <span class="pill mono">${m.totalSizeGB} ГБ</span>
        <span class="pill info mono">${esc(m.speedHint)}</span>
        ${m.isVision ? '<span class="pill info">Vision (Фото)</span>' : ''}
        ${m.installed ? '<span class="pill ok">Скачана</span>' : ''}
      </div>
      <div class="muted">${esc(m.summaryRu)}</div>
      <div class="row" style="justify-content:space-between;margin-top:auto;">
        <span class="muted">${esc(m.tag)}</span>
        ${m.installed
          ? `<button class="btn danger sm" onclick="deleteModel('${m.id}')">Удалить</button>`
          : `<button class="btn primary sm" onclick="installModel('${m.id}')">Скачать (${m.missingGB} ГБ)</button>`}
      </div>
    </div>
  `).join('');
}

async function loadCatalog() {
  const res = await fetch('/api/catalog');
  const data = await res.json();
  catalogModelsCache = data.models || [];
  renderCatalogGrid();
}

async function installModel(modelId) {
  haptic('confirm');
  await fetch('/api/catalog/install', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelId }) });
  toast('Скачивание запущено (можно свернуть, качается в фоне)');
  pollStatus();
}

async function deleteModel(modelId) {
  const ok = await m3Confirm('Удалить модель?', 'Файлы этой модели будут удалены с накопителя телефона.', 'Удалить', true);
  if (!ok) return;
  await fetch('/api/catalog/delete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelId }) });
  toast('Модель удалена');
  loadCatalog();
  pollStatus();
}

async function searchHf() {
  const q = document.getElementById('hfQuery').value.trim();
  if (!q) return;
  const box = document.getElementById('hfResults');
  box.innerHTML = '<div class="muted">Поиск GGUF на Hugging Face...</div>';
  const res = await fetch('/api/hf/search?q=' + encodeURIComponent(q));
  const items = await res.json();
  if (!items.length) { box.innerHTML = '<div class="muted">Ничего не найдено.</div>'; return; }
  box.innerHTML = items.map(r => `
    <div class="card">
      <div class="row" style="justify-content:space-between;">
        <div style="min-width:0;"><strong style="word-break:break-all;">${esc(r.repoId)}</strong><div class="muted">Скачиваний: ${r.downloads} · Лайков: ${r.likes}</div></div>
        <button class="btn primary sm" onclick="showHfFiles('${esc(r.repoId)}')">Файлы</button>
      </div>
      <div id="hff-${esc(r.repoId).replace(/[^a-zA-Z0-9]/g, '_')}" style="display:none;flex-direction:column;gap:5px;"></div>
    </div>
  `).join('');
}

async function showHfFiles(repoId) {
  const box = document.getElementById('hff-' + repoId.replace(/[^a-zA-Z0-9]/g, '_'));
  box.style.display = 'flex';
  box.innerHTML = '<div class="muted">Загрузка списка .gguf...</div>';
  const res = await fetch('/api/hf/files?repo=' + encodeURIComponent(repoId));
  const files = await res.json();
  box.innerHTML = files.slice(0, 10).map(f => `
    <div class="row" style="justify-content:space-between;background:var(--bg);padding:6px 8px;border-radius:7px;">
      <span class="mono" style="font-size:11px;word-break:break-all;">${esc(f.path)}</span>
      <div class="row">
        <span class="pill mono">${(f.sizeBytes / 1073741824).toFixed(2)} ГБ</span>
        <button class="btn ok sm" onclick="dlHfFile('${esc(repoId)}','${esc(f.path)}')">Скачать</button>
      </div>
    </div>
  `).join('') || '<div class="muted">Нет .gguf файлов</div>';
}

async function dlHfFile(repoId, filePath) {
  await fetch('/api/hf/download', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ repoId, filePath }) });
  toast('Скачивание с Hugging Face начато');
  pollStatus();
}

// ==================== CHAT 2.0 SESSIONS, VOICE, CAMERA, DOCS ====================
async function loadSessions() {
  const res = await fetch('/api/sessions');
  sessions = await res.json();
  if (!sessions.length) { await newSession(); return; }
  if (!activeSessionId || !sessions.some(s => s.id === activeSessionId)) activeSessionId = sessions[0].id;
  renderSessions();
  renderMessages();
}

async function newSession() {
  const res = await fetch('/api/sessions', { method: 'POST' });
  const s = await res.json();
  sessions.unshift(s);
  activeSessionId = s.id;
  renderSessions();
  renderMessages();
  toast('Создан новый чат');
}

async function renameActiveSession() {
  const s = sessions.find(x => x.id === activeSessionId);
  if (!s) return;
  const next = await m3Prompt('Переименовать чат', 'Введи новое название диалога:', s.title || 'Новый чат');
  if (!next || !next.trim()) return;
  await fetch('/api/sessions/' + activeSessionId, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ title: next.trim() })
  });
  s.title = next.trim();
  renderSessions();
  toast('Чат переименован');
}

async function saveSessionMeta() {
  if (!activeSessionId) return;
  const personaId = document.getElementById('personaSelect').value;
  const temperature = Number(document.getElementById('chatTemp').value);
  await fetch('/api/sessions/' + activeSessionId, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ personaId, temperature })
  });
}

async function deleteActiveSession() {
  if (!activeSessionId) return;
  await fetch('/api/sessions/' + activeSessionId, { method: 'DELETE' });
  activeSessionId = null;
  toggleChatDrawer(false);
  loadSessions();
}

function selectSession(id) {
  activeSessionId = id;
  renderSessions();
  renderMessages();
  toggleChatDrawer(false);
  const inp = document.getElementById('chatInput');
  if (inp) { inp.disabled = false; inp.readOnly = false; }
}

function renderSessions() {
  const el = document.getElementById('sessionList');
  if (!el) return;
  el.innerHTML = sessions.map(s =>
    `<div class="session-item ${s.id === activeSessionId ? 'active' : ''}" onclick="selectSession('${s.id}')">${esc(s.title)}</div>`
  ).join('');
}

function fmtNum(n) {
  return Number(n || 0).toLocaleString('ru-RU');
}

function renderContextBar(s) {
  const lbl = document.getElementById('ctxLabel');
  const rem = document.getElementById('ctxRemain');
  const fill = document.getElementById('ctxFill');
  const miniPct = document.getElementById('ctxMiniPct');
  const ctxDot = document.getElementById('ctxDot');
  const quickCompact = document.getElementById('btnQuickCompact');
  const btnCompact = document.getElementById('btnCompact');
  const btnMem = document.getElementById('btnMemory');
  if (!s) return;

  const used = s.ctxUsedTokens || 0;
  const max = s.ctxMaxTokens || 8192;
  const remain = typeof s.ctxRemainingTokens === 'number' ? s.ctxRemainingTokens : Math.max(0, max - used);
  const pct = typeof s.ctxPercent === 'number' ? s.ctxPercent : Math.min(100, Math.round((used * 100) / Math.max(1, max)));
  const approx = s.ctxIsExact ? '' : '≈';

  if (lbl) lbl.textContent = `Контекст: ${approx}${fmtNum(used)} / ${fmtNum(max)} (${pct}%)`;
  if (rem) rem.textContent = `своб. ${fmtNum(remain)}`;
  if (fill) {
    fill.style.width = `${Math.max(0, Math.min(100, pct))}%`;
    fill.className = 'ctx-fill' + (pct >= 85 ? ' err' : pct >= 65 ? ' warn' : '');
  }
  if (miniPct) miniPct.textContent = `${pct}%`;
  if (ctxDot) ctxDot.className = 'ctx-dot' + (pct >= 85 ? ' err' : pct >= 65 ? ' warn' : '');
  if (quickCompact) quickCompact.style.display = pct >= 70 ? 'inline-flex' : 'none';

  if (btnCompact) btnCompact.className = 'btn' + (pct >= 70 ? ' warn-soft' : '');
  const hasMem = !!(s.memorySummary && s.memorySummary.trim());
  const hasSys = !!(s.customSystemPrompt && s.customSystemPrompt.trim());
  if (btnMem) {
    btnMem.className = 'btn' + ((hasMem || hasSys) ? ' info-soft' : '');
    btnMem.textContent = hasMem ? 'Память ✓' : (hasSys ? 'Промпт ✓' : 'Память / Промпт');
  }
  const sysEl = document.getElementById('ctxCustomSys');
  const memEl = document.getElementById('ctxMemSummary');
  if (sysEl && document.activeElement !== sysEl) sysEl.value = s.customSystemPrompt || '';
  if (memEl && document.activeElement !== memEl) memEl.value = s.memorySummary || '';
}

async function saveCtxDrawer() {
  if (!activeSessionId) return;
  const customSystemPrompt = document.getElementById('ctxCustomSys').value.trim();
  const memorySummary = document.getElementById('ctxMemSummary').value.trim();
  const res = await fetch('/api/sessions/' + activeSessionId, {
    method: 'PATCH', headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ customSystemPrompt, memorySummary })
  });
  if (res.ok) {
    const updated = await res.json();
    const idx = sessions.findIndex(x => x.id === activeSessionId);
    if (idx >= 0) sessions[idx] = updated;
    renderMessages();
    toggleChatSheet(false);
    toast('Память и системный промпт сохранены');
  }
}

async function compactActiveSession() {
  if (!activeSessionId) return;
  const s = sessions.find(x => x.id === activeSessionId);
  if (!s || !s.messages || !s.messages.length) {
    toast('В чате ещё нет сообщений для сжатия');
    return;
  }
  const btn = document.getElementById('btnCompact');
  if (btn) btn.disabled = true;
  toast('Сжимаю контекст через локальную LLM...');
  try {
    const res = await fetch('/api/sessions/' + activeSessionId + '/compact', { method: 'POST' });
    const data = await res.json();
    if (!res.ok) {
      toast(data.error || 'Не удалось сжать контекст');
      return;
    }
    if (data.session) {
      const idx = sessions.findIndex(x => x.id === activeSessionId);
      if (idx >= 0) sessions[idx] = data.session;
    }
    renderMessages();
    toast(`Контекст сжат: ${fmtNum(data.beforeTokens)} → ${fmtNum(data.afterTokens)} ток. (-${data.savedPct}%)`);
  } catch (e) {
    toast('Ошибка сжатия: ' + e.message);
  } finally {
    if (btn) btn.disabled = false;
    pollStatus();
  }
}

async function clearActiveSession() {
  if (!activeSessionId) return;
  const s = sessions.find(x => x.id === activeSessionId);
  if (!s || (!s.messages?.length && !s.memorySummary)) return;
  const ok = await m3Confirm('Очистить чат?', 'Сообщения и сжатая память этого диалога будут удалены.', 'Очистить', true);
  if (!ok) return;
  const res = await fetch('/api/sessions/' + activeSessionId + '/clear', { method: 'POST' });
  if (res.ok) {
    const updated = await res.json();
    const idx = sessions.findIndex(x => x.id === activeSessionId);
    if (idx >= 0) sessions[idx] = updated;
    renderMessages();
    toast('Контекст чата очищен');
  }
}

async function regenerateLast() {
  if (!activeSessionId || abortCtrl) return;
  const s = sessions.find(x => x.id === activeSessionId);
  if (!s || !s.messages || !s.messages.length) {
    toast('Нет сообщений для повтора');
    return;
  }
  const res = await fetch('/api/sessions/' + activeSessionId + '/pop-last', { method: 'POST' });
  const data = await res.json();
  if (!res.ok || !data.prompt) {
    toast(data.error || 'Нечего перегенерировать');
    return;
  }
  if (data.session) {
    const idx = sessions.findIndex(x => x.id === activeSessionId);
    if (idx >= 0) sessions[idx] = data.session;
  }
  renderMessages();
  document.getElementById('chatInput').value = data.prompt;
  sendChat();
}

function exportChatMarkdown() {
  const s = sessions.find(x => x.id === activeSessionId);
  if (!s) return;
  const lines = [`# ${s.title || 'Чат Brazhnichenko Hub'}`, ''];
  if (s.memorySummary) lines.push('## Сжатая память контекста', s.memorySummary, '');
  for (const m of (s.messages || [])) {
    lines.push(m.role === 'user' ? '### Вы' : '### Локальная LLM', m.content || '', '');
  }
  const md = lines.join('\n').trim();
  if (window.AndroidHub && window.AndroidHub.shareText) {
    window.AndroidHub.shareText(md);
  } else if (window.AndroidHub && window.AndroidHub.copyText) {
    window.AndroidHub.copyText(md);
    toast('Чат скопирован в Markdown');
  } else if (navigator.clipboard) {
    navigator.clipboard.writeText(md);
    toast('Чат скопирован в Markdown');
  }
}

const SVG_COPY = '<svg viewBox="0 0 24 24"><rect x="9" y="9" width="13" height="13" rx="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg>';
const SVG_SPEAK = '<svg viewBox="0 0 24 24"><polygon points="11 5 6 9 2 9 2 15 6 15 11 19 11 5"/><path d="M15.54 8.46a5 5 0 0 1 0 7.07"/><path d="M19.07 4.93a10 10 0 0 1 0 14.14"/></svg>';
const SVG_SHARE = '<svg viewBox="0 0 24 24"><circle cx="18" cy="5" r="3"/><circle cx="6" cy="12" r="3"/><circle cx="18" cy="19" r="3"/><line x1="8.59" y1="13.51" x2="15.42" y2="17.49"/><line x1="15.41" y1="6.51" x2="8.59" y2="10.49"/></svg>';

function renderMessages() {
  const s = sessions.find(x => x.id === activeSessionId);
  const box = document.getElementById('chatMsgs');
  codeBlockStore.length = 0;

  if (s) {
    document.getElementById('personaSelect').value = s.personaId || 'default';
    const t = typeof s.temperature === 'number' ? s.temperature : 0.7;
    document.getElementById('chatTemp').value = t;
    document.getElementById('chatTempVal').textContent = t.toFixed(2);
    renderContextBar(s);
  }

  const memHtml = (s && s.memorySummary && s.memorySummary.trim())
    ? `<details class="mem-card"><summary>Сжатая память${s.compactedCount ? ` (${s.compactedCount} сообщ.)` : ''}</summary><div class="mem-body">${renderMarkdown(s.memorySummary)}</div><div class="row" style="margin-top:6px;"><button class="btn sm" onclick="toggleCtxDrawer(true)">Редактировать</button></div></details>`
    : '';

  if (!s || !s.messages || !s.messages.length) {
    box.innerHTML = memHtml + `
      <div class="welcome-card">
        <div class="welcome-title">Локальная AI офлайн · Hexagon NPU</div>
        <div class="welcome-sub">Приватный чат без интернета и облака. Задай вопрос, продиктуй голосом, сфотографируй документ или выбери быстрый сценарий ниже.</div>
        <div class="welcome-tags">
          <span class="pill info">NPU + GPU</span>
          <span class="pill">Vision (Фото)</span>
          <span class="pill">PDF / Документы</span>
          <span class="pill">Голос + TTS</span>
        </div>
      </div>`;
    updateQuickChipsVisibility(true);
    return;
  }
  updateQuickChipsVisibility(false);

  box.innerHTML = memHtml + s.messages.map((m, idx) => {
    const isUser = m.role === 'user';
    if (isUser) {
      return `<div class="msg user"><div class="msg-body">${esc(m.content)}</div></div>`;
    }
    const metaParts = [];
    if (m.tokPerSec) metaParts.push(`${m.tokPerSec} t/s`);
    if (m.tokens) metaParts.push(`${m.tokens} ток`);
    if (m.backend) metaParts.push(String(m.backend).toUpperCase());
    const metaText = metaParts.join(' · ') || 'Локальная LLM';
    return `
      <div class="msg asst">
        ${m.webSources && m.webSources.length ? renderSourcesHtml(m.webSources, m.webProvider) : ''}
        ${m.reasoning ? `<details class="think"><summary>Размышления (&lt;think&gt;)</summary><div class="msg-body">${esc(m.reasoning)}</div></details>` : ''}
        <div class="msg-body">${renderMarkdown(m.content)}</div>
        <div class="msg-foot">
          <span class="msg-meta mono">${esc(metaText)}</span>
          <div class="msg-acts">
            <button class="msg-act-btn" title="Копировать" aria-label="Копировать" onclick="copyMsg(${idx})">${SVG_COPY}</button>
            <button class="msg-act-btn" title="Озвучить" aria-label="Озвучить" onclick="speakText(${idx})">${SVG_SPEAK}</button>
            <button class="msg-act-btn" title="Поделиться" aria-label="Поделиться" onclick="shareMsg(${idx})">${SVG_SHARE}</button>
          </div>
        </div>
      </div>
    `;
  }).join('');
  box.scrollTop = box.scrollHeight;
}

function copyMsg(idx) {
  const s = sessions.find(x => x.id === activeSessionId);
  const m = s?.messages?.[idx];
  if (m && window.AndroidHub) window.AndroidHub.copyText(m.content);
}
function shareMsg(idx) {
  const s = sessions.find(x => x.id === activeSessionId);
  const m = s?.messages?.[idx];
  if (m && window.AndroidHub) window.AndroidHub.shareText(m.content);
}
function speakText(idx) {
  const s = sessions.find(x => x.id === activeSessionId);
  const m = s?.messages?.[idx];
  if (m && window.AndroidHub) window.AndroidHub.speak(stripMarkdownForTts(m.content), 'ru');
}
function speakDemo() {
  const t = document.getElementById('ttsBox').value.trim();
  if (t && window.AndroidHub) window.AndroidHub.speak(t, 'ru');
}
function stopSpeak() { if (window.AndroidHub) window.AndroidHub.stopSpeak(); }

// ==================== NATIVE ANDROID BRIDGE & SYSTEM ASSISTANT ====================
function startVoiceInput() {
  haptic('tick');
  if (window.AndroidHub && window.AndroidHub.startVoiceInput) window.AndroidHub.startVoiceInput();
  else toast('Голосовой ввод доступен в Android APK');
}

function stopOfflineVoice(cancel) {
  haptic('tick');
  if (window.AndroidHub && window.AndroidHub.stopOfflineVoice) window.AndroidHub.stopOfflineVoice(!!cancel);
  const vb = document.getElementById('voiceBar');
  if (vb && cancel) vb.style.display = 'none';
}

window.onAndroidVoiceState = function(state, detail, levelPct, seconds) {
  const vb = document.getElementById('voiceBar');
  const title = document.getElementById('voiceBarTitle');
  const fill = document.getElementById('voiceBarFill');
  if (!vb) return;
  if (state === 'recording') {
    vb.style.display = 'flex';
    if (title) title.textContent = `Слушаю офлайн (${detail || 'STT'}) · ${Number(seconds || 0).toFixed(1)}с`;
    if (fill) fill.style.width = `${Math.max(8, Math.min(100, levelPct || 0))}%`;
  } else if (state === 'transcribing') {
    vb.style.display = 'flex';
    if (title) title.textContent = detail || 'Распознаю офлайн...';
    if (fill) fill.style.width = '100%';
  } else {
    vb.style.display = 'none';
  }
};

window.onAndroidVoiceResult = function(text, autoSend, speakReply, forceWebSearch, decodeMs, assistModel) {
  const vb = document.getElementById('voiceBar');
  if (vb) vb.style.display = 'none';
  if (!text) return;
  switchView('chat');
  const inp = document.getElementById('chatInput');
  inp.value = inp.value ? (inp.value.trim() + ' ' + text) : text;
  autoGrowInput();
  if (autoSend) {
    toast(forceWebSearch ? 'Ассистент: ищу в сети и отвечаю...' : 'Ассистент: отправляю запрос...');
    sendChat({
      speakReply: !!speakReply,
      webSearch: typeof forceWebSearch === 'boolean' ? forceWebSearch : undefined,
      modelOverride: assistModel || ''
    });
  } else {
    inp.focus();
    toast(decodeMs ? `Распознано офлайн за ${(decodeMs / 1000).toFixed(2)}с` : 'Распознано голосом');
  }
};

window.onAndroidAssistLaunch = function(opts) {
  switchView('chat');
  opts = opts || {};
  pendingManualAssistModel = opts.assistModel || '';
  const banner = document.getElementById('assistScreenOffer');
  if (opts.hasScreenshot) {
    if (opts.autoAttachScreen) {
      attachAssistScreen();
    } else if (banner) {
      banner.style.display = 'flex';
    }
  } else if (banner) {
    banner.style.display = 'none';
  }
  const inp = document.getElementById('chatInput');
  if (inp && !opts.autoVoice) {
    inp.focus();
  }
};

function attachAssistScreen() {
  const banner = document.getElementById('assistScreenOffer');
  if (banner) banner.style.display = 'none';
  if (window.AndroidHub && window.AndroidHub.attachLastAssistScreen) {
    window.AndroidHub.attachLastAssistScreen();
  }
}

function dismissAssistScreen() {
  const banner = document.getElementById('assistScreenOffer');
  if (banner) banner.style.display = 'none';
}

function openAssistantSettings() {
  if (window.AndroidHub && window.AndroidHub.openAssistantSettings) {
    window.AndroidHub.openAssistantSettings();
  } else {
    toast('Доступно в Android APK');
  }
}

function refreshAssistantStatus() {
  const pill = document.getElementById('assistStatusPill');
  if (!pill) return;
  if (window.AndroidHub && window.AndroidHub.isDefaultAssistant) {
    const active = !!window.AndroidHub.isDefaultAssistant();
    pill.textContent = active ? 'Активен ✓' : 'Не выбран';
    pill.className = 'pill ' + (active ? 'ok' : 'warn');
  } else {
    pill.textContent = 'Только Android';
    pill.className = 'pill';
  }
}

function openCamera() {
  if (window.AndroidHub && window.AndroidHub.openCamera) window.AndroidHub.openCamera();
  else document.getElementById('imgFile').click();
}
window.onAndroidCameraImage = function(dataUrl, customLabel) {
  attachedDoc = null;
  attachedImg = dataUrl;
  updateAttachStrip(customLabel || 'Фото с камеры (Vision)', dataUrl);
};

function pickDocument() {
  if (window.AndroidHub && window.AndroidHub.pickDocument) window.AndroidHub.pickDocument();
  else toast('Выбор документов доступен в Android APK');
}
window.onAndroidDocAttached = function(doc) {
  if (!doc) return;
  if (doc.kind === 'image' && doc.imageDataUrl) {
    attachedDoc = null;
    attachedImg = doc.imageDataUrl;
    updateAttachStrip('PDF (Vision): ' + doc.name, doc.imageDataUrl);
    toast('Скан-PDF прикреплён как изображение для Vision');
  } else if (doc.text) {
    attachedImg = null;
    attachedDoc = { name: doc.name || 'document.txt', text: doc.text };
    const kb = Math.max(0.1, doc.text.length / 1024).toFixed(1);
    updateAttachStrip(`Документ: ${attachedDoc.name} (${kb} КБ)`, null);
    toast('Документ прикреплён');
  }
};

function onPickImage(file) {
  if (!file) return;
  const r = new FileReader();
  r.onload = () => {
    attachedDoc = null;
    attachedImg = r.result;
    updateAttachStrip('Фото из галереи (Vision)', attachedImg);
  };
  r.readAsDataURL(file);
}

function updateAttachStrip(label, imgSrc) {
  const strip = document.getElementById('attachStrip');
  const imgEl = document.getElementById('imgPreview');
  document.getElementById('attachLabel').textContent = label;
  if (imgSrc) {
    imgEl.src = imgSrc;
    imgEl.style.display = 'block';
  } else {
    imgEl.style.display = 'none';
  }
  strip.classList.add('show');
  updateQuickChipsVisibility(true);
}

function clearAttachments() {
  attachedImg = null;
  attachedDoc = null;
  const imgFile = document.getElementById('imgFile');
  if (imgFile) imgFile.value = '';
  document.getElementById('attachStrip').classList.remove('show');
  updateQuickChipsVisibility(false);
}

function updateQuickChipsVisibility(forceShow) {
  const el = document.getElementById('quickChips');
  if (!el) return;
  const s = sessions.find(x => x.id === activeSessionId);
  const emptyChat = !s || !s.messages || !s.messages.length;
  const hasAttach = !!(attachedImg || attachedDoc);
  el.classList.toggle('show', !!(forceShow || emptyChat || hasAttach));
}

function applyQuickPrompt(prefix) {
  const inp = document.getElementById('chatInput');
  const cur = inp.value.trim();
  if (attachedImg || attachedDoc) {
    inp.value = cur ? (prefix + '\n' + cur) : prefix.replace(/:$/, '.');
    autoGrowInput();
    sendChat();
    return;
  }
  if (cur) {
    inp.value = prefix + '\n\n' + cur;
    autoGrowInput();
    sendChat();
  } else {
    inp.value = prefix + ' ';
    autoGrowInput();
    inp.focus();
  }
}

window.onAndroidQuickAction = async function(action) {
  switchView('chat');
  if (action === 'new_chat') await newSession();
  const inp = document.getElementById('chatInput');
  if (inp) {
    inp.disabled = false;
    inp.readOnly = false;
    if (action === 'ask' || action === 'new_chat') inp.focus();
  }
  updateQuickChipsVisibility(action === 'ask' || action === 'new_chat');
};

window.onAndroidSharedText = function(text, sourceLabel) {
  if (!text) return;
  switchView('chat');
  const inp = document.getElementById('chatInput');
  inp.value = text;
  autoGrowInput();
  inp.focus();
  updateQuickChipsVisibility(true);
  toast(`Получено (${sourceLabel || 'Поделиться'}) — выбери сценарий или отправь`);
};

// ==================== LLM START / STOP / STREAM ====================
async function startLlm() {
  const modelFile = document.getElementById('modelSelect').value;
  toast('Запуск локальной LLM...');
  const res = await fetch('/api/llm/start', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelFile }) });
  const data = await res.json();
  toast(res.ok ? 'LLM запущена и готова!' : (data.error || 'Ошибка запуска'));
  pollStatus();
}

async function stopLlm() {
  await fetch('/api/llm/stop', { method: 'POST' });
  toast('LLM остановлена, RAM и NPU освобождены');
  pollStatus();
}

async function sendChat(opts) {
  opts = opts || {};
  const inp = document.getElementById('chatInput');
  const text = inp.value.trim();
  if (!text && !attachedImg && !attachedDoc) return;
  inp.value = '';
  autoGrowInput();
  const img = attachedImg;
  const doc = attachedDoc;
  clearAttachments();
  dismissAssistScreen();

  const box = document.getElementById('chatMsgs');
  const welcome = box.querySelector('.welcome-card');
  if (welcome) welcome.remove();

  const uEl = document.createElement('div');
  uEl.className = 'msg user';
  const docBadge = doc ? `<span class="pill info">${esc(doc.name)}</span>` : '';
  uEl.innerHTML = `${docBadge}${img ? `<img src="${img}" class="thumb" style="display:block;"/>` : ''}<div class="msg-body">${esc(text || (doc ? 'Разбери документ' : 'Что на фото?'))}</div>`;
  box.appendChild(uEl);

  const aEl = document.createElement('div');
  aEl.className = 'msg asst';
  aEl.innerHTML = `<div class="src-slot"></div><details class="think" style="display:none;"><summary>Размышления (&lt;think&gt;)</summary><div class="th msg-body"></div></details><div class="ans msg-body">Запуск...</div><div class="msg-foot"><span class="msg-meta mono spd">Генерация...</span></div>`;
  box.appendChild(aEl);
  box.scrollTop = box.scrollHeight;

  const srcSlot = aEl.querySelector('.src-slot');
  const ans = aEl.querySelector('.ans');
  const thBox = aEl.querySelector('.think');
  const th = aEl.querySelector('.th');
  const spd = aEl.querySelector('.spd');

  const chkWeb = document.getElementById('chkWebSearch');
  const useWebSearch = typeof opts.webSearch === 'boolean' ? opts.webSearch : !!(chkWeb && chkWeb.checked);

  document.getElementById('btnSend').style.display = 'none';
  document.getElementById('btnAbort').style.display = 'inline-flex';
  abortCtrl = new AbortController();

  let rawStreamText = '';
  let lastRenderTs = 0;

  const useModelOverride = (opts.modelOverride || pendingManualAssistModel || '').trim();
  pendingManualAssistModel = '';
  const targetModelFile = useModelOverride || (document.getElementById('modelSelect').value || '');

  try {
    const res = await fetch('/api/chat/stream', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      signal: abortCtrl.signal,
      body: JSON.stringify({
        sessionId: activeSessionId,
        message: text,
        personaId: document.getElementById('personaSelect').value,
        temperature: Number(document.getElementById('chatTemp').value),
        modelFile: targetModelFile,
        temporaryModel: !!useModelOverride,
        imageBase64: img || '',
        docName: doc ? doc.name : '',
        docText: doc ? doc.text : '',
        webSearch: useWebSearch
      })
    });

    const reader = res.body.getReader();
    const dec = new TextDecoder();
    let buf = '';

    while (true) {
      const { done, value } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      const parts = buf.split('\n\n');
      buf = parts.pop();
      for (const p of parts) {
        if (!p.startsWith('data:')) continue;
        const ev = JSON.parse(p.slice(5).trim());
        if (ev.type === 'status') ans.textContent = ev.data;
        else if (ev.type === 'sources' && ev.data && ev.data.items) {
          srcSlot.innerHTML = renderSourcesHtml(ev.data.items, ev.data.provider);
        }
        else if (ev.type === 'think') { thBox.style.display = 'block'; th.textContent += ev.data; }
        else if (ev.type === 'token') {
          rawStreamText += ev.data;
          const now = Date.now();
          if (now - lastRenderTs > 90) {
            lastRenderTs = now;
            ans.innerHTML = renderMarkdown(rawStreamText);
          }
        }
        else if (ev.type === 'vision_missing') {
          aEl.className = 'msg warn';
          ans.innerHTML = `${esc(ev.data)}<div class="row" style="margin-top:8px;"><button class="btn primary sm" onclick="switchView('models')">Открыть Vision-модели</button></div>`;
        }
        else if (ev.type === 'error') { aEl.className = 'msg warn'; ans.textContent = 'Ошибка: ' + ev.data; }
        else if (ev.type === 'done') {
          ans.innerHTML = renderMarkdown(rawStreamText);
          const tokPart = ev.data.tokens ? ` · ${ev.data.tokens} ток` : '';
          spd.textContent = `${ev.data.tokPerSec} t/s${tokPart}${ev.data.backend ? ' · ' + String(ev.data.backend).toUpperCase() : ''}`;
          await loadSessions();
          if (opts.speakReply && rawStreamText.trim() && window.AndroidHub && window.AndroidHub.speak) {
            window.AndroidHub.speak(stripMarkdownForTts(rawStreamText), 'ru');
          }
        }
        box.scrollTop = box.scrollHeight;
      }
    }
  } catch (e) {
    if (e.name !== 'AbortError') ans.textContent = 'Ошибка: ' + e.message;
  } finally {
    document.getElementById('btnSend').style.display = 'inline-flex';
    document.getElementById('btnAbort').style.display = 'none';
    abortCtrl = null;
    pollStatus();
  }
}

function abortChat() { if (abortCtrl) abortCtrl.abort(); }

// ==================== SETTINGS & LOGS ====================
function populateAssistModelSelect(llms) {
  const sel = document.getElementById('cfgAssistModel');
  if (!sel) return;
  const cur = assistSettings.assistModel || sel.value || '';
  const opts = ['<option value="">Текущая модель из чата (по умолчанию)</option>'];
  for (const x of (llms || [])) {
    opts.push(`<option value="${esc(x.fileName)}" ${x.fileName === cur ? 'selected' : ''}>${esc(shortModelLabel(x.fileName, x.sizeGB))}${x.hasVision ? ' · Vision' : ''}</option>`);
  }
  sel.innerHTML = opts.join('');
  if (cur) sel.value = cur;
}

async function loadSettings() {
  const res = await fetch('/api/settings');
  const s = await res.json();
  document.getElementById('cfgBackend').value = s.backendMode;
  document.getElementById('cfgIdle').value = String(s.idleUnloadMin ?? 10);
  document.getElementById('cfgCtx').value = String(s.ctxSize);
  document.getElementById('cfgThreads').value = s.threads;

  assistSettings.assistAutoVoice = s.assistAutoVoice !== false;
  assistSettings.assistAutoTts = s.assistAutoTts !== false;
  assistSettings.assistAttachScreen = !!s.assistAttachScreen;
  assistSettings.assistWebSearch = s.assistWebSearch !== false;
  assistSettings.assistModel = s.assistModel || '';
  const elV = document.getElementById('cfgAssistAutoVoice');
  const elT = document.getElementById('cfgAssistAutoTts');
  const elS = document.getElementById('cfgAssistAttachScreen');
  const elW = document.getElementById('cfgAssistWebSearch');
  if (elV) elV.checked = assistSettings.assistAutoVoice;
  if (elT) elT.checked = assistSettings.assistAutoTts;
  if (elS) elS.checked = assistSettings.assistAttachScreen;
  if (elW) elW.checked = assistSettings.assistWebSearch;
  if (lastStatus && lastStatus.availableLlms) populateAssistModelSelect(lastStatus.availableLlms);
  const elM = document.getElementById('cfgAssistModel');
  if (elM) elM.value = assistSettings.assistModel;

  const sp = document.getElementById('cfgSearchProvider');
  if (sp) sp.value = s.searchProvider || 'auto';
  const skp = document.getElementById('searchKeysPill');
  if (skp) {
    const keys = [];
    if (s.tavilyConnected) keys.push('Tavily ✓');
    if (s.braveConnected) keys.push('Brave ✓');
    skp.textContent = keys.length ? keys.join(' · ') : 'Бесплатный гибрид';
    skp.className = 'pill ' + (keys.length ? 'ok' : 'info');
  }
}

async function saveAssistSettings() {
  const elM = document.getElementById('cfgAssistModel');
  const body = {
    assistAutoVoice: !!document.getElementById('cfgAssistAutoVoice').checked,
    assistAutoTts: !!document.getElementById('cfgAssistAutoTts').checked,
    assistAttachScreen: !!document.getElementById('cfgAssistAttachScreen').checked,
    assistWebSearch: !!document.getElementById('cfgAssistWebSearch').checked,
    assistModel: elM ? elM.value : ''
  };
  await fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  Object.assign(assistSettings, body);
  toast('Настройки ассистента сохранены');
}

async function saveSearchSettings() {
  const body = {
    searchProvider: document.getElementById('cfgSearchProvider').value
  };
  const tk = document.getElementById('cfgTavilyKey').value.trim();
  const bk = document.getElementById('cfgBraveKey').value.trim();
  if (tk) body.tavilyKey = tk;
  if (bk) body.braveKey = bk;
  await fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  document.getElementById('cfgTavilyKey').value = '';
  document.getElementById('cfgBraveKey').value = '';
  await loadSettings();
  toast('Настройки гибридного поиска сохранены');
}

async function saveSettings() {
  const body = {
    backendMode: document.getElementById('cfgBackend').value,
    idleUnloadMin: Number(document.getElementById('cfgIdle').value),
    ctxSize: Number(document.getElementById('cfgCtx').value),
    threads: Number(document.getElementById('cfgThreads').value)
  };
  const tok = document.getElementById('cfgHfToken').value.trim();
  if (tok) body.hfToken = tok;
  await fetch('/api/settings', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
  toast('Настройки сохранены (перезапусти LLM для применения)');
}

async function loadLogs() {
  const res = await fetch('/api/llm/logs');
  const data = await res.json();
  document.getElementById('logsBox').textContent = data.logs;
}

// ==================== 100% OFFLINE STT MANAGEMENT (GIGAAM / WHISPER) ====================
let sttPollTimer = null;

async function loadSttStatus() {
  try {
    const res = await fetch('/api/stt/status');
    const st = await res.json();
    const pill = document.getElementById('sttStatusPill');
    const modeSel = document.getElementById('cfgSttMode');
    const dlWrap = document.getElementById('sttDlWrap');
    const dlLbl = document.getElementById('sttDlLabel');
    const dlPct = document.getElementById('sttDlPct');
    const dlFill = document.getElementById('sttDlFill');
    const list = document.getElementById('sttModelsList');

    if (modeSel) modeSel.value = st.sttMode === 'system' ? 'system' : 'auto';
    if (pill) {
      pill.textContent = st.offlineReady ? 'Офлайн ✓' : 'Системный';
      pill.className = 'pill ' + (st.offlineReady ? 'ok' : '');
    }
    if (dlWrap) {
      if (st.dlState === 'downloading') {
        dlWrap.style.display = 'flex';
        if (dlLbl) dlLbl.textContent = 'Скачивание ' + (st.dlModelId || 'STT') + '...';
        if (dlPct) dlPct.textContent = `${st.dlPercent || 0}%`;
        if (dlFill) dlFill.style.width = `${st.dlPercent || 0}%`;
        if (!sttPollTimer) sttPollTimer = setInterval(loadSttStatus, 700);
      } else {
        dlWrap.style.display = 'none';
        if (sttPollTimer) { clearInterval(sttPollTimer); sttPollTimer = null; }
        if (st.dlState === 'error' && st.dlError) toast('Ошибка скачивания STT: ' + st.dlError);
      }
    }
    if (list && st.models) {
      list.innerHTML = st.models.map(m => `
        <div class="stt-item ${m.active ? 'active' : ''}">
          <div class="row" style="justify-content:space-between;">
            <strong style="font-size:13px;">${esc(m.name)}</strong>
            <span class="pill mono">${m.sizeMB} МБ</span>
          </div>
          <div class="muted">${esc(m.desc)}</div>
          <div class="row" style="justify-content:flex-end;">
            ${m.installed
              ? `${m.active
                  ? '<span class="pill ok">Активна ✓</span>'
                  : `<button class="btn ok sm" onclick="selectSttModel('${esc(m.id)}')">Выбрать</button>`}
                 <button class="btn danger sm" onclick="deleteSttModel('${esc(m.id)}')">Удалить</button>`
              : `<button class="btn primary sm" onclick="downloadSttModel('${esc(m.id)}')">Скачать (${m.sizeMB} МБ)</button>`}
          </div>
        </div>
      `).join('');
    }
  } catch (e) { }
}

async function saveSttMode() {
  const sttMode = document.getElementById('cfgSttMode').value;
  await fetch('/api/stt/select', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sttMode }) });
  toast('Режим голоса сохранён');
  loadSttStatus();
}

async function selectSttModel(modelId) {
  haptic('confirm');
  await fetch('/api/stt/select', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sttMode: 'auto', sttModel: modelId }) });
  toast('Офлайн-модель речи активирована');
  loadSttStatus();
}

async function downloadSttModel(modelId) {
  haptic('confirm');
  await fetch('/api/stt/download', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelId }) });
  toast('Скачивание офлайн-модели речи начато');
  loadSttStatus();
}

async function cancelSttDownload() {
  await fetch('/api/stt/cancel', { method: 'POST' });
  toast('Загрузка STT отменена');
  loadSttStatus();
}

async function deleteSttModel(modelId) {
  const ok = await m3Confirm('Удалить модель речи?', 'Файлы офлайн-STT будут удалены с устройства.', 'Удалить', true);
  if (!ok) return;
  await fetch('/api/stt/delete', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ modelId }) });
  toast('Модель речи удалена');
  loadSttStatus();
}

// ==================== IN-APP GITHUB RELEASES AUTO-UPDATER ====================
async function checkAppUpdate(silent) {
  const btn = document.getElementById('btnCheckUpdate');
  const txt = document.getElementById('updStatusText');
  const instBtn = document.getElementById('btnInstallUpdate');
  const verPill = document.getElementById('updVersionPill');
  if (btn && !silent) { btn.disabled = true; btn.textContent = 'Проверка...'; }
  try {
    const res = await fetch('/api/update/check');
    const d = await res.json();
    if (verPill && d.currentVersion) verPill.textContent = 'v' + d.currentVersion;
    if (!d.ok) {
      if (!silent) toast('Не удалось проверить GitHub: ' + (d.error || 'ошибка сети'));
      if (txt && !silent) txt.textContent = 'Ошибка проверки: ' + (d.error || 'нет сети');
      return;
    }
    latestUpdateInfo = d;
    const directLink = document.getElementById('btnDirectApkLink');
    if (d.hasUpdate) {
      if (txt) txt.textContent = `Доступна новая версия v${d.latestVersion} (${d.apkSizeMB} МБ). ${d.notes ? d.notes.split('\n')[0] : ''}`;
      if (instBtn) {
        instBtn.style.display = 'inline-flex';
        instBtn.disabled = false;
        instBtn.textContent = `Обновить до v${d.latestVersion}`;
        instBtn.onclick = startAppUpdate;
      }
      if (directLink && d.apkUrl) {
        directLink.href = d.apkUrl;
        directLink.style.display = 'inline-block';
      }
      const banner = document.getElementById('updateBanner');
      const bText = document.getElementById('updateBannerText');
      if (banner && bText) {
        bText.textContent = `Доступно обновление Hub AI v${d.latestVersion} (${d.apkSizeMB} МБ)`;
        banner.style.display = 'flex';
      }
      if (!silent) toast(`Найдена новая версия v${d.latestVersion}!`);
    } else {
      if (txt) txt.textContent = `Установлена актуальная версия v${d.currentVersion} (GitHub: v${d.latestVersion})`;
      if (instBtn) instBtn.style.display = 'none';
      if (directLink) directLink.style.display = 'none';
      if (!silent) toast(`У тебя последняя версия v${d.currentVersion}`);
    }
  } catch (e) {
    if (!silent) toast('Нет интернета для проверки обновлений');
  } finally {
    if (btn && !silent) { btn.disabled = false; btn.textContent = 'Проверить обновления'; }
  }
}

function dismissUpdateBanner() {
  const b = document.getElementById('updateBanner');
  if (b) b.style.display = 'none';
}

async function startAppUpdate() {
  if (!latestUpdateInfo || !latestUpdateInfo.apkUrl) {
    await checkAppUpdate(false);
    if (!latestUpdateInfo || !latestUpdateInfo.apkUrl) return;
  }
  haptic('confirm');
  dismissUpdateBanner();
  switchView('system');
  const progWrap = document.getElementById('updProgWrap');
  const instBtn = document.getElementById('btnInstallUpdate');
  if (progWrap) progWrap.style.display = 'flex';
  if (instBtn) {
    instBtn.disabled = true;
    instBtn.textContent = 'Скачивание...';
  }
  const res = await fetch('/api/update/download', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ apkUrl: latestUpdateInfo.apkUrl, version: latestUpdateInfo.latestVersion })
  });
  if (!res.ok) {
    if (instBtn) {
      instBtn.disabled = false;
      instBtn.textContent = 'Повторить обновление';
    }
    toast('Не удалось начать загрузку обновления');
    return;
  }
  toast('Скачиваю обновление v' + latestUpdateInfo.latestVersion + '...');
  clearInterval(updatePollTimer);
  updatePollTimer = setInterval(pollUpdateProgress, 450);
}

async function pollUpdateProgress() {
  try {
    const res = await fetch('/api/update/status');
    const st = await res.json();
    const wrap = document.getElementById('updProgWrap');
    const lbl = document.getElementById('updProgLabel');
    const pct = document.getElementById('updProgPct');
    const fill = document.getElementById('updProgFill');
    const instBtn = document.getElementById('btnInstallUpdate');
    const directLink = document.getElementById('btnDirectApkLink');
    if (wrap) wrap.style.display = 'flex';
    if (fill) fill.style.width = `${st.percent || 0}%`;
    if (pct) pct.textContent = `${st.percent || 0}%${st.speedMBs > 0 && st.state === 'downloading' ? ' · ' + st.speedMBs + ' МБ/с' : ''}`;
    if (directLink && st.apkUrl) {
      directLink.href = st.apkUrl;
      directLink.style.display = 'inline-block';
    }

    if (st.state === 'downloading') {
      if (lbl) lbl.textContent = 'Скачивание APK...';
      if (instBtn) {
        instBtn.style.display = 'inline-flex';
        instBtn.disabled = true;
        instBtn.textContent = 'Скачивание...';
      }
    } else if (st.state === 'need_permission') {
      clearInterval(updatePollTimer);
      if (lbl) lbl.textContent = 'Разреши установку обновлений в открывшемся окне Android';
      if (instBtn) {
        instBtn.style.display = 'inline-flex';
        instBtn.disabled = false;
        instBtn.textContent = 'Установить скачанный APK';
        instBtn.onclick = installDownloadedUpdate;
      }
    } else if (st.state === 'ready' || st.state === 'installing') {
      clearInterval(updatePollTimer);
      if (lbl) {
        lbl.textContent = st.state === 'installing'
          ? 'Открыто системное окно обновления Android — подтверди установку'
          : 'APK проверен и готов к установке';
      }
      if (instBtn) {
        instBtn.style.display = 'inline-flex';
        instBtn.disabled = false;
        instBtn.textContent = 'Установить скачанный APK';
        instBtn.onclick = installDownloadedUpdate;
      }
    } else if (st.state === 'error') {
      clearInterval(updatePollTimer);
      if (lbl) lbl.textContent = 'Ошибка: ' + (st.error || 'сбой загрузки');
      if (instBtn) {
        instBtn.style.display = 'inline-flex';
        instBtn.disabled = false;
        instBtn.textContent = 'Скачать заново';
        instBtn.onclick = startAppUpdate;
      }
      toast('Ошибка обновления: ' + (st.error || ''));
    }
  } catch (e) { }
}

async function installDownloadedUpdate() {
  haptic('confirm');
  try {
    if (window.AndroidHub && typeof window.AndroidHub.installUpdate === 'function') {
      const raw = window.AndroidHub.installUpdate();
      const d = JSON.parse(raw || '{}');
      if (d.needsPermission) {
        toast(d.message || 'Разреши установку обновлений — она продолжится автоматически');
      } else if (!d.ok) {
        toast(d.error || 'Не удалось запустить системный установщик');
      }
      return;
    }
    const res = await fetch('/api/update/install', { method: 'POST' });
    const d = await res.json();
    if (d.needsPermission || d.backgroundNotified) {
      toast(d.message || 'Разреши установку обновлений и нажми снова');
    } else if (!d.ok) {
      toast(d.error || 'Не удалось запустить установщик');
    }
  } catch (e) {
    toast('Ошибка вызова установщика: ' + e.message);
  }
}

(async function boot() {
  initKeyboardWatcher();
  initWebSearchToggle();
  initSwipeGestures();
  await pollStatus();
  await loadSessions();
  refreshAssistantStatus();
  if (window.AndroidHub && window.AndroidHub.onWebReady) {
    window.AndroidHub.onWebReady();
  }
  setInterval(pollStatus, 2500);
  setTimeout(() => checkAppUpdate(true), 4000);
})();
