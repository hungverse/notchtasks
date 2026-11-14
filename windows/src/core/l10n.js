'use strict';
// Port of Sources/L10n.swift. English text is the key; tables are l10n-<code>.json (every code but en).
const fs = require('fs');
const path = require('path');
const { resourceDir } = require('./resources');

const languages = [
  { code: 'en', name: 'English' }, { code: 'vi', name: 'Tiếng Việt' },
  { code: 'zh-Hans', name: '简体中文' }, { code: 'zh-Hant', name: '繁體中文' }, { code: 'ja', name: '日本語' },
  { code: 'ko', name: '한국어' }, { code: 'es', name: 'Español' }, { code: 'fr', name: 'Français' },
  { code: 'de', name: 'Deutsch' }, { code: 'pt-BR', name: 'Português (Brasil)' },
  { code: 'it', name: 'Italiano' }, { code: 'ru', name: 'Русский' }, { code: 'id', name: 'Bahasa Indonesia' },
  { code: 'th', name: 'ไทย' }, { code: 'tr', name: 'Türkçe' }, { code: 'hi', name: 'हिन्दी' },
];

let choiceCode = ''; // "" = follow the device
let testing = false;
const listeners = [];
const cache = new Map();

// Intl.Locale rejects junk like "C" or "POSIX"; those are skipped.
function parseLocale(tag) {
  try { return new Intl.Locale(tag); } catch { return null; }
}

// The first of the device's languages the app speaks; English when none.
function match(preferred) {
  for (const p of preferred) {
    const l = parseLocale(p);
    if (!l || !l.language) continue;
    if (l.language === 'zh') {
      const hant = l.script === 'Hant' || ['TW', 'HK', 'MO'].includes(l.region);
      return hant ? 'zh-Hant' : 'zh-Hans';
    }
    if (l.language === 'pt') return 'pt-BR';
    const hit = languages.find((x) => x.code === l.language);
    if (hit) return hit.code;
  }
  return 'en';
}

// Node's LANG is "fr_FR.UTF-8"; turn it into "fr-FR" before matching.
function device() {
  const env = (process.env.LANG || '').split('.')[0].replace(/_/g, '-');
  const preferred = [Intl.DateTimeFormat().resolvedOptions().locale];
  if (env) preferred.push(env);
  return match(preferred);
}

function readJson(file) {
  try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch { return {}; }
}

// Base table from Sources, Windows-only strings merged over it.
function table(code) {
  if (cache.has(code)) return cache.get(code);
  const base = readJson(path.join(resourceDir('l10n'), `l10n-${code}.json`));
  const extra = readJson(path.join(__dirname, '../../l10n/windows.json'))[code] || {};
  const t = { ...base, ...extra };
  cache.set(code, t);
  return t;
}

function code() {
  return testing ? 'en' : choiceCode || device();
}

// A Vietnamese text (old key, saved value, data) maps to its English key first; l10n-legacy.json (rekey.py).
function text(key, lang) {
  const k = table('legacy')[key] ?? key;
  if (lang === 'en') return k;
  return table(lang)[k] ?? k;
}

function fill(s, args) {
  return args.reduce((out, a, i) => out.split(`{${i}}`).join(String(a)), s);
}

// A text in the app's language. {0}, {1}… take the arguments in order.
function L(key, ...args) {
  return fill(text(key, code()), args);
}

function getChoice() { return choiceCode; }

function setChoice(c) {
  choiceCode = c || '';
  for (const fn of listeners) fn(choiceCode);
}

function onChange(fn) { listeners.push(fn); }

function setTesting(on) { testing = !!on; }

module.exports = { languages, match, device, table, text, fill, code, L, getChoice, setChoice, onChange, setTesting };
