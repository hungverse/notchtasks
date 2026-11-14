'use strict';
// The Control Panel (Sources/Preferences.swift, SettingsView.swift, BadgesPane.swift). Every page is built from state
// pushed by main; a change goes out as nt.act('setSetting', key, value) and the local copy is updated at once.
(function () {
  const nt = window.nt;
  const U = window.NTUI;
  const { h, fill, icon, toggle, select, segmented, modal, menu, NotchBadge, NotchPreview, BadgeEditor, BadgeBoard, spriteEl, syncSprites, accentHex } = U;
  const L = (k, ...a) => nt.L(k, ...a);
  const CAT = nt.catalog();
  U.setCatalog(CAT);

  // Sidebar, in the Mac's order. Fans, permissions and the lock screen are Mac-only and not listed.
  const PANES = [
    { id: 'overview', title: 'Tổng quan', icon: 'layout-grid' },
    { id: 'modules', title: 'Mô-đun', icon: 'puzzle' },
    { id: 'agents', title: 'Theo dõi', icon: 'eye' },
    { id: 'phone', title: 'Ghép nối', icon: 'link' },
    { id: 'appearance', title: 'Giao diện', icon: 'paintbrush' },
    { id: 'badges', title: 'Biểu tượng', icon: 'star' },
    { id: 'store', title: 'Kho biểu tượng', icon: 'sparkles' },
    { id: 'sounds', title: 'Âm thanh', icon: 'volume-2' },
    { id: 'behavior', title: 'Hành vi', icon: 'hand' },
  ];
  const AGENT_NOTES = {
    claude: 'Duyệt lệnh, sửa file, trả lời câu hỏi', gemini: 'Duyệt lệnh, sửa file, trả lời câu hỏi',
    antigravity: 'Duyệt lệnh, sửa file, trả lời câu hỏi', codex: 'Duyệt lệnh, sửa file',
    cursor: 'Từ chối lệnh. Cho phép chỉ khi Cursor đang tự chạy lệnh', opencode: 'Duyệt lệnh, sửa file, truy cập web',
  };
  const PLACE_LABEL = { auto: 'Tự động', left: 'Bên trái', right: 'Bên phải' };
  const BG_LABEL = { black: 'Đen', glass: 'Kính mờ', gradient: 'Gradient', glow: 'Viền phát sáng' };
  const TOAST_LABEL = { island: 'Đảo', compact: 'Gọn', card: 'Thẻ nổi', banner: 'Dải ngang' };
  const TOAST_BLURB = { island: 'Trượt xuống từ notch.', compact: 'Nằm gọn trên notch.', card: 'Thẻ nổi dưới notch.', banner: 'Rộng, chữ lớn.' };
  const MOTION_LABEL = { smooth: 'Mượt', bouncy: 'Nảy', snappy: 'Nhanh', reduced: 'Tối thiểu' };
  const THEME_ID = (t) => t.id;

  let state = null;
  let lastSig = '';
  let pane = 'overview';
  let dragMod = null;
  let dragItem = null;
  let showPhoneCode = false;
  let storeCat = '';
  let storeQuery = '';
  let justAdded = null;

  const S = () => state.settings;
  const isWin = nt.platform === 'win32';
  let reportDialog = null;
  const sig = () => JSON.stringify(state);

  // ---- state in and actions out ---------------------------------------------------------------------------------------

  /** Local change: the page moves at once, main gets the value and pushes the echo. */
  function set(key, value, { rerender = true } = {}) {
    state = { ...state, settings: { ...state.settings, [key]: value } };
    nt.act('setSetting', key, value);
    U.setSettingsForAccent(state.settings);
    lastSig = sig();
    if (rerender) render();
  }

  function setItems(items) {
    state = { ...state, items };
    nt.act('setItems', items);
    lastSig = sig();
    render();
  }

  // ---- building blocks ------------------------------------------------------------------------------------------------

  function card(title, sub, ...kids) {
    return h('section', { class: 'card' },
      title ? h('div', { class: 'card-h' }, h('div', { class: 'card-t' }, title), sub ? h('div', { class: 'card-s' }, sub) : null) : null,
      ...kids);
  }

  /** One setting row: label (and an optional caption) on the left, the control on the right. */
  function row(label, control, caption) {
    return h('div', { class: 'srow' },
      h('div', { class: 'srow-t' }, h('div', { class: 'srow-l' }, label), caption ? h('div', { class: 'srow-c dim' }, caption) : null),
      h('div', { class: 'srow-c2' }, control));
  }

  function group(...rows) {
    const kids = [];
    rows.forEach((r, i) => { if (i) kids.push(h('div', { class: 'sep' })); kids.push(r); });
    return h('div', { class: 'group' }, kids);
  }

  function button(label, onClick, opts = {}) {
    return h('button', { class: `btn${opts.primary ? ' primary' : ''}${opts.danger ? ' danger' : ''}${opts.small ? ' sm' : ''}`,
      type: 'button', disabled: opts.disabled, onclick: onClick }, opts.icon ? icon(opts.icon, 14) : null, label);
  }

  function slider(value, min, max, step, onInput, onChange) {
    const out = h('span', { class: 'slider-v dim' });
    const input = h('input', { type: 'range', min, max, step, value });
    const show = (v) => { out.textContent = v; };
    input.addEventListener('input', () => { onInput(Number(input.value)); show(input.value); });
    input.addEventListener('change', () => { if (onChange) onChange(Number(input.value)); });
    return { el: h('div', { class: 'slider' }, input, out), show, input };
  }

  function stat(iconName, value, label) {
    return h('div', { class: 'stat' }, icon(iconName, 16, 'stat-i'), h('div', { class: 'stat-v' }, String(value)), h('div', { class: 'stat-l dim' }, label));
  }

  function tileIcon(name, on) {
    return h('span', { class: `mtile${on ? ' on' : ''}` }, icon(name, 16));
  }

  // ---- overview -------------------------------------------------------------------------------------------------------

  /** The notch on a desktop-like tile, so its top edge reads as the screen's edge. */
  function wall(node) { return h('div', { class: 'wall' }, node); }

  function nextSteps() {
    const out = [];
    for (const a of state.agents) if (a.present && !a.installed) out.push({ kind: 'agent', agent: a });
    if (!state.sync.phones.length) out.push({ kind: 'phone' });
    return out;
  }

  function overview() {
    const s = S();
    const live = state.live || { working: 0, alerts: 0 };
    const total = CAT.modules.length;
    const enabled = CAT.modules.filter((m) => !s.disabled.includes(m.id)).length;
    const steps = nextSteps();
    return [
      h('div', { class: 'hero' },
        wall(NotchPreview(s, live, {})),
        h('div', { class: 'hero-text' }, h('div', { class: 'hero-t' }, 'NotchTasks'),
          h('div', { class: 'dim' }, L('Notch hiện những gì đang chạy, và im lặng khi không có gì.')))),
      h('div', { class: 'stats' }, stat('zap', live.working, L('Đang chạy')), stat('octagon-x', live.alerts, L('Lỗi CI')),
        stat('puzzle', `${enabled}/${total}`, L('Mô-đun'))),
      s.onboardingDone && steps.length ? h('div', { class: 'block' }, h('div', { class: 'sec-t' }, L('Tiếp theo')),
        steps.map((st) => h('div', { class: 'step' },
          icon(st.kind === 'agent' ? 'sparkles' : 'smartphone', 16, 'dim'),
          h('div', { class: 'step-t' }, st.kind === 'agent' ? `${st.agent.name} · ${L('Trả lời agent trên notch')}` : L('Ghép nối')),
          button(st.kind === 'agent' ? L('Bật') : L('Ghép nối'), () => go(st.kind === 'agent' ? 'agents' : 'phone'), { small: true })))) : null,
      h('div', { class: 'sec-t' }, L('Mô-đun')),
      CAT.moduleGroups.map((g) => h('div', { class: 'block' }, h('div', { class: 'grp-t' }, L(g.title)),
        h('div', { class: 'grid2' }, g.ids.map((id) => moduleCell(id))))),
      h('div', { class: 'row gap wrap' },
        button(L('Xem hướng dẫn'), () => nt.act('openOnboarding')),
        button(L('Báo lỗi'), openReport)),
    ];
  }

  function moduleCell(id) {
    const m = CAT.modules.find((x) => x.id === id);
    const on = !S().disabled.includes(id);
    return h('div', { class: 'mcell' }, tileIcon(m.icon, on),
      h('div', { class: `mcell-t${on ? '' : ' off'}` }, L(m.title)),
      toggle(on, (v) => setModuleOn(id, v), L(m.title)));
  }

  function setModuleOn(id, on) {
    const disabled = S().disabled.filter((x) => x !== id);
    if (!on) disabled.push(id);
    set('disabled', disabled);
  }

  function go(id) { pane = id; render(); }

  // ---- modules --------------------------------------------------------------------------------------------------------

  function modules() {
    const s = S();
    const reorder = (from, to) => {
      if (!from || from === to) return;
      const list = s.modules.filter((x) => x !== from);
      list.splice(list.indexOf(to), 0, from);
      set('modules', list);
    };
    return [
      h('div', { class: 'hint-row' },
        h('span', { class: 'dim' }, L('Kéo để đổi thứ tự trên notch.')),
        h('span', { class: 'cols' }, h('span', { class: 'col w-pl' }, L('Vị trí khi thu gọn')), h('span', { class: 'col w-col' }, L('Màu')),
          h('span', { class: 'col w-tg' }, L('Bật')))),
      h('div', { class: 'list' }, s.modules.map((id) => {
        const m = CAT.modules.find((x) => x.id === id);
        if (!m) return null;
        const on = !s.disabled.includes(id);
        const color = s.moduleColors[id] || '#808080';
        const colorInput = h('input', { type: 'color', class: 'color', value: color, title: L('Màu'),
          oncontextmenu: (e) => { e.preventDefault(); const map = { ...s.moduleColors }; delete map[id]; set('moduleColors', map); },
          onchange: (e) => set('moduleColors', { ...s.moduleColors, [id]: e.target.value.toUpperCase() }) });
        return h('div', { class: 'mrow', draggable: 'true',
          ondragstart: (e) => { dragMod = id; e.dataTransfer.setData('text/plain', id); e.dataTransfer.effectAllowed = 'move'; },
          ondragover: (e) => e.preventDefault(),
          ondrop: (e) => { e.preventDefault(); reorder(dragMod, id); dragMod = null; } },
        h('span', { class: 'grip' }, icon('grip-vertical', 14)),
        tileIcon(m.icon, on),
        h('span', { class: 'mtext' }, h('span', { class: `mt${on ? '' : ' off'}` }, L(m.title)), h('span', { class: 'ms dim' }, L(m.blurb))),
        select(['auto', 'left', 'right'].map((v) => ({ value: v, label: L(PLACE_LABEL[v]) })), s.placements[id] || 'auto',
          (v) => set('placements', { ...s.placements, [id]: v }), L('Vị trí khi thu gọn')),
        colorInput,
        toggle(on, (v) => setModuleOn(id, v), L(m.title)));
      })),
    ];
  }

  // ---- watch list, agents, build tools ------------------------------------------------------------------------------

  function itemSymbol(item) {
    return item.symbolName || (item.kind === 'dev' ? 'hammer' : item.kind === 'app' ? 'app-window' : 'terminal');
  }

  function itemIcon(item, size = 26) {
    if (item.icon) return NotchBadge(item.icon, size, accentHex(S()));
    return icon(itemSymbol(item), size * 0.6, 'item-ico');
  }

  function addPreset(p) {
    const item = { ...p, id: crypto.randomUUID() };
    setItems([...state.items, item]);
  }

  function presetAdded(p) {
    return state.items.some((i) => i.kind === p.kind && i.process === p.process && i.args === p.args && i.bundleID === p.bundleID);
  }

  function addMenu(anchor) {
    const sections = CAT.presetGroups.map((g) => ({ title: L(g.title), items: g.items.map((p) => ({
      label: p.name + (presetAdded(p) ? L(' (đã thêm)') : ''), disabled: presetAdded(p), onClick: () => addPreset(p) })) }));
    sections.push({ title: L('Build & CI'), items: CAT.buildPresets.map((p) => ({ label: p.name + (presetAdded(p) ? L(' (đã thêm)') : ''),
      disabled: presetAdded(p), onClick: () => addPreset(p) })) });
    sections.push({ title: L('Ứng dụng đang mở'), empty: L('Không có ứng dụng nào'), items: (state.running || []).map((r) => ({
      label: r.name, onClick: () => setItems([...state.items, { id: crypto.randomUUID(), name: r.name, kind: 'app', bundleID: r.bundleID, enabled: true }]) })) });
    sections.push({ items: [
      { label: L('Chọn ứng dụng khác…'), onClick: () => nt.act('pickApp') },
      { label: L('Tự nhập tiến trình…'), onClick: () => openItemEditor(null) }] });
    menu(anchor, sections);
  }

  function agentNote(a) { return L(AGENT_NOTES[a.kind] || 'Duyệt lệnh, sửa file'); }

  function watch() {
    const s = S();
    const itemRows = state.items.map((item) => h('div', { class: 'irow', draggable: 'true',
      ondragstart: (e) => { dragItem = item.id; e.dataTransfer.setData('text/plain', item.id); },
      ondragover: (e) => e.preventDefault(),
      ondrop: (e) => {
        e.preventDefault();
        if (!dragItem || dragItem === item.id) return;
        const list = state.items.slice();
        const from = list.findIndex((x) => x.id === dragItem);
        const [moved] = list.splice(from, 1);
        list.splice(list.findIndex((x) => x.id === item.id), 0, moved);
        dragItem = null;
        setItems(list);
      } },
    grip(), itemIcon(item), h('div', { class: 'itext' }, h('div', { class: 'it' }, item.name),
      h('div', { class: 'is dim' }, (nt.itemIsBuild(item) ? L('Build & CI') : L('AI agent')) + ' · ' + nt.itemSummary(item))),
    h('span', { class: 'sp' }),
    button(L('Sửa'), () => openItemEditor(item), { small: true }),
    toggle(item.enabled, (v) => setItems(state.items.map((x) => (x.id === item.id ? { ...x, enabled: v } : x))), L('Bật')),
    h('button', { class: 'icon-btn', type: 'button', title: L('Xoá'), 'aria-label': L('Xoá'),
      onclick: () => setItems(state.items.filter((x) => x.id !== item.id)) }, icon('trash-2', 14))));

    const agents = state.agents.map((a) => {
      const setup = a.setup && a.installed ? h('span', { class: 'warn' }, L(a.setup.missing), button(L(a.setup.button),
        () => nt.act('openSetup', a.kind), { small: true })) : null;
      return h('div', { class: `arow${a.present ? '' : ' absent'}` },
        icon('bot', 22, 'dim'),
        h('div', { class: 'itext' }, h('div', { class: 'it' }, a.name,
          h('span', { class: `chip ${a.tested ? 'ok' : ''}` }, L(a.tested ? 'Đã kiểm tra' : 'Chưa kiểm tra'))),
        h('div', { class: 'is dim' }, a.present ? agentNote(a) : L('Chưa thấy trên máy này'))),
        h('span', { class: 'sp' }),
        setup,
        a.present && a.installed && !a.setup ? h('span', { class: 'ok-t' }, icon('circle-check', 14), L('Sẵn sàng')) : null,
        toggle(a.installed, (on) => {
          state = { ...state, agents: state.agents.map((x) => (x.kind === a.kind ? { ...x, installed: on } : x)) };
          nt.act(on ? 'installHook' : 'uninstallHook', a.kind);
          render();
        }, a.name));
    });

    return [
      h('p', { class: 'dim lead' }, L('Những gì notch theo dõi. Chỉ cái đang làm việc mới hiện lên notch.')),
      h('div', { class: 'list' }, itemRows.length ? itemRows : h('div', { class: 'empty dim' }, L('Chưa có mục nào.'))),
      h('div', { class: 'row gap' }, button(L('Thêm mục theo dõi'), (e) => addMenu(e.currentTarget), { icon: 'circle-plus' }),
        h('span', { class: 'dim small' }, L('Chọn agent, ứng dụng đang mở, hoặc nhập tên tiến trình.'))),
      card(L('Trả lời agent trên notch'), L('Khi agent hỏi, notch hiện sẵn nút trả lời.'),
        h('div', { class: 'list' }, agents),
        state.agentError ? h('div', { class: 'err' }, state.agentError) : null,
        h('div', { class: 'dim small pt' }, L('Có hiệu lực từ lần mở agent tiếp theo.'))),
      buildTools(),
    ];
  }

  function grip() { return h('span', { class: 'grip' }, icon('grip-vertical', 14)); }

  let showAllTools = false;
  function buildTools() {
    const s = S();
    const tools = showAllTools ? [...new Set([...s.seenTools, ...CAT.knownTools])] : s.seenTools;
    return card('Build & CI', null,
      h('div', { class: 'row between' }, h('span', { class: 'dim small' },
        s.seenTools.length || showAllTools ? L('Không cần thêm: công cụ nào chạy là hiện. Bấm một công cụ để đổi biểu tượng.')
          : L('Chưa thấy lần build nào. Bật "Hiện tất cả" để đặt biểu tượng trước.'))),
      h('div', { class: 'row between' }, h('span', { class: 'dim small' }, ''),
        h('label', { class: 'check' }, h('input', { type: 'checkbox', checked: showAllTools,
          onchange: (e) => { showAllTools = e.target.checked; render(); } }), L('Hiện tất cả công cụ'))),
      h('div', { class: 'tools' }, tools.map((t) => {
        const custom = s.toolIcons[t];
        return h('button', { class: 'tool', type: 'button', onclick: () => openToolEditor(t) },
          custom ? NotchBadge(custom, 20, accentHex(s)) : icon('hammer', 16, 'dim'),
          h('span', { class: 'tool-t' }, t),
          custom ? h('span', { class: 'dot' }) : null);
      })));
  }

  function openToolEditor(tool) {
    const cur = S().toolIcons[tool] || { id: crypto.randomUUID(), symbol: 'hammer.fill', shape: 'rounded', border: false, right: false };
    BadgeEditor({ badge: cur, title: L('Biểu tượng'), subtitle: tool,
      onChange: (b) => set('toolIcons', { ...S().toolIcons, [tool]: b }),
      onReset: () => { const map = { ...S().toolIcons }; delete map[tool]; set('toolIcons', map); } });
  }

  // The item editor: name, kind, the process, CPU threshold, and what it matches right now.
  function openItemEditor(item) {
    const isNew = !item;
    let it = item ? { ...item } : { id: crypto.randomUUID(), name: '', kind: 'process', process: '', args: null, childTasks: false, enabled: true };
    const configurable = it.kind === 'process' || it.kind === 'app';
    const threshold = () => (it.threshold ?? (it.kind === 'app' ? 5 : it.childTasks ? 0.5 : 1.5));
    const valid = () => it.name.trim() && (it.kind !== 'process' || it.process.trim() || (it.args || '').trim());
    const body = h('div', { class: 'stack' });
    const save = h('button', { class: 'btn primary', type: 'button', onclick: () => {
      const clean = { ...it, name: it.name.trim(), process: (it.process || '').trim() };
      setItems(isNew ? [...state.items, clean] : state.items.map((x) => (x.id === clean.id ? clean : x)));
      dlg.close();
    } }, isNew ? L('Thêm') : L('Lưu'));
    const refresh = () => { save.disabled = !valid(); };

    function draw() {
      const iconBtn = h('button', { class: 'icon-pick', type: 'button', title: L('Đổi biểu tượng'), onclick: () => {
        BadgeEditor({ badge: it.icon || { id: crypto.randomUUID(), symbol: itemSymbol(it), shape: 'rounded', border: false, right: false },
          title: L('Biểu tượng'), subtitle: it.name || null,
          onChange: (b) => { it = { ...it, icon: b }; draw(); },
          onReset: () => { it = { ...it, icon: null }; draw(); } });
      } }, itemIcon(it, 30));
      const matches = state.matches && state.matches.id === it.id ? state.matches.rows : null;
      fill(body, [
        h('div', { class: 'ie-head' }, iconBtn, h('div', { class: 'itext' }, h('div', { class: 'it' }, it.name || L(isNew ? 'Thêm mục theo dõi' : 'Mục theo dõi')),
          h('div', { class: 'is dim' }, it.inBuild ? 'Build & CI' : 'AI agent'))),
        h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Tên hiển thị')),
          h('input', { class: 'inp', value: it.name, placeholder: 'vd: My Agent', oninput: (e) => { it.name = e.target.value; refresh(); } })),
        configurable ? h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Hiện trong')),
          segmented([{ value: false, label: 'AI agent' }, { value: true, label: 'Build & CI' }], !!it.build,
            (v) => { it = { ...it, build: v }; draw(); })) : null,
        it.kind === 'process' ? h('div', { class: 'block' },
          h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Tên tiến trình')),
            h('span', { class: 'row gap' }, h('input', { class: 'inp', value: it.process, placeholder: 'vd: node',
              oninput: (e) => { it.process = e.target.value; refresh(); } }))),
          h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Dòng lệnh có chứa')),
            h('input', { class: 'inp', value: it.args || '', placeholder: L('tuỳ chọn, vd: gemini-cli'),
              oninput: (e) => { it.args = e.target.value || null; refresh(); } })),
          h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Mỗi dòng trên notch là')),
            segmented([{ value: false, label: L('Mỗi lần chạy') }, { value: true, label: L('Mỗi lệnh con (runner, worker)') }],
              !!it.childTasks, (v) => { it = { ...it, childTasks: v }; draw(); }))) : null,
        configurable ? h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Đang làm việc khi CPU trên')),
          slider(threshold(), 0.5, 50, 0.5, (v) => { it = { ...it, threshold: v }; }, null).el) : null,
        it.kind === 'process' ? h('div', { class: 'field' }, h('span', { class: 'field-l' }, L('Đang khớp: {0}', matches ? matches.length : 0)),
          h('div', { class: 'matches' }, matches && matches.length ? matches.map((m) => h('div', { class: 'mt-row' },
            h('div', { class: 'mono' }, `${m.name} · pid ${m.pid}`), h('div', { class: 'mono dim small' }, m.command))) : h('div', { class: 'dim small' },
            L('Chưa thấy. Mở công cụ rồi bấm Kiểm tra lại.'))),
          h('div', { class: 'row gap' }, button(L('Kiểm tra lại'), () => nt.act('checkItem', it), { small: true }))) : null]);
      refresh();
    }
    draw();
    const dlg = modal({ width: 520, title: null, body: h('div', { class: 'ie' }, body),
      footer: [button(L('Huỷ'), () => dlg.close()), save] });
    if (it.kind === 'process') nt.act('checkItem', it);
  }

  // ---- phone ----------------------------------------------------------------------------------------------------------

  function proStatus(until) {
    if (!until || until <= Date.now()) return L('Chưa có Pro: một điện thoại, thông báo cơ bản');
    if (until >= 30_000_000_000_000) return L('Pro trọn đời');
    return L('Pro đến {0}', new Date(until).toLocaleDateString());
  }

  function qrBlock(url) {
    const img = h('img', { class: 'qr', alt: 'QR', src: '' });
    nt.qr(url).then((src) => { img.src = src; });
    return h('div', { class: 'qr-box' }, img);
  }

  function copy(text) { navigator.clipboard.writeText(text).catch(() => {}); }

  function phone() {
    const sy = state.sync;
    const hasPhones = sy.phones.length > 0;
    const pairing = (sy.url && !hasPhones) || (hasPhones && showPhoneCode);
    return [
      card(null, null,
        h('div', { class: 'pro-t' }, proStatus(sy.proUntil)),
        h('div', { class: 'dim' }, L('Trả lời agent và xem việc đang chạy ngay trên điện thoại. Dữ liệu được mã hoá giữa các thiết bị.'))),
      !hasPhones ? card(null, null,
        h('ol', { class: 'steps' }, h('li', {}, L('Cài NotchTasks trên điện thoại')), h('li', {}, L('Bấm Ghép thiết bị')),
          h('li', {}, L('Quét mã QR bằng app hoặc Camera')))) : null,
      pairing ? card(null, null,
        h('div', { class: 'pair' }, qrBlock(sy.url), h('div', { class: 'pair-text' },
          h('div', {}, L('Mở NotchTasks trên điện thoại → Ghép với máy tính, rồi quét mã này.')),
          h('div', { class: 'dim small' }, L('Mã này chỉ dùng cho các thiết bị của bạn.')),
          h('div', { class: 'row gap wrap' }, button(L('Sao chép mã'), () => copy(sy.url), { small: true }),
            hasPhones ? button(L('Ẩn mã'), () => { showPhoneCode = false; render(); }, { small: true }) : null)))) : null,
      !pairing ? h('div', { class: 'row gap' }, button(sy.busy ? L('Đang tạo mã…') : (hasPhones ? L('Ghép thêm thiết bị…') : L('Ghép thiết bị…')),
        () => { if (hasPhones) { showPhoneCode = true; render(); } if (!sy.url) nt.act('pair'); }, { primary: true, disabled: !!sy.busy })) : null,
      sy.problem ? h('div', { class: 'warn' }, sy.problem) : null,
      h('div', { class: 'row gap' }, button(L('Báo lỗi'), openReport, { small: true })),
      hasPhones ? group(
        row(L('Nối trực tiếp khi cùng Wi‑Fi'), toggle(sy.lan, (v) => { state = { ...state, sync: { ...sy, lan: v } }; nt.act('setSyncOption', 'lan', v); render(); })),
        row(L('Chỉ báo điện thoại khi tôi rời máy'), toggle(sy.onlyAway, (v) => { state = { ...state, sync: { ...sy, onlyAway: v } }; nt.act('setSyncOption', 'onlyAway', v); render(); })),
        row(L('Đang nối'), h('span', { class: 'dim' }, sy.direct > 0 ? L('{0} thiết bị qua Wi‑Fi', sy.direct) : L('Qua Internet')))) : null,
      hasPhones ? h('p', { class: 'dim small' }, L('Cùng Wi‑Fi thì điện thoại nhận ngay, khác mạng thì qua Internet. Khi bạn đang dùng máy này, điện thoại không reo.')) : null,
      hasPhones ? card(L('Đã ghép'), L('Mọi thiết bị sẽ ngắt khỏi máy này. Ghép lại sẽ dùng mã mới.'),
        h('div', { class: 'list' }, sy.phones.map((p) => h('div', { class: 'prow' }, icon('smartphone', 16, 'dim'), h('span', { class: 'it' }, p.name),
          h('span', { class: 'sp' }), p.pro ? h('span', { class: 'pro' }, 'Pro') : null,
          h('button', { class: 'icon-btn', type: 'button', title: L('Huỷ ghép'), 'aria-label': L('Huỷ ghép'),
            onclick: () => nt.act('unpair', p.uid) }, icon('x', 14))))),
        h('div', { class: 'row gap' }, button(L('Huỷ ghép tất cả'), () => nt.act('unpairAll'), { danger: true, small: true }))) : null,
    ];
  }

  // ---- appearance -----------------------------------------------------------------------------------------------------

  function themeTile(t) {
    const s = S();
    const selected = s.background === t.background && s.toastStyle === t.toastStyle && s.accent === t.accent &&
      s.motion === t.motion && (t.accent !== 'custom' || s.customAccent === t.customAccent);
    const accent = t.accent === 'custom' ? t.customAccent : (CAT.accents.find((a) => a.name === t.accent) || {}).hex || '#0A84FF';
    const bg = t.background === 'gradient' ? `linear-gradient(135deg, ${t.gradientA}, ${t.gradientB})`
      : t.background === 'glass' ? 'rgba(90,90,90,.85)' : '#1f1f1f';
    return h('button', { class: `theme${selected ? ' on' : ''}`, type: 'button', onclick: () => {
      nt.act('applyTheme', THEME_ID(t));
      const patch = { background: t.background, gradientA: t.gradientA, gradientB: t.gradientB, accent: t.accent,
        customAccent: t.customAccent, toastStyle: t.toastStyle, motion: t.motion };
      state = { ...state, settings: { ...state.settings, ...patch } };
      lastSig = sig();
      render();
    } },
    h('span', { class: 'theme-art', style: { background: bg } },
      h('span', { class: `theme-notch${t.background === 'glow' ? ' glow' : ''}`, style: { '--acc': accent } },
        h('i', { style: { background: accent } }), h('b', { style: { background: accent } }))),
    h('span', { class: 'theme-t' }, h('span', {}, L(t.title)), selected ? icon('circle-check', 14, 'acc') : null));
  }

  function appearance() {
    const s = S();
    return [
      h('div', { class: 'hero' }, wall(NotchPreview(s, state.live, {})), h('span', { class: 'sp' }),
        button(L('Thử thông báo'), () => nt.act('testToast'), { primary: true, icon: 'bell-ring' })),
      h('div', { class: 'themes' }, CAT.themes.map(themeTile)),
      card('Notch', isWin ? L('Trên máy tính này notch là viên thuốc nổi ở giữa đỉnh màn hình.') : null,
        group(
          row(L('Nền'), select(CAT.backgrounds.map((v) => ({ value: v, label: L(BG_LABEL[v]) })), s.background, (v) => set('background', v))),
          s.background === 'gradient' ? row(L('Màu trên'), colorInput(s.gradientA, (v) => set('gradientA', v))) : null,
          s.background === 'gradient' ? row(L('Màu dưới'), colorInput(s.gradientB, (v) => set('gradientB', v))) : null,
          row(L('Kích thước'), segmented([{ value: 'compact', label: L('Gọn') }, { value: 'regular', label: L('Lớn') }], s.size, (v) => set('size', v))),
          row(L('Chuyển động'), segmented(CAT.motions.map((v) => ({ value: v, label: L(MOTION_LABEL[v]) })), s.motion, (v) => set('motion', v))))),
      card(L('Thông báo'), L(TOAST_BLURB[s.toastStyle]),
        group(row(L('Kiểu'), segmented(CAT.toastStyles.map((v) => ({ value: v, label: L(TOAST_LABEL[v]) })), s.toastStyle, (v) => set('toastStyle', v))))),
      card(L('Màu'), null,
        group(
          row(L('Màu nhấn'), select(CAT.accents.map((a) => ({ value: a.name, label: L(a.title) })), s.accent, (v) => set('accent', v))),
          s.accent === 'custom' ? row(L('Màu tự chọn'), colorInput(s.customAccent, (v) => set('customAccent', v))) : null)),
      card(L('Sao lưu cấu hình'), L('Toàn bộ tuỳ chỉnh trong một file .json.'),
        h('div', { class: 'row gap' }, button(L('Xuất…'), () => nt.act('exportProfile'), { small: true }),
          button(L('Nhập…'), () => nt.act('importProfile'), { small: true }))),
    ];
  }

  function colorInput(value, onChange) {
    return h('input', { type: 'color', class: 'color', value, onchange: (e) => onChange(e.target.value.toUpperCase()) });
  }

  // ---- badges and icon store ------------------------------------------------------------------------------------------

  function screenStrip(badges) {
    const s = S();
    const left = badges.filter((b) => !b.right);
    const right = badges.filter((b) => b.right);
    const size = s.size === 'regular' ? 20 : 16;
    return h('div', { class: 'desk' }, h('div', { class: 'notch-strip' },
      h('div', { class: 'ns-side' }, left.map((b) => NotchBadge(b, size, accentHex(s)))),
      h('span', { class: 'ns-cam' }),
      h('div', { class: 'ns-side' }, right.map((b) => NotchBadge(b, size, accentHex(s))))));
  }

  function openBadge(id) {
    const b = S().badges.find((x) => x.id === id);
    if (!b) return;
    const save = (nb) => set('badges', S().badges.map((x) => (x.id === nb.id ? nb : x)), { rerender: true });
    BadgeEditor({ badge: b, title: L('Biểu tượng cố định'), subtitle: b.right ? L('Bên phải notch') : L('Bên trái notch'),
      onChange: save, onDelete: () => set('badges', S().badges.filter((x) => x.id !== id)) });
  }

  function addBadge(right) {
    const s = S();
    const count = s.badges.filter((b) => b.right === right).length;
    if (count >= CAT.perSide) return;
    const b = { id: crypto.randomUUID(), symbol: 'star', shape: 'circle', border: false, right };
    set('badges', [...s.badges, b]);
    openBadge(b.id);
  }

  function badges() {
    const s = S();
    const board = BadgeBoard(s.badges, {
      perSide: CAT.perSide,
      onChange: (list) => set('badges', list),
      onEdit: ({ id, right }) => { if (id) openBadge(id); else addBadge(right); },
      onStore: () => go('store'),
    });
    return [card(L('Màn hình chính'), L('Biểu tượng luôn hiện trên notch thu nhỏ.'),
      screenStrip(s.badges), board)];
  }

  function store() {
    const s = S();
    const cats = [...new Set(CAT.sprites.map((x) => x.category).filter((c) => c !== 'pets'))];
    const q = storeQuery.trim().toLowerCase();
    const shown = CAT.sprites.filter((x) => x.category !== 'pets' && (!storeCat || x.category === storeCat)
      && (!q || L(x.title).toLowerCase().includes(q) || x.category.toLowerCase().includes(q)));
    const used = (id) => s.badges.some((b) => b.sprite === id) || state.items.some((i) => i.icon && i.icon.sprite === id)
      || Object.values(s.toolIcons).some((b) => b.sprite === id);
    const search = h('input', { class: 'inp narrow', value: storeQuery, placeholder: L('Tìm'),
      oninput: (e) => { storeQuery = e.target.value; render(); } });
    return [
      h('div', { class: 'store-head' }, h('div', { class: 'store-t' }, L('Có sẵn')),
        h('span', { class: 'dim small' }, L('{0} biểu tượng động · Miễn phí', CAT.sprites.filter((x) => x.category !== 'pets').length)),
        h('span', { class: 'sp' }), search,
        h('button', { class: 'icon-btn', type: 'button', title: L('Cho mọi biểu tượng động chạy lại từ đầu cùng lúc'),
          onclick: () => syncSprites(document) }, icon('refresh-cw', 14))),
      h('div', { class: 'chips' }, h('button', { class: `chip-b${storeCat === '' ? ' on' : ''}`, type: 'button', onclick: () => { storeCat = ''; render(); } }, L('Tất cả')),
        cats.map((c) => h('button', { class: `chip-b${storeCat === c ? ' on' : ''}`, type: 'button', onclick: () => { storeCat = c; render(); } }, L(c)))),
      shown.length ? h('div', { class: 'sprite-tiles' }, shown.map((x) => h('button', {
        class: `stile${justAdded === x.id ? ' added' : ''}`, type: 'button', title: L(x.title),
        onclick: (e) => storeMenu(e.currentTarget, x) },
      h('span', { class: 'stile-art' }, spriteEl(nt.spriteURL(x.id), x.frames, x.fps, 40, x.pixel), used(x.id) ? h('span', { class: 'dot' }) : null),
      h('span', { class: 'stile-t' }, L(x.title))))) : h('div', { class: 'empty dim' }, L('Không có biểu tượng nào khớp “{0}”.', storeQuery)),
      h('div', { class: 'dim small' }, icon('users', 14), ' ', L('Gói của cộng đồng: sắp có.')),
    ];
  }

  function storeMenu(anchor, sprite) {
    const s = S();
    const icon2 = { id: crypto.randomUUID(), symbol: 'star', sprite: sprite.id, plain: true, shape: 'circle', border: false, right: false };
    const flash = () => { justAdded = sprite.id; render(); setTimeout(() => { justAdded = null; render(); }, 1600); };
    const full = (right) => s.badges.filter((b) => b.right === right).length >= CAT.perSide;
    menu(anchor, [
      { title: L('Biểu tượng cố định trên notch'), items: [
        { label: L('Thêm bên trái'), disabled: full(false), onClick: () => { set('badges', [...s.badges, { ...icon2, right: false }]); flash(); } },
        { label: L('Thêm bên phải'), disabled: full(true), onClick: () => { set('badges', [...s.badges, { ...icon2, right: true }]); flash(); } }] },
      { title: L('Đặt cho ứng dụng đang theo dõi'), items: state.items.map((it) => ({ label: it.name,
        onClick: () => { setItems(state.items.map((x) => (x.id === it.id ? { ...x, icon: { ...icon2, right: false } } : x))); flash(); } })) },
      { title: L('Đặt cho công cụ build'), items: s.seenTools.map((t) => ({ label: t,
        onClick: () => { set('toolIcons', { ...s.toolIcons, [t]: { ...icon2, right: false } }); flash(); } })) },
    ]);
  }

  // ---- sounds ---------------------------------------------------------------------------------------------------------

  function currentPack() {
    const m = S().soundMap;
    const pack = CAT.soundPacks.find((p) => CAT.soundEvents.every((e) => (m[e.id] ?? '') === (p.map[e.id] ?? '')));
    return pack ? pack.name : '';
  }

  function soundName(v) {
    if (!v) return L('Không');
    return v.includes('/') || v.includes('\\') ? `♪ ${v.split(/[\\/]/).pop()}` : v;
  }

  function sounds() {
    const s = S();
    const packOptions = [...(currentPack() ? [] : [{ value: '', label: L('Của bạn') }]),
      ...CAT.soundPacks.map((p) => ({ value: p.name, label: L(p.name) }))];
    const fileInput = h('input', { type: 'file', accept: '.wav,.mp3,.m4a,.ogg,.aiff', class: 'hidden' });
    let pendingEvent = null;
    fileInput.addEventListener('change', () => {
      const f = fileInput.files[0];
      if (f && pendingEvent) {
        const path = nt.importSound(f.path);
        if (path) set('soundMap', { ...S().soundMap, [pendingEvent]: path });
      }
      pendingEvent = null;
    });
    const vol = slider(s.volume, 0, 1, 0.05, (v) => { state = { ...state, settings: { ...state.settings, volume: v } }; },
      (v) => set('volume', v, { rerender: false }));
    vol.show(`${Math.round(s.volume * 100)}%`);

    const soundGroup = group(
      row(L('Âm thanh'), toggle(s.sounds, (v) => set('sounds', v))),
      row(L('Âm lượng'), vol.el),
      row(L('Gói âm thanh'), select(packOptions, currentPack(), (v) => { if (v) nt.act('applyPack', v); }, L('Gói âm thanh'))),
      row(L('Sao lưu, chia sẻ'), h('div', { class: 'row gap' },
        button(L('Xuất gói…'), () => nt.act('exportSounds'), { small: true }),
        button(L('Nhập gói…'), () => nt.act('importSounds'), { small: true }))));

    const eventRows = CAT.soundEvents.map((e) => {
      const cur = s.soundMap[e.id] ?? '';
      const options = [{ value: '', label: L('Không') }, ...CAT.soundNames.map((n) => ({ value: n, label: n }))];
      if (cur && !CAT.soundNames.includes(cur)) options.push({ value: cur, label: soundName(cur) });
      options.push({ value: '__file__', label: L('Chọn file của bạn…') });
      const pick = select(options, cur, (v) => {
        if (v === '__file__') { pendingEvent = e.id; fileInput.click(); return; }
        set('soundMap', { ...S().soundMap, [e.id]: v });
        if (v) nt.act('playSound', v, S().volume);
      }, L(e.title));
      const play = h('button', { class: 'icon-btn', type: 'button', title: L('Nghe thử'), disabled: !cur,
        onclick: () => nt.act('playSound', cur, S().volume) }, icon('play', 14));
      return h('div', { class: 'srow2' }, icon(e.icon, 16, 'dim'), h('span', { class: 'it' }, L(e.title)), h('span', { class: 'sp' }), pick, play);
    });

    return [
      soundGroup,
      h('div', { class: 'dim small pt' }, L('Âm thanh giữ riêng, đổi giao diện không mất. Xuất gói để mang sang máy khác.')),
      card(L('Sự kiện'), null, fileInput, h('div', { class: `list${s.sounds ? '' : ' muted'}` }, eventRows)),
    ];
  }

  // ---- behavior -------------------------------------------------------------------------------------------------------

  function behavior() {
    const s = S();
    const langs = [{ value: '', label: `${L('Theo máy')} · ${(CAT.languages.find((x) => x.code === state.device) || {}).name || 'English'}` },
      ...CAT.languages.map((x) => ({ value: x.code, label: x.name }))];
    const delay = slider(s.hoverDelay, 0, 1, 0.05, (v) => { state = { ...state, settings: { ...state.settings, hoverDelay: v } }; },
      (v) => set('hoverDelay', v, { rerender: false }));
    delay.show(`${Math.round(s.hoverDelay * 1000)}`);
    const limit = slider(s.limitWarn, 50, 100, 5, (v) => { state = { ...state, settings: { ...state.settings, limitWarn: v } }; },
      (v) => set('limitWarn', v, { rerender: false }));
    limit.show(`${Math.round(s.limitWarn)}%`);
    return [
      card(L('Mở notch'), null, group(
        row(L('Cách mở'), segmented([{ value: 'hover', label: L('Rê chuột') }, { value: 'click', label: L('Bấm') }], s.expandMode, (v) => set('expandMode', v))),
        s.expandMode === 'hover' ? row(L('Độ trễ {0} ms', Math.round(s.hoverDelay * 1000)), delay.el) : null,
        row(L('CPU và RAM khi mở rộng'), toggle(s.systemLoad, (v) => set('systemLoad', v)))),
      ),
      card(L('Thông báo'), null, group(
        row(L('Thông báo hệ thống'), toggle(s.systemNotifications, (v) => set('systemNotifications', v))),
        row(L('Báo xong khi chạy từ {0} giây', Math.round(s.doneAfter)), stepper(s.doneAfter, 5, 600, 5, (v) => set('doneAfter', v))),
        row(L('Cảnh báo giới hạn ở {0}%', Math.round(s.limitWarn)), limit.el))),
      card(L('Khác'), null, group(
        row(L('Ngôn ngữ'), select(langs, state.choice || '', (v) => nt.act('setLanguage', v))),
        row(L('Gửi thống kê sử dụng ẩn danh'), toggle(state.analytics, (v) => { state = { ...state, analytics: v }; nt.act('setAnalytics', v); render(); })))),
    ];
  }

  function stepper(value, min, max, step, onChange) {
    const out = h('span', { class: 'stepper-v' }, String(value));
    const bump = (d) => {
      const v = Math.min(max, Math.max(min, value + d));
      value = v;
      out.textContent = String(v);
      onChange(v);
    };
    return h('span', { class: 'stepper' }, h('button', { class: 'icon-btn', type: 'button', onclick: () => bump(-step), 'aria-label': '−' }, icon('minus', 14)),
      out, h('button', { class: 'icon-btn', type: 'button', onclick: () => bump(step), 'aria-label': '+' }, icon('plus', 14)));
  }

  // ---- report ---------------------------------------------------------------------------------------------------------

  function openReport() {
    const topics = ['Ghép nối', 'Cập nhật', 'Khác'];
    let topic = topics[2];
    let text = '';
    let contact = '';
    const area = h('textarea', { class: 'inp area', rows: 4, placeholder: L('Chuyện gì đã xảy ra?'), oninput: (e) => { text = e.target.value; send.disabled = !text.trim() || !!state.report?.code || !!state.report?.busy; } });
    const mail = h('input', { class: 'inp', placeholder: L('Email hoặc cách liên hệ (để chúng tôi trả lời)'), oninput: (e) => { contact = e.target.value; } });
    const status = h('div', { class: 'status' });
    const send = h('button', { class: 'btn primary', type: 'button', disabled: true, onclick: () => {
      nt.act('sendReport', { topic: L(topic), text, contact });
      send.disabled = true;
    } }, L('Gửi'));
    const body = h('div', { class: 'stack' },
      h('label', { class: 'field' }, h('span', { class: 'field-l' }, L('Loại vấn đề')),
        select(topics.map((t) => ({ value: t, label: L(t) })), topic, (v) => { topic = v; })),
      area, mail,
      h('div', { class: 'dim small' }, L('Nội dung và liên hệ bạn nhập; mã thiết bị ẩn danh, mã nhóm, tình trạng ghép nối, số điện thoại, phiên bản ứng dụng, hệ điều hành, mẫu thiết bị, ngôn ngữ, trạng thái Pro và giao dịch nếu có. Không tự động gửi lệnh, mã nguồn, đường dẫn, tên công việc hay tên dự án.')),
      status);
    const refresh = () => {
      const r = state.report || {};
      status.replaceChildren();
      if (r.code) status.append(h('div', {}, L('Đã gửi. Mã báo lỗi: {0}', String(r.code).slice(-6))));
      if (r.problem) status.append(h('div', { class: 'warn' }, r.problem));
    };
    refresh();
    const dlg = modal({ width: 500, title: L('Báo lỗi'), body, footer: [button(L('Đóng'), () => dlg.close()), send],
      onClose: () => { reportDialog = null; } });
    reportDialog = { refresh, send };
  }

  // ---- update footer --------------------------------------------------------------------------------------------------

  function updateFooter() {
    const u = state.update || {};
    if (u.busy && u.progress != null) {
      return h('div', { class: 'upd' }, h('div', { class: 'dim' }, L('Đang tải… {0}%', Math.round(u.progress * 100))),
        h('div', { class: 'bar' }, h('i', { style: { width: `${Math.round(u.progress * 100)}%` } })));
    }
    if (u.busy) return h('div', { class: 'upd dim' }, L('Đang kiểm tra…'));
    if (u.available) return h('div', { class: 'upd' },
      button(L('Cập nhật {0}', u.version || ''), () => nt.act('installUpdate'), { primary: true, small: true, icon: 'download' }),
      u.notes ? h('div', { class: 'dim small clamp', title: u.notes }, u.notes) : null);
    if (u.error) return h('div', { class: 'upd' }, h('div', { class: 'warn small' }, u.error),
      button(L('Thử lại'), () => nt.act('checkUpdate'), { small: true }));
    return h('button', { class: 'upd-ver dim', type: 'button', title: L('Kiểm tra ngay'), onclick: () => nt.act('checkUpdate') }, `v${state.version}`);
  }

  // ---- frame ----------------------------------------------------------------------------------------------------------

  const PAGES = { overview, modules, agents: watch, phone, appearance, badges, store, sounds, behavior };

  function render() {
    if (!state) return;
    const titleOf = PANES.find((p) => p.id === pane) || PANES[0];
    document.getElementById('nav').replaceChildren(...PANES.map((p) => h('button', {
      class: `nav-i${p.id === pane ? ' on' : ''}`, type: 'button', onclick: () => go(p.id) }, icon(p.icon, 16), L(p.title))));
    fill(document.getElementById('update'), [updateFooter()]);
    const page = PAGES[pane] ? PAGES[pane]() : [];
    fill(document.getElementById('pane'), [h('h1', { class: 'ptitle' }, L(titleOf.title)), page]);
    if (reportDialog) reportDialog.refresh();
  }

  nt.onState((next) => {
    const first = !state;
    state = next;
    U.setSettingsForAccent(next.settings);
    const s = sig();
    if (first || s !== lastSig) { lastSig = s; render(); }
    if (reportDialog && next.report && next.report.busy === false) reportDialog.send.disabled = !!next.report.code;
  });
  nt.onPane((p) => { if (PANES.some((x) => x.id === p)) { pane = p; render(); } });
})();
