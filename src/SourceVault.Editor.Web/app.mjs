// Bootstrap: block network and export paths first, then ask the host which mode to run. Nothing is rendered from
// any mode until the mode is known, so a vault editor never shows the synthetic sample or the demo.
import { installNetworkBlocks, installExportBlocks } from './shell.mjs';

installNetworkBlocks();
installExportBlocks();
const mode = await detectMode();
if (mode.kind === 'vault') (await import('./vault-app.mjs')).startVault(mode.transport, mode.metadata, mode.onSession);
else if (mode.kind === 'demo') await startDemo(mode.metadata);
else (await import('./preview-app.mjs')).startPreview();

async function detectMode() {
  const host = window.chrome?.webview;
  if (host) {
    // The host answers every ready message with its metadata (mode, capture exclusion, service state).
    const metadata = await new Promise(resolve => {
      const timer = setTimeout(() => resolve(null), 3000);
      host.addEventListener('message', function onMessage(e) {
        if (e.data?.type !== 'sourcevault.host.metadata') return;
        clearTimeout(timer); host.removeEventListener('message', onMessage); resolve(e.data);
      });
      host.postMessage({ type: 'sourcevault.preview.ready', locked: true, dirtyCount: 0, revisionCount: 0 });
    });
    if (metadata?.mode === 'vault') {
      return { kind: 'vault', metadata, transport: {
        send: message => host.postMessage(message),
        subscribe: handler => { const listener = e => handler(e.data); host.addEventListener('message', listener); return () => host.removeEventListener('message', listener); },
      } };
    }
    if (metadata?.mode === 'demo') return { kind: 'demo', metadata };
    return { kind: 'preview' };
  }
  // In a browser (development review): ?demo, or the older ?fake-vault, runs the demo without a host.
  const query = new URLSearchParams(location.search);
  if (query.has('demo') || query.has('fake-vault')) return { kind: 'demo', metadata: { mode: 'demo', captureExclusion: false, serviceConnected: false } };
  return { kind: 'preview' };
}

// Demo mode: the full vault editor on the in-page demo vault (demo-host.mjs). Nothing is encrypted or stored.
async function startDemo(metadata) {
  const [{ DemoVaultHost, installDemoPanel }, { startVault }] = await Promise.all([import('./demo-host.mjs'), import('./vault-app.mjs')]);
  const demo = new DemoVaultHost({ latencyMs: 40 });
  let session = null;
  installDemoPanel(demo, () => session?.project && session.activeId ? { projectId: session.project.projectId, fileId: session.activeId } : null);
  startVault(demo.transport, { ...metadata, demo: true }, s => { session = s; });
}
