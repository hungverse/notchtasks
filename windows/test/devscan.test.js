'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('fs');
const os = require('os');
const path = require('path');
const P = require('../src/core/proc');
const D = require('../src/core/devscan');
const W = require('../src/core/watchitems');
const { L } = require('../src/core/l10n');

const FIX = path.join(__dirname, 'fixtures', 'builds');
const readFix = (n) => fs.readFileSync(path.join(FIX, n), 'utf8');

// ---- process table -------------------------------------------------------

test('Win32_Process JSON: .exe names, null CommandLine, /Date(ms)/ dates', () => {
  const procs = P.parseWin32(readFix('win32-processes.json'));
  assert.equal(procs.length, 7);
  const node = procs.find((p) => p.pid === 812);
  assert.equal(node.name, 'node');                                   // "node.exe" -> "node"
  assert.equal(node.start.getTime(), 1731500600000);
  assert.match(node.cmdline, /next\\dist\\bin\\next build$/);
  const idle = procs.find((p) => p.pid === 0);
  assert.equal(idle, undefined, 'pid 0 is dropped');
  const sys = procs.find((p) => p.pid === 4);
  assert.equal(sys.cmdline, '');                                     // null CommandLine
  assert.equal(sys.cpuNs, 1200000 * 100);                            // 100 ns units
});

test('Win32_Process: a single object (one process) and ISO dates from pwsh', () => {
  const one = P.parseWin32('{"ProcessId":10,"ParentProcessId":4,"Name":"Cursor.exe","CreationDate":"2026-11-10T10:00:00+07:00","CommandLine":null}');
  assert.equal(one.length, 1);
  assert.equal(one[0].name, 'cursor');
  assert.ok(one[0].start instanceof Date && !isNaN(one[0].start));
  assert.deepEqual(P.parseWin32(''), []);
});

test('ps -axo pid,ppid,lstart,time,args: fields, CPU time, args', () => {
  const procs = P.parsePs(readFix('ps-processes.txt'));
  assert.equal(procs.length, 6);
  const next = procs.find((p) => p.pid === 812);
  assert.equal(next.ppid, 640);
  assert.equal(next.name, 'node');
  assert.equal(next.cmdline, 'node /Users/dev/proj/node_modules/next/dist/bin/next build');
  assert.equal(next.cpuNs, 91e9);                                    // 1:31.00
  const ff = procs.find((p) => p.pid === 5120);
  assert.equal(ff.name, 'ffmpeg');
  assert.equal(ff.cpuNs, ((1 * 86400) + 2 * 3600 + 3 * 60 + 4) * 1e9); // 1-02:03:04
  assert.equal(ff.start.getFullYear(), 2026);
});

test('splitArgs honours quotes; normName drops path and .exe', () => {
  assert.deepEqual(P.splitArgs('"C:\\Program Files\\x.exe" "a b" c'), ['C:\\Program Files\\x.exe', 'a b', 'c']);
  assert.equal(P.normName('C:\\Tools\\Unity.EXE'), 'unity');
  assert.equal(P.normName('/usr/bin/node'), 'node');
});

test('CpuMeter: percent between two samples, reused pid starts at 0', () => {
  const m = new P.CpuMeter();
  const a = { pid: 7, cpuNs: 0, start: new Date(1000) };
  m.sample([a], 0);
  m.sample([{ ...a, cpuNs: 1e9 }], 1000);           // 1 s CPU in 1 s wall = 100 %
  assert.equal(m.percent(7), 100);
  m.sample([{ pid: 7, cpuNs: 5e9, start: new Date(2000) }], 2000);  // same pid, new start time
  assert.equal(m.percent(7), 0);
});

// ---- DevRules ------------------------------------------------------------

test('DevRules.classify: Windows and cross-platform build commands', () => {
  const c = (name, argv) => D.classify(name, argv);
  assert.deepEqual(
    { tool: c('node', ['node', 'C:\\p\\node_modules\\next\\dist\\bin\\next', 'build']).tool, action: c('node', ['node', 'C:\\p\\node_modules\\next\\dist\\bin\\next', 'build']).action },
    { tool: 'next', action: 'build' });
  const ms = c('msbuild', ['MSBuild.exe', '/t:Restore', '/p:Configuration=Release', 'C:\\src\\Shop\\Shop.sln']);
  assert.equal(ms.tool, 'MSBuild');
  assert.equal(ms.action, 'Restore');
  assert.equal(ms.subject, 'Shop');
  assert.equal(c('dotnet', ['dotnet', 'build', '-c', 'Release']).action, 'build Release');  // first two words, as the Mac
  assert.equal(c('cargo', ['cargo', 'build', '--release']).tool, 'Cargo');
  assert.equal(c('cargo', ['cargo', 'run']), null, 'cargo run is not a finite task');
  assert.equal(c('git', ['git', 'clone', 'https://github.com/acme/app.git']).subject, 'app');
  assert.equal(c('git', ['git', 'status']), null);
  assert.equal(c('winget', ['winget.exe', 'upgrade', '--all']).tool, 'winget');
  assert.equal(c('python', ['python', '-m', 'pip', 'install', 'requests']).tool, 'pip');
  assert.equal(c('python', ['python', '-m', 'pip', 'list']), null);
  assert.equal(c('java', ['java', '-cp', 'gradle-wrapper.jar', 'org.gradle.wrapper.GradleWrapperMain', 'assembleDebug']).tool, 'Gradle');
  assert.equal(c('blender', ['blender', '-b', 'C:\\art\\hall.blend', '-a']).action, 'render animation');
  assert.equal(c('blender', ['blender', 'C:\\art\\hall.blend']), null, 'GUI Blender is not a render task');
  assert.equal(c('notepad', ['notepad']), null);
});

test('DevRules.node: package scripts, dev servers, and watch mode', () => {
  const n = (argv) => D.node(argv.slice(1));
  assert.deepEqual(n(['node', 'C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npm-cli.js', 'run', 'build']),
    { tool: 'npm', action: 'run build', symbol: 'hammer.fill' });
  assert.equal(n(['node', 'C:\\p\\node_modules\\vite\\bin\\vite.js', 'dev']), null, 'vite dev never ends');
  assert.equal(n(['node', 'C:\\p\\node_modules\\vite\\bin\\vite.js', 'build']).tool, 'vite');
  assert.equal(n(['node', '/p/node_modules/vitest/vitest.mjs', 'run']).tool, 'vitest');
  assert.equal(n(['node', '/p/node_modules/vitest/vitest.mjs', '--watch']), null);
  assert.equal(n(['node', 'server.js']), null, 'a plain script is not a dev task');
});

test('DevRules.project and base handle both separators', () => {
  assert.equal(D.project('C:\\work\\mobile-app\\ios'), 'mobile-app');
  assert.equal(D.project('/Users/dev/shop/web/'), 'shop');
  assert.equal(D.base('C:\\art\\hall.blend'), 'hall');
  assert.equal(D.folderFromArgv(['MSBuild.exe', '/t:Build', 'C:\\src\\Shop\\Shop.sln']), 'Shop');
});

// ---- TaskHistory ---------------------------------------------------------

test('TaskHistory: median of the last runs, progress and label', () => {
  const saved = [];
  const h = new D.TaskHistory({ load: () => ({ 'k': [10, 100, 60, 61] }), save: (o) => saved.push(o) });
  assert.equal(h.expected('k'), 61);                 // sorted [10,60,61,100], index 2
  assert.equal(h.expected('none'), null);
  h.record('k', 3);                                  // under 5 s: ignored
  assert.equal(saved.length, 0);
  h.record('k', 90);
  assert.deepEqual(h.runs.k, [10, 100, 60, 61, 90]);
  h.record('k', 200);
  assert.deepEqual(h.runs.k, [100, 60, 61, 90, 200], 'keeps the last 5');
  assert.equal(saved.length, 2);
  assert.deepEqual(D.TaskHistory.estimate(30, 60), [0.5, '0:30 · ≈ 30s' + L(' left')]);
  assert.deepEqual(D.TaskHistory.estimate(70, 60), [0.99, '1:10' + L(' · longer than usual')]);
  assert.deepEqual(D.TaskHistory.estimate(5, null), [null, '0:05']);
});

// ---- DevScanner start/finish diff -----------------------------------------

test('DevScanner: start and finish across two snapshots, history recorded', () => {
  const saved = [];
  const scanner = new D.DevScanner({ history: { load: () => ({}), save: (o) => saved.push(o) } });
  const procs = P.parseWin32(readFix('win32-processes.json'));
  const t0 = 1731500600000 + 10000;                   // next build started 10 s ago

  const first = scanner.scan(procs, t0);
  const next = first.find((j) => j.id === 'dev:812');
  assert.ok(next, 'next build is a running task');
  assert.equal(next.title, 'next · proj');
  assert.equal(next.tool, 'next');
  assert.match(next.detail, /^build · 0:10/);
  assert.ok(!first.some((j) => j.id === 'dev:2044'), 'jest worker belongs to the build');

  // a minute later the build process is gone
  const later = procs.filter((p) => p.pid !== 812 && p.pid !== 2044);
  const second = scanner.scan(later, t0 + 60000);
  assert.ok(!second.some((j) => j.id === 'dev:812'), 'finished task leaves the list');
  assert.equal(saved.length, 1);
  assert.ok(saved[0]['next|build|proj'].length === 1 && saved[0]['next|build|proj'][0] >= 60);
});

test('DevScanner: MSBuild shows as a task with its solution name', () => {
  const scanner = new D.DevScanner();
  const procs = P.parseWin32(readFix('win32-processes.json'));
  const jobs = scanner.scan(procs, 1731501000000 + 60000);
  const ms = jobs.find((j) => j.id === 'dev:3300');
  assert.ok(ms, 'MSBuild running');
  assert.equal(ms.title, 'MSBuild · Shop');
});

test('DevScanner: a process starting under 3 s is not a task, and git push calls onPush', () => {
  const pushed = [];
  const scanner = new D.DevScanner({ cwd: () => 'C:\\src\\shop', onPush: (d) => pushed.push(d) });
  const now = 1731501500000 + 1000;
  const procs = P.parseWin32(readFix('win32-processes.json'));
  const jobs = scanner.scan(procs, now);
  assert.deepEqual(pushed, ['C:\\src\\shop']);
  assert.ok(!jobs.some((j) => j.id === 'dev:6400'), 'git push is 1 s old: too quick to show');
  const later = scanner.scan(procs, now + 5000);
  assert.ok(later.some((j) => j.id === 'dev:6400' && j.title === 'Git · shop'));
});

test('DevScanner: busy-only render app shows after 10 s of load', () => {
  const scanner = new D.DevScanner();
  const procs = P.parseWin32(readFix('win32-processes.json'));
  // blender uses CPU: user time grows 1 s per 1 s of wall clock (= 100 % of a core, tree sum > 150 needed)
  // cumulative CPU of the GUI Blender (no -b): 4 s of CPU per 1 s of wall clock = 400 %
  const at = (t) => procs.map((p) => (p.pid === 5120 ? { ...p, cpuNs: (t - 1000) * 4e6 } : p));
  let jobs = [];
  for (let t = 1000; t <= 14000; t += 1000) jobs = scanner.scan(at(t), t);
  const rendered = jobs.find((j) => j.id === 'app:5120');
  assert.ok(rendered, 'render shows once sustained for 10 s');
  assert.equal(rendered.tool, 'Blender');
  assert.equal(scanner.scan(at(15000), 15000).some((j) => j.id === 'app:5120'), true);
  // load stops: stays for 8 s after the last busy sample, then goes
  const idle = (t) => procs.map((p) => (p.pid === 5120 ? { ...p, cpuNs: 14000 * 4e6 } : p));
  assert.equal(scanner.scan(idle(16000), 16000).some((j) => j.id === 'app:5120'), true);
  assert.equal(scanner.scan(idle(30000), 30000).some((j) => j.id === 'app:5120'), false);
});

// ---- watch items --------------------------------------------------------

test('WatchItems: .exe and case are ignored when matching processes', () => {
  const it = W.item({ name: 'Ollama', kind: 'process', process: 'ollama' });
  assert.equal(W.matches(it, { pid: 9, name: 'ollama', cmdline: 'ollama.exe serve' }), true);
  assert.equal(W.matches(it, { pid: 9, name: 'Ollama', cmdline: '' }), true, 'normalized process name in the item');
  const gradle = W.item({ name: 'Gradle daemon', kind: 'process', process: 'java', args: 'GradleDaemon' });
  assert.equal(W.matches(gradle, { pid: 3, name: 'java', cmdline: 'java.exe -cp x GradleDaemon' }, undefined, 1), true);
  const anyArgs = W.item({ name: 'Aider', kind: 'process', args: 'bin/aider' });
  assert.equal(W.matches(anyArgs, { pid: 4, name: 'zsh', cmdline: 'zsh -c bin/aider' }, undefined, 1), false, 'shell mentions are not the tool');
  assert.equal(W.matches(anyArgs, { pid: 5, name: 'python', cmdline: 'python C:\\x\\bin\\aider' }, undefined, 1), true, 'backslashes count as slashes');
});

test('WatchItems: presets include Windows build tools and summaries read', () => {
  assert.ok(W.buildPresets.some((i) => i.process === 'msbuild' && i.build));
  assert.ok(W.buildPresets.some((i) => i.name === 'Visual Studio'));
  assert.equal(W.summary(W.item({ name: 'x', kind: 'app', threshold: 30 })), L('App · {0}', 'CPU > 30%'));
  assert.equal(W.inBuild(W.buildItem), true);
});

test('FileProgressWatcher: partial download appears, speed from growth, gone when stalled', () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-dl-'));
  try {
    const f = path.join(dir, 'setup.exe.crdownload');
    fs.writeFileSync(f, Buffer.alloc(6e6));
    let t = Date.now();
    const w = new D.FileProgressWatcher({ folders: [dir], now: () => t });
    const first = w.jobs();
    assert.equal(first.length, 1, 'a 6 MB partial file is a transfer');
    assert.equal(first[0].title, L('Downloads') + ' · setup.exe.crdownload');
    assert.equal(first[0].detail, '6.0 MB');
    fs.appendFileSync(f, Buffer.alloc(1e6));
    t += 1000;
    const growing = w.jobs();
    assert.equal(growing[0].detail, '7.0 MB · 1.0 MB/s');
    t += 9000;                                       // no change for 9 s: stalled, hidden
    assert.equal(w.jobs().length, 0);
    fs.renameSync(f, path.join(dir, 'setup.exe'));   // finished files are not transfers any more
    assert.equal(w.jobs().length, 0);
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
