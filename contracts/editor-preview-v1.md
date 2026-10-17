# Editor preview boundary

The initial editor is a synthetic-only UI. It has no service, vault, key or real-file access. Seed documents and Save Revision history are held in renderer memory; reload/process exit resets them. This is a visible interaction preview, not evidence of plaintext confinement.

Both browser and WPF host serve the same `src/SourceVault.Editor.Web` assets with bundled Monaco. The WPF origin is exactly `https://sourcevault.local/index.html`; browser testing uses an explicitly bound loopback server. External navigation, resources and network features are disabled in the native host. The preview does not enable capture exclusion so its UI can be inspected. Production capture behavior is a separate gate.

Optional renderer-to-host message: exactly four fields: `type` (`sourcevault.preview.ready` or `sourcevault.preview.status`), `locked` (boolean), `dirtyCount` (integer 0..32), `revisionCount` (integer 0..1000000). Reject wrong origin, unknown/duplicate fields, excessive payload (>4096 bytes), and type/range mismatches. No source text, filenames, paths or capabilities pass this bridge.

Host reply: `{"type":"sourcevault.host.metadata","mode":"synthetic-preview","captureExclusion":false,"serviceConnected":false}`. Optional host lock event: `{"type":"sourcevault.preview.lock"}`. No arbitrary script evaluation, host objects, disk-write bridge or generic IPC passthrough exists.

Locking hides/detaches source-bearing editor models as implemented, but cannot promise JavaScript/OS memory erasure. Session-only Save must not claim encryption or durable persistence. The preview must identify its synthetic scope in the visible interface.
