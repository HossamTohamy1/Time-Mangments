import { Injectable } from '@angular/core';
import { TranslocoTranspiler, TranspileParams } from '@jsverse/transloco';

/**
 * ICU message transpiler without runtime code generation (so the app runs under a CSP without 'unsafe-eval').
 * Supports `{arg}`, `{n, plural, =0 {…} one {# …} other {…}}`, `selectordinal`, `select`, `offset:`, nesting and `''` escapes.
 * Plural categories come from Intl.PluralRules, which knows Arabic's zero / one / two / few / many / other forms.
 */
type Node =
  | string
  | { kind: 'arg'; name: string }
  | { kind: 'hash' }
  | { kind: 'choice'; name: string; type: 'plural' | 'selectordinal' | 'select'; offset: number; options: Record<string, Node[]> };

export function parseIcu(src: string): Node[] {
  let i = 0;

  function text(stopAtHash: boolean): string {
    let out = '';
    while (i < src.length) {
      const ch = src[i];
      if (ch === '{' || ch === '}' || (stopAtHash && ch === '#')) break;
      if (ch === "'" && src[i + 1] === "'") { out += "'"; i += 2; continue; }
      out += ch;
      i++;
    }
    return out;
  }

  function skipSpace(): void { while (i < src.length && /\s/.test(src[i])) i++; }

  function word(): string {
    skipSpace();
    let w = '';
    while (i < src.length && !/[\s,{}]/.test(src[i])) w += src[i++];
    return w;
  }

  function message(inPlural: boolean): Node[] {
    const nodes: Node[] = [];
    while (i < src.length && src[i] !== '}') {
      const t = text(inPlural);
      if (t) nodes.push(t);
      if (i >= src.length || src[i] === '}') break;
      if (src[i] === '#') { nodes.push({ kind: 'hash' }); i++; continue; }
      // '{'
      i++;
      const name = word();
      skipSpace();
      if (src[i] === '}') { i++; nodes.push({ kind: 'arg', name }); continue; }
      i++; // ','
      const type = word() as 'plural' | 'selectordinal' | 'select';
      skipSpace();
      if (src[i] === ',') i++;
      let offset = 0;
      const options: Record<string, Node[]> = {};
      for (;;) {
        skipSpace();
        if (i >= src.length || src[i] === '}') { i++; break; }
        const key = word();
        if (key.startsWith('offset:')) { offset = Number(key.slice(7)) || 0; continue; }
        skipSpace();
        if (src[i] !== '{') break;
        i++;
        options[key] = message(type !== 'select' || inPlural);
        i++; // '}'
      }
      nodes.push({ kind: 'choice', name, type, offset, options });
    }
    return nodes;
  }

  return message(false);
}

function render(nodes: Node[], params: Record<string, unknown>, lang: string, hashValue: number | null): string {
  let out = '';
  for (const n of nodes) {
    if (typeof n === 'string') { out += n; continue; }
    if (n.kind === 'hash') { out += hashValue === null ? '#' : String(hashValue); continue; }
    if (n.kind === 'arg') { const v = params[n.name]; out += v === undefined || v === null ? '' : String(v); continue; }
    const raw = params[n.name];
    if (n.type === 'select') {
      const branch = n.options[String(raw)] ?? n.options['other'] ?? [];
      out += render(branch, params, lang, hashValue);
      continue;
    }
    const value = Number(raw);
    const exact = n.options[`=${value}`];
    const category = new Intl.PluralRules(lang, { type: n.type === 'selectordinal' ? 'ordinal' : 'cardinal' }).select(value - n.offset);
    out += render(exact ?? n.options[category] ?? n.options['other'] ?? [], params, lang, value - n.offset);
  }
  return out;
}

@Injectable()
export class IcuTranspiler implements TranslocoTranspiler {
  private lang = 'en';
  private readonly cache = new Map<string, Node[]>();

  onLangChanged(lang: string): void { this.lang = lang; }

  transpile({ value, params }: TranspileParams): unknown {
    if (typeof value === 'string') return this.format(value, params ?? {});
    if (value && typeof value === 'object') {
      return Object.fromEntries(Object.entries(value as Record<string, unknown>).map(([k, v]) => [k, this.transpile({ value: v, params, translation: {}, key: k })]));
    }
    return value;
  }

  format(message: string, params: Record<string, unknown>, lang = this.lang): string {
    if (!message.includes('{') && !message.includes("''")) return message;
    let nodes = this.cache.get(message);
    if (!nodes) {
      nodes = parseIcu(message);
      if (this.cache.size > 2000) this.cache.clear();
      this.cache.set(message, nodes);
    }
    return render(nodes, params, lang, null);
  }
}
