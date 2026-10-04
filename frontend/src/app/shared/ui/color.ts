/** Color utilities: derive accessible light/dark tints from one base color and check WCAG contrast. */
function hexToRgb(hex: string): [number, number, number] | null {
  const m = /^#?([0-9a-f]{6})$/i.exec(hex.trim());
  if (!m) return null;
  const n = parseInt(m[1], 16);
  return [(n >> 16) & 255, (n >> 8) & 255, n & 255];
}

function toHex([r, g, b]: [number, number, number]): string {
  return '#' + [r, g, b].map((v) => Math.round(Math.max(0, Math.min(255, v))).toString(16).padStart(2, '0')).join('');
}

function mix(a: [number, number, number], b: [number, number, number], t: number): [number, number, number] {
  return [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];
}

function luminance([r, g, b]: [number, number, number]): number {
  const f = (v: number) => { const c = v / 255; return c <= 0.03928 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); };
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

export function contrast(a: string, b: string): number {
  const x = hexToRgb(a), y = hexToRgb(b);
  if (!x || !y) return 21;
  const l1 = luminance(x), l2 = luminance(y);
  return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
}

export interface SessionColors { accent: string; background: string; text: string; }

const LIGHT_SURFACE: [number, number, number] = [255, 255, 255];
const DARK_SURFACE: [number, number, number] = [23, 27, 45];

/**
 * From a single base color: light mode = soft tint + darkened accent text; dark mode = deep tint + lightened accent.
 * Text is adjusted step by step until it reaches 4.5:1 on its tint (or gives up → reported by `accessible`).
 */
export function deriveColors(base: string | null | undefined, theme: 'light' | 'dark'): SessionColors {
  const rgb = hexToRgb(base ?? '') ?? [67, 56, 202];
  if (theme === 'light') {
    const bg = mix(rgb, LIGHT_SURFACE, 0.9);
    let text = rgb;
    for (let i = 0; i < 10 && contrast(toHex(text), toHex(bg)) < 4.5; i++) text = mix(text, [0, 0, 0], 0.15);
    return { accent: toHex(rgb), background: toHex(bg), text: toHex(text) };
  }
  const bg = mix(rgb, DARK_SURFACE, 0.78);
  let text = mix(rgb, LIGHT_SURFACE, 0.35);
  for (let i = 0; i < 10 && contrast(toHex(text), toHex(bg)) < 4.5; i++) text = mix(text, LIGHT_SURFACE, 0.2);
  return { accent: toHex(mix(rgb, LIGHT_SURFACE, 0.2)), background: toHex(bg), text: toHex(text) };
}

/** True when both derived themes reach AA (4.5:1) for text on the tint. */
export function accessible(base: string | null | undefined): boolean {
  return (['light', 'dark'] as const).every((t) => { const c = deriveColors(base, t); return contrast(c.text, c.background) >= 4.5; });
}
