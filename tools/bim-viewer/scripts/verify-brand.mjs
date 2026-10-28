import { readFile, readdir } from 'node:fs/promises';
import path from 'node:path';
const blocked = /\u76c8\u5609|boswinner|boswiner|bosrevit|\bbos\b/iu;
const targets = ['dist', 'electron', 'native', 'src', 'README.md', 'THIRD-PARTY-NOTICES.md', 'package.json'];
async function inspect(file) {
  const entries = await readdir(file, { withFileTypes: true }).catch(() => null);
  if (entries) { for (const entry of entries) await inspect(path.join(file, entry.name)); return; }
  if (/\.(?:ts|js|cjs|mjs|css|html|json|md|cs)$/i.test(file) && blocked.test(await readFile(file, 'utf8'))) throw new Error(`旧品牌残留：${file}`);
}
for (const target of targets) await inspect(target);
console.log('Product source, interface, converter, notices and built assets passed the branding check.');
