// Verifies the production build landed in the backend wwwroot and (optionally) that a running API serves it.
// Usage: node scripts/verify-build.mjs [baseUrl]
import { existsSync } from 'node:fs';
import { join } from 'node:path';

const root = new URL('..', import.meta.url).pathname;
const index = join(root, '..', 'backend', 'src', 'Timetable.Api', 'wwwroot', 'index.html');
if (!existsSync(index)) { console.error(`✗ ${index} not found. Run npm run build:prod.`); process.exit(1); }
console.log(`✓ ${index} exists`);

const base = process.argv[2];
if (base) {
  const check = async (path, pred, label) => {
    const r = await fetch(new URL(path, base));
    const body = await r.text();
    if (!pred(r, body)) { console.error(`✗ ${label} (${r.status} ${r.headers.get('content-type')})`); process.exit(1); }
    console.log(`✓ ${label}`);
  };
  await check('/', (r, b) => r.ok && b.includes('<app-root'), 'GET / serves index.html');
  await check('/timetable/123', (r, b) => r.ok && b.includes('<app-root'), 'deep link /timetable/123 serves index.html');
  await check('/api/v1/unknown', (r) => r.status === 404 && (r.headers.get('content-type') ?? '').includes('json'), '/api/v1/unknown returns 404 JSON');
}
