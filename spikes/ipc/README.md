# T02: local named-pipe feasibility spike

Windows-only, BCL-only .NET 10 console. Run `dotnet run --project spikes/ipc/SourceVault.IpcSpike.csproj -- self-test` from the repository root. No service installation, source/key handling, filesystem outputs, policy changes, or external network operations occur. Pipe names are fresh random local names. Only synthetic metadata is accepted.

The native server uses a protected DACL granting configured SID and SYSTEM only `FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE` (`0x00100083`). Read-attribute access permits endpoint metadata inspection. There is no generic-write grant (which includes the pipe-instance-creation bit). It uses `FILE_FLAG_FIRST_PIPE_INSTANCE`, `FILE_FLAG_OVERLAPPED`, and `PIPE_REJECT_REMOTE_CLIENTS`. The native client requests these same exact rights with identification-level SQOS, then checks OS-reported server PID and its primary token SID/session against trusted policy before sending any application data. First-instance protection detects an existing squatter; endpoint checks remain necessary if a client connects to an impersonating first server. See Microsoft's [pipe access-rights guidance](https://learn.microsoft.com/en-us/windows/win32/ipc/named-pipe-security-and-access-rights).

After reading each bounded frame, the server uses `RunAsClient` to obtain the OS impersonation token and validates SID, token session, and OS-reported client PID against policy. Impersonation is synchronous and scoped; no asynchronous operation executes while impersonating. No message identity claim is trusted. Process handles are opened with query-limited rights; token handles have query rights only. All handles and security descriptors are disposed/freed.

The interactive harness deliberately pins both peers to its own PID, SID and session. This proves plumbing and rejection, not authentication of a separately installed editor. Production must configure a distinct service SID and narrowly scoped server rights; this spike has no service SID. DACL denial with a genuinely separate user token, service-account operation, cross-session operation, remote-host rejection, and process-handle access under deployment identities require lab validation.

## Metadata protocol

Framing/parser code comes from `SourceVault.Contracts`. Frames have a ten-second read-and-response deadline. Connections have a 16-request budget; the harness starts one server instance at a time. Unknown envelope fields are rejected by the shared parser; supported operation bodies reject unknown fields. Malformed frames and denied peers close/fault the connection without reflecting exception text.

- `BeginSession`: body `{ "projectId": "11111111-1111-4111-8111-111111111111" }`. This one synthetic project issues a random 256-bit base64 capability. It is retained only in the connection object, returned only to that connection, and never printed. Repeat begin requests fail.
- `GetBuildStatus`: body `{ "capability": "...", "buildId": "canonical UUID" }`. The capability must match in constant time, with five-minute idle and thirty-minute absolute expiry measured using monotonic `TimeProvider` timestamps. Expiry is terminal for the connection, zeroes the capability, and prevents restarting. No builds exist, so an authorized lookup returns `NotFound`.
- Other catalog operations return `UnsupportedOperation`. There is no source API, build execution, user OS path, or command input.

These body shapes are spike-local pending reviewed production operation schemas. There is no external listener mode: the executable exposes only `self-test`.

## Unverified and deliberately absent

Executable path, signer, hash and protected-installation policy are **not implemented or verified**. SID/session/PID checks do not establish immutable executable identity; a token alone does not prove which software is asking. Future signer/path checks are defense in depth, require process lifetime/handle races to be addressed, and do not prevent same-user injection or administrator inspection.

OS lock/logoff/revocation notifications are **not wired**. Expiry is checked on requests; closing/faulting the connection zeroes the stored capability. A production host must invalidate all affected sessions on lock, revocation and identity policy changes before exposing any source. No idle connections are kept beyond the next frame deadline in this harness. No production service, session manager, installer, audit sink or multiple-client scheduler exists here.

The eleven self-tests cover deterministic monotonic idle/absolute expiry with terminal session state, real local pipe round trips, metadata authorization/capability mismatch, unsupported operations/unknown body properties, wrong client SID/session/PID policy, wrong server SID policy, existing first-instance rejection, truncated payload, empty/malformed frames, and caller cancellation of a stalled partial frame. The ten-second configured deadline is implemented but its full elapsed duration is not asserted by the 150ms caller-cancellation test. A passing loopback check does not close the deployment gates above.

## Initial execution evidence

Built with portable .NET SDK 10.0.402 with zero warnings/errors. Process-local `APPDATA`/`DOTNET_CLI_HOME` were redirected to repository `artifacts/cli-home`; restore used the repository's source-cleared NuGet.Config. The sandbox token cannot open the strict SID-only pipe (`CreateFile` returns access denied); the DACL was retained. Direct execution of the compiled DLL through the SDK host outside that restricted sandbox passes the local checks. Run `dotnet spikes/ipc/bin/Debug/net10.0-windows/SourceVault.IpcSpike.dll self-test` after build in an ordinary interactive Windows process. This execution is not the separate-account/service validation gate.
