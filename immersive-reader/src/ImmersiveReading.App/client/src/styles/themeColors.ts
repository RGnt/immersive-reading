// Colour maths for the themes (Settings -> Appearance): any theme, including a custom one, must give text that can be read.
// The theme chooses its colours; these helpers derive the ones that depend on them (text on an accent button, muted text, status colours).

type Rgb = [number, number, number];

export function parseHex(color: string): Rgb | null {
  const m = color.trim().match(/^#([0-9a-f]{3}|[0-9a-f]{6})$/i);
  if (!m) return null;
  const h = m[1].length === 3 ? m[1].split('').map((c) => c + c).join('') : m[1];
  return [parseInt(h.slice(0, 2), 16), parseInt(h.slice(2, 4), 16), parseInt(h.slice(4, 6), 16)];
}

export function toHex([r, g, b]: Rgb): string {
  return '#' + [r, g, b].map((c) => Math.round(Math.min(255, Math.max(0, c))).toString(16).padStart(2, '0')).join('');
}

export function luminance([r, g, b]: Rgb): number {
  const lin = (c: number) => { const x = c / 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

export function contrast(a: Rgb, b: Rgb): number {
  const la = luminance(a), lb = luminance(b);
  return (Math.max(la, lb) + 0.05) / (Math.min(la, lb) + 0.05);
}

export const mixRgb = (a: Rgb, b: Rgb, t: number): Rgb => [a[0] + (b[0] - a[0]) * t, a[1] + (b[1] - a[1]) * t, a[2] + (b[2] - a[2]) * t];

export const isLightColor = (hex: string): boolean => {
  const rgb = parseHex(hex);
  return !!rgb && luminance(rgb) > 0.4;
};

/** Black or white, whichever reads better on `background` (the text of a button painted in the accent colour). */
export function readableOn(background: string): string {
  const bg = parseHex(background);
  if (!bg) return '#000000';
  return contrast(bg, [0, 0, 0]) >= contrast(bg, [255, 255, 255]) ? '#000000' : '#ffffff';
}

/**
 * `color` moved towards `target` (usually the theme's main text colour) just far enough to reach `min` contrast on every background in
 * `backgrounds`. A colour that already reads well is returned unchanged, so a theme's own look is kept wherever it is readable.
 */
export function ensureContrast(color: string, backgrounds: string[], target: string, min = 4.5): string {
  const fg = parseHex(color), to = parseHex(target);
  const bgs = backgrounds.map(parseHex).filter((b): b is Rgb => b !== null);
  if (!fg || !to || bgs.length === 0) return color;
  const worst = (c: Rgb) => Math.min(...bgs.map((b) => contrast(c, b)));
  if (worst(fg) >= min) return color;
  for (let t = 0.05; t <= 1.0001; t += 0.05) {
    const candidate = mixRgb(fg, to, t);
    if (worst(candidate) >= min) return toHex(candidate);
  }
  return toHex(to);
}

/** Tailwind's palette is tuned for dark surfaces: its pale shades (200 to 400) are the "text colours" and vanish on a light theme. */
const DARKER_SHADES: Record<string, [string, string, string]> = {
  // hue: the replacement for shade [200, 300, 400]: 900, 800, 800 (700 left 4:1 on tinted panels, so one step darker)
  red: ['oklch(39.6% 0.141 25.723)', 'oklch(44.4% 0.177 26.899)', 'oklch(44.4% 0.177 26.899)'],
  amber: ['oklch(41.4% 0.112 45.904)', 'oklch(47.3% 0.137 46.201)', 'oklch(47.3% 0.137 46.201)'],
  yellow: ['oklch(42.1% 0.095 57.708)', 'oklch(47.6% 0.114 61.907)', 'oklch(47.6% 0.114 61.907)'],
  orange: ['oklch(40.8% 0.123 38.172)', 'oklch(47% 0.157 37.304)', 'oklch(47% 0.157 37.304)'],
  lime: ['oklch(40.5% 0.101 131.063)', 'oklch(45.3% 0.124 130.933)', 'oklch(45.3% 0.124 130.933)'],
  green: ['oklch(39.3% 0.095 152.535)', 'oklch(44.8% 0.119 151.328)', 'oklch(44.8% 0.119 151.328)'],
  emerald: ['oklch(37.8% 0.077 168.94)', 'oklch(43.2% 0.095 166.913)', 'oklch(43.2% 0.095 166.913)'],
  teal: ['oklch(38.6% 0.063 188.416)', 'oklch(43.7% 0.078 188.216)', 'oklch(43.7% 0.078 188.216)'],
  cyan: ['oklch(39.8% 0.07 227.392)', 'oklch(45% 0.085 224.283)', 'oklch(45% 0.085 224.283)'],
  sky: ['oklch(39.1% 0.09 240.876)', 'oklch(44.3% 0.11 240.79)', 'oklch(44.3% 0.11 240.79)'],
  blue: ['oklch(37.9% 0.146 265.522)', 'oklch(42.4% 0.199 265.638)', 'oklch(42.4% 0.199 265.638)'],
  indigo: ['oklch(35.9% 0.144 278.697)', 'oklch(39.8% 0.195 277.366)', 'oklch(39.8% 0.195 277.366)'],
  violet: ['oklch(38% 0.189 293.745)', 'oklch(43.2% 0.232 292.759)', 'oklch(43.2% 0.232 292.759)'],
  purple: ['oklch(38.1% 0.176 304.987)', 'oklch(43.8% 0.218 303.724)', 'oklch(43.8% 0.218 303.724)'],
  pink: ['oklch(40.8% 0.153 2.432)', 'oklch(45.9% 0.187 3.815)', 'oklch(45.9% 0.187 3.815)'],
  rose: ['oklch(41% 0.159 10.272)', 'oklch(45.5% 0.188 13.697)', 'oklch(45.5% 0.188 13.697)'],
};

/** The palette variables to set on <html> for a light theme (null entries for a dark one: Tailwind's own values are right there). */
export function paletteOverrides(light: boolean): [string, string | null][] {
  const out: [string, string | null][] = [];
  for (const [hue, shades] of Object.entries(DARKER_SHADES)) {
    [200, 300, 400].forEach((shade, i) => out.push([`--color-${hue}-${shade}`, light ? shades[i] : null]));
  }
  return out;
}

/** Status colours for text (and for plugin stylesheets, which cannot use Tailwind): readable on the theme's surfaces. */
export function statusColors(light: boolean): Record<string, string> {
  return light
    ? { '--danger-text': '#b91c1c', '--success-text': '#047857', '--warning-text': '#92400e', '--info-text': '#0369a1', '--violet-text': '#6d28d9' }
    : { '--danger-text': '#fda4af', '--success-text': '#6ee7b7', '--warning-text': '#fbbf24', '--info-text': '#7dd3fc', '--violet-text': '#d8b4fe' };
}
