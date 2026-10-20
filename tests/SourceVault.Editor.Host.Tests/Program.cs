using System.Text;
using SourceVault.Editor.Host;

int passed = 0;
var failures = new List<string>();

void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failures.Add(name); Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Assert(bool value, string detail) { if (!value) throw new Exception(detail); }
void Accepts(Func<string?, bool> policy, params string[] inputs)
{
    foreach (string input in inputs) Assert(policy(input), "expected accept: " + Show(input));
}
void Rejects(Func<string?, bool> policy, params string?[] inputs)
{
    foreach (string? input in inputs) Assert(!policy(input), "expected reject: " + Show(input));
}
string Show(string? value) => value is null ? "<null>" : value.Length > 80 ? value[..80] + "..." : value;

const string App = "https://sourcevault.local";
const string Guid1 = "6f3e1c52-8a6b-4f4e-9d0c-1b2a3c4d5e6f";

// ---- Origin and URI policy ----
Check("app origin accepts the expected asset, query and fragment forms", () => Accepts(PreviewPolicy.IsAppUri,
    App, App + "/", App + "/index.html", App + "/index.html?x=1", App + "/index.html#top", App + "/index.html?a=b&c=%2e%2e%2f#frag",
    App + ":443/index.html", App + "?q=1", App + "#f", App + "/vendor/monaco/vs/editor/editor.main.js", App + "/a%20b.txt",
    "HTTPS://SOURCEVAULT.LOCAL/index.html", App.ToUpperInvariant() + "/x"));

Check("lookalike hosts are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    "https://sourcevault.local.evil", "https://sourcevault.local.evil/", "https://sourcevault.local.evil.example/index.html",
    "https://evil.sourcevault.local/", "https://sourcevault.localx/", "https://sourcevault.loca/", "https://sourcevault-local/",
    "https://sourcevault.local./", "https://sourcevault.local.:443/", "https://xsourcevault.local/", "https://sourcevaultXlocal/",
    "https://sourcevault.local%2eevil/", "https://sourcevault.local%00.evil/", "https://sourcevault.local_.evil/"));

Check("userinfo and authority confusion are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    "https://sourcevault.local@evil", "https://sourcevault.local@evil/", "https://sourcevault.local:443@evil/",
    "https://user@sourcevault.local/", "https://user:pw@sourcevault.local/", "https://evil@sourcevault.local@evil/",
    "https://evil/@sourcevault.local", "https://evil?@sourcevault.local/", "https://evil#@sourcevault.local/",
    "https://sourcevault.local:pw@evil/", "https://@sourcevault.local/", "https://:@sourcevault.local/"));

Check("backslash authority and path tricks are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    "https://sourcevault.local\\@evil", "https://sourcevault.local\\index.html", "https://sourcevault.local/\\evil/",
    "https://evil\\@sourcevault.local/", "https:\\\\sourcevault.local/", "https://sourcevault.local/a\\..\\b"));

Check("alternate ports and explicit-port forms are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    "https://sourcevault.local:80/", "https://sourcevault.local:8443/", "https://sourcevault.local:444/", "https://sourcevault.local:4430/",
    "https://sourcevault.local:0443/", "https://sourcevault.local:00443/", "https://sourcevault.local:/", "https://sourcevault.local:443:443/",
    "https://sourcevault.local:+443/", "https://sourcevault.local:443x/", "https://sourcevault.local:65535/", "https://sourcevault.local:-1/"));

Check("non-https schemes and scheme-relative forms are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    "http://sourcevault.local/", "http://sourcevault.local:443/", "ws://sourcevault.local/", "wss://sourcevault.local/", "ftp://sourcevault.local/",
    "file://sourcevault.local/index.html", "file:///C:/Windows/win.ini", "data:text/html,<script>1</script>", "javascript:alert(1)",
    "about:blank", "//sourcevault.local/index.html", "/index.html", "index.html", "sourcevault.local", "sourcevault.local/index.html",
    "https:sourcevault.local", "https:/sourcevault.local/", "https:///sourcevault.local/", "https://", "https:", "", "blob:", "x-https://sourcevault.local/"));

Check("IDN, punycode, homoglyph and non-ASCII tricks are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    "https://sourcevault\u3002local/", "https://sourcevault\uFF0Elocal/", "https://sourcevault\uFF61local/",
    "https://\uFF53ourcevault.local/", "https://s\u00F6urcevault.local/", "https://sourcevau\u0131t.local/", "https://sourcevault.l\u043Ecal/",
    "https://sourcevault.local\u200B/", "https://\u200Bsourcevault.local/", "https://sourcevault.local/\u2215evil",
    "https://xn--sourcevault-local-3x1e/", "https://xn--ourcevault-0ib.local/", "https://xn--sourcevault.local/", "https://sourcevault.xn--local-7ya/",
    App + "/\u00E9", App + "/index.html?\u00E9", App + "/#\u00E9"));

Check("whitespace and control characters are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    " " + App + "/", App + "/ ", App + "/index.html ", "\t" + App + "/", App + "/\r\nHost: evil", App + "/\n", App + "/a b",
    App + "/index.html\0", "\0" + App + "/", App + "/\u0001", App + "/index.html?a=b c", "https://sourcevault.local /", "https://sourcevault.local\t/"));

Check("percent-encoded traversal and separators in the path are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    App + "/%2e%2e/secret", App + "/%2E%2E/secret", App + "/%2e%2E/", App + "/.%2e/", App + "/%2e./", App + "/a/%2e%2e/b",
    App + "/a%2fb", App + "/a%2Fb", App + "/%2f%2fevil", App + "/a%5cb", App + "/a%5Cb", App + "/..%5c..%5cwin.ini", App + "/%2e%2e%5c",
    App + "/%252e%252e/", App + "/%252e%252e%252f", App + "/%25252e/", App + "/%25", App + "/a%00.html", App + "/index.html%00.js",
    App + "/%", App + "/a%", App + "/a%2", App + "/a%zz", App + "/%g1", App + "/%2e"));

Check("literal dot segments and double slashes that escape the asset root are rejected", () => Rejects(PreviewPolicy.IsAppUri,
    App + "/..", App + "/../secret", App + "/a/../b", App + "/./index.html", App + "/a/./b", App + "/a/..", App + "/a/.", App + "/../x", App + "/..;/..", App + "/a/../../b"));

Check("encoded traversal confined to the query or fragment is not a path decision", () => Accepts(PreviewPolicy.IsAppUri,
    App + "/index.html?next=%2e%2e%2fsecret", App + "/index.html?next=../x", App + "/index.html#/%2e%2e/x", App + "/?%252e%252e"));

Check("overlong URIs are rejected", () =>
{
    Rejects(PreviewPolicy.IsAppUri, App + "/" + new string('a', PreviewPolicy.MaxUriLength));
    Accepts(PreviewPolicy.IsAppUri, App + "/" + new string('a', 1000));
});

Check("null and empty URIs are rejected by every URI entry point", () =>
{
    Rejects(PreviewPolicy.IsAppUri, null, "");
    Rejects(PreviewPolicy.IsAppBlobUri, null, "");
    Rejects(PreviewPolicy.IsNavigationAllowed, null, "");
    Rejects(PreviewPolicy.IsResourceAllowed, null, "");
});

// ---- Blob URIs ----
Check("blob URIs from the app origin with a GUID path are allowed", () => Accepts(PreviewPolicy.IsAppBlobUri,
    "blob:" + App + "/" + Guid1, "blob:" + App + ":443/" + Guid1, "blob:" + App + "/" + Guid1.ToUpperInvariant(), "blob:" + App + "/" + Guid1 + "#frag"));

Check("blob URIs of other origins are rejected", () => Rejects(PreviewPolicy.IsAppBlobUri,
    "blob:https://evil.example/" + Guid1, "blob:https://sourcevault.local.evil/" + Guid1, "blob:https://sourcevault.local@evil/" + Guid1,
    "blob:http://sourcevault.local/" + Guid1, "blob:https://sourcevault.local:8443/" + Guid1, "blob:null/" + Guid1, "blob:null",
    "blob:file:///C:/x/" + Guid1, "blob:https://user@sourcevault.local/" + Guid1, "blob:https://sourcevault\u3002local/" + Guid1,
    "blob:blob:" + App + "/" + Guid1, "blob:data:text/html,x", "blob:javascript:alert(1)"));

Check("malformed blob URIs from the app origin are rejected", () => Rejects(PreviewPolicy.IsAppBlobUri,
    "blob:" + App, "blob:" + App + "/", "blob:" + App + "/index.html", "blob:" + App + "/../" + Guid1, "blob:" + App + "/" + Guid1 + "/extra",
    "blob:" + App + "/" + Guid1 + "?x=1", "blob:" + App + "/not-a-guid", "blob:" + App + "/" + Guid1[..^1], "blob:" + App + "/{" + Guid1 + "}",
    "blob:" + App + "/%2e%2e/" + Guid1, "blob:" + App + "/" + Guid1.Replace("-", ""), "blob: " + App + "/" + Guid1, " blob:" + App + "/" + Guid1,
    "BLOB:" + App + "/" + Guid1, "Blob:" + App + "/" + Guid1, App + "/" + Guid1));

Check("navigation never allows blob URIs, resources allow only app URIs and app blobs", () =>
{
    Rejects(PreviewPolicy.IsNavigationAllowed, "blob:" + App + "/" + Guid1, "https://evil.example/", "http://sourcevault.local/");
    Accepts(PreviewPolicy.IsNavigationAllowed, App + "/index.html", PreviewPolicy.EntryUri);
    Accepts(PreviewPolicy.IsResourceAllowed, App + "/vs/loader.js", "blob:" + App + "/" + Guid1);
    Rejects(PreviewPolicy.IsResourceAllowed, "https://cdn.jsdelivr.net/npm/monaco-editor/min/vs/loader.js", "https://sourcevault.local.evil/vs/loader.js",
        "http://127.0.0.1:8080/index.html", "data:text/javascript,1", "blob:https://evil.example/" + Guid1, "ws://sourcevault.local/", "file:///C:/secret.txt");
});

// ---- Bridge messages ----
string Msg(string type = "sourcevault.preview.status", string locked = "false", string dirty = "0", string revision = "0") =>
    $$"""{"type":"{{type}}","locked":{{locked}},"dirtyCount":{{dirty}},"revisionCount":{{revision}}}""";
bool Parses(string? json) => PreviewPolicy.TryParseMessage(json, out _);
void BadMessages(params string?[] inputs)
{
    foreach (string? input in inputs)
    {
        bool ok = PreviewPolicy.TryParseMessage(input, out var m);
        Assert(!ok, "expected reject: " + Show(input));
        Assert(m.Equals(default(PreviewMessage)), "rejected message must not leak parsed state: " + Show(input));
    }
}

Check("valid ready and status messages parse with exact values", () =>
{
    Assert(PreviewPolicy.TryParseMessage(Msg("sourcevault.preview.ready", "true", "3", "17"), out var ready) &&
        ready == new PreviewMessage(PreviewMessageKind.Ready, true, 3, 17), "ready");
    Assert(PreviewPolicy.TryParseMessage(Msg(), out var status) &&
        status == new PreviewMessage(PreviewMessageKind.Status, false, 0, 0), "status");
    Assert(PreviewPolicy.TryParseMessage(Msg(locked: "true", dirty: "32", revision: "1000000"), out var max) &&
        max == new PreviewMessage(PreviewMessageKind.Status, true, 32, 1_000_000), "bounds inclusive");
});

Check("property order, insignificant whitespace and escaped but valid names are accepted", () =>
{
    Assert(Parses("""{"revisionCount":1,"dirtyCount":2,"locked":true,"type":"sourcevault.preview.status"}"""), "reordered");
    Assert(Parses("  {\n \"type\" : \"sourcevault.preview.ready\" ,\"locked\":false,\"dirtyCount\":0,\"revisionCount\":0 }\n"), "whitespace");
    Assert(Parses("""{"\u0074ype":"sourcevault.preview.ready","locked":false,"dirtyCount":0,"revisionCount":0}"""), "escaped name");
    Assert(Parses("""{"type":"sourcevault.preview.\u0072eady","locked":false,"dirtyCount":0,"revisionCount":0}"""), "escaped value is the same value");
});

Check("duplicate keys are rejected, including escaped and case-variant-equivalent forms", () => BadMessages(
    """{"type":"sourcevault.preview.status","type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"locked":true,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","\u0074ype":"sourcevault.preview.ready","locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"x","type":"sourcevault.preview.status","locked":false,"dirtyCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"dirtyCount":1}"""));

Check("extra, missing, renamed and case-variant fields are rejected", () => BadMessages(
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0,"text":"secret source"}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0,"__proto__":{}}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"revisionCount":0}""",
    """{"locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0,"x":null}""",
    """{"Type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","Locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"RevisionCount":0}""",
    """{"type ":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{}""", """{"type":"sourcevault.preview.status"}"""));

Check("unknown, near-miss and wrong-cased message types are rejected", () => BadMessages(
    Msg("sourcevault.preview.lock"), Msg("sourcevault.host.metadata"), Msg("sourcevault.preview.Ready"), Msg("SOURCEVAULT.PREVIEW.READY"),
    Msg("sourcevault.preview.ready "), Msg(" sourcevault.preview.ready"), Msg("sourcevault.preview.ready\\u0000"), Msg("sourcevault.preview."),
    Msg(""), Msg("save"), Msg("sourcevault.preview.readyx"), Msg("sourcevault.preview.status\\n"), Msg("sourcevault\\u002Epreview\\u002Eerase")));

Check("wrong value types are rejected", () => BadMessages(
    Msg(locked: "\"false\""), Msg(locked: "0"), Msg(locked: "1"), Msg(locked: "null"), Msg(locked: "[]"), Msg(locked: "\"true\""),
    Msg(dirty: "\"0\""), Msg(dirty: "null"), Msg(dirty: "true"), Msg(dirty: "[0]"), Msg(dirty: "{}"), Msg(dirty: "\"\""),
    Msg(revision: "\"0\""), Msg(revision: "null"), Msg(revision: "false"), Msg(revision: "[1]"), Msg(revision: "{\"a\":1}"),
    """{"type":null,"locked":false,"dirtyCount":0,"revisionCount":0}""", """{"type":1,"locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":["sourcevault.preview.status"],"locked":false,"dirtyCount":0,"revisionCount":0}""",
    """{"type":{"v":"sourcevault.preview.status"},"locked":false,"dirtyCount":0,"revisionCount":0}"""));

Check("floating point, exponent and non-integer numbers are rejected for counts", () => BadMessages(
    Msg(dirty: "1.5"), Msg(dirty: "1.0"), Msg(dirty: "0.0"), Msg(dirty: "1e0"), Msg(dirty: "1E1"), Msg(dirty: "2e1"), Msg(dirty: "0.1e1"),
    Msg(revision: "1.5"), Msg(revision: "1.0"), Msg(revision: "1e3"), Msg(revision: "1000000.0"), Msg(revision: "1e6"), Msg(revision: "0.0"),
    Msg(dirty: "3.0000000000000001"), Msg(dirty: "NaN"), Msg(dirty: "Infinity"), Msg(dirty: "-Infinity"), Msg(dirty: "0x10"), Msg(dirty: "+1"),
    Msg(dirty: "01"), Msg(dirty: ".5"), Msg(dirty: "5."), Msg(revision: "007")));

Check("out-of-range and overflowing counts are rejected", () => BadMessages(
    Msg(dirty: "-1"), Msg(dirty: "33"), Msg(dirty: "100"), Msg(dirty: "2147483647"), Msg(dirty: "2147483648"), Msg(dirty: "4294967296"),
    Msg(dirty: "99999999999999999999999999"), Msg(dirty: "-2147483648"), Msg(dirty: "-2147483649"),
    Msg(revision: "-1"), Msg(revision: "1000001"), Msg(revision: "2147483647"), Msg(revision: "2147483648"), Msg(revision: "9223372036854775807"),
    Msg(revision: "-9223372036854775808"), Msg(revision: "1" + new string('0', 400))));

Check("count boundaries are exact", () =>
{
    Assert(Parses(Msg(dirty: "0")) && Parses(Msg(dirty: "32")) && !Parses(Msg(dirty: "33")) && !Parses(Msg(dirty: "-1")), "dirtyCount 0..32");
    Assert(Parses(Msg(revision: "0")) && Parses(Msg(revision: "1000000")) && !Parses(Msg(revision: "1000001")) && !Parses(Msg(revision: "-1")), "revisionCount 0..1000000");
});

Check("non-object roots are rejected", () => BadMessages(
    "[]", "[" + Msg() + "]", "null", "true", "false", "0", "1", "\"string\"", "\"" + Msg().Replace("\"", "\\\"") + "\"", "[[[]]]", "-1.5e3",
    "\"sourcevault.preview.ready\""));

Check("invalid JSON, trailing content and JSON extensions are rejected", () => BadMessages(
    null, "", " ", "\0", "{", "}", "{\"type\":", Msg() + "}", Msg() + "{}", Msg() + Msg(), Msg() + ",", Msg() + " x", Msg()[..^1],
    Msg().Replace("\"locked\":false,", "\"locked\":false,,"), Msg().Replace("\"revisionCount\":0", "\"revisionCount\":0,"),
    Msg().Replace("\"", "'"), "{type:\"sourcevault.preview.status\",locked:false,dirtyCount:0,revisionCount:0}",
    "/*c*/" + Msg(), Msg().Replace("{", "{/*c*/"), "// c\n" + Msg(), Msg().Replace("\"locked\"", "\"locked\" //x\n"),
    "\uFEFF" + Msg(), "\ufffe" + Msg(), "{\"type\":\"sourcevault.preview.status\",\"locked\":false,\"dirtyCount\":0,\"revisionCount\":0,\"z\":\"\ud800\"}",
    Msg("sourcevault.preview.status\ud800"), Msg().Replace("\"locked\":false", "\"locked\":tru"), Msg().Replace("false", "False"),
    Msg().Replace("\"type\"", "type"), Msg() + "\0"));

Check("unterminated strings and lone surrogates never throw out of the parser", () =>
{
    foreach (string input in new[] { "{\"type\":\"abc", "\"\\ud800\"", "{\"\\ud800\":1}", "{\"type\":\"\\uZZZZ\"}", "\ud800", "{\"type\":\"\udc00\"}" })
        Assert(!Parses(input), "expected quiet reject: " + Show(input));
});

Check("deep nesting is rejected and cannot be used to smuggle fields", () => BadMessages(
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0,"n":[[[[0]]]]}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":{"a":{"b":{"c":1}}}}""",
    """{"type":"sourcevault.preview.status","locked":false,"dirtyCount":[[[0]]],"revisionCount":0}""",
    """{"type":"sourcevault.preview.status","locked":{"v":false},"dirtyCount":0,"revisionCount":0}""",
    "{\"a\":" + new string('[', 1000) + new string(']', 1000) + "}",
    new string('[', 100000) + new string(']', 100000),
    "{\"type\":\"sourcevault.preview.status\",\"locked\":false,\"dirtyCount\":0,\"revisionCount\":0,\"a\":{\"b\":{\"c\":{}}}}"));

Check("payload size limit is enforced in UTF-8 bytes, not characters", () =>
{
    string padded = "{\"type\":\"sourcevault.preview.status\",\"locked\":false,\"dirtyCount\":0,\"revisionCount\":0}" + new string(' ', 4096);
    Assert(Encoding.UTF8.GetByteCount(padded) > 4096 && !Parses(padded), "whitespace padded past 4096 bytes");
    string exact = Msg() + new string(' ', 4096 - Encoding.UTF8.GetByteCount(Msg()));
    Assert(Encoding.UTF8.GetByteCount(exact) == 4096 && Parses(exact), "exactly 4096 bytes is accepted");
    Assert(!Parses(exact + " "), "4097 bytes is rejected");
    string huge = Msg() + new string(' ', 5_000_000);
    Assert(!Parses(huge), "multi-megabyte payload");
    // 1400 three-byte characters = 4200 bytes but only 1400 chars: a char-count check would pass this.
    string wide = "{\"type\":\"sourcevault.preview.status\",\"locked\":false,\"dirtyCount\":0,\"revisionCount\":0,\"k\":\"" + new string('\u20AC', 1400) + "\"}";
    Assert(wide.Length < 4096 && Encoding.UTF8.GetByteCount(wide) > 4096 && !Parses(wide), "multibyte payload over 4096 bytes");
    Assert(!Parses(Msg("sourcevault.preview.status" + new string('x', 5000))), "oversized type string");
});

Check("rejected messages never expose renderer-controlled strings in the result", () =>
{
    Assert(!PreviewPolicy.TryParseMessage("""{"type":"sourcevault.preview.status","locked":false,"dirtyCount":0,"revisionCount":0,"secret":"TOP-SECRET-SOURCE"}""", out var m), "rejects");
    Assert(m.ToString()!.IndexOf("TOP-SECRET", StringComparison.Ordinal) < 0, "no leakage");
    Assert(typeof(PreviewMessage).GetProperties().All(p => p.PropertyType != typeof(string)), "PreviewMessage has no string members");
});

// ---- WEBVIEW2_* environment refusal ----
string? Find(params (string Key, string? Value)[] vars) =>
    PreviewPolicy.FindWebView2Override(vars.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value)));

Check("clean and unrelated environments are allowed", () =>
{
    Assert(Find() is null, "empty environment");
    Assert(Find(("PATH", "C:\\x"), ("TEMP", "C:\\t"), ("WEBVIEW", "1"), ("WEBVIEW2", "1"), ("WEBVIEW2X", "1"), ("MY_WEBVIEW2_X", "1"), ("XWEBVIEW2_Y", "1"),
        ("WEBVIEW_2_A", "1"), ("WEBVIEW2-A", "1")) is null, "lookalike names are not WEBVIEW2_ prefixed");
});

Check("every nonempty WEBVIEW2_ override is refused", () =>
{
    foreach (string name in new[] { "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER",
        "WEBVIEW2_RELEASE_CHANNEL_PREFERENCE", "WEBVIEW2_CHANNEL_SEARCH_KIND", "WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER", "WEBVIEW2_WAIT_FOR_SCRIPT_DEBUGGER",
        "WEBVIEW2_", "WEBVIEW2_FUTURE_UNKNOWN_SWITCH", "WEBVIEW2_X" })
        Assert(Find(("PATH", "x"), (name, "--remote-debugging-port=9222")) == name, "refused: " + name);
});

Check("WEBVIEW2_ matching is case-insensitive", () =>
{
    foreach (string name in new[] { "webview2_additional_browser_arguments", "WebView2_User_Data_Folder", "Webview2_x", "wEbViEw2_Release_Channel_Preference" })
        Assert(Find((name, "1")) == name, "refused: " + name);
});

Check("empty and null values are treated as unset, whitespace and zero are not", () =>
{
    Assert(Find(("WEBVIEW2_USER_DATA_FOLDER", ""), ("webview2_x", null)) is null, "empty/null values");
    Assert(Find(("WEBVIEW2_USER_DATA_FOLDER", " ")) == "WEBVIEW2_USER_DATA_FOLDER", "whitespace value is an override");
    Assert(Find(("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "0")) is not null, "'0' is an override");
    Assert(Find(("WEBVIEW2_X", "false")) is not null, "'false' is an override");
});

Check("one offending variable among many empty ones is still found", () =>
{
    Assert(Find(("WEBVIEW2_A", ""), ("WEBVIEW2_B", null), ("PATH", "x"), ("WebView2_C", "--no-sandbox"), ("WEBVIEW2_D", "")) == "WebView2_C", "finds the nonempty one");
});

Check("the real process environment feeds the same policy without throwing", () =>
{
    var real = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
        .Select(e => new KeyValuePair<string, string?>((string)e.Key, e.Value as string));
    string? found = PreviewPolicy.FindWebView2Override(real);
    bool expected = real.Any(e => e.Key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(e.Value));
    Assert((found is not null) == expected, "consistent with an independent scan");
});

// ---- Bridge v2: renderer-to-host vault requests ----
const string P = "0b6f3c1e-8d24-4a97-9c51-7e2a40d8b6f1";
const string F = "3d9a5e72-c6b1-4f08-a2d4-91b7c3e60f58";
string Req(string op, string args, string? id = null, string type = "sourcevault.vault.request") =>
    $"{{\"type\":\"{type}\",\"requestId\":\"{id ?? Guid1}\",\"operation\":\"{op}\",\"args\":{args}}}";
bool Vault(string? json) => BridgePolicy.TryParseVaultRequest(json, out _);
VaultBridgeRequest Parsed(string json) => BridgePolicy.TryParseVaultRequest(json, out var r) ? r : throw new Exception("rejected: " + Show(json));

Check("bridge accepts every operation with its exact arguments", () =>
{
    Assert(Parsed(Req("listProjects", "{}")).Operation == VaultOperation.ListProjects, "listProjects");
    Assert(Parsed(Req("createProject", "{\"name\":\"Demo\"}")).Name == "Demo", "createProject");
    var delete = Parsed(Req("deleteProject", $"{{\"projectId\":\"{P}\",\"name\":\"Demo\"}}"));
    Assert(delete.Operation == VaultOperation.DeleteProject && delete.ProjectId == Guid.Parse(P) && delete.Name == "Demo", "deleteProject");
    Assert(Parsed(Req("renameProject", $"{{\"projectId\":\"{P}\",\"newName\":\"Demo 2\"}}")).NewName == "Demo 2", "renameProject");
    var renameFile = Parsed(Req("renameFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"newName\":\"lib/a.ts\"}}"));
    Assert(renameFile.Operation == VaultOperation.RenameFile && renameFile.FileId == Guid.Parse(F) && renameFile.NewName == "lib/a.ts", "renameFile");
    var renameFolder = Parsed(Req("renameFolder", $"{{\"projectId\":\"{P}\",\"name\":\"src\",\"newName\":\"lib\"}}"));
    Assert(renameFolder.Operation == VaultOperation.RenameFolder && renameFolder.Name == "src" && renameFolder.NewName == "lib", "renameFolder");
    Assert(Parsed(Req("deleteFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}")).Operation == VaultOperation.DeleteFile, "deleteFile");
    Assert(Parsed(Req("listHistory", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"page\":3}}")).Page == 3, "listHistory");
    Assert(Parsed(Req("openRevision", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"revisionId\":\"{P}\"}}")).RevisionId == Guid.Parse(P), "openRevision");
    Assert(Parsed(Req("listDeleted", $"{{\"projectId\":\"{P}\"}}")).Operation == VaultOperation.ListDeleted, "listDeleted");
    Assert(Parsed(Req("restoreFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"newName\":\"a.ts\"}}")).NewName == "a.ts", "restoreFile");
    Assert(Parsed(Req("mergeSources", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}")).Operation == VaultOperation.MergeSources, "mergeSources");
    var merged = Parsed(Req("saveMerged", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":\"x\",\"revisionId\":\"{F}\"}}"));
    Assert(merged.Operation == VaultOperation.SaveMerged && merged.Text == "x" && merged.RevisionId == Guid.Parse(F), "saveMerged");
    Assert(Parsed(Req("listBuildTargets", "{}")).Operation == VaultOperation.ListBuildTargets, "listBuildTargets");
    var build = Parsed(Req("requestBuild", $"{{\"projectId\":\"{P}\",\"name\":\"linux\"}}"));
    Assert(build.Operation == VaultOperation.RequestBuild && build.Name == "linux", "requestBuild");
    Assert(Parsed(Req("listBuilds", $"{{\"projectId\":\"{P}\"}}")).Operation == VaultOperation.ListBuilds, "listBuilds");
    Assert(Parsed(Req("buildStatus", $"{{\"projectId\":\"{P}\",\"buildNumber\":12}}")).BuildNumber == 12, "buildStatus");
    Assert(Parsed(Req("openBuildLog", $"{{\"projectId\":\"{P}\",\"buildNumber\":1}}")).Operation == VaultOperation.OpenBuildLog, "openBuildLog");
    Assert(Parsed(Req("listFiles", $"{{\"projectId\":\"{P}\"}}")).ProjectId == Guid.Parse(P), "listFiles");
    var open = Parsed(Req("openDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"));
    Assert(open.Operation == VaultOperation.OpenDocument && open.FileId == Guid.Parse(F), "openDocument");
    Assert(Parsed(Req("closeDocument", $"{{\"fileId\":\"{F}\",\"projectId\":\"{P}\"}}")).Operation == VaultOperation.CloseDocument, "field order is free");
    var save = Parsed(Req("saveDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":\"line\\n\\\"quoted\\\" \\u00e9 \\ud834\\udd1e\"}}"));
    Assert(save.Text == "line\n\"quoted\" é 𝄞", "escaped text and a surrogate pair decode");
    Assert(Parsed(Req("createFile", $"{{\"projectId\":\"{P}\",\"name\":\"a.ts\",\"text\":\"\"}}")).Text == "", "empty text");
    Assert(Parsed(Req("lock", "{}")).Operation == VaultOperation.Lock && Parsed(Req("unlock", "{}")).Operation == VaultOperation.Unlock, "lock/unlock");
});

Check("bridge rejects wrong envelopes, unknown operations and duplicate keys", () => Rejects(Vault,
    null, "", "[]", "\"x\"", "{}", Req("listProjects", "{}", type: "sourcevault.preview.status"),
    Req("ListProjects", "{}"), Req("deleteFile", "{}"), Req("renameProject", "{}"), Req("openDocument ", "{}"), Req("listProjects", "[]"), Req("listProjects", "null"),
    Req("listProjects", "{}").Replace("\"args\":{}", "\"args\":{},\"extra\":1"),
    Req("listProjects", "{}").Replace("\"type\":", "\"type\":\"sourcevault.vault.request\",\"type\":"),
    Req("listProjects", "{}").Replace("{\"type\"", "{\"capability\":\"x\",\"type\""),
    "{\"type\":\"sourcevault.vault.request\",\"requestId\":\"" + Guid1 + "\",\"operation\":\"listProjects\"}",
    Req("listProjects", "{}") + "x", Req("listProjects", "{}").Replace("}", ",}")));

Check("bridge rejects missing, extra, duplicate and mistyped arguments", () => Rejects(Vault,
    Req("listProjects", "{\"projectId\":\"" + P + "\"}"), Req("createProject", "{}"), Req("createProject", "{\"name\":7}"),
    Req("listFiles", "{}"), Req("listFiles", $"{{\"projectId\":\"{P}\",\"projectId\":\"{P}\"}}"),
    Req("openDocument", $"{{\"projectId\":\"{P}\"}}"), Req("openDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":\"x\"}}"),
    Req("saveDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"), Req("saveDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":null}}"),
    Req("saveDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":[\"x\"]}}"),
    Req("saveDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":\"x\",\"expectedSnapshotId\":\"{P}\"}}"),
    Req("lock", "{\"reason\":\"x\"}"),
    Req("deleteProject", "{}"), Req("deleteProject", $"{{\"projectId\":\"{P}\"}}"), Req("deleteProject", "{\"name\":\"Demo\"}"),
    Req("deleteProject", $"{{\"projectId\":\"{P}\",\"name\":\"\"}}"), Req("deleteProject", $"{{\"projectId\":\"{P}\",\"confirmName\":\"Demo\"}}"),
    Req("renameProject", $"{{\"projectId\":\"{P}\",\"name\":\"Demo\"}}"), Req("renameProject", $"{{\"projectId\":\"{P}\",\"newName\":7}}"),
    Req("renameFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"), Req("renameFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"newName\":\"\"}}"),
    Req("renameFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"newName\":\"" + new string('n', BridgePolicy.MaxNameChars + 1) + "\"}}"),
    Req("renameFolder", $"{{\"projectId\":\"{P}\",\"newName\":\"lib\"}}"), Req("renameFolder", $"{{\"projectId\":\"{P}\",\"name\":\"src\",\"newName\":\"tab\\tname\"}}"),
    Req("deleteFile", $"{{\"projectId\":\"{P}\"}}"), Req("deleteFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"expectedSnapshotId\":\"{P}\"}}"),
    Req("listHistory", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"), Req("listHistory", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"page\":-1}}"),
    Req("listHistory", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"page\":1.5}}"), Req("listHistory", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"page\":\"0\"}}"),
    Req("listHistory", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"page\":10001}}"),
    Req("openRevision", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"), Req("openRevision", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"revisionId\":\"x\"}}"),
    Req("saveMerged", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":\"x\"}}"), Req("restoreFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"),
    Req("listBuildTargets", $"{{\"projectId\":\"{P}\"}}"), Req("requestBuild", $"{{\"projectId\":\"{P}\"}}"), Req("requestBuild", $"{{\"projectId\":\"{P}\",\"target\":\"linux\"}}"),
    Req("buildStatus", $"{{\"projectId\":\"{P}\",\"buildNumber\":0}}"), Req("buildStatus", $"{{\"projectId\":\"{P}\",\"buildNumber\":\"1\"}}"),
    Req("openBuildLog", $"{{\"projectId\":\"{P}\"}}"), Req("openBuildLog", $"{{\"projectId\":\"{P}\",\"buildNumber\":-3}}")));

Check("bridge rejects non-canonical, empty and malformed ids", () => Rejects(Vault,
    Req("listProjects", "{}", id: Guid1.ToUpperInvariant()), Req("listProjects", "{}", id: "{" + Guid1 + "}"),
    Req("listProjects", "{}", id: "00000000-0000-0000-0000-000000000000"), Req("listProjects", "{}", id: Guid1.Replace("-", "")),
    Req("listFiles", "{\"projectId\":\"" + P.ToUpperInvariant() + "\"}"), Req("listFiles", "{\"projectId\":\" " + P + "\"}"),
    Req("listFiles", "{\"projectId\":123}"), Req("openDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"not-a-guid\"}}")));

Check("bridge enforces the text limit in UTF-8 bytes and rejects lone surrogates", () =>
{
    string Save(string escapedText) => Req("saveDocument", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"text\":\"{escapedText}\"}}");
    Assert(BridgePolicy.MaxTextBytes == 1024 * 1024 && BridgePolicy.MaxMessageChars >= 6 * BridgePolicy.MaxTextBytes + 4096, "1 MiB of text, room for JSON escaping");
    Assert(Vault(Save(new string('a', BridgePolicy.MaxTextBytes))), "exactly 1 MiB accepted");
    Rejects(Vault, Save(new string('a', BridgePolicy.MaxTextBytes + 1)), Save(new string('é', BridgePolicy.MaxTextBytes / 2 + 1)),
        Save("\\ud800"), Save("x\\udc00y"), Save("\\ud834"));
    Assert(Vault(Save(string.Concat(Enumerable.Repeat("\\n", 100000)))), "escaped content within limits");
    Assert(!Vault(Req("listProjects", "{}") + new string(' ', BridgePolicy.MaxMessageChars)), "oversized message rejected before parsing");
});

Check("bridge rejects bad names and excessive nesting", () => Rejects(Vault,
    Req("createProject", "{\"name\":\"\"}"), Req("createProject", "{\"name\":\"" + new string('n', BridgePolicy.MaxNameChars + 1) + "\"}"),
    Req("createProject", "{\"name\":\"tab\\tname\"}"), Req("createProject", "{\"name\":\"\\ud800\"}"),
    Req("createFile", $"{{\"projectId\":\"{P}\",\"name\":\"a\\u0000b\",\"text\":\"x\"}}"),
    Req("createProject", "{\"name\":{\"a\":{\"b\":{\"c\":1}}}}")));

Check("host messages are well-formed JSON and carry text losslessly", () =>
{
    var text = "</script>\"quote\" \\ back\nline é 𝄞";
    var response = BridgePolicy.BuildResponse(Guid.Parse(Guid1), "Ok", w => { w.WriteString("fileId", F); w.WriteString("text", text); });
    using var doc = System.Text.Json.JsonDocument.Parse(response);
    Assert(doc.RootElement.GetProperty("type").GetString() == "sourcevault.vault.response" && doc.RootElement.GetProperty("requestId").GetString() == Guid1, "envelope");
    Assert(doc.RootElement.GetProperty("result").GetProperty("text").GetString() == text, "text round-trips");
    Assert(!response.Contains("</script>"), "HTML-significant characters are escaped");
    using var error = System.Text.Json.JsonDocument.Parse(BridgePolicy.BuildResponse(Guid.Parse(Guid1), "Conflict", null));
    Assert(error.RootElement.GetProperty("result").ValueKind == System.Text.Json.JsonValueKind.Null, "errors carry no result");
    Assert(BridgePolicy.BuildLocked("windows").Contains("\"reason\":\"windows\""), "locked event");
    try { BridgePolicy.BuildLocked("anything"); throw new Exception("accepted unknown reason"); } catch (ArgumentException) { }
});

// ---- Every shipped asset is reachable ----
// The native editor maps https://sourcevault.local/ to the shipped copy of SourceVault.Editor.Web (without dev/).
// A policy tightening that rejected any of these files would silently break a Monaco worker, language or font.
Check("every shipped web asset, including all of vendor/monaco, is allowed at its app-origin URL", () =>
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SourceVault.sln"))) dir = dir.Parent;
    Assert(dir is not null, "repository root not found above the test binary");
    var web = Path.Combine(dir!.FullName, "src", "SourceVault.Editor.Web");
    var shipped = Directory.EnumerateFiles(web, "*", SearchOption.AllDirectories)
        .Select(f => Path.GetRelativePath(web, f).Replace('\\', '/'))
        .Where(r => !r.StartsWith("dev/", StringComparison.Ordinal)).ToList();
    Assert(shipped.Count(r => r.StartsWith("vendor/monaco/", StringComparison.Ordinal)) > 100, "vendored Monaco found");
    Assert(shipped.Contains("index.html") && shipped.Contains("vendor/monaco/vs/editor/editor.main.js"), "entry points present");
    foreach (var relative in shipped)
    {
        var url = App + "/" + string.Join('/', relative.Split('/').Select(Uri.EscapeDataString));
        Assert(PreviewPolicy.IsResourceAllowed(url), "asset refused: " + relative);
    }
});

// ---- Only the bundled app can be navigated to ----
Check("top-level navigation allows only the exact entry document", () =>
{
    Accepts(PreviewPolicy.IsNavigationAllowed, App + "/index.html", "HTTPS://SOURCEVAULT.LOCAL/index.html", App + ":443/index.html");
    Rejects(PreviewPolicy.IsNavigationAllowed,
        App, App + "/", App + "/index.html?fake-vault", App + "/index.html?", App + "/index.html#", App + "/index.html#top",
        App + "/INDEX.HTML", App + "/Index.html", App + "/index.htm", App + "/index.html/", App + "/index.html/x", App + "//index.html",
        App + "/index%2ehtml", App + "/./index.html", App + "/vendor/../index.html", App + "/vendor/monaco/vs/loader.js",
        App + "/vendor/monaco/LICENSE", App + "/README.md", App + "/app.mjs", App + "/demo-host.mjs", App + "/vendor/monaco/vs/editor/editor.main.js");
});

Check("external URI schemes and browser-internal pages are never navigable", () => Rejects(PreviewPolicy.IsNavigationAllowed,
    "mailto:someone@example.com", "mailto:?body=secret", "tel:+15550100", "sms:+15550100", "ms-settings:", "ms-settings:privacy-webcam",
    "ms-windows-store://home", "ms-word:ofe|u|https://evil.example/a.docx", "search-ms:query=secret", "ms-msdt:/id", "microsoft-edge:https://evil.example/",
    "microsoft-edge://settings", "edge://settings", "edge://inspect", "chrome://settings", "chrome-extension://abc/x.html", "devtools://devtools/bundled/inspector.html",
    "view-source:" + App + "/index.html", "about:blank", "about:srcdoc", "about:config", "data:text/html,<script>alert(1)</script>",
    "data:text/html;base64,PHNjcmlwdD4=", "javascript:alert(1)", "vbscript:msgbox(1)", "file:///C:/Windows/win.ini", "file://evil.example/share/x",
    "\\\\evil.example\\share\\x", "//evil.example/", "intent://x#Intent;scheme=https;end", "ftp://example.com/", "ws://sourcevault.local/", "wss://sourcevault.local/",
    "blob:" + App + "/" + Guid1, "filesystem:" + App + "/temporary/x", "sourcevault.local:443/index.html", "web+sourcevault:x", "callto:x", "shell:startup"));

Check("network addresses in every spelling are refused for navigation and resources", () =>
{
    string[] addresses =
    [
        "https://example.com/", "http://example.com/", "https://127.0.0.1/", "http://127.0.0.1:4293/index.html", "https://localhost/", "http://localhost:1/",
        "https://[::1]/", "https://[::ffff:127.0.0.1]/", "https://0.0.0.0/", "https://2130706433/", "https://0x7f000001/", "https://0177.0.0.1/",
        "https://127.1/", "https://10.0.0.1/", "https://192.168.1.1/", "https://169.254.169.254/latest/meta-data", "https://[fe80::1%25eth0]/",
        "https://sourcevault.local.example.com/", "https://sourcevault.localhost/", "https://sourcevault/", "http://sourcevault.local/index.html",
        "https://sourcevault.local:1/", "https://sourcevault.local:9222/json", "https://cdn.jsdelivr.net/npm/monaco-editor@0.57.0/min/vs/loader.js",
        "https://unpkg.com/monaco-editor/min/vs/loader.js", "ws://127.0.0.1:9222/devtools/browser", "wss://example.com/socket", "stun:stun.example.com:3478",
        "turn:turn.example.com", "https://login.microsoftonline.com/", "https://smartscreen.microsoft.com/",
    ];
    Rejects(PreviewPolicy.IsNavigationAllowed, addresses);
    Rejects(PreviewPolicy.IsResourceAllowed, addresses);
    Rejects(PreviewPolicy.IsAppUri, addresses);
});

// ---- Browser launch arguments (no network underneath the request filters) ----
// Windows command-line splitting as CommandLineToArgvW does it for these simple cases: spaces separate, quotes group.
List<string> SplitArgs(string line)
{
    var args = new List<string>();
    var current = new StringBuilder();
    bool quoted = false, any = false;
    foreach (char c in line)
    {
        if (c == '"') { quoted = !quoted; any = true; }
        else if (c == ' ' && !quoted) { if (any) args.Add(current.ToString()); current.Clear(); any = false; }
        else { current.Append(c); any = true; }
    }
    Assert(!quoted, "unbalanced quote in browser arguments");
    if (any) args.Add(current.ToString());
    return args;
}
Dictionary<string, string?> Switches(string line)
{
    var map = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (string arg in SplitArgs(line))
    {
        Assert(arg.StartsWith("--", StringComparison.Ordinal), "not a switch: " + arg);
        int eq = arg.IndexOf('=');
        string name = eq < 0 ? arg[2..] : arg[2..eq];
        Assert(map.TryAdd(name, eq < 0 ? null : arg[(eq + 1)..]), "switch given twice (the last copy would win): " + name);
    }
    return map;
}

Check("browser arguments parse cleanly: only switches, each once, no control characters", () =>
{
    string line = NetworkPolicy.AdditionalBrowserArguments;
    Assert(line.All(c => c >= 0x20 && c < 0x7F), "printable ASCII only");
    Assert(SplitArgs(line).Count == NetworkPolicy.BrowserSwitches.Count, "each list entry is exactly one argument after Windows quoting");
    Switches(line);
});

Check("every connection goes to a dead loopback proxy with no bypass and no direct fallback", () =>
{
    var s = Switches(NetworkPolicy.AdditionalBrowserArguments);
    Assert(s.TryGetValue("proxy-server", out var proxy) && proxy is not null, "proxy-server present");
    Assert(!proxy!.Contains(';') && !proxy.Contains('=') && !proxy.Contains(',') && !proxy.Contains("direct", StringComparison.OrdinalIgnoreCase),
        "a single proxy for every scheme, no per-scheme list, no DIRECT fallback");
    Assert(Uri.TryCreate(proxy, UriKind.Absolute, out var uri) && uri.Scheme == "http" && uri.AbsolutePath == "/" && uri.UserInfo.Length == 0, "plain http proxy URI");
    Assert(System.Net.IPAddress.TryParse(uri!.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip), "proxy is a loopback IP literal, so reaching it needs no DNS");
    Assert(!uri.IsDefaultPort && uri.Port is > 0 and < 1024, "explicit nonzero privileged port");
    Assert(s.TryGetValue("proxy-bypass-list", out var bypass) && bypass == "<-loopback>", "the only bypass entry removes the implicit loopback bypass");
    foreach (string loosening in new[] { "no-proxy-server", "proxy-auto-detect", "proxy-pac-url", "winhttp-proxy-resolver" })
        Assert(!s.ContainsKey(loosening), "must not override the fixed proxy: " + loosening);
});

Check("host names never resolve and WebRTC may not send unproxied UDP", () =>
{
    var s = Switches(NetworkPolicy.AdditionalBrowserArguments);
    Assert(s.TryGetValue("host-resolver-rules", out var rules) && rules == "MAP * ~NOTFOUND", "every host maps to NOTFOUND, with no EXCLUDE");
    foreach (string spelling in new[] { "webrtc-ip-handling-policy", "force-webrtc-ip-handling-policy" })
        Assert(s.TryGetValue(spelling, out var webrtc) && webrtc == "disable_non_proxied_udp", "WebRTC limited to the dead proxy: " + spelling);
    foreach (string background in new[] { "disable-background-networking", "disable-component-update", "disable-domain-reliability", "no-pings" })
        Assert(s.TryGetValue(background, out var value) && value is null, "background service switch: " + background);
});

Check("browser arguments contain no debugging, sandbox, security or profile overrides", () =>
{
    var s = Switches(NetworkPolicy.AdditionalBrowserArguments);
    foreach (string name in s.Keys)
        Assert(!name.Contains("debug", StringComparison.OrdinalIgnoreCase) && !name.Contains("devtools", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("sandbox", StringComparison.OrdinalIgnoreCase) && !name.Contains("insecure", StringComparison.OrdinalIgnoreCase) &&
            !name.Contains("certificate", StringComparison.OrdinalIgnoreCase) && !name.Contains("extension", StringComparison.OrdinalIgnoreCase),
            "forbidden switch family: " + name);
    foreach (string forbidden in new[] { "disable-web-security", "user-data-dir", "remote-allow-origins", "disable-site-isolation-trials", "enable-features",
        "allow-file-access-from-files", "unsafely-treat-insecure-origin-as-secure", "edge-webview-switches", "single-process", "in-process-gpu" })
        Assert(!s.ContainsKey(forbidden), "forbidden switch: " + forbidden);
});

Check("the document-created script removes every WebRTC constructor with the renderer guard's exact descriptor", () =>
{
    string script = NetworkPolicy.DocumentCreatedScript;
    foreach (string name in new[] { "RTCPeerConnection", "webkitRTCPeerConnection", "RTCDataChannel" })
        Assert(NetworkPolicy.WebRtcGlobals.Contains(name), "covered: " + name);
    Assert(script.Contains("{ value: undefined, writable: false, configurable: false }", StringComparison.Ordinal), "non-configurable, non-writable, undefined");
    Assert(NetworkPolicy.WebRtcGlobals.All(n => n.All(char.IsAsciiLetter)), "names are plain identifiers, nothing to escape");
    Assert(!script.Contains('\n') && !script.Contains("eval", StringComparison.Ordinal) && !script.Contains("postMessage", StringComparison.Ordinal), "a fixed, single-statement script");
    // shell.mjs redefines the same globals later; that only succeeds if it uses the identical descriptor and covers them all.
    string shell = File.ReadAllText(Path.Combine(RepoRoot(), "src", "SourceVault.Editor.Web", "shell.mjs"));
    var list = System.Text.RegularExpressions.Regex.Match(shell, @"blockedNetworkGlobals = \[([^\]]*)\]");
    Assert(list.Success, "blockedNetworkGlobals found in shell.mjs");
    foreach (string name in NetworkPolicy.WebRtcGlobals) Assert(list.Groups[1].Value.Contains("'" + name + "'", StringComparison.Ordinal), "renderer guard also lists " + name);
    Assert(shell.Contains("{ value, writable: false, configurable: false }", StringComparison.Ordinal), "renderer guard uses the same descriptor attributes");
});

// ---- Registry policy overrides ----
string? Reg(string exe, params (string Setting, string ValueName)[] values) => NetworkPolicy.FindRegistryOverride(values, exe);
const string Exe = "SourceVault.Editor.exe";

Check("no WebView2 policy values means no override", () =>
{
    Assert(Reg(Exe) is null, "empty");
    Assert(Reg(Exe, ("AdditionalBrowserArguments", "other.exe"), ("BrowserExecutableFolder", "msedge.exe"), ("UserDataFolder", "OTHER.EXE"),
        ("ReleaseChannelPreference", "Teams.exe")) is null, "values for other executables never apply to this process");
});

Check("values that the loader would apply to this editor are refused", () =>
{
    foreach (string setting in new[] { "AdditionalBrowserArguments", "BrowserExecutableFolder", "UserDataFolder", "ReleaseChannelPreference", "ChannelSearchKind", "SomeFutureSetting" })
        foreach (string name in new[] { Exe, "sourcevault.editor.exe", "SOURCEVAULT.EDITOR.EXE", "*" })
        {
            string? found = Reg(Exe, ("AdditionalBrowserArguments", "other.exe"), (setting, name));
            Assert(found is not null && found.StartsWith(setting + "\\", StringComparison.Ordinal), $"refused: {setting}\\{name}");
        }
});

Check("value names that might be an AppUserModelID, or are near misses, fail closed", () =>
{
    foreach (string name in new[] { "SourceVault.Editor", "Contoso.SourceVault_8wekyb3d8bbwe!App", "SourceVault.Editor.exe ", "SourceVault.Editor.exe.bak",
        "SourceVault.Editor.ex", " *", "**", "", "SourceVault.Editor.exe\0" })
        Assert(Reg(Exe, ("AdditionalBrowserArguments", name)) is not null, "refused: [" + name + "]");
    Assert(Reg(Exe, ("AdditionalBrowserArguments", "")) == "AdditionalBrowserArguments\\(Default)", "default value is named readably");
});

Check("missing executable name or unreadable entries fail closed", () =>
{
    Assert(Reg("", ("AdditionalBrowserArguments", "other.exe")) is not null, "no executable name: nothing can be ruled out");
    Assert(NetworkPolicy.FindRegistryOverride([(null!, "x")], Exe) is not null, "null setting");
    Assert(NetworkPolicy.FindRegistryOverride([("AdditionalBrowserArguments", null!)], Exe) is not null, "null value name");
});

// ---- Shipped renderer: CSP and first-party code cannot reach the network ----
string RepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SourceVault.sln"))) dir = dir.Parent;
    Assert(dir is not null, "repository root not found above the test binary");
    return dir!.FullName;
}
string WebRoot() => Path.Combine(RepoRoot(), "src", "SourceVault.Editor.Web");
List<string> Shipped() => Directory.EnumerateFiles(WebRoot(), "*", SearchOption.AllDirectories)
    .Select(f => Path.GetRelativePath(WebRoot(), f).Replace('\\', '/'))
    .Where(r => !r.StartsWith("dev/", StringComparison.Ordinal)).ToList();

Check("index.html is the only shipped HTML document", () =>
{
    var html = Shipped().Where(r => r.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".htm", StringComparison.OrdinalIgnoreCase) ||
        r.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || r.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)).ToList();
    Assert(html.SequenceEqual(["index.html"]), "shipped documents: " + string.Join(", ", html));
});

Check("index.html CSP allows only the app origin, before any resource loads", () =>
{
    string page = File.ReadAllText(Path.Combine(WebRoot(), "index.html"));
    var metas = System.Text.RegularExpressions.Regex.Matches(page, "<meta[^>]*http-equiv=\"Content-Security-Policy\"[^>]*content=\"([^\"]*)\"[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    Assert(metas.Count == 1, "exactly one CSP meta element");
    int firstResource = new[] { page.IndexOf("<link", StringComparison.OrdinalIgnoreCase), page.IndexOf("<script", StringComparison.OrdinalIgnoreCase) }.Where(i => i >= 0).Min();
    Assert(metas[0].Index < firstResource, "CSP meta precedes every link and script (a meta CSP only governs what follows it)");
    var policy = new Dictionary<string, string[]>(StringComparer.Ordinal);
    foreach (string directive in metas[0].Groups[1].Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        string[] parts = directive.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Assert(policy.TryAdd(parts[0], parts[1..]), "duplicate directive: " + parts[0]);
    }
    // Every directive and source is allowlisted; hosts, schemes, wildcards and eval fail this test by design.
    var allowed = new Dictionary<string, string[]>
    {
        ["default-src"] = ["'none'"], ["script-src"] = ["'self'"], ["style-src"] = ["'self'", "'unsafe-inline'"], ["font-src"] = ["'self'", "data:"],
        ["img-src"] = ["'self'", "data:"], ["worker-src"] = ["'self'", "blob:"], ["connect-src"] = ["'self'"], ["base-uri"] = ["'none'"],
        ["form-action"] = ["'none'"], ["object-src"] = ["'none'"],
    };
    foreach (var (name, sources) in policy)
    {
        Assert(allowed.TryGetValue(name, out var permitted), "unexpected directive: " + name);
        foreach (string source in sources) Assert(permitted!.Contains(source), $"{name} source not allowed: {source}");
    }
    foreach (string required in new[] { "default-src", "connect-src", "form-action", "base-uri", "object-src" })
        Assert(policy.ContainsKey(required), "missing directive: " + required);
    Assert(policy["default-src"].SequenceEqual(["'none'"]) && policy["connect-src"].SequenceEqual(["'self'"]), "default none, connections same-origin only");
});

Check("index.html has no external links, hints, frames, forms, base or refresh", () =>
{
    string page = File.ReadAllText(Path.Combine(WebRoot(), "index.html"));
    foreach (string pattern in new[] { @"[a-z][a-z0-9+.\-]*://", @"\brel\s*=\s*""?[^"">]*(prefetch|preconnect|dns-prefetch|prerender|preload|modulepreload|manifest|icon)",
        "<iframe", "<frame", "<object", "<embed", "<base", @"http-equiv\s*=\s*""?refresh", @"\baction\s*=", @"\bping\s*=", @"\bsrcdoc\s*=", @"\bhref\s*=\s*""(?!app\.css"")",
        @"\bsrc\s*=\s*""(?!vendor/monaco/vs/loader\.js""|app\.mjs"")" })
        Assert(!System.Text.RegularExpressions.Regex.IsMatch(page, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase), "index.html matches " + pattern);
});

Check("first-party renderer code has no network calls, URL sinks or external URLs", () =>
{
    var firstParty = Shipped().Where(r => !r.StartsWith("vendor/", StringComparison.Ordinal) &&
        (r.EndsWith(".mjs", StringComparison.Ordinal) || r.EndsWith(".js", StringComparison.Ordinal) || r.EndsWith(".css", StringComparison.Ordinal))).ToList();
    Assert(firstParty.Contains("app.mjs") && firstParty.Contains("shell.mjs") && firstParty.Contains("app.css"), "first-party files found");
    string[] banned =
    [
        @"[a-z][a-z0-9+.\-]*://(?!/)", // a URL with an authority (file:/// model ids have none)
        @"\bfetch\s*\(", @"XMLHttpRequest", @"new\s+(WebSocket|WebSocketStream|EventSource|WebTransport|RTCPeerConnection|webkitRTCPeerConnection|SharedWorker|Worker)\b",
        @"sendBeacon\s*\(", @"\b(window|globalThis|self|top|parent|opener)\.open\s*\(", @"navigator\.serviceWorker", @"importScripts", @"\bimport\s*\(\s*(?!'\./)",
        @"\blocation\s*(\.\s*(href|assign|replace|reload)\b|=[^=])", @"\.(src|href|action|formAction|srcdoc)\s*=[^=]", @"setAttribute\(\s*'(src|href|action|formaction|srcdoc|ping)'",
        @"innerHTML|outerHTML|insertAdjacentHTML|document\.write|createContextualFragment|DOMParser",
        @"createElement\(\s*'(script|iframe|frame|a|link|img|image|object|embed|base|meta|audio|video|source|track|portal)'\s*\)",
        @"\burl\s*\(", @"@import",
    ];
    foreach (string relative in firstParty)
    {
        string text = File.ReadAllText(Path.Combine(WebRoot(), relative));
        foreach (string pattern in banned)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, pattern);
            Assert(!match.Success, $"{relative}: matches {pattern} at offset {match.Index}");
        }
    }
});

Check("app.mjs removes network APIs before it does anything else", () =>
{
    string app = File.ReadAllText(Path.Combine(WebRoot(), "app.mjs"));
    int guard = app.IndexOf("\ninstallNetworkBlocks();", StringComparison.Ordinal);
    Assert(guard > 0, "installNetworkBlocks() is called at top level");
    foreach (string later in new[] { "\ninstallExportBlocks();", "await detectMode()", "await import(" })
        Assert(app.IndexOf(later, StringComparison.Ordinal) > guard, "network guard runs before: " + later.Trim());
});

// ---- Search all files and display settings (editor comfort features, 2026-10-19) ----
const string GoodSettings = "{\"theme\":\"light\",\"fontSize\":15,\"wordWrap\":true,\"minimap\":false,\"tabSize\":4,\"insertSpaces\":true,\"renderWhitespace\":false,\"lineNumbers\":true}";

Check("bridge: searchProject takes one line of plain text and two flags", () =>
{
    var search = Parsed(Req("searchProject", $"{{\"projectId\":\"{P}\",\"query\":\"Widget\",\"matchCase\":true,\"wholeWord\":false}}"));
    Assert(search.Operation == VaultOperation.SearchProject && search.Query == "Widget" && search.MatchCase && !search.WholeWord, "parsed");
    foreach (var args in new[]
    {
        $"{{\"projectId\":\"{P}\",\"query\":\"\",\"matchCase\":false,\"wholeWord\":false}}",                            // empty
        $"{{\"projectId\":\"{P}\",\"query\":\"{new string('q', 201)}\",\"matchCase\":false,\"wholeWord\":false}}",      // too long
        $"{{\"projectId\":\"{P}\",\"query\":\"a\\nb\",\"matchCase\":false,\"wholeWord\":false}}",                      // two lines
        $"{{\"projectId\":\"{P}\",\"query\":\"a\\u0000\",\"matchCase\":false,\"wholeWord\":false}}",                   // control character
        $"{{\"projectId\":\"{P}\",\"query\":\"a\",\"matchCase\":\"true\",\"wholeWord\":false}}",                       // flag as text
        $"{{\"projectId\":\"{P}\",\"query\":\"a\",\"matchCase\":false}}",                                              // missing flag
        $"{{\"projectId\":\"{P}\",\"query\":\"a\",\"matchCase\":false,\"wholeWord\":false,\"regex\":true}}",           // unknown field
        $"{{\"projectId\":\"not-an-id\",\"query\":\"a\",\"matchCase\":false,\"wholeWord\":false}}",
    })
        Rejects(Vault, Req("searchProject", args));
});

// ---- Any-file vault (docs/vault-any-file-design.md section 4) ----
Check("bridge: info panel, from disk, keep mine/theirs and revert carry ids only, never a path or content", () =>
{
    foreach (var (op, kind) in new[] { ("fileInfo", VaultOperation.FileInfo), ("replaceFromDisk", VaultOperation.ReplaceFromDisk), ("keepMine", VaultOperation.KeepMine), ("keepTheirs", VaultOperation.KeepTheirs) })
    {
        var parsed = Parsed(Req(op, $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"));
        Assert(parsed.Operation == kind && parsed.FileId == Guid.Parse(F) && parsed.Text is null && parsed.Name is null, op);
        Rejects(Vault, Req(op, $"{{\"projectId\":\"{P}\"}}"), Req(op, $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"path\":\"C:\\\\x\"}}"),
            Req(op, $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"contentBase64\":\"AA==\"}}"));
    }
    var revert = Parsed(Req("revertFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\",\"revisionId\":\"{Guid1}\"}}"));
    Assert(revert.Operation == VaultOperation.RevertFile && revert.RevisionId == Guid.Parse(Guid1), "revert");
    Rejects(Vault, Req("revertFile", $"{{\"projectId\":\"{P}\",\"fileId\":\"{F}\"}}"));
    Assert(Parsed(Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"\"}}")).Folder == "", "the project root");
    Assert(Parsed(Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"res/icons\"}}")).Folder == "res/icons", "a folder");
    Rejects(Vault, Req("addFromDisk", $"{{\"projectId\":\"{P}\"}}"), Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"/res\"}}"),
        Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"res/\"}}"), Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":null}}"),
        Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"a\\u0000b\"}}"), Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"{new string('f', BridgePolicy.MaxNameChars + 1)}\"}}"),
        Req("addFromDisk", $"{{\"projectId\":\"{P}\",\"folder\":\"res\",\"files\":[\"C:\\\\a.png\"]}}"));
});

Check("bridge: settings carry exactly eight bounded display preferences", () =>
{
    Assert(Parsed(Req("getSettings", "{}")).Operation == VaultOperation.GetSettings, "getSettings");
    var saved = Parsed(Req("saveSettings", $"{{\"settings\":{GoodSettings}}}")).Settings!;
    Assert(saved == new EditorSettings("light", 15, true, false, 4, true, false, true), "parsed");
    foreach (var (from, to) in new[]
    {
        ("\"light\"", "\"neon\""), ("15", "9"), ("15", "29"), ("15", "15.5"), ("\"tabSize\":4", "\"tabSize\":3"), ("true,\"minimap\"", "1,\"minimap\""),
        ("\"lineNumbers\":true", "\"lineNumbers\":true,\"recentFiles\":[\"src/a.ts\"]"), // nothing that could hold a name
        (",\"lineNumbers\":true", ""),
    })
        Rejects(Vault, Req("saveSettings", $"{{\"settings\":{GoodSettings.Replace(from, to)}}}"));
    Rejects(Vault, Req("saveSettings", "{\"settings\":\"dark\"}"), Req("getSettings", "{\"x\":1}"));
});

Check("settings file: round trip; a malformed, oversized or tampered file gives the defaults", () =>
{
    var settings = new EditorSettings("highContrast", 20, true, false, 8, false, true, false);
    Assert(EditorSettings.Parse(settings.ToJson()) == settings, "round trip");
    Assert(EditorSettings.Parse("not json") == EditorSettings.Default, "malformed");
    Assert(EditorSettings.Parse(GoodSettings.Replace("15", "99")) == EditorSettings.Default, "out of range");
    Assert(EditorSettings.Parse(GoodSettings + new string(' ', EditorSettings.MaxFileBytes)) == EditorSettings.Default, "oversized");
    var path = Path.Combine(Path.GetTempPath(), "sv-settings-" + Guid.NewGuid().ToString("N"), "editor-settings.json");
    try
    {
        Assert(EditorSettings.Load(path) == EditorSettings.Default, "missing file");
        settings.Save(path);
        Assert(EditorSettings.Load(path) == settings && !File.Exists(path + ".tmp"), "saved atomically and loaded");
    }
    finally { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); }
});

Console.WriteLine($"Editor host policy tests: {passed} passed; {failures.Count} failed.");
return failures.Count == 0 ? 0 : 1;
