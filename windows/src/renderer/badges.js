'use strict';
// Shared UI kit for prefs.html and onboarding.html (plain script, global NTUI): DOM helpers, controls, modal and menu,
// the notch preview, icons on the notch (NotchBadge), the icon editor (BadgeEditor) and the icon board (BadgeBoard).
// Mirrors Sources/BadgesPane.swift and Sources/Preferences.swift (NotchPreview, ThemeTile) and Customize.swift.
(function () {
  const nt = window.nt;
  const ACCENT_DEFAULT = '#0A84FF';
  const SWATCHES = ['#FFFFFF', '#000000', '#FF453A', '#FF9F0A', '#FFD60A', '#30D158', '#64D2FF', '#0A84FF', '#BF5AF2'];
  const EMOJI = ['🔥', '🚀', '⚡️', '🤖', '🐱', '🐶', '💻', '☕️', '✅', '🎯', '🌟', '💡', '🛠️', '🍀', '🎧', '🧠'];
  // SF Symbol names (icons.js), the ones a badge is most often given
  const ICON_PRESETS = ['star.fill', 'heart.fill', 'bolt.fill', 'flame.fill', 'pawprint.fill', 'face.smiling', 'leaf.fill', 'moon.fill',
    'sun.max.fill', 'music.note', 'gamecontroller.fill', 'cup.and.saucer.fill', 'sparkles', 'hammer.fill', 'terminal.fill', 'cpu.fill',
    'brain.head.profile', 'wrench.and.screwdriver.fill', 'shippingbox.fill', 'server.rack', 'cloud.fill', 'rocket.fill', 'gearshape.fill',
    'eye.fill'];
  const SHOW_LIMIT = 240;

  // ---- DOM ---------------------------------------------------------------------------------------------------------

  function h(tag, props, ...kids) {
    const el = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (v === undefined || v === null || v === false) continue;
      if (k === 'class') el.className = v;
      else if (k === 'style' && typeof v === 'object') {
        for (const [sk, sv] of Object.entries(v)) { if (sk.startsWith('--')) el.style.setProperty(sk, sv); else el.style[sk] = sv; }
      } else if (k.startsWith('on')) el.addEventListener(k.slice(2), v);
      else if (k === 'text') el.textContent = v;
      else el.setAttribute(k, v === true ? '' : String(v));
    }
    for (const kid of kids.flat(Infinity)) {
      if (kid === null || kid === undefined || kid === false) continue;
      el.append(kid instanceof Node ? kid : document.createTextNode(String(kid)));
    }
    return el;
  }

  /** A lucide icon as an inline SVG (the files ship with the app; the markup is ours, never user text). */
  function icon(name, size = 16, cls = '') {
    const el = h('span', { class: `ico ${cls}`.trim(), style: { width: `${size}px`, height: `${size}px` } });
    el.innerHTML = nt.icon(name) || nt.icon('circle-help');
    return el;
  }

  function L(key, ...args) { return nt.L(key, ...args); }

  /** Replaces an element's children; null/false entries are skipped (never printed as text). */
  function fill(el, kids) {
    el.replaceChildren(...kids.flat(Infinity).filter((k) => k !== null && k !== undefined && k !== false)
      .map((k) => (k instanceof Node ? k : document.createTextNode(String(k)))));
    return el;
  }

  // ---- controls ------------------------------------------------------------------------------------------------------

  function toggle(on, onChange, label) {
    const input = h('input', { type: 'checkbox', class: 'sw-input', 'aria-label': label || '' });
    input.checked = !!on;
    input.addEventListener('change', () => onChange(input.checked));
    return h('label', { class: 'switch' }, input, h('span', { class: 'sw-track' }, h('span', { class: 'sw-knob' })));
  }

  /** Segmented control. options: [{value, label, icon?}]. */
  function segmented(options, value, onChange) {
    const wrap = h('div', { class: 'seg', role: 'radiogroup' });
    for (const o of options) {
      wrap.append(h('button', { class: `seg-b${o.value === value ? ' on' : ''}`, type: 'button', 'aria-checked': o.value === value,
        onclick: () => onChange(o.value) }, o.icon ? icon(o.icon, 14) : null, o.label));
    }
    return wrap;
  }

  function select(options, value, onChange, label) {
    const sel = h('select', { class: 'sel', 'aria-label': label || '' });
    for (const o of options) sel.append(h('option', { value: o.value }, o.label));
    sel.value = value;
    sel.addEventListener('change', () => onChange(sel.value));
    return sel;
  }

  // ---- modal and menu -------------------------------------------------------------------------------------------------

  const modalStack = [];
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape' && modalStack.length) modalStack[modalStack.length - 1]();
  });

  /** A dialog over the page. Escape or a click outside closes the top one. Returns { close, box }. */
  function modal({ width = 520, title, body, footer, onClose }) {
    const box = h('div', { class: 'dialog', style: { width: `min(${width}px, 94vw)` } },
      title ? h('div', { class: 'dlg-title' }, title) : null, body, footer ? h('div', { class: 'dlg-foot' }, footer) : null);
    const scrim = h('div', { class: 'scrim' }, box);
    document.body.append(scrim);
    let closed = false;
    const close = () => {
      if (closed) return;
      closed = true;
      scrim.remove();
      const at = modalStack.indexOf(close);
      if (at >= 0) modalStack.splice(at, 1);
      if (onClose) onClose();
    };
    scrim.addEventListener('mousedown', (e) => { if (e.target === scrim) close(); });
    modalStack.push(close);
    return { close, box };
  }

  /** A popover under an element. sections: [{title, items: [{label, icon?, disabled?, onClick}]}]. */
  function menu(anchor, sections) {
    const r = anchor.getBoundingClientRect();
    const panel = h('div', { class: 'menu', style: { left: `${Math.min(r.left, window.innerWidth - 240)}px`, top: `${r.bottom + 4}px` } });
    for (const s of sections) {
      if (s.title) panel.append(h('div', { class: 'menu-h' }, s.title));
      if (!s.items.length) panel.append(h('div', { class: 'menu-empty' }, s.empty || ''));
      for (const it of s.items) {
        panel.append(h('button', { class: 'menu-i', type: 'button', disabled: it.disabled, onclick: () => { close(); it.onClick(); } },
          it.icon ? icon(it.icon, 14) : null, h('span', { class: 'menu-t' }, it.label)));
      }
    }
    document.body.append(panel);
    const outside = (e) => { if (!panel.contains(e.target)) close(); };
    function close() {
      panel.remove();
      document.removeEventListener('mousedown', outside, true);
    }
    setTimeout(() => document.addEventListener('mousedown', outside, true), 0);
    return close;
  }

  // ---- notch and icons on it ------------------------------------------------------------------------------------------

  let catalog = { sprites: [] };
  function setCatalog(c) { catalog = c; }
  function spriteEntry(id) { return catalog.sprites.find((s) => s.id === id); }

  /** Accent color of the settings: "auto" uses the blue default, "custom" the picked hex. */
  function accentHex(s) {
    if (s.accent === 'custom') return s.customAccent;
    const a = catalog.accents.find((x) => x.name === s.accent);
    return (a && a.hex) || ACCENT_DEFAULT;
  }

  /** A built-in or custom spritesheet (frames side by side, square frames). The frame width is known once the image loads. */
  function spriteEl(src, frames, fps, size, pixel) {
    const el = h('span', { class: `sprite${pixel ? ' px' : ''}`, style: { width: `${size}px`, height: `${size}px`, backgroundImage: `url("${src}")` } });
    const img = new Image();
    img.onload = () => {
      const n = Math.max(1, frames || 1);
      const total = size * (img.naturalWidth / img.naturalHeight);  // the sheet's width at this height
      el.style.backgroundSize = `${total}px ${size}px`;
      el.style.setProperty('--n', String(n));
      // steps through the frames on NTClock (sprites.js); without it the first frame stays up
      if (window.NTClock) NTClock.add(el, fps || 10, n, (x, i) => { x.style.backgroundPosition = `-${(i * total) / n}px 0`; });
    };
    img.src = src;
    return el;
  }

  /** Restarts every animated icon on the page together (the "Đồng bộ chuyển động" button). */
  function syncSprites(root) {
    if (window.NTClock) NTClock.restart(root);
  }

  /** One fixed icon as it sits on the notch: circle or rounded square, fill or plain, with its ink. */
  function NotchBadge(b, size, accent) {
    const acc = accent || ACCENT_DEFAULT;
    const fill = b.plain === true ? null : b.color || acc;
    const ink = b.fg || (b.plain === true ? acc : '#FFFFFF');
    const radius = b.shape === 'rounded' ? '24%' : '50%';
    const box = h('span', { class: `nb${b.border ? ' ring' : ''}`,
      style: { width: `${size}px`, height: `${size}px`, borderRadius: radius, background: fill || 'transparent', color: ink } });
    const innerSize = Math.round(size * 0.62);
    if (b.sprite && spriteEntry(b.sprite)) {
      const e = spriteEntry(b.sprite);
      const sp = spriteEl(nt.spriteURL(e.id), e.frames, e.fps, Math.round(size * 0.9), e.pixel);
      sp.dataset.fps = e.fps;
      box.append(sp);
    } else if (b.frames && b.image) {
      const sp = spriteEl(nt.fileURL(b.image), b.frames, b.fps || 10, Math.round(size * 0.9), false);
      sp.dataset.fps = b.fps || 10;
      box.append(sp);
    } else if (b.image) {
      box.append(h('img', { class: 'nb-img', src: nt.fileURL(b.image), alt: '', style: { borderRadius: radius } }));
    } else if (b.text) {
      box.append(h('span', { class: 'nb-text', style: { fontSize: `${Math.round(size * (Array.from(b.text).length > 2 ? 0.36 : 0.5))}px` } }, b.text));
    } else {
      box.append(icon(b.symbol || 'star', innerSize));
    }
    return box;
  }

  /** The notch as it will look: working count and bars on the left, alerts on the right, the camera gap between. */
  function NotchPreview(s, live, opts = {}) {
    const acc = accentHex(s);
    const size = s.size === 'regular' ? 20 : 16;
    const working = live ? live.working : 2;
    const alerts = live ? live.alerts : 1;
    const bg = { black: { background: '#000' }, glass: { background: 'rgba(38,38,38,.82)', backdropFilter: 'blur(12px)' },
      gradient: { background: `linear-gradient(160deg, ${s.gradientA}, ${s.gradientB})` }, glow: { background: '#000' } }[s.background];
    const shell = h('div', { class: `nprev bg-${s.background}`, style: { ...bg, '--acc': acc } });
    const row = h('div', { class: 'nprev-row' },
      h('div', { class: 'np-l' },
        working > 0 ? h('span', { class: 'np-ico', style: { width: `${size}px`, height: `${size}px` } }, icon('sparkles', size * 0.7)) : null,
        working > 0 ? h('span', { class: 'np-num' }, String(working)) : null,
        working > 0 ? h('span', { class: 'np-bars' }, h('i', {}), h('i', {}), h('i', {}), h('i', {})) : null),
      h('div', { class: 'np-gap' }),
      h('div', { class: 'np-r' },
        alerts > 0 ? icon('hammer', size * 0.65, 'np-acc') : null,
        alerts > 0 ? h('span', { class: 'np-ring', style: { '--p': '60%' } }) : null));
    shell.append(row);
    if (opts.caption) shell.append(h('div', { class: 'np-cap' }, opts.caption));
    return shell;
  }

  // ---- icon board: pure helpers (the same rules as Customize.swift) ------------------------------------------------

  /** Swaps two icons' places, across sides too. */
  function swapBadges(list, aId, bId) {
    const out = list.map((b) => ({ ...b }));
    const i = out.findIndex((b) => b.id === aId);
    const j = out.findIndex((b) => b.id === bId);
    if (i < 0 || j < 0 || i === j) return out;
    const ri = out[i].right;
    const rj = out[j].right;
    [out[i], out[j]] = [out[j], out[i]];
    out[i].right = ri;
    out[j].right = rj;
    return out;
  }

  /** Moves an icon to the outer end of the other side, if that side has room. */
  function moveBadge(list, id, right, perSide) {
    const i = list.findIndex((b) => b.id === id);
    if (i < 0 || list[i].right === right) return list;
    if (list.filter((b) => b.right === right).length >= perSide) return list;
    const out = list.map((b) => ({ ...b }));
    const [b] = out.splice(i, 1);
    b.right = right;
    // the left side lists outside-in, the right inside-out: outer end = front on the left, back on the right
    if (right) out.push(b);
    else {
      const at = out.findIndex((x) => !x.right);
      out.splice(at < 0 ? out.length : at, 0, b);
    }
    return out;
  }

  /** The icon board: left side, camera, right side. A click edits, a drag swaps or moves to the other side. */
  function BadgeBoard(badges, { perSide, onChange, onEdit, onStore }) {
    let dragId = null;
    const side = (right) => {
      const mine = badges.filter((b) => b.right === right);
      const tiles = mine.map((b) => tile(b));
      const add = mine.length < perSide ? slot(right) : null;
      return h('div', { class: `bside ${right ? 'r' : 'l'}` }, right ? tiles : [add, ...tiles], right ? add : null);
    };
    const slot = (right) => h('button', { class: 'add-slot', type: 'button', title: L(right ? 'Add on the right' : 'Add on the left'),
      ondragover: (e) => e.preventDefault(),
      ondrop: (e) => { e.preventDefault(); if (dragId) onChange(moveBadge(badges, dragId, right, perSide)); dragId = null; },
      onclick: () => onEdit({ id: null, right }) }, icon('plus', 14));
    const tile = (b) => h('button', { class: 'bt', type: 'button', draggable: 'true', title: b.symbol,
      ondragstart: (e) => { dragId = b.id; e.dataTransfer.setData('text/plain', b.id); e.dataTransfer.effectAllowed = 'move'; },
      ondragover: (e) => e.preventDefault(),
      ondrop: (e) => { e.preventDefault(); if (dragId && dragId !== b.id) onChange(swapBadges(badges, dragId, b.id)); dragId = null; },
      onclick: () => onEdit({ id: b.id, right: b.right }) },
    NotchBadge(b, 22, accentHex(catalogSettings)));
    const row = h('div', { class: 'board-row' }, side(false), h('div', { class: 'camera' }, h('i', {})), side(true));
    return h('div', { class: 'board' }, row,
      h('div', { class: 'board-hint' }, L('Click + to add, click to edit, drag to swap places. Up to {0} per side{1}', perSide,
        perSide < catalog.proPerSide ? L(', Pro gets 4.') : '.')),
      h('div', { class: 'board-actions' }, h('button', { class: 'btn sm', type: 'button', onclick: onStore }, icon('sparkles', 14), L('Add from Icon Library'))));
  }

  // The settings the board's accent comes from; prefs.js keeps it current.
  let catalogSettings = { accent: 'auto', customAccent: ACCENT_DEFAULT };
  function setSettingsForAccent(s) { catalogSettings = s; }

  // ---- icon editor ----------------------------------------------------------------------------------------------------

  const TABS = [
    { id: 'symbol', icon: 'star', label: 'Symbol', hint: 'Over 1,000 thin-line icons' },
    { id: 'text', icon: 'type', label: 'Text, emoji', hint: 'A few characters or emoji' },
    { id: 'image', icon: 'image', label: 'Photos', hint: 'Your picture' },
    { id: 'motion', icon: 'clapperboard', label: 'Animation', hint: 'Animated characters, GIFs' },
  ];

  /**
   * The icon window. badge: the icon as it is; onChange(badge) on every edit; onDelete / onReset optional; onDone on close.
   * Edits go out whole: the caller stores them where the icon lives (fixed icon, watched app, build tool).
   */
  function BadgeEditor({ badge, title, subtitle, onChange, onDelete, onReset, onDone }) {
    let b = { ...badge };
    let tab = b.sprite || (b.frames && b.image) ? 'motion' : b.image ? 'image' : b.text != null ? 'text' : 'symbol';
    let query = '';
    let motionCat = null;
    const side = h('div', { class: 'be-side' });
    const content = h('div', { class: 'be-content' });
    const style = h('div', { class: 'be-style' });

    const big = h('div', { class: 'be-big' });
    const live = h('div', { class: 'be-live', title: L('On the notch') });
    const head = h('div', { class: 'be-head' },
      big, h('div', { class: 'be-titles' }, h('div', { class: 'be-title' }, title), subtitle ? h('div', { class: 'be-sub' }, subtitle) : null),
      h('span', { class: 'sp' }), live);
    const emit = () => { onChange({ ...b }); renderPreview(); };
    function renderPreview() {
      const acc = accentHex(catalogSettings);
      big.replaceChildren(NotchBadge(b, 40, acc));
      live.replaceChildren(h('div', { class: 'be-notch' }, NotchBadge(b, catalogSettings.size === 'regular' ? 20 : 16, acc), h('i', {})));
    }

    function renderSide() {
      side.replaceChildren(...TABS.map((t) => h('button', { class: `be-tab${t.id === tab ? ' on' : ''}`, type: 'button', onclick: () => { tab = t.id; renderSide(); renderContent(); } },
        icon(t.icon, 15), h('span', {}, h('b', {}, L(t.label)), h('small', {}, L(t.hint))))));
    }

    function renderContent() {
      content.replaceChildren(tab === 'symbol' ? symbolTab() : tab === 'text' ? textTab() : tab === 'image' ? imageTab() : motionTab());
    }

    function symbolTab() {
      const gridItem = (name) => h('button', { class: `ig${b.symbol === name && !b.image && !b.sprite && b.text == null ? ' on' : ''}`,
        type: 'button', title: name,
        onclick: () => { b = { ...b, symbol: name, image: null, sprite: null, frames: null, fps: null, text: null }; emit(); renderContent(); } },
      icon(name, 20));
      const results = iconResults();
      const grid = h('div', { class: 'icon-grid' }, results.map(gridItem));
      const count = h('div', { class: 'caption' });
      const input = h('input', { class: 'inp', placeholder: L('Search in English: star, cloud, cat, hammer…'), value: query,
        oninput: (e) => {
          query = e.target.value;
          const hits = iconResults();
          grid.replaceChildren(...hits.map(gridItem));
          count.textContent = query ? L('{0} results', hits.length) : L('{0} icons', hits.length);
        } });
      count.textContent = query ? L('{0} results', results.length) : L('{0} icons', results.length);
      return h('div', { class: 'stack' }, input, grid, count);
    }

    function iconResults() {
      const q = query.trim().toLowerCase();
      if (!q) return ICON_PRESETS;
      const all = nt.iconNames();
      const hits = [];
      for (const n of all) { if (n.includes(q)) hits.push(n); if (hits.length >= SHOW_LIMIT) break; }
      return hits;
    }

    function textTab() {
      const input = h('input', { class: 'inp big', maxlength: 32, value: b.text || '', placeholder: L('Type up to 8 characters'),
        oninput: (e) => { const v = Array.from(e.target.value).slice(0, 8).join(''); e.target.value = v; b = { ...b, text: v, image: null, sprite: null, frames: null, fps: null }; emit(); } });
      return h('div', { class: 'stack' }, input, h('div', { class: 'caption' }, L('Quick picks')),
        h('div', { class: 'emoji-grid' }, EMOJI.map((e) => h('button', { class: `eg${b.text === e ? ' on' : ''}`, type: 'button',
          onclick: () => { b = { ...b, text: e, image: null, sprite: null, frames: null, fps: null }; emit(); renderContent(); } }, e))));
    }

    function imageTab() {
      const pick = h('input', { type: 'file', accept: 'image/*', class: 'hidden' });
      const adopt = (src) => {
        const path = nt.importImage(src);
        if (!path) return;
        b = { ...b, image: path, sprite: null, frames: null, fps: null, text: null };
        emit(); renderContent();
      };
      pick.addEventListener('change', () => { const f = pick.files[0]; if (f) adopt(f.path); });
      const zone = h('div', { class: 'drop', ondragover: (e) => { e.preventDefault(); zone.classList.add('over'); },
        ondragleave: () => zone.classList.remove('over'),
        ondrop: (e) => { e.preventDefault(); zone.classList.remove('over'); const f = e.dataTransfer.files[0]; if (f) adopt(f.path); } },
      b.image ? h('img', { class: 'drop-img', src: nt.fileURL(b.image), alt: '' }) : icon('image', 40, 'dim'),
      h('div', { class: 'dim' }, L(b.image ? 'Drop another picture here' : 'Drop a picture here')),
      h('button', { class: 'btn', type: 'button', onclick: () => pick.click() }, L('Choose picture…')),
      pick,
      h('div', { class: 'caption' }, L('PNG, JPG, HEIC… Square pictures look best.')));
      return zone;
    }

    function motionTab() {
      const cats = [...new Set(catalog.sprites.map((s) => s.category).filter((c) => c !== 'pets'))];
      if (!motionCat) motionCat = (spriteEntry(b.sprite) || {}).category || cats[0];
      const chips = h('div', { class: 'chips' }, cats.map((c) => h('button', { class: `chip${c === motionCat ? ' on' : ''}`, type: 'button',
        onclick: () => { motionCat = c; renderContent(); } }, L(c))));
      const grid = h('div', { class: 'sprite-grid' }, catalog.sprites.filter((s) => s.category === motionCat).map((s) => h('button', {
        class: `sg${b.sprite === s.id ? ' on' : ''}`, type: 'button', title: L(s.title),
        onclick: () => { b = { ...b, sprite: s.id, plain: b.plain === undefined || b.plain === null ? true : b.plain, image: null, text: null, frames: null, fps: null }; emit(); renderContent(); } },
      h('span', { class: 'sg-art' }, spriteEl(nt.spriteURL(s.id), s.frames, s.fps, 36, s.pixel)), h('span', { class: 'sg-t' }, s.title))));
      const own = h('input', { type: 'file', accept: '.gif,.png,image/gif,image/png', class: 'hidden' });
      own.addEventListener('change', () => {
        const f = own.files[0];
        if (!f) return;
        const path = nt.importImage(f.path);
        if (!path) return;
        const probe = new Image();
        probe.onload = () => {
          const isPNG = /\.png$/i.test(path);
          const frames = isPNG ? Math.max(1, Math.round(probe.naturalWidth / Math.max(1, probe.naturalHeight))) : null;
          b = { ...b, image: path, sprite: null, text: null, frames, fps: frames ? 10 : null, plain: b.plain ?? true };
          emit(); renderContent();
        };
        probe.src = nt.fileURL(path);
      });
      const custom = [h('button', { class: 'btn', type: 'button', onclick: () => own.click() }, icon('film', 14), L('Your GIF or spritesheet…')), own];
      const count = b.frames && b.image && !b.sprite ? h('div', { class: 'row gap' },
        h('span', { class: 'dim' }, `${b.frames} khung`),
        h('input', { type: 'range', min: 1, max: 120, value: b.frames, oninput: (e) => { b = { ...b, frames: Number(e.target.value) }; emit(); } }),
        h('input', { type: 'range', min: 1, max: 30, value: b.fps || 10, oninput: (e) => { b = { ...b, fps: Number(e.target.value) }; emit(); } }),
        h('span', { class: 'dim' }, L('{0}/sec', b.fps || 10))) : null;
      return h('div', { class: 'stack' }, chips, grid, h('div', { class: 'row gap' }, custom), count);
    }

    const FILLS = ['#FFFFFF', '#000000', '#FF453A', '#FF9F0A', '#FFD60A', '#30D158', '#64D2FF', '#0A84FF', '#BF5AF2'];
    function renderStyle() {
      const paints = tab === 'symbol' || tab === 'text';
      const fills = h('div', { class: 'swatches' },
        h('button', { class: `sw-none${b.plain === true ? ' on' : ''}`, type: 'button', title: L('No background'),
          onclick: () => { b = { ...b, plain: true }; emit(); renderStyle(); } }, icon('circle-slash', 14)),
        FILLS.map((c) => swatch(c, b.plain !== true && b.color === c, () => { b = { ...b, color: c, plain: null }; emit(); renderStyle(); })),
        h('input', { type: 'color', class: 'color', value: b.color || '#0A84FF', title: L('Other color'),
          onchange: (e) => { b = { ...b, color: e.target.value, plain: null }; emit(); renderStyle(); } }));
      const inks = paints ? h('div', { class: 'swatches' }, FILLS.map((c) => swatch(c, b.fg === c, () => { b = { ...b, fg: c }; emit(); renderStyle(); })),
        h('input', { type: 'color', class: 'color', value: b.fg || '#FFFFFF', title: L('Other color'),
          onchange: (e) => { b = { ...b, fg: e.target.value }; emit(); renderStyle(); } })) : null;
      fill(style, [
        h('div', { class: 'be-row' }, h('span', { class: 'dim' }, L('Shape')),
          segmented([{ value: 'circle', label: L('Circle'), icon: 'circle' }, { value: 'rounded', label: L('Square'), icon: 'square' }], b.shape,
            (v) => { b = { ...b, shape: v }; emit(); renderStyle(); }),
          h('span', { class: 'sp' }), h('span', { class: 'dim' }, L('Border')),
          toggle(b.border, (v) => { b = { ...b, border: v }; emit(); renderStyle(); }, L('Border'))),
        h('div', { class: 'be-row' }, h('span', { class: 'dim' }, L('Background')), fills),
        inks ? h('div', { class: 'be-row' }, h('span', { class: 'dim' }, tab === 'text' ? L('Text color') : L('Symbol color')), inks) : null]);
    }
    function swatch(c, on, go) {
      return h('button', { class: `sw${on ? ' on' : ''}`, type: 'button', title: c, style: { background: c }, onclick: go });
    }

    renderPreview();
    renderSide();
    renderContent();
    renderStyle();

    const footer = h('div', { class: 'be-foot' },
      onDelete ? h('button', { class: 'btn danger', type: 'button', onclick: () => { m.close(); onDelete(); } }, icon('trash-2', 14), L('Remove icon')) : null,
      onReset ? h('button', { class: 'btn', type: 'button', onclick: () => { m.close(); onReset(); } }, L('Use the default icon')) : null,
      h('span', { class: 'sp' }),
      h('button', { class: 'btn primary', type: 'button', onclick: () => m.close() }, L('Done')));
    const body = h('div', { class: 'be' }, head, h('div', { class: 'be-main' }, side, content), style);
    const m = modal({ width: 680, body, footer, onClose: onDone });
    m.box.classList.add('be-box');
    return m;
  }

  // ---- exports --------------------------------------------------------------------------------------------------------

  window.NTUI = {
    h, fill, icon, L, toggle, segmented, select, modal, menu, NotchBadge, NotchPreview, BadgeEditor, BadgeBoard, spriteEl, syncSprites,
    accentHex, setCatalog, setSettingsForAccent, swapBadges, moveBadge, spriteEntry, SWATCHES, EMOJI, ICON_PRESETS,
    ACCENT_DEFAULT,
    get catalog() { return catalog; },
  };
})();
