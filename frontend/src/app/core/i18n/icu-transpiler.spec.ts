import { IcuTranspiler } from './icu-transpiler';

describe('IcuTranspiler (eval-free ICU)', () => {
  const t = new IcuTranspiler();

  it('substitutes simple arguments and keeps plain text untouched', () => {
    expect(t.format('Welcome, {name}', { name: 'Sara' })).toBe('Welcome, Sara');
    expect(t.format('No placeholders', {})).toBe('No placeholders');
  });

  it('selects English plural forms, exact matches and #', () => {
    const msg = '{count, plural, =0 {No conflicts} one {# conflict} other {# conflicts}}';
    expect(t.format(msg, { count: 0 }, 'en')).toBe('No conflicts');
    expect(t.format(msg, { count: 1 }, 'en')).toBe('1 conflict');
    expect(t.format(msg, { count: 7 }, 'en')).toBe('7 conflicts');
  });

  it('supports all six Arabic plural categories', () => {
    const msg = '{count, plural, zero {لا شيء} one {واحد} two {اثنان} few {# قليلة} many {# كثيرة} other {# أخرى}}';
    expect(t.format(msg, { count: 0 }, 'ar')).toBe('لا شيء');
    expect(t.format(msg, { count: 1 }, 'ar')).toBe('واحد');
    expect(t.format(msg, { count: 2 }, 'ar')).toBe('اثنان');
    expect(t.format(msg, { count: 5 }, 'ar')).toBe('5 قليلة');
    expect(t.format(msg, { count: 11 }, 'ar')).toBe('11 كثيرة');
    expect(t.format(msg, { count: 100 }, 'ar')).toBe('100 أخرى');
  });

  it('handles select, nested arguments and escaped quotes', () => {
    const msg = "{kind, select, room {Room {code}} other {It''s {code}}}";
    expect(t.format(msg, { kind: 'room', code: 'R1' })).toBe('Room R1');
    expect(t.format(msg, { kind: 'x', code: 'Z' })).toBe("It's Z");
  });

  it('transpiles nested translation objects', () => {
    const out = t.transpile({ value: { a: 'Hi {n}', b: { c: '{n, plural, one {#} other {many}}' } }, params: { n: 1 }, translation: {}, key: 'x' }) as Record<string, unknown>;
    expect(out['a']).toBe('Hi 1');
    expect((out['b'] as Record<string, string>)['c']).toBe('1');
  });
});
