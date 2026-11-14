'use strict';
// Frame clock and SpriteLibrary for the pages. Global scripts: window.NTClock and window.NTSprites.
// Pets (category "pets") hold 8 poses of 4 frames: the idle pose is shown.

// Steps every animated element by hand. Chromium runs each CSS infinite animation at 60 fps even when it is stepped,
// so none are used: one timer per distinct fps moves all elements registered at that rate. Paused (document hidden,
// body reduced motion or .hidden) shows frame 0 everywhere and stops the timers.
window.NTClock = (function () {
  const groups = new Map();   // fps -> { items: Map(el -> item), timer }
  const stopped = () => document.hidden || !!document.body &&
    (document.body.dataset.motion === 'reduced' || document.body.classList.contains('reduced') || document.body.classList.contains('hidden'));
  function show(it, i) { it.i = i; it.apply(it.el, i); }
  function run(fps) {
    const g = groups.get(fps);
    if (!g) return;
    clearTimeout(g.timer); g.timer = 0;
    for (const it of g.items.values()) {
      if (!it.el.isConnected) g.items.delete(it.el);   // gone from the page: stop stepping it
      else show(it, stopped() ? 0 : (it.i + 1) % it.frames);
    }
    if (!g.items.size) { groups.delete(fps); return; }
    if (!stopped()) g.timer = setTimeout(() => run(fps), 1000 / fps);
  }
  // el: the element; fps: frames per second; frames: frame count; apply(el, i): shows frame i
  function add(el, fps, frames, apply) {
    fps = Math.max(1, Number(fps) || 8);
    let g = groups.get(fps);
    if (!g) groups.set(fps, g = { items: new Map(), timer: 0 });
    const it = { el, frames: Math.max(1, Math.floor(frames) || 1), apply, i: 0 };
    g.items.set(el, it);
    show(it, 0);
    if (!g.timer && !stopped()) g.timer = setTimeout(() => run(fps), 1000 / fps);
  }
  let off = stopped();
  function sync() {
    const now = stopped();
    if (now === off) return;
    off = now;
    for (const fps of [...groups.keys()]) run(fps);
  }
  // back to frame 0 for everything under root (or everything), the "sync motion" button
  function restart(root) {
    for (const g of groups.values()) for (const it of g.items.values()) if (!root || root.contains(it.el)) show(it, 0);
  }
  const watch = () => new MutationObserver(sync).observe(document.body, { attributes: true, attributeFilter: ['class', 'data-motion'] });
  if (document.body) watch(); else document.addEventListener('DOMContentLoaded', watch);
  document.addEventListener('visibilitychange', sync);
  return { add, restart };
})();

(function () {
  let byId = null;
  function entry(id) {
    if (!byId) { byId = new Map(); for (const e of nt.catalog()) byId.set(e.id, e); }
    return byId.get(id) || null;
  }
  function strip(o) {
    const el = document.createElement('span');
    el.className = 'sp' + (o.pixel ? ' px' : '');
    el.style.setProperty('--s', o.size + 'px');
    el.style.setProperty('--n', o.total);
    el.style.backgroundImage = 'url("' + o.url + '")';
    const from = o.from || 0, take = o.take || o.total;
    NTClock.add(el, o.fps || 8, take, (x, i) => { x.style.backgroundPosition = `-${(from + i) * o.size}px 0`; });
    return el;
  }
  // a built-in animation by id, or null when the catalog has no such one
  function builtIn(id, size) {
    const e = entry(id);
    if (!e) return null;
    const pet = e.category === 'pets';
    return strip({ url: nt.spriteURL(id), total: e.frames, fps: e.fps, pixel: e.pixel,
                   from: 0, take: pet ? 4 : e.frames, size });
  }
  // a user's spritesheet (frames side by side) at a file path
  function userStrip(path, frames, fps, size) {
    return strip({ url: fileURL(path), total: frames, fps, pixel: false, size });
  }
  function fileURL(p) { return 'file:///' + encodeURI(String(p).replace(/\\/g, '/').replace(/^\/+/, '')); }
  window.NTSprites = { entry, builtIn, userStrip, fileURL };
})();
