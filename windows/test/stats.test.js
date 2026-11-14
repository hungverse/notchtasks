'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const stats = require('../src/core/stats');
const { finishedNames, inProgressNames, createDownloads } = require('../src/core/downloads');

const cpu = (user, sys, idle, nice = 0, irq = 0) => ({ times: { user, sys, idle, nice, irq } });

test('CPU load is the busy share of the tick delta across all cores', () => {
  const a = stats.cpuTotals([cpu(100, 100, 800), cpu(100, 100, 800)]);
  const b = stats.cpuTotals([cpu(250, 150, 1000), cpu(150, 100, 1100)]);
  // core 1: +400 ticks, 200 idle; core 2: +350 ticks, 300 idle. Busy 250 of 750.
  assert.equal(stats.cpuLoad(a, b), 250 / 750);
});

test('CPU load is 0 when nothing ticked, and never leaves 0…1', () => {
  const a = stats.cpuTotals([cpu(10, 10, 80)]);
  assert.equal(stats.cpuLoad(a, a), 0);
  assert.equal(stats.cpuLoad(a, stats.cpuTotals([cpu(10, 10, 80, 0, 5)])) <= 1, true);
});

test('CPU counts nice and irq as busy time, idle only as idle', () => {
  const a = stats.cpuTotals([cpu(0, 0, 0)]);
  const b = stats.cpuTotals([cpu(0, 0, 50, 25, 25)]);
  assert.equal(stats.cpuLoad(a, b), 0.5);
});

test('memory load is used over total, clamped', () => {
  assert.equal(stats.memoryLoad(1000, 250), 0.75);
  assert.equal(stats.memoryLoad(1000, 2000), 0);
  assert.equal(stats.memoryLoad(0, 0), 0);
});

test('steps are 5 % steps, as Sync.swift rounds them', () => {
  assert.equal(stats.stepPercent(0.37), 35);
  assert.equal(stats.stepPercent(0.38), 40);
  assert.equal(stats.stepPercent(null), null);
});

test('WMI thermal JSON: one zone, several zones, nothing, garbage', () => {
  // tenths of Kelvin: 3131 -> 39.95 °C
  const one = stats.parseThermalJson('{"InstanceName":"ACPI\\\\THM0","CurrentTemperature":3131}');
  assert.equal(one.length, 1);
  assert.equal(one[0].name, 'ACPI\\THM0');
  assert.ok(Math.abs(one[0].celsius - 39.95) < 1e-9);

  const many = stats.parseThermalJson('[{"InstanceName":"a","CurrentTemperature":3131},{"InstanceName":"b","CurrentTemperature":3231}]');
  assert.deepEqual(many.map((r) => r.name), ['a', 'b']);

  assert.deepEqual(stats.parseThermalJson(''), []);
  assert.deepEqual(stats.parseThermalJson('not json'), []);
  assert.deepEqual(stats.parseThermalJson('null'), []);
  // 2000 -> -73 °C and 4000 -> 126.85 °C are outside 5…125 and dropped
  assert.deepEqual(stats.parseThermalJson('[{"InstanceName":"x","CurrentTemperature":2000},{"InstanceName":"y","CurrentTemperature":4000}]'), []);
});

test('hottest picks the hottest plausible reading', () => {
  const r = [{ name: 'a', celsius: 53 }, { name: 'b', celsius: 68.8 }, { name: 'bad', celsius: 130 }];
  assert.equal(stats.hottest(r).name, 'b');
  assert.equal(stats.hottest([{ name: 'bad', celsius: 130 }]), null);
});

test('thermals are tried once on Windows: an empty or failed read is final', async () => {
  let calls = 0;
  const failing = stats.createStats({ interval: 0, platform: 'win32', readThermals: async () => { calls++; throw new Error('wmi'); } });
  const first = await failing.sample();
  const second = await failing.sample();
  assert.equal(first.thermalsAvailable, false);
  assert.equal(second.thermalsAvailable, false);
  assert.equal(calls, 1);
  failing.stop();

  let empty = 0;
  const none = stats.createStats({ interval: 0, platform: 'win32', readThermals: async () => { empty++; return []; } });
  await none.sample();
  await none.sample();
  assert.equal(empty, 1);
});

test('readable thermals keep being read; fans are never available', async () => {
  let calls = 0;
  const s = stats.createStats({ interval: 0, platform: 'win32',
    readThermals: async () => { calls++; return [{ name: 'THM0', celsius: 44 }]; } });
  const snap = await s.sample();
  await s.sample();
  assert.equal(snap.thermalsAvailable, true);
  assert.equal(snap.hottest.name, 'THM0');
  assert.equal(snap.fansAvailable, false);
  assert.equal(calls, 2);
});

test('off Windows thermals are unavailable and nothing is read', async () => {
  let calls = 0;
  const s = stats.createStats({ platform: 'darwin', readThermals: async () => { calls++; return [{ name: 'x', celsius: 40 }]; } });
  const snap = await s.sample();
  assert.equal(snap.thermalsAvailable, false);
  assert.equal(snap.fansAvailable, false);
  assert.equal(calls, 0);
});

test('sample() is pull-only: cached inside the interval, null after stop()', async () => {
  let t = 1000;
  const s = stats.createStats({ interval: 500, platform: 'linux', now: () => t });
  const a = await s.sample();
  const b = await s.sample();
  assert.equal(a, b);
  t += 600;
  assert.notEqual(await s.sample(), a);
  s.stop();
  assert.equal(await s.sample(), null);
});

test('battery mapping and the low-battery rule', () => {
  const low = stats.batteryFrom({ level: 0.2, charging: false });
  assert.deepEqual(low, { percent: 20, charging: false, plugged: false, symbol: 'battery.25percent' });
  assert.equal(stats.isLowBattery(low), true);

  const plugged = stats.batteryFrom({ level: 0.1, charging: true });
  assert.equal(plugged.symbol, 'battery.100percent.bolt');
  assert.equal(stats.isLowBattery(plugged), false);

  assert.equal(stats.batteryFrom({ level: 0.29, charging: false }).percent, 29);
  assert.equal(stats.batteryFrom({ level: 0.21, charging: false }).percent, 21);
  assert.equal(stats.isLowBattery(stats.batteryFrom({ level: 0.21, charging: false })), false);
});

test('network: online by any routable address, VPN by tunnel name, kind by name', () => {
  const v4 = (address) => ({ address, family: 'IPv4', internal: false });
  const lo = { address: '127.0.0.1', family: 'IPv4', internal: true };
  const linkLocal = { address: 'fe80::1', family: 'IPv6', internal: false };

  assert.deepEqual(stats.netInfo({ lo: [lo], 'Ethernet 2': [linkLocal] }), { online: false, vpn: false, kind: null });
  assert.deepEqual(stats.netInfo({ 'Wi-Fi': [v4('192.168.1.5')] }), { online: true, vpn: false, kind: 'wifi' });
  assert.deepEqual(stats.netInfo({ 'WireGuard Tunnel': [v4('10.0.0.2')], Ethernet: [v4('192.168.0.9')] }),
    { online: true, vpn: true, kind: 'ethernet' });
});

test('free disk is a positive byte count for the home folder', () => {
  const free = stats.freeDisk();
  assert.ok(free === null || free > 0);
  assert.equal(stats.freeDisk('/definitely/not/here'), null);
});

test('downloads: partial files are in progress, dot files ignored', () => {
  const names = ['a.pdf', 'b.crdownload', 'c.partial', 'd.part', '.hidden', 'e.zip'];
  assert.deepEqual(finishedNames(names), ['a.pdf', 'e.zip']);
  assert.deepEqual(inProgressNames(names), ['b.crdownload', 'c.partial', 'd.part']);
});

test('downloads: the first scan is a baseline, later new files are added', async () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'nt-dl-'));
  try {
    fs.writeFileSync(path.join(dir, 'old.zip'), 'x');
    const seen = [];
    const d = createDownloads({ folder: dir, debounceMs: 0, onChange: (e) => seen.push(e) });
    assert.equal(d.start(), true);
    assert.deepEqual(seen[0].added, []);
    fs.writeFileSync(path.join(dir, 'new.pdf.crdownload'), 'x');
    d.scan();
    assert.deepEqual(seen[1].inProgress, ['new.pdf.crdownload']);
    fs.renameSync(path.join(dir, 'new.pdf.crdownload'), path.join(dir, 'new.pdf'));
    d.scan();
    assert.deepEqual(seen[2].added, ['new.pdf']);
    d.stop();
  } finally {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});
