# Design tokens

All colours, radii, shadows and fonts are CSS custom properties defined in `frontend/src/styles/_tokens.scss`.
Components use **only** these tokens (no raw hex values), so Light, Dark and System themes and the print style switch by
redefining tokens on `:root[data-theme]`. The theme is applied before first paint by the inline bootstrap script in
`index.html` (stored choice `tt.theme`, falling back to `prefers-color-scheme`), so there is no flash of the wrong theme.

Session-type colours are data (each session type has a colour in Settings › Lookups). Cards derive an accessible
accent / tinted background / text triple per theme from that one colour (`shared/ui/color.ts`), and the lookup editor
warns when a colour does not reach WCAG AA contrast.

## Surfaces & text

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-bg` | `#f5f6fa` | `#0f1220` |
| `--tt-surface` | `#ffffff` | `#171b2d` |
| `--tt-surface-variant` | `#f9fafc` | `#1e2338` |
| `--tt-surface-sunken` | `#f2f3f8` | `#12162a` |
| `--tt-on-surface` | `#1d2133` | `#e7e9f3` |
| `--tt-on-surface-muted` | `#5f6578` | `#a6abc2` |
| `--tt-on-surface-faint` | `#8a90a3` | `#7d839c` |
| `--tt-border` | `#e4e7ef` | `#2a3049` |
| `--tt-border-strong` | `#d5d9e4` | `#3a4160` |

## Brand

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-primary` | `#4338ca` | `#8f97ff` |
| `--tt-primary-strong` | `#3730a3` | `#a9afff` |
| `--tt-primary-soft` | `#eef0ff` | `#252a52` |
| `--tt-primary-soft-border` | `#c9ccf5` | `#3c4384` |

## Feasibility & conflicts

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-valid` | `#15803d` | `#4ade80` |
| `--tt-valid-bg` | `#effbf4` | `#11291c` |
| `--tt-valid-border` | `#86d4a6` | `#1f6a3c` |
| `--tt-penalty` | `#a16207` | `#fbbf24` |
| `--tt-penalty-bg` | `#fff9e6` | `#2d2410` |
| `--tt-penalty-border` | `#f0c75e` | `#7a5b12` |
| `--tt-conflict` | `#dc2626` | `#f87171` |
| `--tt-conflict-strong` | `#b91c1c` | `#fca5a5` |
| `--tt-conflict-bg` | `#fff2f2` | `#351518` |
| `--tt-conflict-border` | `#f8caca` | `#7a2a2f` |
| `--tt-pinned` | `#4338ca` | `#a5b4fc` |

## Information

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-info` | `#2563eb` | `#60a5fa` |
| `--tt-info-bg` | `#eff6ff` | `#13233f` |

## Charts & patterns

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-chart-1` | `#4338ca` | `#8f97ff` |
| `--tt-chart-2` | `#0f9488` | `#2dd4bf` |
| `--tt-chart-3` | `#c2410c` | `#fb923c` |
| `--tt-chart-4` | `#8b3fd9` | `#c084fc` |
| `--tt-chart-5` | `#15803d` | `#4ade80` |
| `--tt-chart-grid` | `#e4e7ef` | `#2a3049` |
| `--tt-hatch-a` | `#f9fafc` | `#1a1f33` |
| `--tt-hatch-b` | `#f2f3f8` | `#151a2c` |
| `--tt-scrim` | `rgb(15 18 30 / 40%)` | `rgb(0 0 0 / 60%)` |

## Elevation

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-shadow-sm` | `0 1px 2px rgb(20 24 40 / 6%)` | `0 1px 2px rgb(0 0 0 / 40%)` |
| `--tt-shadow-md` | `0 4px 14px rgb(20 24 40 / 8%)` | `0 4px 14px rgb(0 0 0 / 40%)` |
| `--tt-shadow-lg` | `0 12px 32px rgb(20 24 40 / 16%), 0 2px 6px rgb(20 24 40 / 8%)` | `0 12px 32px rgb(0 0 0 / 55%), 0 2px 6px rgb(0 0 0 / 40%)` |

## Other

| Token | Light | Dark |
| --- | --- | --- |
| `--tt-on-primary` | `#ffffff` | `#10132a` |
| `--tt-on-conflict-bg` | `#7f1d1d` | `#fecaca` |

## Shape & typography (theme-independent)

| Token | Value |
| --- | --- |
| `--tt-radius` | `10px` |
| `--tt-radius-sm` | `7px` |
| `--tt-radius-lg` | `12px` |
| `--tt-sidebar-w` | `248px` |
| `--tt-header-h` | `64px` |
| `--tt-font-en` | `'Inter', system-ui, -apple-system, 'Segoe UI', sans-serif` |
| `--tt-font-ar` | `'IBM Plex Sans Arabic', 'Inter', system-ui, sans-serif` |
| `--tt-font` | `var(--tt-font-ar)` |
| `--tt-line-height` | `1.65` |

Arabic (`:root[lang='ar']`) switches `--tt-font` to IBM Plex Sans Arabic. Layout uses logical properties
(`inset-inline-start`, `margin-inline-end`, `border-inline-start`, …) everywhere, so RTL mirroring needs no separate
stylesheet; directional icons carry a `mirror` flag.

## Usage rules

- Use semantic tokens (`--tt-valid`, `--tt-conflict`, …), never palette colours, so meaning survives theme changes.
- Feasibility in the editor: **valid** = green, **penalty** (soft) = amber, **conflict** (hard) = red, in both themes.
- Print: a light, ink-friendly override is applied in `@media print` regardless of the active theme.
- Reduced motion: animations (cell transitions, "now" pulse, highlight flash) are disabled under `prefers-reduced-motion`.
