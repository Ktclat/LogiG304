'use strict';
// Operates only on the exact supported, caller-provided local archive.
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const asar = require('asar');
const originalHash = 'ddea4da4a7d4833819dcf3e89b430889f7ec10a4462677cb526a5df558e6d4a0';
const offlineHash = '89a2d868a6540f2eb51f53ea28a765728d4e69497eaaf386c97577ae607d38bc';
const hash = data => crypto.createHash('sha256').update(data).digest('hex');

async function main() {
  if (process.argv.length !== 4) throw new Error('Usage: node scripts/patch-asar.cjs input.asar new-output.asar');
  const input = path.resolve(process.argv[2]);
  const output = path.resolve(process.argv[3]);
  if (fs.existsSync(output) || input.toLowerCase() === output.toLowerCase()) throw new Error('Output must be a new file. The original archive is never changed.');
  if (hash(fs.readFileSync(input)) !== originalHash) throw new Error('Unsupported original archive hash.');
  const parent = path.dirname(output);
  fs.mkdirSync(parent, {recursive:true});
  const staging = fs.mkdtempSync(path.join(parent, 'logig304-asar-'));
  const temporary = path.join(staging, 'built.asar');
  const extracted = path.join(staging, 'source');
  try {
    asar.extractAll(input, extracted);
    for (const name of ['main.js', 'app.min.js']) {
      const file = path.join(extracted, name);
      const original = fs.readFileSync(file, 'utf8');
      const initializer = /dsn:"https:\/\/[^"\s]+sentry\.io\/\d+"/g;
      const matches = [...original.matchAll(initializer)];
      if (matches.length !== 1) throw new Error('Unexpected telemetry initializer count: ' + name);
      let patched = original.replace(initializer, 'dsn:"",enabled:!1,autoSessionTracking:!1,defaultIntegrations:[]');
      if (name === 'main.js') patched = 'require("./offline-mode.cjs");\n' + patched;
      fs.writeFileSync(file, patched);
    }
    const policy = fs.readFileSync(path.join(__dirname, '../frontend/offline-mode.cjs'), 'utf8').replace(/\r\n/g, '\n');
    fs.writeFileSync(path.join(extracted, 'offline-mode.cjs'), policy);
    await asar.createPackage(extracted, temporary);
    if (hash(fs.readFileSync(temporary)) !== offlineHash) throw new Error('Result differs from the tested offline archive; no output published.');
    for (const item of asar.listPackage(input)) {
      const name = item.replace(/^[\\/]/, '').replace(/\\/g, '/');
      if (asar.statFile(input, name).files || ['main.js', 'app.min.js'].includes(name)) continue;
      if (hash(asar.extractFile(input,name)) !== hash(asar.extractFile(temporary,name))) throw new Error('Unrelated resource changed: ' + name);
    }
    fs.copyFileSync(temporary, output, fs.constants.COPYFILE_EXCL);
    console.log(JSON.stringify({success:true,originalSha256:originalHash,patchedSha256:offlineHash}));
  } finally {
    if (path.dirname(staging) !== parent || !path.basename(staging).startsWith('logig304-asar-')) throw new Error('Temporary cleanup boundary mismatch');
    fs.rmSync(staging, {recursive:true,force:true});
  }
}
main().catch(error => {console.error(error.message);process.exitCode=1;});
