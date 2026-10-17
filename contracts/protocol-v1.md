# SourceVault contracts v1 — feasibility baseline

This freezes wire framing and authorization semantics for spikes. Production APIs remain non-operational until their component gates pass. Unknown versions, operations, fields, and enum values fail closed.

## Transport

Local Windows named pipe, configured by trusted host. Remote clients rejected by OS pipe flags. Explicit DACL; use exact rights instead of generic write granting instance creation. Server uses first-instance protection. Mutual endpoint validation is mandatory for the eventual editor. OS token and session are authoritative; claims in a message are not identity evidence.

Every frame: 4-byte unsigned little-endian payload byte length, then UTF-8 JSON. Length must be 1..1,048,576 before allocation. Truncation, invalid UTF-8, duplicate JSON properties, unknown properties, missing required fields, excessive nesting (>16), and malformed JSON reject the connection. Ten-second whole-frame deadline in spikes. At most one request in flight per spike connection. Do not deserialize untrusted CLR type names.

Envelope: `version` (integer 1), `requestId` (canonical nonempty UUID), `operation` (exact case-sensitive string), `body` (object). All response envelopes use the same version and requestId, plus `ok` (boolean), `code` (defined error string), and `body` (object or null). No exception messages are sent to peers.

## Authorization and lifecycle

BeginSession occurs only after validating principal, session, and configured peer policy. A fresh random 256-bit capability is bound to that connection, principal, session, project, and expiry; never log it. Session idle expiry is 5 minutes, absolute lifetime 30 minutes. Closing/locking/revoking access invalidates the connection. A session token alone is not executable authentication. Each operation rechecks authorization.

Protocol operation catalog: BeginSession, ListProjects, ListFiles, OpenDocument, ApplyEdits, CloseDocument, RequestBuild, GetBuildStatus, EndSession. The initial IPC feasibility spike supports only `BeginSession` and metadata-only `GetBuildStatus`; it must not read/decrypt source. Unsupported catalog operations return UnsupportedOperation.

Use canonical nonempty UUIDs for project, file, revision, and build IDs. Documents are UTF-8 text <=256 KiB in the initial storage implementation. No user-supplied OS paths. ApplyEdits is a complete replacement of document text with an expected immutable revision (no arbitrary patch language initially); conflicts return RevisionConflict. A build names a committed immutable revision and a trusted profile ID. No arbitrary commands, recipe text, environment, or paths in operator requests.

Error codes: Ok, InvalidRequest, UnsupportedVersion, UnsupportedOperation, Unauthorized, NotFound, RevisionConflict, LimitExceeded, SessionExpired, Canceled, Unavailable, InternalError. Only metadata is audited. Bound request and response sizes, time, connection count, and job count.

## Storage interface boundary

The storage component receives an IKeyProtector-like abstraction with key wrapping/unwrapping only. TPM implementation must check the selected hardware provider and nonexportable policy and fail closed. Test protectors are test-only and never selected by production configuration. Ciphertext records and manifest formats require separate reviewed specification before production persistence.

## Build interface boundary

Request = immutable snapshot ID + approved recipe ID + job ID + fixed resource limits. Guest receives a bounded context stream and no key/capability. Guest agent cannot request more vault data. Reply = structured status plus bounded artifacts in broker-owned quarantine; no raw compiler text in ordinary logs. Exact recipe, VM channel authentication, and artifact contract are T03 decision gates. Do not invent an insecure unauthenticated guest listener to make the demo run.
