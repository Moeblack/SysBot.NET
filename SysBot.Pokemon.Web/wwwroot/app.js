'use strict';
(() => {
  const $ = id => document.getElementById(id);
  const storageKey = 'sysbot.local-web.draft.v1';
  let saved = {};
  try { saved = JSON.parse(localStorage.getItem(storageKey) || '{}'); } catch { /* Storage may be unavailable. */ }
  let mode = saved.mode === 'team' ? 'team' : 'quick';
  let catalog = [], catalogReady = false, online = false, submitting = false, connecting = false;
  let stateBusy = false, mutationBusy = 0, stateVersion = 0, queueSignature = '';
  let state = null, requestId = saved.requestId || crypto.randomUUID();
  const cancelling = new Set();
  const statuses = { queued:'待派送', preparing:'准备中', searching:'正在搜索交换', trading:'交换中', completed:'已完成', failed:'派送失败', cancelled:'已取消' };
  $('species').value = saved.species || 'Metagross';
  $('quick-text').value = saved.quickText || '';
  $('team-text').value = saved.teamText || '';
  $('shiny').checked = Boolean(saved.shiny);
  function persist() {
    try { localStorage.setItem(storageKey, JSON.stringify({ mode, species:$('species').value, shiny:$('shiny').checked, quickText:$('quick-text').value, teamText:$('team-text').value, requestId })); } catch { /* Draft remains in memory. */ }
  }
  function edited() { requestId = crypto.randomUUID(); persist(); updateControls(); }
  function message(id, text, error = false) { const el = $(id); el.textContent = text; el.className = 'message ' + (error ? 'error' : 'success'); }
  async function api(path, method = 'GET', body) {
    const controller = new AbortController();
    const timer = setTimeout(() => controller.abort(), path === "/api/orders" && method === "POST" ? 180000 : 12000);
    try {
      const options = { method, signal:controller.signal, cache:'no-store' };
      if (method !== 'GET') { options.headers = { 'Content-Type':'application/json', 'X-SysBot-Request':'local-web' }; if (body !== undefined) options.body = JSON.stringify(body); }
      const response = await fetch(path, options);
      let data;
      try { data = await response.json(); } catch { throw new Error('服务没有返回可读取的数据，请检查本地服务后重试。'); }
      if (!response.ok) throw new Error([data.error || '请求未完成，请重试。', ...(Array.isArray(data.details) ? data.details : [])].join('\n'));
      return data;
    } catch (error) {
      if (error.name === 'AbortError') throw new Error('服务响应超时。输入已保留，请检查连接后重试。');
      if (error instanceof TypeError) throw new Error('无法连接本地服务。输入已保留，请检查服务后重试。');
      throw error;
    } finally { clearTimeout(timer); }
  }
  function teamCount(text) {
    return text.trim().split(/\n\s*\n/).filter(block => {
      const first = block.trim().split('\n')[0];
      return first && !/^(===|#|\/\/|(?:Ability|Level|Shiny|EVs|IVs|Tera Type|Happiness|Friendship|Gender|Ball|Language|OT|TID|SID):|[-.])/i.test(first);
    }).length;
  }
  function updateControls() {
    const count = teamCount($('team-text').value);
    $('send').textContent = mode === 'team' ? '一起派送' : '派送这一只';
    $('send').disabled = submitting || !online || (mode === 'quick' && !catalogReady);
    $('send').setAttribute('aria-busy', String(submitting));
    $('submit-hint').textContent = submitting ? '正在创建派送单，请稍候；无需再次点击。' : !online ? '连接本地服务后可创建派送单；输入会保留。' : mode === 'team' ? `识别到 ${count} 只，按顺序逐只派送。最终数量以服务校验为准；未连接 USB 时先排队等待。` : !catalogReady ? '宝可梦目录未就绪，请重新打开页面重试。' : '加入队列后自动尝试连接并派送；没有 USB 设备时先排队等待。';
    $('connect').disabled = connecting || !$('usb-port').value;
    $('connect').setAttribute('aria-busy', String(connecting));
    ['species','shiny','quick-text','team-text','example','tab-quick','tab-team'].forEach(id => { $(id).disabled = submitting; });
  }
  function setMode(next, focus = false) {
    mode = next;
    ['quick','team'].forEach(value => {
      const selected = value === mode;
      $('tab-' + value).setAttribute('aria-selected', String(selected));
      $('tab-' + value).tabIndex = selected ? 0 : -1;
      $(value + '-panel').hidden = !selected;
    });
    if (focus) $('tab-' + mode).focus();
    persist(); updateControls();
  }
  ['quick','team'].forEach(value => {
    $('tab-' + value).addEventListener('click', () => { if (mode !== value) { setMode(value); edited(); } });
    $('tab-' + value).addEventListener('keydown', event => {
      if (['ArrowLeft','ArrowRight','Home','End'].includes(event.key)) { event.preventDefault(); setMode(event.key === 'Home' ? 'quick' : event.key === 'End' ? 'team' : mode === 'quick' ? 'team' : 'quick', true); edited(); }
    });
  });
  function findSpecies(value) { const query = value.trim().toLowerCase(); return catalog.find(p => String(p.id).toLowerCase() === query || p.name.toLowerCase() === query || `${p.name} / ${p.id}`.toLowerCase() === query); }
  function applyShiny() {
    const lines = $('quick-text').value.replace(/\r/g, '').split('\n').filter(line => !/^\s*Shiny\s*:/i.test(line));
    if ($('shiny').checked) lines.splice(Math.min(1, lines.length), 0, 'Shiny: Yes');
    $('quick-text').value = lines.join('\n');
  }
  $('species').addEventListener('input', () => {
    const pokemon = findSpecies($('species').value);
    if (pokemon) { $('quick-text').value = pokemon.template; $('shiny').checked = /^\s*Shiny\s*:\s*Yes\s*$/im.test(pokemon.template); applyShiny(); $('catalog-message').textContent = `已载入 ${pokemon.name} 的默认配置。`; }
    else $('catalog-message').textContent = '输入中文或英文名称，从目录建议中选择。';
    edited();
  });
  $('shiny').addEventListener('change', () => { applyShiny(); edited(); });
  $('quick-text').addEventListener('input', () => { $('shiny').checked = /^\s*Shiny\s*:\s*Yes\s*$/im.test($('quick-text').value); edited(); });
  $('team-text').addEventListener('input', edited);
  $('example').addEventListener('click', () => { $('team-text').value = 'Pikachu\nAbility: Static\nLevel: 100\n- Thunderbolt\n\nEevee\nAbility: Adaptability\nLevel: 100\n- Quick Attack'; edited(); $('team-text').focus(); });
  async function loadCatalog() {
    try {
      const data = await api('/api/catalog');
      if (!Array.isArray(data.pokemon) || !data.pokemon.length) throw new Error('目录为空，请检查本地服务后重新打开页面。');
      catalog = data.pokemon; catalogReady = true;
      const fragment = document.createDocumentFragment();
      catalog.forEach(p => { const option = document.createElement('option'); option.value = `${p.name} / ${p.id}`; option.label = p.name; fragment.append(option); const english = document.createElement('option'); english.value = String(p.id); english.label = p.name; fragment.append(english); });
      $('species-list').replaceChildren(fragment);
      if (!saved.species) $('species').value = data.defaultSpecies || 'Metagross';
      const selected = findSpecies($('species').value);
      if (!saved.species && selected) $('species').value = `${selected.name} / ${selected.id}`;
      if (!$('quick-text').value && selected) { $('quick-text').value = selected.template; $('shiny').checked = /^\s*Shiny\s*:\s*Yes\s*$/im.test(selected.template); applyShiny(); }
      $('catalog-message').textContent = '可搜索中文或英文名称；默认配置已准备好。';
      persist();
    } catch (error) { $('catalog-message').textContent = error.message; }
    updateControls();
  }
  function renderQueue(orders) {
    const signature = JSON.stringify(orders) + online + [...cancelling].join(',');
    if (signature === queueSignature) return;
    queueSignature = signature;
    const focusedId = document.activeElement?.dataset?.orderId;
    const fragment = document.createDocumentFragment();
    orders.forEach(order => {
      const li = document.createElement('li'); li.className = 'order';
      const main = document.createElement('div'); main.className = 'order-main';
      const name = document.createElement('div'); name.className = 'order-name'; name.textContent = order.name || order.species || '宝可梦';
      const status = document.createElement('div'); status.className = 'order-status'; status.dataset.status = order.status; status.textContent = statuses[order.status] || '状态待确认';
      const detail = document.createElement('p'); detail.className = 'order-message'; detail.textContent = order.message || '';
      main.append(name, status, detail); li.append(main);
      if (order.status === 'queued') {
        const cancel = document.createElement('button'); cancel.type = 'button'; cancel.className = 'cancel'; cancel.textContent = '取消待派单'; cancel.dataset.orderId = String(order.id); cancel.setAttribute('aria-label', `取消 ${name.textContent} 的待派单`); cancel.disabled = !online || cancelling.has(String(order.id)); cancel.addEventListener('click', () => cancelOrder(String(order.id))); li.append(cancel);
      }
      fragment.append(li);
    });
    $('orders').replaceChildren(fragment);
    if (focusedId) [...$('orders').querySelectorAll('button')].find(button => button.dataset.orderId === focusedId)?.focus();
  }
  function applyState(data) {
    if (!data.device || !Array.isArray(data.orders)) throw new Error('设备状态暂不可读取，请检查服务。');
    state = data; online = true;
    const labels = { disconnected:'设备未连接', connecting:'正在连接设备', ready:'设备已就绪', error:'设备连接异常' };
    $('device-status').textContent = labels[data.device.status] || '设备状态待确认';
    $('device-status').dataset.status = data.device.status;
    $('device-status').title = data.device.message || '';
    $('port-summary').textContent = data.device.port == null ? '' : `端口 ${data.device.port}`;
    $('usb-message').textContent = data.device.message || '';
    const code = String(data.settings?.tradeCode || '03180318');
    $('trade-code').textContent = code.replace(/^(\d{4})(\d{4})$/, '$1 $2');
    $('queue-count').textContent = `${data.pending ?? data.orders.filter(o => ['queued','preparing','searching','trading'].includes(o.status)).length} 只待完成 · 共 ${data.orders.length} 单`;
    $('queue-empty').hidden = data.orders.length > 0;
    $('queue-empty').textContent = '还没有派送单。选一只宝可梦，或贴上队伍开始派送。';
    $('queue-note').textContent = '仅待派送的单可以取消；准备中、搜索中和交换中的单不能取消。';
    renderQueue(data.orders); updateControls();
  }
  function markOffline(error) {
    online = false;
    $('device-status').textContent = '连接中断'; $('device-status').dataset.status = 'offline'; $('device-status').title = error.message;
    $('queue-note').textContent = '连接中断，队列为上次读取的状态；正在自动重连，暂不能派送或取消。';
    if (!state) { $('queue-empty').textContent = '无法读取队列，正在自动重试。'; $('queue-count').textContent = '状态未知'; }
    if (state) renderQueue(state.orders);
    updateControls();
  }
  async function pollState() {
    if (stateBusy || mutationBusy) return;
    stateBusy = true;
    const version = stateVersion;
    try { const data = await api('/api/state'); if (version === stateVersion) applyState(data); }
    catch (error) { if (version === stateVersion) markOffline(error); }
    finally { stateBusy = false; }
  }
  async function loadUsb() {
    $('refresh-usb').disabled = true;
    try {
      const data = await api('/api/usb');
      const old = $('usb-port').value || String(state?.settings?.usbPort ?? '');
      const options = data.ports.map(port => { const option = document.createElement('option'); option.value = String(port); option.textContent = `端口 ${port}`; return option; });
      if (!options.length) { const option = document.createElement('option'); option.value = ''; option.textContent = '未发现 USB 设备'; options.push(option); }
      $('usb-port').replaceChildren(...options);
      if (data.ports.map(String).includes(old)) $('usb-port').value = old;
      $('usb-message').textContent = data.message || (data.ports.length ? '选择端口后连接设备。' : '连接 Switch 的 USB 数据线后刷新端口。');
    } catch (error) { $('usb-message').textContent = error.message; }
    finally { $('refresh-usb').disabled = false; updateControls(); }
  }
  $('usb-port').addEventListener('change', updateControls);
  $('refresh-usb').addEventListener('click', loadUsb);
  $('connect').addEventListener('click', async () => {
    if (connecting || !$('usb-port').value) return;
    connecting = true; mutationBusy++; stateVersion++; updateControls(); $('usb-message').textContent = '正在连接所选 USB 端口。';
    try { applyState(await api('/api/connect', 'POST', { port:Number($('usb-port').value) })); }
    catch (error) { $('usb-message').textContent = error.message; awaitRecovery(error); }
    finally { connecting = false; mutationBusy--; updateControls(); }
  });
  function awaitRecovery(error) { if (/无法连接|超时|没有返回/.test(error.message)) markOffline(error); }
  $('order-form').addEventListener('submit', async event => {
    event.preventDefault();
    if (submitting || !online) return;
    const text = $(mode === 'quick' ? 'quick-text' : 'team-text').value.trim();
    if (!text) { message('form-message', '请先选择宝可梦或贴入 Showdown 配置。', true); $(mode === 'quick' ? 'species' : 'team-text').focus(); return; }
    if (mode === 'quick' && !findSpecies($('species').value)) { message('form-message', '请从目录中选择中文或英文种类，再派送。', true); $('species').focus(); return; }
    submitting = true; stateVersion++; updateControls(); message('form-message', '');
    try {
      const result = await api('/api/orders', 'POST', { text, requestId });
      if (!Array.isArray(result.orders) || !Number.isInteger(result.count)) throw new Error('派送结果尚未确认。请保留输入并重试，系统会防止重复入队。');
      requestId = crypto.randomUUID(); persist();
      message('form-message', `已加入 ${result.count} 只到派送队列。使用固定交换码，收完一只后再次搜索。`);
    } catch (error) { message('form-message', error.message + '\n输入已保留；本次程序运行内，未编辑时重试不会重复入队。', true); awaitRecovery(error); }
    finally { submitting = false; updateControls(); pollState(); }
  });
  async function cancelOrder(id) {
    if (!online || cancelling.has(id) || state?.orders.find(order => String(order.id) === id)?.status !== 'queued') return;
    cancelling.add(id); mutationBusy++; stateVersion++; renderQueue(state.orders); message('queue-error', '');
    try { applyState(await api('/api/orders/' + encodeURIComponent(id), 'DELETE')); message('queue-error', '待派单已取消。如需恢复，请重新派送。'); }
    catch (error) { message('queue-error', error.message + '\n请查看最新状态；已开始的单不能取消。', true); awaitRecovery(error); }
    finally { cancelling.delete(id); mutationBusy--; if (state) renderQueue(state.orders); pollState(); }
  }
  $('copy-code').addEventListener('click', async () => {
    const code = $('trade-code').textContent.replace(/\s/g, '');
    try { await navigator.clipboard.writeText(code); $('copy-message').textContent = '交换码已复制。'; }
    catch { $('copy-message').textContent = '无法自动复制，请选中交换码手动复制。'; const range = document.createRange(); range.selectNodeContents($('trade-code')); const selection = window.getSelection(); selection.removeAllRanges(); selection.addRange(range); }
  });
  setMode(mode); loadCatalog(); pollState(); loadUsb();
  setInterval(pollState, 1000);
})();
