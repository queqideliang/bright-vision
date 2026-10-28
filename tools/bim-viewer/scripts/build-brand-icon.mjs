import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { mkdir, writeFile } from 'node:fs/promises';

// Original Bright Vision B/V mark. These are the same owned curves as the
// Revit plugin's BrandPixels.cs; no third-party art, font or image is needed.
const ellipse = (x, y, cx, cy, rx, ry) => ((x - cx) / rx) ** 2 + ((y - cy) / ry) ** 2 <= 1;
function distance(x, y, ax, ay, bx, by) {
  const dx = bx - ax, dy = by - ay, t = Math.max(0, Math.min(1, ((x - ax) * dx + (y - ay) * dy) / (dx * dx + dy * dy)));
  return Math.hypot(x - ax - t * dx, y - ay - t * dy);
}
export function createBrandPixels(size) {
  if (![16, 32, 48, 64, 128, 256].includes(size)) throw new Error('Unsupported BV icon size.');
  const pixels = Buffer.alloc(size * size * 4);
  for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
    let red = 0, green = 0, blue = 0, samples = 0;
    for (let sy = 0; sy < 4; sy++) for (let sx = 0; sx < 4; sx++) {
      const px = (x + (sx + 0.5) / 4) * 32 / size, py = (y + (sy + 0.5) / 4) * 32 / size;
      const cornerX = Math.max(Math.abs(px - 16) - 8, 0), cornerY = Math.max(Math.abs(py - 16) - 8, 0);
      if (cornerX * cornerX + cornerY * cornerY > 49) continue;
      let r = 16, g = 37, b = 63;
      const stem = px >= 5.5 && px <= 8.5 && py >= 6 && py <= 26;
      const upper = px >= 8 && ellipse(px, py, 10.7, 11.1, 6.2, 5.1) && !ellipse(px, py, 10.4, 11.1, 3.1, 2.25);
      const lower = px >= 8 && ellipse(px, py, 10.7, 20.7, 6.2, 5.3) && !ellipse(px, py, 10.4, 20.7, 3.1, 2.4);
      if (stem || upper || lower) { r = 64; g = 219; b = 215; }
      if (distance(px, py, 18.3, 7.5, 22.5, 24.5) < 1.55 || distance(px, py, 22.5, 24.5, 27, 7.5) < 1.55) { r = 68; g = 228; b = 163; }
      red += r; green += g; blue += b; samples++;
    }
    if (!samples) continue;
    const offset = (y * size + x) * 4;
    pixels[offset] = Math.floor(red / samples); pixels[offset + 1] = Math.floor(green / samples);
    pixels[offset + 2] = Math.floor(blue / samples); pixels[offset + 3] = Math.floor(255 * samples / 16);
  }
  return pixels;
}
export function createBrandIcon() {
  const sizes = [16, 32, 48, 64, 128, 256], header = Buffer.alloc(6 + 16 * sizes.length), images = [];
  header.writeUInt16LE(1, 2); header.writeUInt16LE(sizes.length, 4);
  let offset = header.length;
  sizes.forEach((size, index) => {
    const rgba = createBrandPixels(size), maskStride = Math.ceil(size / 32) * 4, bitmap = Buffer.alloc(40 + rgba.length + size * maskStride);
    bitmap.writeUInt32LE(40, 0); bitmap.writeInt32LE(size, 4); bitmap.writeInt32LE(size * 2, 8);
    bitmap.writeUInt16LE(1, 12); bitmap.writeUInt16LE(32, 14); bitmap.writeUInt32LE(rgba.length, 20);
    for (let y = 0; y < size; y++) for (let x = 0; x < size; x++) {
      const source = (y * size + x) * 4, target = 40 + ((size - y - 1) * size + x) * 4;
      bitmap[target] = rgba[source + 2]; bitmap[target + 1] = rgba[source + 1]; bitmap[target + 2] = rgba[source]; bitmap[target + 3] = rgba[source + 3];
      if (!rgba[source + 3]) bitmap[40 + rgba.length + (size - y - 1) * maskStride + Math.floor(x / 8)] |= 0x80 >> (x % 8);
    }
    const entry = 6 + index * 16; header[entry] = header[entry + 1] = size === 256 ? 0 : size;
    header.writeUInt16LE(1, entry + 4); header.writeUInt16LE(32, entry + 6);
    header.writeUInt32LE(bitmap.length, entry + 8); header.writeUInt32LE(offset, entry + 12);
    images.push(bitmap); offset += bitmap.length;
  });
  return Buffer.concat([header, ...images]);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const output = fileURLToPath(new URL('../electron/assets/bv.ico', import.meta.url));
  await mkdir(path.dirname(output), { recursive: true }); await writeFile(output, createBrandIcon());
  console.log('Built original BV icon (six Windows sizes; no third-party artwork).');
}
