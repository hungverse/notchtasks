'use strict';
// Port of Processes.swift SystemLoad, Thermals.swift, Extras.swift BatteryState and Sensors.swift NetInfo.
// CPU and memory come from node's os module. Temperatures come from WMI, read once: if that fails or answers
// nothing, thermalsAvailable stays false for good. Fans are not readable on Windows (fansAvailable is always false).
// Battery is not read here: the renderer (navigator.getBattery) or main (powerMonitor) feeds batteryFrom().

const fs = require('node:fs');
const os = require('node:os');
const { execFile } = require('node:child_process');

const THERMAL_SCRIPT = 'Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature'
  + ' | Select-Object InstanceName, CurrentTemperature | ConvertTo-Json -Compress';
const LOW_BATTERY = 20;
const BUCKETS = [100, 75, 50, 25, 0];
const VPN_NAME = /tun|tap|wg|wireguard|ppp|ipsec|vpn/i;
const WIFI_NAME = /wi-?fi|wlan|wireless/i;
const ETHERNET_NAME = /ethernet|local area/i;

const clamp01 = (v) => Math.min(1, Math.max(0, v));

// Cumulative ticks summed over all cores. Same shape as SystemLoad.cpuTicks(): idle and total.
function cpuTotals(cpus = os.cpus()) {
  let idle = 0;
  let total = 0;
  for (const { times: t } of cpus) {
    idle += t.idle;
    total += t.user + t.nice + t.sys + t.idle + t.irq;
  }
  return { idle, total };
}

// Busy share between two readings, as SystemLoad.cpu(from:to:).
function cpuLoad(from, to) {
  const total = to.total - from.total;
  return total > 0 ? clamp01((total - (to.idle - from.idle)) / total) : 0;
}

// shortcut: Windows' free figure counts standby cache as free, so this reads a little lower than Task Manager's
// "In use"; upgrade with a Get-Counter memory query if the numbers must match exactly.
function memoryLoad(total = os.totalmem(), free = os.freemem()) {
  return total > 0 ? clamp01((total - free) / total) : 0;
}

// Sync.swift sends CPU and memory in 5 % steps.
const stepPercent = (v) => (v == null ? null : Math.round(v * 20) * 5);

// ConvertTo-Json gives an object for one zone, an array for several, and nothing for none. tenths of Kelvin -> °C.
function parseThermalJson(text) {
  let data;
  try {
    data = JSON.parse(text);
  } catch {
    return [];
  }
  return [].concat(data ?? [])
    .filter((z) => z && typeof z.CurrentTemperature === 'number')
    .map((z) => ({ name: String(z.InstanceName ?? 'ACPI'), celsius: z.CurrentTemperature / 10 - 273.15 }))
    .filter((r) => r.celsius >= 5 && r.celsius <= 125);
}

const plausible = (readings) => readings.filter((r) => r.celsius >= 5 && r.celsius <= 125);

// The UI and the wire use the hottest plausible reading, as Thermals.hottest.
const hottest = (readings) => plausible(readings).reduce((best, r) => (!best || r.celsius > best.celsius ? r : best), null);

function readThermalsWmi() {
  return new Promise((resolve, reject) => {
    execFile('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command', THERMAL_SCRIPT],
      { windowsHide: true, timeout: 8000 }, (err, out) => (err ? reject(err) : resolve(parseThermalJson(out))));
  });
}

// BatteryState from the renderer's or powerMonitor's values. level is 0…1; plugged defaults to charging.
function batteryFrom({ level, charging, plugged = charging }) {
  const percent = Math.floor(clamp01(level) * 100 + 1e-6);
  const bucket = BUCKETS.find((b) => percent >= b - 12) ?? 0;
  return { percent, charging, plugged, symbol: plugged ? 'battery.100percent.bolt' : `battery.${bucket}percent` };
}

// The low-battery warning rule from Model.updateBattery: unplugged and 20 % or less.
const isLowBattery = (b) => !b.plugged && b.percent <= LOW_BATTERY;

// Online when some interface has a non-loopback address. VPN is a tunnel-named interface with IPv4.
// kind is the first non-VPN interface's guess by name: Windows calls them "Wi-Fi" and "Ethernet".
function netInfo(ifaces = os.networkInterfaces()) {
  let online = false;
  let vpn = false;
  let kind = null;
  for (const [name, addrs] of Object.entries(ifaces)) {
    const live = (addrs ?? []).filter((a) => !a.internal && !(a.family === 'IPv6' && a.address.startsWith('fe80')));
    if (!live.length) continue;
    online = true;
    if (VPN_NAME.test(name) && live.some((a) => a.family === 'IPv4')) {
      vpn = true;
      continue;
    }
    if (kind == null) kind = WIFI_NAME.test(name) ? 'wifi' : ETHERNET_NAME.test(name) ? 'ethernet' : 'other';
  }
  return { online, vpn, kind };
}

// Free bytes for the user's files, on the drive of the home folder.
function freeDisk(dir = os.homedir()) {
  try {
    const s = fs.statfsSync(dir);
    return s.bavail * s.bsize;
  } catch {
    return null;
  }
}

// readThermals must return readings (possibly none) or throw. platform is injectable for tests.
// sample() is pull-only: no timer runs. Calls within `interval` ms get the last snapshot back.
function createStats({ interval = 1000, platform = process.platform, readThermals = readThermalsWmi, now = Date.now } = {}) {
  let prev = cpuTotals();
  let thermalState = platform === 'win32' ? 'untried' : 'none'; // untried -> ok | none, and none is final
  let thermals = [];
  let last = null;
  let lastAt = 0;
  let inflight = null;
  let stopped = false;

  async function read() {
    const cur = cpuTotals();
    const cpu = cpuLoad(prev, cur);
    prev = cur;
    if (thermalState !== 'none') {
      try {
        thermals = await readThermals();
      } catch {
        thermals = [];
      }
      thermalState = thermals.length ? 'ok' : 'none';
    }
    return {
      cpu,
      memory: memoryLoad(),
      thermalsAvailable: thermalState === 'ok',
      thermals: thermalState === 'ok' ? thermals : [],
      hottest: thermalState === 'ok' ? hottest(thermals) : null,
      fansAvailable: false,
      net: netInfo(),
      freeDisk: freeDisk(),
    };
  }

  async function sample() {
    if (stopped) return null;
    if (last && now() - lastAt < interval) return last;
    if (!inflight) {
      inflight = read()
        .then((s) => { last = s; lastAt = now(); return s; })
        .finally(() => { inflight = null; });
    }
    return inflight;
  }

  return { sample, stop() { stopped = true; last = null; } };
}

module.exports = {
  cpuTotals, cpuLoad, memoryLoad, stepPercent, parseThermalJson, hottest, batteryFrom, isLowBattery,
  netInfo, freeDisk, createStats, readThermalsWmi, THERMAL_SCRIPT, LOW_BATTERY,
};
