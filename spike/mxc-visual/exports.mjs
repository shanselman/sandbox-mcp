// Read PE export tables without loading DLLs or invoking private entry points.
import fs from 'node:fs';
import path from 'node:path';

function inspect(file) {
  const b = fs.readFileSync(file);
  if (b.toString('ascii', 0, 2) !== 'MZ') throw new Error('Not a PE file');
  const pe = b.readUInt32LE(0x3c);
  if (b.readUInt32LE(pe) !== 0x4550) throw new Error('Invalid PE signature');
  const optional = pe + 24;
  const magic = b.readUInt16LE(optional);
  if (![0x10b, 0x20b].includes(magic)) throw new Error('Unknown optional header');
  const directory = optional + (magic === 0x20b ? 112 : 96);
  const exportRva = b.readUInt32LE(directory);
  const exportSize = b.readUInt32LE(directory + 4);
  const sections = [];
  for (let i = 0; i < b.readUInt16LE(pe + 6); i++) {
    const s = optional + b.readUInt16LE(pe + 20) + i * 40;
    sections.push({ rva: b.readUInt32LE(s + 12), size: Math.max(b.readUInt32LE(s + 8), b.readUInt32LE(s + 16)), raw: b.readUInt32LE(s + 20) });
  }
  const offset = (rva) => {
    const s = sections.find(s => rva >= s.rva && rva < s.rva + s.size);
    if (!s) throw new Error(`Unmapped RVA ${rva}`);
    return s.raw + rva - s.rva;
  };
  const str = (rva) => { const p = offset(rva); return b.toString('ascii', p, b.indexOf(0, p)); };
  const exports = [];
  if (exportRva) {
    const e = offset(exportRva);
    const names = offset(b.readUInt32LE(e + 32));
    const ordinals = offset(b.readUInt32LE(e + 36));
    const functions = offset(b.readUInt32LE(e + 28));
    for (let i = 0; i < b.readUInt32LE(e + 24); i++) {
      const name = str(b.readUInt32LE(names + i * 4));
      const rva = b.readUInt32LE(functions + b.readUInt16LE(ordinals + i * 2) * 4);
      if (/headless|isolat|agent|sandbox|microvm|Hcs|WHv|vmcreat|vmstart/i.test(name))
        exports.push({ name, forward: rva >= exportRva && rva < exportRva + exportSize ? str(rva) : null });
    }
  }
  return { file, machine: `0x${b.readUInt16LE(pe + 4).toString(16)}`, exports };
}

let failed = false;
const results = process.argv.slice(2).map(file => {
  try { return inspect(path.resolve(file)); }
  catch (error) { failed = true; return { file, error: error.message }; }
});
console.log(JSON.stringify(results, null, 2));
if (failed) process.exitCode = 1;
