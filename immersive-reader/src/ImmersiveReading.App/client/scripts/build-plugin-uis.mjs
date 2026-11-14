// Compiles plugin UI sources (src/Plugins/*/Assets/ui.src.jsx|tsx) into the plain-ES `ui.js` that the
// plugin embeds and the host loads as a blob module (specs/11-plugin-ui-loading-spec.md).
// Host-provided modules stay external; everything else a plugin imports is bundled into the file.
// Usage: npm run build:plugins   (then commit the generated ui.js)
import { rolldown } from 'rolldown';
import { readdirSync, existsSync, statSync } from 'node:fs';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const here = dirname(fileURLToPath(import.meta.url));
const pluginsRoot = resolve(here, '../../../Plugins');
const EXTERNAL = ['react', 'react-dom', 'react/jsx-runtime', 'lucide-react', '@ir/sdk'];

let built = 0;
for (const dir of readdirSync(pluginsRoot)) {
  const assets = join(pluginsRoot, dir, 'Assets');
  if (!existsSync(assets) || !statSync(assets).isDirectory()) continue;
  const src = ['ui.src.jsx', 'ui.src.tsx'].map((f) => join(assets, f)).find(existsSync);
  if (!src) continue;

  const bundle = await rolldown({
    input: src,
    external: EXTERNAL,
    transform: { jsx: { runtime: 'automatic' } },
    logLevel: 'warn',
  });
  await bundle.write({ file: join(assets, 'ui.js'), format: 'esm', sourcemap: false });
  await bundle.close();
  console.log(`built ${dir}/Assets/ui.js from ${src.split(/[\/]/).pop()}`);
  built++;
}
console.log(built === 0 ? 'no ui.src.* files found' : `done (${built})`);
