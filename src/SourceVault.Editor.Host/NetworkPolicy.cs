namespace SourceVault.Editor.Host;

/// <summary>
/// Pure, UI-free decisions that keep the editor's browser off the network: the browser arguments the host
/// launches WebView2 with, and the registry overrides that would let someone replace them. No WPF, WebView2 or
/// registry types appear here so the policy can be exercised by plain console tests. Navigation and resource
/// filtering by origin is in <see cref="PreviewPolicy"/>; these arguments are the layer underneath, for traffic
/// that never reaches those filters (DNS prefetch, preconnect, WebRTC, browser background services) or a
/// renderer that gets past them.
/// </summary>
internal static class NetworkPolicy
{
    /// <summary>
    /// A proxy that goes nowhere: loopback port 1 (tcpmux), which Windows does not serve. Every HTTP(S) and
    /// WebSocket connection the browser makes is sent to it and fails. A fixed proxy list has no DIRECT fallback.
    /// A same-user process could listen on this port and receive proxied requests; same-user processes are out
    /// of scope. The virtual host https://sourcevault.local is served inside the browser and never uses it.
    /// </summary>
    internal const string DeadProxy = "http://127.0.0.1:1";

    /// <summary>
    /// The browser switches, in order. Each is a Chromium switch; the WebView2 runtime may ignore switches it
    /// considers important to its own operation, so the native lab check (packet capture) is what proves the effect.
    /// </summary>
    internal static readonly IReadOnlyList<string> BrowserSwitches =
    [
        "--proxy-server=" + DeadProxy,
        // Chromium bypasses the proxy for loopback by default; "<-loopback>" removes that implicit bypass, so
        // http://127.0.0.1:<port> and http://localhost:<port> are proxied (and fail) too.
        "--proxy-bypass-list=<-loopback>",
        // Every host name, including localhost and the proxy-free paths (DNS prefetch, preconnect), resolves to nothing.
        "--host-resolver-rules=\"MAP * ~NOTFOUND\"",
        // WebRTC may use only proxied TCP, and the proxy is dead. Measured on runtime 155.0.4283.62 (2026-10-19): this
        // spelling stopped all ICE candidates and STUN UDP; upstream Chromium's "force-" spelling did not, and is kept
        // only in case a later runtime honours it instead. DocumentCreatedScript below closes WebRTC independently.
        "--webrtc-ip-handling-policy=disable_non_proxied_udp",
        "--force-webrtc-ip-handling-policy=disable_non_proxied_udp",
        "--disable-background-networking",
        "--disable-component-update",
        "--disable-domain-reliability",
        "--no-pings",
    ];

    /// <summary>The value for CoreWebView2EnvironmentOptions.AdditionalBrowserArguments.</summary>
    internal static string AdditionalBrowserArguments => string.Join(' ', BrowserSwitches);

    /// <summary>
    /// WebRTC traffic (STUN/TURN/peer UDP) bypasses the proxy, the host request filter and CSP, and only one spelling
    /// of the browser switch above was honoured when measured. Peer connections can only be created from window
    /// globals (not workers), so the host removes those globals in every document before any page script runs. The
    /// descriptor matches shell.mjs installNetworkBlocks(), which then redefines them identically (allowed for
    /// non-configurable properties).
    /// </summary>
    internal static readonly IReadOnlyList<string> WebRtcGlobals =
        ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCDataChannel", "RTCIceTransport"];

    /// <summary>The script passed to AddScriptToExecuteOnDocumentCreatedAsync.</summary>
    internal static string DocumentCreatedScript =>
        "for (const n of [" + string.Join(",", WebRtcGlobals.Select(n => "'" + n + "'")) + "]) " +
        "Object.defineProperty(globalThis, n, { value: undefined, writable: false, configurable: false });";

    /// <summary>Registry roots the WebView2 loader reads overrides from, HKLM first.</summary>
    internal const string PolicyKey = @"Software\Policies\Microsoft\Edge\WebView2";

    /// <summary>
    /// Returns "&lt;setting&gt;\&lt;value name&gt;" for the first registry policy override that could apply to this
    /// editor, or null when there is none. The WebView2 loader reads
    /// [HKLM|HKCU]\Software\Policies\Microsoft\Edge\WebView2\&lt;setting&gt; and uses the value named for the
    /// process's AppUserModelID, else its executable file name, else "*". It replaces the runtime folder or profile
    /// with those values and appends AdditionalBrowserArguments, where the last copy of a switch wins: such a value
    /// could restore direct network access or enable remote debugging.
    /// Fails closed: any value under any setting subkey counts (including unknown future settings and empty data),
    /// except values named for a different executable ("other.exe"), which the loader never applies to this process.
    /// Values directly under the WebView2 key are runtime-wide policies, not per-app overrides, and are not examined.
    /// </summary>
    /// <param name="values">Every (setting subkey name, value name) pair found under the policy key, from both roots.</param>
    /// <param name="executableName">This process's executable file name, e.g. SourceVault.Editor.exe.</param>
    internal static string? FindRegistryOverride(IEnumerable<(string Setting, string ValueName)> values, string executableName)
    {
        foreach (var (setting, valueName) in values)
        {
            if (setting is null || valueName is null) return setting + "\\" + valueName; // unreadable entry: fail closed
            // With no known executable name nothing can be ruled out.
            bool otherExecutable = !string.IsNullOrEmpty(executableName) && valueName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(valueName, executableName, StringComparison.OrdinalIgnoreCase);
            if (!otherExecutable) return setting + "\\" + (valueName.Length == 0 ? "(Default)" : valueName);
        }
        return null;
    }
}
