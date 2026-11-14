'use strict';
const test = require('node:test');
const assert = require('node:assert');
const fs = require('fs');
const path = require('path');
const l10n = require('../src/core/l10n');
const { resourceDir } = require('../src/core/resources');

test('match picks the app language', () => {
  assert.strictEqual(l10n.match(['zh-TW']), 'zh-Hant');
  assert.strictEqual(l10n.match(['zh-Hans-CN']), 'zh-Hans');
  assert.strictEqual(l10n.match(['zh-CN']), 'zh-Hans');
  assert.strictEqual(l10n.match(['pt-PT']), 'pt-BR');
  assert.strictEqual(l10n.match(['ja-JP']), 'ja');
  assert.strictEqual(l10n.match(['xx']), 'en');
  assert.strictEqual(l10n.match(['sw', 'vi-VN']), 'vi');
  assert.strictEqual(l10n.match([]), 'en');
});

test('L returns the English key in en', () => {
  l10n.setChoice('en');
  l10n.setTesting(false);
  assert.strictEqual(l10n.L('Run command'), 'Run command');
  assert.strictEqual(l10n.L('{0} requests waiting for you', 3), '3 requests waiting for you');
  l10n.setChoice('');
});

test('vi is a translation table; testing mode shows the English keys', () => {
  l10n.setChoice('vi');
  assert.strictEqual(l10n.L('Run command'), 'Chạy lệnh');
  l10n.setChoice('en');
  l10n.setTesting(true);
  assert.strictEqual(l10n.code(), 'en');
  assert.strictEqual(l10n.L('Run command'), 'Run command');
  l10n.setTesting(false);
  l10n.setChoice('');
});

test('a Vietnamese text (old key, saved value) maps to its English key', () => {
  l10n.setChoice('fr');
  assert.strictEqual(l10n.L('Đang chờ bạn'), l10n.L('Waiting for you'));
  assert.notStrictEqual(l10n.L('Đang chờ bạn'), 'Đang chờ bạn');
  l10n.setChoice('');
});

test('unknown key returns the key with args filled', () => {
  l10n.setChoice('en');
  assert.strictEqual(l10n.L('Khóa không có {0} và {1}', 'a', 2), 'Khóa không có a và 2');
  l10n.setChoice('');
});

test('setChoice notifies listeners', () => {
  let seen = null;
  l10n.onChange((c) => { seen = c; });
  l10n.setChoice('de');
  assert.strictEqual(l10n.getChoice(), 'de');
  assert.strictEqual(seen, 'de');
  l10n.setChoice('');
});

test('all 15 tables but en load and are non-empty', () => {
  const dir = resourceDir('l10n');
  const codes = l10n.languages.map((x) => x.code).filter((c) => c !== 'en');
  assert.strictEqual(codes.length, 15);
  for (const c of codes) {
    assert.ok(fs.existsSync(path.join(dir, `l10n-${c}.json`)), `missing table ${c}`);
    assert.ok(Object.keys(l10n.table(c)).length > 0, `empty table ${c}`);
  }
});
