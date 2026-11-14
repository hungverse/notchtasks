'use strict';
const fs = require('fs');
const path = require('path');

// Dev paths relative to the repo root; a packaged app finds them under process.resourcesPath.
const DEV = { Sprites: 'mac/Resources/Sprites', l10n: 'mac/Sources' };
const REPO = path.resolve(__dirname, '../../..');

function resourceDir(name) {
  const packed = process.resourcesPath && path.join(process.resourcesPath, name);
  if (packed && fs.existsSync(packed)) return packed;
  return path.join(REPO, DEV[name] || name);
}

module.exports = { resourceDir };
