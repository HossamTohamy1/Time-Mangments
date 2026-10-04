import { accessible, contrast, deriveColors } from './color';

describe('color utilities', () => {
  it('computes WCAG contrast', () => {
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 0);
    expect(contrast('#777777', '#777777')).toBeCloseTo(1, 5);
  });

  it('derives tints whose text reaches AA in both themes for typical session colors', () => {
    for (const base of ['#3b5bdb', '#8b3fd9', '#15a35a', '#c2410c']) {
      for (const theme of ['light', 'dark'] as const) {
        const c = deriveColors(base, theme);
        expect(contrast(c.text, c.background)).toBeGreaterThanOrEqual(4.5);
      }
      expect(accessible(base)).toBe(true);
    }
  });

  it('falls back to the primary color for invalid input', () => {
    expect(deriveColors('not-a-color', 'light').accent).toBe('#4338ca');
  });
});
