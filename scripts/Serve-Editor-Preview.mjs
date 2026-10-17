import http from 'node:http';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fs.realpathSync(path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../src/SourceVault.Editor.Web'));
const port = Number(process.argv[2] ?? '4173');
if (!Number.isInteger(port) || port < 1024 || port > 65535) throw new Error('Invalid preview port');
const types = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.mjs': 'text/javascript; charset=utf-8', '.css': 'text/css; charset=utf-8', '.json': 'application/json; charset=utf-8', '.ttf': 'font/ttf', '.woff': 'font/woff', '.woff2': 'font/woff2', '.svg': 'image/svg+xml' };
const server = http.createServer((request, response) => {
  const expectedHost = `127.0.0.1:${port}`;
  if (request.headers.host !== expectedHost || !['GET', 'HEAD'].includes(request.method)) {
    response.writeHead(403); response.end(); return;
  }
  try {
    const url = new URL(request.url, `http://${expectedHost}`);
    const name = decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname);
    if (name.includes('\\') || name.includes('\0') || name.includes(':')) throw new Error();
    const candidate = path.resolve(root, `.${name}`);
    const real = fs.realpathSync(candidate);
    if (!real.startsWith(root + path.sep) || !fs.statSync(real).isFile()) throw new Error();
    response.writeHead(200, {
      'Content-Type': types[path.extname(real)] ?? 'application/octet-stream',
      'Cache-Control': 'no-store',
      'X-Content-Type-Options': 'nosniff',
      'Referrer-Policy': 'no-referrer',
      'Cross-Origin-Resource-Policy': 'same-origin',
      'Permissions-Policy': 'camera=(), microphone=(), geolocation=(), clipboard-read=(), clipboard-write=()',
      'Content-Security-Policy': "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; font-src 'self' data:; img-src 'self' data:; worker-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'"
    });
    if (request.method === 'HEAD') response.end();
    else fs.createReadStream(real).on('error', () => response.destroy()).pipe(response);
  } catch { response.writeHead(404, { 'Cache-Control': 'no-store' }); response.end('Not found'); }
});
server.on('error', () => { console.error('Editor preview could not bind the requested loopback port.'); process.exitCode = 1; });
server.listen(port, '127.0.0.1', () => console.log(`Synthetic editor preview: http://127.0.0.1:${port}/index.html`));
