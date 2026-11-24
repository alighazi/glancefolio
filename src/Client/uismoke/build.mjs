// Bundles the smoke-test entry with esbuild's JS API. Going through the API
// rather than the CLI keeps the quoting of `define` out of the npm script,
// where nested quotes get eaten by the shell.
import { build } from 'esbuild';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));

await build({
  entryPoints: [path.join(here, 'entry.tsx')],
  // dist/ is build output, not source: the bundle is ~1.3MB of React.
  outfile: path.join(here, '..', 'dist', 'uismoke', 'bundle.mjs'),
  bundle: true,
  format: 'esm',
  platform: 'browser',
  loader: { '.css': 'text' },
  // React's CJS entry branches on this; without it the bundle references a
  // `process` that does not exist in the browser build.
  define: { 'process.env.NODE_ENV': '"development"' },
  logLevel: 'warning',
});

console.log('ui smoke bundle built');
