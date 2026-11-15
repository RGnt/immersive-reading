// A contrast audit for the real window: every piece of visible text is compared with the background it is painted on. Used by --verify-ui
// to prove that text stays readable in every colour theme, including text that plugins draw. Not shipped as a user feature.

type Rgba = [number, number, number, number];

const canvas = (() => {
  const c = document.createElement('canvas');
  c.width = c.height = 1;
  return c.getContext('2d', { willReadFrequently: true });
})();

/** Any CSS colour (rgb, hex, oklch, color-mix ...) as sRGB bytes: it is painted on one canvas pixel and read back, whatever the format. */
function parse(color: string): Rgba {
  if (!canvas || !color) return [0, 0, 0, 1];
  canvas.clearRect(0, 0, 1, 1);
  canvas.fillStyle = '#000';
  canvas.fillStyle = color;
  canvas.fillRect(0, 0, 1, 1);
  const d = canvas.getImageData(0, 0, 1, 1).data;
  return [d[0], d[1], d[2], d[3] / 255];
}

function over(top: Rgba, bottom: Rgba): Rgba {
  const a = top[3] + bottom[3] * (1 - top[3]);
  if (a === 0) return [0, 0, 0, 0];
  const mix = (i: number) => (top[i] * top[3] + bottom[i] * bottom[3] * (1 - top[3])) / a;
  return [mix(0), mix(1), mix(2), a];
}

function luminance([r, g, b]: Rgba): number {
  const lin = (c: number) => { const x = c / 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; };
  return 0.2126 * lin(r) + 0.7152 * lin(g) + 0.0722 * lin(b);
}

export function contrastRatio(fg: Rgba, bg: Rgba): number {
  const a = luminance(fg), b = luminance(bg);
  return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}

/** The colour behind an element: its ancestors' background colours stacked (gradients and images are ignored). */
function backgroundOf(el: Element): Rgba {
  const layers: Rgba[] = [];
  for (let node: Element | null = el; node; node = node.parentElement) {
    const style = getComputedStyle(node);
    const bg = parse(style.backgroundColor);
    if (bg[3] > 0) layers.push(bg);
    if (bg[3] >= 1) break;
  }
  let result: Rgba = parse(getComputedStyle(document.body).backgroundColor);
  if (result[3] < 1) result = over(result, [255, 255, 255, 1]);
  for (let i = layers.length - 1; i >= 0; i--) result = over(layers[i], result);
  return result;
}

function opacityOf(el: Element): number {
  let o = 1;
  for (let node: Element | null = el; node; node = node.parentElement) o *= parseFloat(getComputedStyle(node).opacity || '1');
  return o;
}

export interface Offender {
  where: string;
  text: string;
  fg: string;
  bg: string;
  ratio: number;
}

const hex = (c: Rgba) => '#' + [0, 1, 2].map((i) => Math.round(c[i]).toString(16).padStart(2, '0')).join('');

function describe(el: Element): string {
  const classes = (el.getAttribute('class') ?? '').split(/\s+/).filter(Boolean);
  const meaningful = classes.filter((c) => /(text|bg|border)-/.test(c)).slice(0, 3).join('.');
  return `${el.tagName.toLowerCase()}${meaningful ? '.' + meaningful : classes[0] ? '.' + classes[0] : ''}`;
}

/** Visible text in `root` whose contrast with its background is below `minRatio` (3:1 = hard to read, 4.5:1 = WCAG AA for body text). */
export function auditContrast(root: ParentNode = document.body, minRatio = 3): Offender[] {
  const found: Offender[] = [];
  const elements = Array.from(root.querySelectorAll('*'));
  for (const el of elements) {
    if (el.closest('.monaco-editor, svg, script, style, [aria-hidden="true"]')) continue;       // the code editor colours itself; icons are not text
    const tag = el.tagName;
    const isField = tag === 'INPUT' || tag === 'TEXTAREA' || tag === 'SELECT';
    let text = '';
    if (isField) {
      const f = el as HTMLInputElement;
      if (f.type === 'checkbox' || f.type === 'radio' || f.type === 'range' || f.type === 'color' || f.type === 'file' || f.type === 'hidden') continue;
      text = (f.value || f.placeholder || '').trim();
    } else {
      text = Array.from(el.childNodes).filter((n) => n.nodeType === Node.TEXT_NODE).map((n) => n.textContent ?? '').join(' ').trim();
    }
    if (!text) continue;
    const rect = (el as HTMLElement).getBoundingClientRect();
    if (rect.width < 2 || rect.height < 2) continue;
    const style = getComputedStyle(el);
    if (style.visibility === 'hidden' || style.display === 'none') continue;
    const bg = backgroundOf(el);
    let fg = parse(style.color);
    const alpha = fg[3] * opacityOf(el);
    fg = over([fg[0], fg[1], fg[2], alpha], bg);
    if (alpha < 0.15) continue;                                       // invisible by design (hover-only controls, transparent text)
    if ((el as HTMLButtonElement).disabled || el.closest('[disabled], [aria-disabled="true"]')) continue;   // disabled controls are exempt
    const ratio = contrastRatio(fg, bg);
    if (ratio < minRatio) found.push({ where: describe(el), text: text.slice(0, 28), fg: hex(fg), bg: hex(bg), ratio: Math.round(ratio * 10) / 10 });
  }
  return found;
}

/** Groups equal findings (same element signature and colours) so a list of 200 rows reads as one line. */
export function summarise(offenders: Offender[]): { key: string; count: number; sample: Offender }[] {
  const map = new Map<string, { key: string; count: number; sample: Offender }>();
  for (const o of offenders) {
    const key = `${o.where} ${o.fg} on ${o.bg}`;
    const entry = map.get(key);
    if (entry) entry.count++;
    else map.set(key, { key, count: 1, sample: o });
  }
  return [...map.values()].sort((a, b) => b.count - a.count);
}
