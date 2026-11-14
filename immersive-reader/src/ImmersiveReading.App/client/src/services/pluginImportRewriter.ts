/**
 * Rewrites the bare import specifiers of a plugin UI module so it can run as a blob: module.
 *
 * Why: the SPA runs from file://, where the WebView blocks ir-app:// fetch/import, so plugin code is
 * fetched over RPC and imported from a blob: URL. Blob modules have no import map, so `import 'react'`
 * must be turned into a blob: URL of a host-provided shim. See specs/11-plugin-ui-loading-spec.md.
 *
 * Pure and dependency-free so it can be self-checked (selfTestRunner) without a unit-test runner.
 * Limitation (documented in wiki/plugin-ui-authoring.md): only string-literal specifiers are handled.
 */

/** Modules every plugin UI may import. Each maps to a key of `window.__IR_RUNTIME__`. */
export const SUPPORTED_SPECIFIERS = [
  'react',
  'react-dom',
  'react/jsx-runtime',
  'lucide-react',
  '@ir/sdk',
] as const;

export class UnsupportedImportError extends Error {
  readonly pluginId: string;
  readonly specifier: string;

  constructor(pluginId: string, specifier: string, reason: string) {
    super(`Plugin '${pluginId}' imports '${specifier}', ${reason} (supported: ${SUPPORTED_SPECIFIERS.join(', ')}).`);
    this.pluginId = pluginId;
    this.specifier = specifier;
    this.name = 'UnsupportedImportError';
  }
}

// import x from 'a' | import {x} from "a" | export {x} from 'a' | export * from 'a'
const FROM_RE = /(\b(?:import|export)\b[^'"`;]*?\bfrom\s*)(['"])([^'"\n]+)\2/g;
// import 'a'
const SIDE_EFFECT_RE = /(\bimport\s*)(['"])([^'"\n]+)\2/g;
// import('a')
const DYNAMIC_RE = /(\bimport\s*\(\s*)(['"])([^'"\n]+)\2/g;

const isSupported = (spec: string): boolean => (SUPPORTED_SPECIFIERS as readonly string[]).includes(spec);
const isAbsoluteUrl = (spec: string): boolean => /^[a-zA-Z][a-zA-Z0-9+.-]*:/.test(spec);

export function rewriteBareImports(
  source: string,
  pluginId: string,
  resolve: (specifier: string) => string
): string {
  const replace = (_match: string, head: string, quote: string, spec: string): string => {
    if (isAbsoluteUrl(spec)) return `${head}${quote}${spec}${quote}`; // blob:/data:/https: left alone
    if (spec.startsWith('.') || spec.startsWith('/')) {
      throw new UnsupportedImportError(
        pluginId,
        spec,
        'a relative import; plugin UI must be a single self-contained file, bundle it first'
      );
    }
    if (!isSupported(spec)) {
      throw new UnsupportedImportError(pluginId, spec, 'which is not provided by the host');
    }
    return `${head}${quote}${resolve(spec)}${quote}`;
  };

  return source.replace(FROM_RE, replace).replace(SIDE_EFFECT_RE, replace).replace(DYNAMIC_RE, replace);
}

/** Cases checked by the `--verify-ui` self-test. Returns a list of failures (empty = all good). */
export function selfCheckRewriter(): string[] {
  const failures: string[] = [];
  const r = (s: string) => rewriteBareImports(s, 'selfcheck', (spec) => `blob:shim/${spec}`);
  const expectEq = (name: string, actual: string, expected: string) => {
    if (actual !== expected) failures.push(`${name}: expected ${expected} got ${actual}`);
  };
  const expectThrows = (name: string, input: string, specifier: string) => {
    try {
      r(input);
      failures.push(`${name}: expected UnsupportedImportError`);
    } catch (e) {
      if (!(e instanceof UnsupportedImportError) || e.specifier !== specifier) {
        failures.push(`${name}: wrong error ${String(e)}`);
      }
    }
  };

  expectEq('default+named', r(`import React, { useState } from 'react';`), `import React, { useState } from 'blob:shim/react';`);
  expectEq('double quotes + alias', r(`import {a as b} from "lucide-react"`), `import {a as b} from "blob:shim/lucide-react"`);
  expectEq('re-export', r(`export { X } from '@ir/sdk'`), `export { X } from 'blob:shim/@ir/sdk'`);
  expectEq('minified', r(`import{jsx as a}from"react/jsx-runtime";`), `import{jsx as a}from"blob:shim/react/jsx-runtime";`);
  expectEq('side effect', r(`import 'react';`), `import 'blob:shim/react';`);
  expectEq('dynamic', r(`const m = await import('react-dom');`), `const m = await import('blob:shim/react-dom');`);
  expectEq('multiline', r(`import {\n  a,\n  b\n} from 'react';`), `import {\n  a,\n  b\n} from 'blob:shim/react';`);
  expectEq('absolute url untouched', r(`import x from 'blob:abc';`), `import x from 'blob:abc';`);
  expectEq('no imports', r(`export const x = 1;`), `export const x = 1;`);
  expectThrows('relative', `import x from './x.js';`, './x.js');
  expectThrows('unsupported bare', `import pad from 'left-pad';`, 'left-pad');
  expectThrows('unsupported dynamic', `import('left-pad')`, 'left-pad');
  return failures;
}
