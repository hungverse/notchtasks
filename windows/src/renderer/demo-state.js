'use strict';
// A rich sample of what main sends the pill, for demo.js and the screenshots. CommonJS (main process).
const path = require('path');

const sprites = path.resolve(__dirname, '../../../mac/Resources/Sprites');
const now = Date.now();
const min = 60 * 1000;

function demoState() {
  return {
    lang: 'vi',
    expanded: false,
    settings: {
      modules: ['agents', 'ci', 'media', 'calendar', 'timer', 'shelf', 'limits', 'battery', 'devices', 'calls',
        'privacy', 'levels', 'network', 'downloads', 'health', 'mirror'],
      disabled: [],
      expandMode: 'hover',
      hoverDelay: 0.15,
      haptics: true,
      systemLoad: true,
      sounds: true,
      systemNotifications: true,
      size: 'compact',
      accent: 'auto',
      customAccent: '#FF9F0A',
      menuBarIcon: true,
      doneAfter: 30,
      limitWarn: 80,
      background: 'black',
      gradientA: '#1E1B4B',
      gradientB: '#000000',
      toastStyle: 'island',
      motion: 'bouncy',
      moduleColors: {},
      placements: {},
      badges: [
        { id: 'badge-cat', sprite: 'px-cat', symbol: 'star.fill', shape: 'circle', border: false, right: false },
        { id: 'badge-fox', sprite: 'ip-fox', symbol: 'star.fill', shape: 'circle', border: false, right: true },
      ],
      lockShows: ['lock'],
      toolIcons: {},
    },
    items: [
      { id: 'claude', name: 'Claude', kind: 'claude', enabled: true, symbolName: 'sparkles' },
      { id: 'codex', name: 'Codex', kind: 'codex', enabled: true, symbolName: 'terminal.fill' },
      { id: 'build', name: 'Build & CI', kind: 'dev', enabled: true },
    ],
    states: [
      {
        item: { id: 'claude', name: 'Claude', kind: 'claude', symbolName: 'sparkles' },
        jobs: [
          { id: 'c1', title: 'Sửa AgentAsks.swift', detail: '3/5', working: true, progress: 0.6, symbol: 'pencil' },
          { id: 'c2', title: 'Chạy unit test', detail: 'đang chạy', working: true, symbol: 'checkmark.seal.fill' },
        ],
      },
      {
        item: { id: 'codex', name: 'Codex', kind: 'codex', symbolName: 'terminal.fill' },
        jobs: [
          { id: 'x1', title: 'Viết migration', detail: '1m 20s', working: true, estimated: true, progress: 0.35 },
        ],
      },
    ],
    limits: [
      {
        kind: 'claude', name: 'Claude', bundleID: '', updated: now,
        windows: [
          { label: '5h', used: 72, resets: now + 2 * 60 * min },
          { label: '7d', used: 30, resets: now + 3 * 24 * 60 * min },
        ],
      },
    ],
    alerts: [
      { id: '4721', name: 'test:unit', project: 'notchtasks', reason: 'Job failed: exit code 1', url: 'https://example.com/job/4721' },
    ],
    nowPlaying: {
      source: 'com.spotify.client', appName: 'Spotify', title: 'Bài hát mẫu', artist: 'Nghệ sĩ mẫu',
      playing: true, position: 72, duration: 215, artwork: null,
      item: { id: 'spotify', name: 'Spotify', kind: 'app' },
    },
    toast: null,
    updatePending: true,
    asks: [
      {
        id: 'ask-bash', pid: 1, session: 'NotchTasks-windows', tool: 'Bash', summary: 'rm -rf build',
        questions: [], at: now - 42 * 1000, agent: 'claude',
      },
      {
        id: 'ask-codex', pid: 2, session: 'Job', tool: 'AskUserQuestion', summary: 'Chọn thư viện và tính năng',
        at: now - 9 * 1000, agent: 'codex',
        questions: [
          { question: 'Dùng thư viện nào?', header: 'Thư viện', options: ['Chart.js', 'D3', 'Không dùng'], multiSelect: false },
          { question: 'Tính năng cần có?', header: 'Tính năng', options: ['Chế độ tối', 'Xuất PDF', 'Đa ngôn ngữ'], multiSelect: true },
        ],
      },
    ],
    battery: { percent: 64, plugged: false },
    load: { cpu: 0.23, memory: 0.61 },
    thermals: [],
    timer: { end: now + 9 * min, total: 15 * 60 },
    shelf: [
      path.join(sprites, 'px-cat.png'),
      path.join(sprites, 'ip-fox.png'),
      'C:\\Users\\demo\\Documents\\ghi-chu-tuan.txt',
    ],
    sync: { paired: false, phones: [], direct: false },
  };
}

module.exports = { demoState };
