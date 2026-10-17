#!/usr/bin/env bash
# EXPERIMENTAL LAB ONLY. Run inside an already provisioned Linux VM.
# This checks mount/swap preconditions; it cannot prove RAM-only processing.
set -euo pipefail
umask 077
if [[ $# != 3 ]]; then
  echo 'Usage: run-guest-synthetic.sh COMPILER_DLL SYNTHETIC_CONTEXT_TAR EXISTING_TMPFS_WORKSPACE' >&2
  exit 2
fi
compiler=$(realpath -e -- "$1")
context=$(realpath -e -- "$2")
workspace=$(realpath -e -- "$3")
[[ -f "$compiler" && -f "$context" && -d "$workspace" ]]
[[ $(findmnt -n -o FSTYPE -T "$workspace") == tmpfs ]] || { echo 'Rejected: workspace is not tmpfs' >&2; exit 1; }
[[ $(wc -l < /proc/swaps) == 1 ]] || { echo 'Rejected: active swap' >&2; exit 1; }
[[ $(stat -c %s -- "$context") -le 1048576 ]] || { echo 'Rejected: context limit' >&2; exit 1; }
job=$(mktemp -d -- "$workspace/sourcevault-synthetic.XXXXXXXX")
cp -- "$context" "$job/context.tar"
export TMPDIR="$job"
export DOTNET_EnableDiagnostics=0
export COMPlus_EnableDiagnostics=0
export DOTNET_CLI_TELEMETRY_OPTOUT=1
ulimit -c 0
ulimit -t 10
ulimit -f 4096
# No shell command from context, no network listener, no compiler diagnostics.
timeout --signal=KILL 15s dotnet "$compiler" synthetic-compile "$job/context.tar" "$job/artifact.tar" >/dev/null 2>&1
timeout --signal=KILL 5s dotnet "$compiler" validate-artifacts "$job/artifact.tar" >/dev/null 2>&1
echo 'Synthetic guest compile and artifact validation passed.'
echo 'Job retained in supplied tmpfs workspace for administrator inspection.'
# No automatic extraction, publication, VM deletion, or secure-erasure claim.
