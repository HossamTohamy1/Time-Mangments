// Fails when a translation key exists in one language file but not the other,
// or when a template contains an obvious hard-coded user-facing string.
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join, relative } from 'node:path';

const root = new URL('..', import.meta.url).pathname;
const i18nDir = join(root, 'public', 'i18n');
let failures = 0;

function flatten(obj, prefix = '', out = {}) {
  for (const [k, v] of Object.entries(obj)) {
    const key = prefix ? `${prefix}.${k}` : k;
    if (v && typeof v === 'object') flatten(v, key, out); else out[key] = v;
  }
  return out;
}

function compare(dir) {
  const files = readdirSync(dir);
  for (const sub of files.filter((f) => statSync(join(dir, f)).isDirectory())) compare(join(dir, sub));
  if (!files.includes('en.json') && !files.includes('ar.json')) return;
  const load = (l) => { try { return flatten(JSON.parse(readFileSync(join(dir, `${l}.json`), 'utf8'))); } catch (e) { console.error(`✗ ${relative(root, dir)}/${l}.json: ${e.message}`); failures++; return {}; } };
  const en = load('en'), ar = load('ar');
  for (const k of Object.keys(en)) if (!(k in ar)) { console.error(`✗ missing in ar: ${relative(root, dir)} :: ${k}`); failures++; }
  for (const k of Object.keys(ar)) if (!(k in en)) { console.error(`✗ missing in en: ${relative(root, dir)} :: ${k}`); failures++; }
  for (const [k, v] of Object.entries(ar)) if (typeof v === 'string' && !v.trim()) { console.error(`✗ empty ar value: ${k}`); failures++; }
}

// Heuristic hard-coded string detection in inline/external templates:
// a text node with 2+ consecutive latin letters that is not inside {{ }} and not an allowed token.
// Language endonyms are shown in their own language on purpose (standard language-switcher practice).
const allowed = new Set(['EN', 'AR', 'PDF', 'CSV', 'XLSX', 'JSON', 'ID', 'AA', 'TT', 'English', 'العربية']);
function scanTemplates(dir) {
  for (const f of readdirSync(dir)) {
    const p = join(dir, f);
    if (statSync(p).isDirectory()) { if (f !== 'generated') scanTemplates(p); continue; }
    if (!/\.(html|ts)$/.test(f) || f.endsWith('.spec.ts')) continue;
    let src = readFileSync(p, 'utf8');
    if (f.endsWith('.ts')) {
      const m = src.match(/template:\s*`([\s\S]*?)`\s*,?\s*\n\s*(styles|styleUrl|host|providers|\})/);
      if (!m) continue;
      src = m[1];
    }
    src = src.replace(/<!--[\s\S]*?-->/g, '').replace(/\{\{[\s\S]*?\}\}/g, ' ').replace(/@let\s[^;]*;/g, ' ')
      .replace(/@(if|for|else|switch|case|default|empty|defer)[^{]*\{/g, ' ');
    src = src.replace(/<(style|script)[\s\S]*?<\/\1>/g, '');
    // Tags may contain '>' inside quoted attribute expressions (e.g. [disabled]="a >= b").
    const texts = src.replace(/<[a-zA-Z\/!](?:"[^"]*"|'[^']*'|[^'">])*>/g, '\u0000').split('\u0000');
    for (const raw of texts) {
      const t = raw.replace(/[{}()]/g, ' ').trim();
      if (!t || !/[A-Za-z\u0600-\u06FF]{2,}/.test(t)) continue;
      if (allowed.has(t)) continue;
      if (/^[\s\-–·•|:/%#.,+0-9A-Za-z]{0,3}$/.test(t)) continue;
      if (/^[a-z_$][\w$.]*\s*[;)]?$/.test(t)) continue; // stray bindings
      console.error(`✗ hard-coded text in ${relative(root, p)}: "${t.slice(0, 60)}"`);
      failures++;
    }
  }
}

compare(i18nDir);
scanTemplates(join(root, 'src', 'app'));
if (failures) { console.error(`\ni18n check failed with ${failures} problem(s).`); process.exit(1); }
console.log('✓ i18n check passed (key parity + no hard-coded template strings).');
