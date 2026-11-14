'use strict';
// Sync v1, the Mac's side (docs/sync-v1.md): a port of SyncHub in Sources/Sync.swift. Group and pairing, Firebase REST
// (anonymous account, snapshot writes, answers over the event stream, entitlement), and the same-Wi-Fi TCP link with mDNS.
// No electron here: main passes the group store, the away check, the answer handler and the state sink (see deps below).

const crypto = require('node:crypto');
const box = require('./box');
const wire = require('./wire');
const { toSwiftDate, dropNil } = require('./swiftjson');
const { L } = require('./l10n');
const { WAIT } = require('./asks'); // seconds an answer may be old: the hook's wait (AgentAsks.wait)

// the Mac app's Firebase client config (Config/GoogleService-Info.plist): public by design, the rules guard the data
const API_KEY = 'AIzaSyDeHy-Giegt9j4OsBLOeYFqHilTZYQ0bDM';
const DB = 'https://notchtasks-default-rtdb.asia-southeast1.firebasedatabase.app';
const TOKEN_MS = 3000 * 1000;   // the ID token is renewed after 50 minutes (Firebase's lasts an hour)
const PRO_MS = 120 * 1000;      // the group's Pro is read at most this often
const WRITE_MS = 60 * 1000;     // non-ask changes reach Firebase at most this often (asks go at once)
const MAX_CONNS = 8;
const LIVE_JOB_MS = 300 * 1000; // a finished build stays on the phone this long

const same = (a, b) => JSON.stringify(a) === JSON.stringify(b);
const ms = (d) => (d ? new Date(d).getTime() : 0);
const BARE = /\s·\s(\d+:\d{2}(?::\d{2})?|≈[^·]*|\d+\s(?:giây|phút|giờ)[^·]*)/g;

/** The Live Activity counts the time itself: the clock and the estimate come out of the detail. */
function bare(detail) {
  return String(detail).replace(BARE, '');
}

/** Each agent once (by agent and icon), oldest first, at most four. */
function members(runs) {
  const seen = [];
  for (const r of [...runs].sort((a, b) => a.since - b.since)) {
    const m = { agent: r.agent, icon: r.icon };
    if (!seen.some((x) => same(x, m))) seen.push(m);
  }
  return seen.slice(0, 4);
}

/** What the push function is told about the agents at work: starting and all stopping at once, else at most every 10 minutes. */
function crewSignal(count, at, last, nowMs) {
  if ((count > 0) !== (last.count > 0)) return { count, at, stamp: nowMs };
  if ((count !== last.count || at !== last.at) && nowMs - last.stamp >= 600000) return { count, at, stamp: nowMs };
  return last;
}

function isPro(e, nowMs) {
  return !!e && e.pro === true && Number(e.until) > nowMs;
}

function proNoteText(why) {
  switch (why) {
    case 'moved': return L('Pro của nhóm đã chuyển sang máy tính khác');
    case 'refunded': return L('Gói Pro của nhóm đã được hoàn tiền');
    case 'expired': return L('Gói Pro của nhóm đã hết hạn');
    default: return null;
  }
}

function healthOf(h) {
  if (!h) return undefined;
  const step = (v) => (v == null ? null : Math.round(v * 20) * 5); // whole 5 % steps, as SyncHub.health
  return {
    battery: h.battery ?? null,
    charging: h.charging === true,
    thermal: h.thermal ?? 0,
    temp: h.temp == null ? null : Math.round(h.temp),
    fan: h.fan ?? null,
    cpu: step(h.cpu),
    memory: step(h.memory),
  };
}

function sleep(msWait, signal) {
  return new Promise((resolve) => {
    const t = setTimeout(done, msWait);
    function done() {
      clearTimeout(t);
      signal.removeEventListener('abort', done);
      resolve();
    }
    signal.addEventListener('abort', done, { once: true });
  });
}

/**
 * deps: apiKey, db, fetch, net (node:net), createMdns () -> {publish, unpublishAll, destroy}, now () -> ms, isAway () ->
 * bool, loadGroup () -> {g,k,uid,refresh}|null, saveGroup (g|null), macName, onAnswer (answer), onState (state), log,
 * helloTimeoutMs (5000), lan, onlyAway (initial switches), track (name), getNoteAt/setNoteAt (gid), onProNote (text).
 */
function createSync(deps = {}) {
  const apiKey = deps.apiKey || API_KEY;
  const db = deps.db || DB;
  const doFetch = deps.fetch || globalThis.fetch;
  const netMod = deps.net || require('node:net');
  const mkMdns = deps.createMdns || (() => { const { Bonjour } = require('bonjour-service'); return new Bonjour(); });
  const now = deps.now || (() => Date.now());
  const isAway = deps.isAway || (() => false);
  const onAnswer = deps.onAnswer || (() => {});
  const onState = deps.onState || (() => {});
  const onProNote = deps.onProNote || (() => {});
  const log = deps.log || (() => {});
  const track = deps.track || (() => {});
  const helloMs = deps.helloTimeoutMs ?? 5000;
  const macName = deps.macName || 'PC';

  let group = deps.loadGroup ? deps.loadGroup() || null : null;   // { g, k (base64url key), uid, refresh }
  let lan = deps.lan ?? true;
  let onlyAway = deps.onlyAway ?? true;
  let running = false;
  let phones = {};                                  // uid -> phone name
  let pro = false, proUntil = null, proHolders = [], proNote = null, proAt = -Infinity;
  let problem = null;
  let lanPhones = 0;
  let token = null;                                 // { id, until }
  let events = [], eventAt = 0, eventBox = '';
  let followed = null, ended = null;                // the build followed, and the one just ended
  const since = new Map();                          // id -> Date a run or build was first seen
  let latest = null;                                // the last snapshot input from main
  let sent = null, sentAway = null, sentLocal = null, sending = false, lastWrite = -Infinity;
  let sigCrew = { count: 0, at: 0, stamp: -Infinity };
  let streamCtl = null;                             // AbortController of the answers stream
  let server = null, mdns = null, lanPortNo = null, connSeq = 0;
  const conns = new Map();                          // id -> { sock, ok, phone, timer }
  let timer = null;

  const keyOf = (g) => box.fromB64url(g.k);
  const emit = () => onState({
    paired: !!group, phones: { ...phones }, lanPhones, problem, pro, proUntil,
    proHolders: [...proHolders], proNote, lan, onlyAway,
  });
  const setProblem = (p) => { problem = p; emit(); };
  const resetPro = () => { pro = false; proUntil = null; proHolders = []; proNote = null; proAt = -Infinity; };
  const codeOf = (g) => {
    const code = box.pairCode({ g: g.g, k: g.k, name: macName });
    return { code, url: box.pairURL(code) };
  };

  // MARK: REST

  async function post(url, body, type) {
    const res = await doFetch(url, { method: 'POST', headers: { 'Content-Type': type }, body });
    const text = await res.text();
    if (!res.ok) throw new Error(`Firebase ${res.status}: ${text}`);
    return JSON.parse(text);
  }

  async function signUp() {
    const r = await post(`https://identitytoolkit.googleapis.com/v1/accounts:signUp?key=${apiKey}`,
      JSON.stringify({ returnSecureToken: true }), 'application/json');
    if (!r.idToken || !r.localId || !r.refreshToken) throw new Error('no anonymous account');
    token = { id: r.idToken, until: now() + TOKEN_MS };
    return { uid: r.localId, refresh: r.refreshToken };
  }

  async function idToken(g) {
    if (token && token.until > now()) return token.id;
    const r = await post(`https://securetoken.googleapis.com/v1/token?key=${apiKey}`,
      `grant_type=refresh_token&refresh_token=${encodeURIComponent(g.refresh)}`, 'application/x-www-form-urlencoded');
    if (!r.id_token) throw new Error('no ID token');
    token = { id: r.id_token, until: now() + TOKEN_MS };
    return token.id;
  }

  /** A Realtime Database call under the group's path. Returns the response text; a non-2xx answer throws. */
  async function rest(method, path, g, body) {
    const res = await doFetch(`${db}/${path}.json?auth=${await idToken(g)}`, {
      method,
      headers: body === undefined ? {} : { 'Content-Type': 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    const text = await res.text();
    if (!res.ok) {
      if (res.status === 401) token = null;
      throw new Error(`Firebase ${res.status}: ${text}`);
    }
    return text;
  }

  // MARK: Pairing

  /** Creates the group when there is none (a fresh anonymous account owns it) and returns its code and URL. */
  async function pair() {
    if (!group) {
      try {
        const acc = await signUp();
        const g = { ...box.randomGroup(), uid: acc.uid, refresh: acc.refresh };
        await rest('PUT', `groups/${g.g}/members/${g.uid}`, g, { role: 'mac', name: macName, at: { '.sv': 'timestamp' } });
        group = g; sent = null; phones = {}; resetPro();
        if (deps.saveGroup) deps.saveGroup(g);
        track('pair_code_created');
      } catch (e) {
        setProblem(L('Không tạo được mã ghép: {0}', e.message));
        throw e;
      }
      problem = null;
      startStream();
      await startLAN();
    }
    emit();
    return codeOf(group);
  }

  async function loadPhones() {
    const g = group;
    if (!g) return;
    try {
      const all = JSON.parse(await rest('GET', `groups/${g.g}/members`, g));
      if (all && typeof all === 'object') {
        phones = {};
        for (const [uid, m] of Object.entries(all)) {
          if (m && m.role === 'phone') phones[uid] = typeof m.name === 'string' ? m.name : L('Điện thoại');
        }
      }
    } catch (e) {
      log(`members: ${e.message}`);
    }
    await checkPro(g, true);
    emit();
  }

  /** Removes one phone: its member entry goes, and its Wi-Fi connection with it. */
  async function unpair(uid) {
    const g = group;
    if (!g) return;
    await rest('DELETE', `groups/${g.g}/members/${uid}`, g);
    delete phones[uid];
    for (const [id, c] of [...conns]) {
      if (c.phone !== uid) continue;
      conns.delete(id);
      if (c.ok) { lanPhones--; sent = null; }
      c.sock.destroy();
    }
    emit();
  }

  /** Removes every phone, then the Mac's own member entry: nobody can read the old group. Pair again for a new code. */
  async function unpairAll() {
    const g = group;
    if (!g) return;
    track('phone_unpaired');
    stopStream();
    await loadPhones();
    for (const uid of Object.keys(phones)) await rest('DELETE', `groups/${g.g}/members/${uid}`, g).catch(() => {});
    await rest('DELETE', `groups/${g.g}/members/${g.uid}`, g).catch(() => {});
    if (deps.saveGroup) deps.saveGroup(null);
    group = null; phones = {}; resetPro(); token = null; sent = null; problem = null;
    await startLAN();
    emit();
  }

  // MARK: Pro (entitlement, grants, notices): read at most every 2 minutes

  async function checkPro(g, force = false) {
    if (!force && now() - proAt <= PRO_MS) return;
    proAt = now();
    let e;
    try {
      e = JSON.parse(await rest('GET', `groups/${g.g}/entitlement`, g));
    } catch {
      return; // a failed read keeps what was known
    }
    pro = isPro(e, now());
    proUntil = pro ? Number(e.until) : null;
    try {
      const grants = JSON.parse(await rest('GET', `groups/${g.g}/grants`, g));
      proHolders = Object.values(grants || {})
        .filter((x) => x && Number(x.until) > now())
        .map((x) => x.uid)
        .filter((u) => typeof u === 'string');
    } catch {
      proHolders = [];
    }
    try {
      const note = JSON.parse(await rest('GET', `groups/${g.g}/proNote`, g));
      if (note && typeof note.why === 'string' && typeof note.at === 'number') {
        proNote = { why: note.why, at: note.at };
        const text = proNoteText(note.why);
        if (text && note.at > (deps.getNoteAt ? deps.getNoteAt(g.g) : 0)) {
          if (deps.setNoteAt) deps.setNoteAt(g.g, note.at);
          onProNote(text);
        }
      } else {
        proNote = null;
      }
    } catch {
      proNote = null;
    }
    emit();
  }

  // MARK: Mac -> phone: the snapshot

  /** The snapshot as the phone reads it, from main's input: runs, builds, asks, limits, health, notch. No `at` here. */
  function buildSnapshot(m, t) {
    const runs = (m.runs || []).map((r) => ({
      id: r.id, agent: r.agent ?? undefined, title: r.title, detail: r.detail,
      since: since.get(r.id) ?? t, icon: r.icon ?? undefined,
    }));
    const builds = (m.builds || []).map((r) => ({
      id: r.id, title: r.title, detail: r.detail, since: since.get(r.id) ?? t, icon: r.icon ?? undefined,
    }));
    const ids = new Set([...runs, ...builds].map((r) => r.id));
    for (const r of [...runs, ...builds]) since.set(r.id, r.since);
    for (const k of [...since.keys()]) if (!ids.has(k)) since.delete(k);
    const oldest = runs.length ? runs.reduce((a, c) => (c.since < a.since ? c : a)) : null;
    return {
      mac: macName,
      runs,
      builds,
      asks: (m.asks || []).map((a) => ({ ...a, at: new Date(a.at) })),
      limits: (m.limits || []).map((l) => ({
        provider: l.provider, label: l.label, used: Math.trunc(l.used),
        resets: new Date(Math.round(new Date(l.resets).getTime() / 60000) * 60000), // whole minutes
      })),
      health: healthOf(m.health),
      events,
      job: jobOf(builds, t),
      notch: m.notch ?? undefined,
      crew: oldest ? {
        count: runs.length, title: oldest.title, detail: bare(oldest.detail), since: oldest.since,
        agent: oldest.agent, icon: oldest.icon, members: members(runs),
      } : undefined,
    };
  }

  /** The longest build at work; when it stops, the same build as done (failed when CI said so) for 5 minutes. */
  function jobOf(builds, t) {
    if (builds.length) {
      const b = builds.reduce((a, c) => (c.since < a.since ? c : a));
      followed = b;
      ended = null;
      return { title: b.title, detail: bare(b.detail), since: b.since, state: 'running', icon: b.icon };
    }
    if (followed) {
      const f = followed;
      followed = null;
      ended = { title: f.title, detail: bare(f.detail), since: f.since, state: 'done', ended: t, icon: f.icon };
    }
    if (!ended || t.getTime() - ended.ended.getTime() >= LIVE_JOB_MS) { ended = null; return undefined; }
    const e = { ...ended };
    // a CI result lands around the time its job leaves the list
    if (events.some((ev) => ev.kind === 'ciFailed' && Math.abs(ev.at.getTime() - e.ended.getTime()) < 30000)) e.state = 'failed';
    return e;
  }

  /** Writes what changed to Firebase, and sends the LAN frame to proven phones at once. Called on every update and each second. */
  function publish() {
    const g = group;
    if (!g || !latest) return;
    const t = new Date(now());
    const s = buildSnapshot(latest, t);

    if (lanPhones > 0) {
      const j = JSON.stringify(s);
      if (j !== sentLocal) {
        sentLocal = j;
        const frame = box.frame(Buffer.from(box.seal(wire.snapshot({ ...s, at: t }), keyOf(g)), 'base64'));
        for (const c of conns.values()) if (c.ok) c.sock.write(frame);
      }
    }

    // phones on this Wi-Fi already have it: Firebase (and its push) only for the others
    const handled = new Set([...conns.values()].filter((c) => c.ok && c.phone).map((c) => c.phone));
    const uids = Object.keys(phones);
    const needsCloud = uids.length === 0 ? lanPhones === 0 : uids.some((u) => !handled.has(u));
    const away = !onlyAway || isAway();
    // just left (or came back) while asks wait: they may need a push now
    if (away !== sentAway && s.asks.length) sent = null;
    if (!needsCloud || sending || (sent && same(s, sent))) return;

    const askChanged = !sent || !same(s.asks, sent.asks);
    if (!askChanged) {
      // asks go at once; anything else only with Pro, and at most once a minute
      if (!pro || !proChanged(s, sent) || now() - lastWrite < WRITE_MS) return;
    }
    sending = true;
    lastWrite = now();
    writeCloud(g, s, t, handled, away).finally(() => { sending = false; });
  }

  function proChanged(s, prev) {
    const pick = (x) => [x.job ? x.job.state : null, ms(x.job?.since), x.crew ? x.crew.count : null, ms(x.crew?.since)];
    return !same(s.events, prev.events) || !same(pick(s), pick(prev)) || !same(s.notch, prev.notch);
  }

  async function writeCloud(g, s, t, handled, away) {
    try {
      await checkPro(g);
      const key = keyOf(g);
      const enc = wire.snapshot({ ...s, at: t });
      const newest = s.asks.reduce((a, c) => (!a || c.at > a.at ? c : a), null);
      const body = {
        box: box.seal(enc, key),
        at: { '.sv': 'timestamp' },
        asks: s.asks.length,
        newest: ms(newest?.at),
        direct: Object.fromEntries([...handled].map((u) => [u, true])),
        push: newest ? box.seal(wire.ask(wire.askForPush(newest)), key) : '',
        quiet: !away,
        eventAt, eventBox,
        job: enc.job ? box.seal(enc.job, key) : '',
        jobState: s.job ? s.job.state : '',
        jobAt: ms(s.job?.since),
        crew: enc.crew ? box.seal(enc.crew, key) : '',
        crewCount: s.crew ? s.crew.count : 0,
        crewAt: ms(s.crew?.since),
      };
      if (!pro) {
        // without Pro the phone gets only what the push needs: the rest stays on the Wi-Fi
        Object.assign(body, { box: '', job: '', eventBox: '', crew: '', jobState: '', eventAt: 0, jobAt: 0, crewCount: 0, crewAt: 0 });
      }
      sigCrew = crewSignal(body.crewCount, body.crewAt, sigCrew, now());
      // `sig`: the push function runs only when this changes (each run is billed)
      body.sig = {
        crewCount: sigCrew.count, crewAt: sigCrew.at,
        asks: body.asks, newest: body.newest, direct: body.direct, quiet: body.quiet,
        eventAt: body.eventAt, jobState: body.jobState, jobAt: body.jobAt,
      };
      await rest('PUT', `groups/${g.g}/snapshot`, g, body);
      if (group !== g) return; // unpaired meanwhile
      sent = s;
      sentAway = away;
      if (problem) setProblem(null);
    } catch (e) {
      setProblem(L('Không gửi được sang điện thoại: {0}', e.message));
    }
  }

  /** A notch announcement worth knowing on the phone (kind: taskDone, ciPassed, ciFailed, limitWarn, lowBattery, warning). */
  function record(t) {
    const g = group;
    if (!g || !(t.toPhone || ['ciPassed', 'ciFailed', 'limitWarn', 'lowBattery'].includes(t.kind))) return;
    const at = new Date(now());
    const ev = { id: crypto.randomUUID().toUpperCase(), kind: t.kind, title: t.title, detail: t.detail ?? '', at };
    events = [...events, ev].filter((e) => e.at.getTime() > now() - 86400000).slice(-20);
    // the push carries it only when away (limit warnings never wake the phone)
    if ((!onlyAway || isAway()) && t.kind !== 'limitWarn') {
      eventAt = at.getTime();
      eventBox = box.seal({ ...ev, at: toSwiftDate(at) }, keyOf(g));
    }
  }

  /** The app's snapshot input, from main, whenever the model changes. Rounding, compare and throttle happen in publish(). */
  function update(input) {
    latest = input;
    publish();
  }

  // MARK: Phone -> Mac: the answers stream

  function startStream() {
    stopStream();
    if (!running || !group) return;
    const ctl = new AbortController();
    streamCtl = ctl;
    streamLoop(group, ctl.signal);
  }

  function stopStream() {
    if (streamCtl) { streamCtl.abort(); streamCtl = null; }
  }

  async function streamLoop(g, signal) {
    let fails = 0;
    while (!signal.aborted) {
      try {
        const t = await idToken(g);
        const res = await doFetch(`${db}/groups/${g.g}/answers.json?auth=${t}`, {
          headers: { Accept: 'text/event-stream' }, signal,
        });
        if (!res.ok) throw new Error(`stream ${res.status}`);
        fails = 0;
        if ((await readEvents(res.body, g)) === 'revoked') token = null;
      } catch (e) {
        if (signal.aborted) return;
        fails++;
        setProblem(L('Mất kết nối với điện thoại, đang thử lại…'));
      }
      if (signal.aborted) return;
      await sleep(Math.min(60000, 5000 * 2 ** Math.max(fails - 1, 0)), signal);
    }
  }

  async function readEvents(body, g) {
    const reader = body.getReader();
    const dec = new TextDecoder();
    let buf = '', event = '', data = [];
    for (;;) {
      const { value, done } = await reader.read();
      if (done) return 'ended';
      buf += dec.decode(value, { stream: true });
      let i;
      while ((i = buf.indexOf('\n')) >= 0) {
        const line = buf.slice(0, i).replace(/\r$/, '');
        buf = buf.slice(i + 1);
        if (line === '') {
          if ((event === 'put' || event === 'patch') && data.length) await handleAnswers(data.join('\n'), g);
          event = ''; data = [];
        } else if (line.startsWith('event:')) {
          event = line.slice(6).trim();
          if (event === 'auth_revoked' || event === 'cancel') return 'revoked';
        } else if (line.startsWith('data:')) {
          data.push(line.slice(5).trimStart());
        }
      }
    }
  }

  /** "/" carries every answer (or null); "/<id>" one answer. Each is opened if fresh, then deleted (as Swift does). */
  async function handleAnswers(text, g) {
    let o;
    try { o = JSON.parse(text); } catch { return; }
    if (!o || typeof o.path !== 'string') return;
    const found = o.path === '/'
      ? (o.data && typeof o.data === 'object' ? o.data : {})
      : { [o.path.slice(1)]: o.data };
    for (const [id, v] of Object.entries(found)) {
      if (!v || typeof v !== 'object' || typeof v.box !== 'string') continue;
      if (typeof v.at === 'number' && (now() - v.at) / 1000 < WAIT) {
        try {
          applyAnswer(wire.readAnswer(box.open(v.box, keyOf(g))));
        } catch (e) {
          log(`answer: ${e.message}`);
        }
      }
      await rest('DELETE', `groups/${g.g}/answers/${id}`, g).catch(() => {});
    }
    if (problem) setProblem(null);
  }

  /** Refresh: the phone opened, the next publish writes everything. The rest goes to main, which replies, edits, times. */
  function applyAnswer(a) {
    if (a.refresh === true) { sent = null; publish(); return; }
    if (a.ask === '' && a.timer === undefined && a.notch === undefined) return; // the hello: nothing to apply
    onAnswer(a);
    publish();
  }

  // MARK: Same Wi-Fi: TCP frames plus mDNS

  function startLAN() {
    stopLAN();
    if (!running || !lan || !group) { emit(); return Promise.resolve(); }
    const g = group;
    const srv = netMod.createServer((sock) => accept(sock, g));
    server = srv;
    return new Promise((resolve) => {
      srv.on('error', (e) => { log(`lan: ${e.message}`); resolve(); });
      srv.listen(0, () => {
        if (server !== srv) { srv.close(); resolve(); return; } // stopped meanwhile
        lanPortNo = srv.address().port;
        try {
          mdns = mkMdns();
          mdns.publish({ name: box.serviceName(g.g), type: 'notchtasks', protocol: 'tcp', port: lanPortNo });
        } catch (e) {
          log(`mdns: ${e.message}`);
        }
        resolve();
      });
    });
  }

  function stopLAN() {
    for (const c of conns.values()) c.sock.destroy();
    conns.clear();
    if (server) { server.close(); server = null; }
    if (mdns) {
      try { mdns.unpublishAll(); mdns.destroy(); } catch (e) { log(`mdns: ${e.message}`); }
      mdns = null;
    }
    lanPortNo = null;
    lanPhones = 0;
  }

  function accept(sock, g) {
    if (conns.size >= MAX_CONNS) { sock.destroy(); return; }
    const id = ++connSeq;
    const c = { sock, ok: false, phone: null, timer: null };
    conns.set(id, c);
    sock.setNoDelay(true);
    sock.setKeepAlive(true, 3000);
    sock.on('error', () => sock.destroy());
    sock.on('close', () => {
      clearTimeout(c.timer);
      if (conns.delete(id) && c.ok) { lanPhones--; sent = null; emit(); }
    });
    // a connection that never says hello is not a phone
    c.timer = setTimeout(() => { if (!c.ok) sock.destroy(); }, helloMs);
    const reader = new box.FrameReader();
    sock.on('data', (chunk) => {
      let answers;
      try {
        answers = reader.push(chunk).map((f) => wire.readAnswer(JSON.parse(box.openBytes(f, keyOf(g)).toString('utf8'))));
      } catch (e) {
        log(`lan frame: ${e.message}`);
        sock.destroy();
        return;
      }
      for (const a of answers) {
        // the first frame is a sealed hello: opening it proves the phone holds the key
        if (a.phone) c.phone = a.phone;
        if (!c.ok) {
          c.ok = true;
          clearTimeout(c.timer);
          lanPhones++;
          sentLocal = null; // send everything now
          emit();
          publish();
        }
        if ((now() - a.at.getTime()) / 1000 < WAIT) applyAnswer(a);
      }
    });
  }

  // MARK: Problem reports (Reports.swift): the reports/ path, under the group or a fresh anonymous account. Returns its id.

  async function sendReport({ topic, text, contact, info = {} }) {
    let g = group;
    if (!g) {
      const acc = await signUp();
      g = { g: '', k: '', uid: acc.uid, refresh: acc.refresh };
    }
    const body = {
      uid: g.uid, g: String(g.g).slice(0, 100), role: 'mac', kind: 'problem', auto: false,
      text: `${topic}: ${text}`.slice(0, 2000), contact: String(contact).slice(0, 200),
      app: {
        version: String(info.version || '').slice(0, 80), build: String(info.build || '').slice(0, 80),
        os: String(info.os || '').slice(0, 80), model: 'PC', lang: String(info.lang || '').slice(0, 40),
      },
      pro: { own: false, product: '', tx: '', until: 0, group: pro === true, groupUntil: proUntil || 0 },
      context: { paired: !!group, phones: Object.keys(phones).length },
      at: { '.sv': 'timestamp' },
    };
    const res = JSON.parse(await rest('POST', 'reports', g, body));
    if (!res || typeof res.name !== 'string') throw new Error('no report id');
    return res.name;
  }

  // MARK: lifecycle

  async function start() {
    running = true;
    if (!timer) { timer = setInterval(publish, 1000); timer.unref(); }
    startStream();
    await startLAN();
    await loadPhones();
  }

  async function stop() {
    running = false;
    clearInterval(timer);
    timer = null;
    stopStream();
    stopLAN();
  }

  async function setLan(v) {
    lan = !!v;
    await startLAN();
    emit();
  }

  function setOnlyAway(v) {
    onlyAway = !!v;
    emit();
  }

  return {
    start, stop, pair, unpair, unpairAll, setLan, setOnlyAway, update, record, sendReport,
    group: () => (group ? { ...group } : null),
    lanPort: () => lanPortNo,
  };
}

module.exports = { createSync, crewSignal, bare, members, API_KEY, DB };
