// Bootstrap: block export paths first, then ask the host which mode to run. Nothing is rendered from either mode
// until the mode is known, so a vault editor never shows the synthetic sample.
import { installExportBlocks } from './shell.mjs';

installExportBlocks();
const mode = await detectMode();
if (mode.kind === 'vault') (await import('./vault-app.mjs')).startVault(mode.transport, mode.metadata, mode.onSession);
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
    return { kind: 'preview' };
  }
  // Browser review of the vault UI without a service: ?fake-vault loads the development fake host,
  // which is excluded from the native editor's assets.
  if (new URLSearchParams(location.search).has('fake-vault')) {
    const { FakeVaultHost, installFakeHostPanel } = await import('./dev/fake-host.mjs');
    const fake = new FakeVaultHost({ latencyMs: 40 });
    let session = null;
    installFakeHostPanel(fake, () => session?.project && session.activeId ? { projectId: session.project.projectId, fileId: session.activeId } : null);
    return { kind: 'vault', transport: fake.transport, onSession: s => { session = s; },
      metadata: { mode: 'vault', captureExclusion: false, serviceConnected: false, fake: true } };
  }
  return { kind: 'preview' };
}
