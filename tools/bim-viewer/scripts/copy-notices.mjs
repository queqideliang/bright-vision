import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdir, readFile, writeFile, stat } from 'node:fs/promises';

const root = fileURLToPath(new URL('../', import.meta.url));
const output = path.join(root, 'dist', 'licenses');
try {
  await stat(path.join(root, 'dist', 'index.html'));
} catch {
  throw new Error('请先运行 Vite 构建，再复制分发许可。');
}

const packages = [
  { name: 'three', license: 'LICENSE' },
  { name: 'fflate', license: 'LICENSE' },
  { name: 'vite', license: 'LICENSE.md' },
];
const contents = await Promise.all(packages.map(async ({ name, license }) => {
  const packageRoot = path.join(root, 'node_modules', name);
  const metadata = JSON.parse(await readFile(path.join(packageRoot, 'package.json'), 'utf8'));
  return { name, version: metadata.version, text: await readFile(path.join(packageRoot, license), 'utf8') };
}));
const notice = await readFile(path.join(root, 'THIRD-PARTY-NOTICES.md'), 'utf8');
const productLicense = await readFile(path.join(root, 'LICENSE'), 'utf8');
const companyNotice = await readFile(path.join(root, 'NOTICE'), 'utf8');
await mkdir(output, { recursive: true });
await writeFile(path.join(output, 'THIRD-PARTY-NOTICES.md'), notice);
await writeFile(path.join(output, 'LICENSE'), productLicense);
await writeFile(path.join(output, 'NOTICE'), companyNotice);
for (const { name, text } of contents) await writeFile(path.join(output, `${name}-LICENSE.txt`), text);
await writeFile(path.join(output, 'versions.json'), JSON.stringify(Object.fromEntries(contents.map(({ name, version }) => [name, version])), null, 2) + '\n');
console.log(`已保留 ${contents.length} 个 Web 构建组件的原始许可文本。`);
