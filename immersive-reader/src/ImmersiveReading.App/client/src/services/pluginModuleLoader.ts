import { rpc } from './rpc';
import { rewriteBareImports, SUPPORTED_SPECIFIERS } from './pluginImportRewriter';

/**
 * Loads plugin UI code. The SPA runs from file://, where the WebView blocks fetch/import of ir-app://
 * URLs, so code is fetched over JSON-RPC (`extensions.getAsset`) and run as blob: modules.
 * See specs/11-plugin-ui-loading-spec.md and wiki/plugin-ui-authoring.md.
 */

/** Import specifier -> key of window.__IR_RUNTIME__ (see sdk/exposeRuntime.ts). */
const RUNTIME_KEY: Record<(typeof SUPPORTED_SPECIFIERS)[number], string> = {
  react: 'React',
  'react-dom': 'ReactDOM',
  'react/jsx-runtime': 'JsxRuntime',
  'lucide-react': 'LucideReact',
  '@ir/sdk': 'HostSdk',
};

const RESERVED = new Set([
  'break', 'case', 'catch', 'class', 'const', 'continue', 'debugger', 'default', 'delete', 'do', 'else',
  'enum', 'export', 'extends', 'false', 'finally', 'for', 'function', 'if', 'import', 'in', 'instanceof',
  'new', 'null', 'return', 'super', 'switch', 'this', 'throw', 'true', 'try', 'typeof', 'var', 'void',
  'while', 'with', 'yield', 'let', 'static', 'await', 'async', 'of',
]);

const shimUrls = new Map<string, string>();

interface RuntimeWindow {
  __IR_RUNTIME__?: Record<string, Record<string, unknown>>;
}

/**
 * A module re-exporting one host runtime object. Named exports are derived from the object's keys at
 * load time, so nothing here is hand-maintained when the SDK or icon set grows.
 */
export function buildShimSource(runtimeKey: string, keys: string[]): string {
  const names = keys.filter((k) => k !== 'default' && /^[A-Za-z_$][\w$]*$/.test(k) && !RESERVED.has(k));
  const lines = [
    `const M = window.__IR_RUNTIME__[${JSON.stringify(runtimeKey)}];`,
    'export default M;',
  ];
  if (names.length > 0) lines.push(`export const { ${names.join(', ')} } = M;`);
  return lines.join('\n');
}

function shimUrl(specifier: (typeof SUPPORTED_SPECIFIERS)[number]): string {
  const cached = shimUrls.get(specifier);
  if (cached) return cached;

  const key = RUNTIME_KEY[specifier];
  const runtime = (window as unknown as RuntimeWindow).__IR_RUNTIME__?.[key];
  if (!runtime) {
    throw new Error(`Host runtime '${key}' is not available; cannot provide '${specifier}' to plugins.`);
  }
  const url = URL.createObjectURL(
    new Blob([buildShimSource(key, Object.keys(runtime))], { type: 'text/javascript' })
  );
  shimUrls.set(specifier, url);
  return url;
}

interface PluginAsset {
  path: string;
  contentType: string;
  text: string;
}

export function fetchPluginAsset(pluginId: string, path: string): Promise<PluginAsset> {
  return rpc.call<PluginAsset>('extensions.getAsset', { pluginId, path });
}

/** Fetches, import-rewrites and imports a plugin's UI entry. A fresh blob per call, so edits are picked up. */
export async function loadPluginModule(pluginId: string, entry: string): Promise<Record<string, unknown>> {
  const asset = await fetchPluginAsset(pluginId, entry);
  const source = rewriteBareImports(asset.text, pluginId, (spec) =>
    shimUrl(spec as (typeof SUPPORTED_SPECIFIERS)[number])
  );
  const blobUrl = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
  try {
    return (await import(/* @vite-ignore */ blobUrl)) as Record<string, unknown>;
  } finally {
    URL.revokeObjectURL(blobUrl);
  }
}

/** Returns the plugin's stylesheet text. */
export async function loadPluginStyle(pluginId: string, entry: string): Promise<string> {
  return (await fetchPluginAsset(pluginId, entry)).text;
}
