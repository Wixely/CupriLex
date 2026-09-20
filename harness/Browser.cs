using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CupriLex.Harness;

/// <summary>What the browser produced for one block, and everything about the run that a
/// surprising score would want explained.</summary>
/// <param name="Frames">One frame per requested time, in ascending time order.</param>
/// <param name="TimelineIds">The timelines found on <c>window.__timelines</c>.</param>
/// <param name="TimelineSeconds">The longest of their durations. Worth comparing against the
/// block's declared duration: <c>bar-chart-race</c> declares 12 seconds and its timeline spans 10,
/// so the last sixth of the reference is a still frame and a score over it means less than it
/// looks like.</param>
public sealed record Reference(
    IReadOnlyList<Frame> Frames, IReadOnlyList<string> TimelineIds, double TimelineSeconds);

/// <summary>
/// The reference renderer: a real browser, seeked rather than played.
///
/// <para>Seeked, because a screenshot of a composition that is playing is a screenshot of whenever
/// the screenshot happened. Every block in the corpus registers its GSAP timeline on
/// <c>window.__timelines</c> - measured, 187 of 187, and 183 of them create it already paused - so
/// the reference can be asked for an exact time instead of being raced for one. The other four are
/// paused here before they are seeked.</para>
///
/// <para>Driven over the DevTools protocol directly. A browser automation library would be another
/// dependency to install and pin, and this needs four of its commands.</para>
/// </summary>
public sealed class Browser : IAsyncDisposable
{
    private readonly Process _process;
    private readonly string _profile;
    private readonly ClientWebSocket _socket;
    private int _id;

    private readonly int _port;

    private Browser(Process process, string profile, ClientWebSocket socket, int port, string version)
    {
        _process = process;
        _profile = profile;
        _socket = socket;
        _port = port;
        Version = version;
    }

    /// <summary>What the reference renderer actually is, as it reports itself. Recorded beside
    /// every score, because "closer to a browser" means nothing without saying which one.</summary>
    public string Version { get; }

    /// <summary>Where the browser is. <c>CUPRILEX_BROWSER</c> overrides, because a machine with
    /// neither of these installed should be told what to set rather than told "not found".</summary>
    public static string? Find()
    {
        if (Environment.GetEnvironmentVariable("CUPRILEX_BROWSER") is { Length: > 0 } chosen)
            return File.Exists(chosen) ? chosen : null;

        string?[] roots =
        [
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        ];

        string[] tails =
        [
            Path.Combine("Microsoft", "Edge", "Application", "msedge.exe"),
            Path.Combine("Google", "Chrome", "Application", "chrome.exe"),
        ];

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)))
        foreach (var tail in tails)
        {
            var candidate = Path.Combine(root!, tail);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var unix in new[]
                 { "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/microsoft-edge" })
            if (File.Exists(unix)) return unix;

        return null;
    }

    public static async Task<Browser> LaunchAsync(CancellationToken cancel = default)
    {
        var executable = Find() ?? throw new BrowserException(
            "No headless browser found. Install Microsoft Edge or Google Chrome, or point "
            + "CUPRILEX_BROWSER at the executable.");

        var profile = Path.Combine(Path.GetTempPath(),
            "cuprilex-harness-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(profile);

        var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            ArgumentList =
            {
                "--headless=new",
                "--disable-gpu",                 // software raster: one machine's GPU is not a reference
                "--hide-scrollbars",             // the engine draws none, and a scrollbar is 15px of pure diff
                "--disable-lcd-text",            // subpixel AA fringes text with colour the engine never draws
                "--force-device-scale-factor=1",
                "--disable-extensions",
                "--no-first-run",
                "--no-default-browser-check",
                "--mute-audio",
                "--remote-debugging-port=0",     // 0 and read it back: a fixed port collides with the last run
                "--user-data-dir=" + profile,
                "about:blank",
            },
        }) ?? throw new BrowserException("Could not start " + executable);

        try
        {
            var port = await PortAsync(profile, process, cancel);
            var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(await PageSocketAsync(port, cancel)), cancel);

            var browser = new Browser(process, profile, socket, port, await DescribeAsync(port, cancel));
            await browser.SendAsync("Page.enable", cancel: cancel);
            await browser.SendAsync("Page.addScriptToEvaluateOnNewDocument",
                new JsonObject { ["source"] = Bootstrap }, cancel);
            return browser;
        }
        catch
        {
            Kill(process);
            throw;
        }
    }

    /// <summary>Render one block at each time, in one page load.</summary>
    /// <remarks>One load for all of them, in ascending time order: a page load costs seconds, a
    /// seek costs milliseconds, and scrubbing forwards is what a player does.</remarks>
    public async Task<Reference> RenderAsync(
        Block block, IReadOnlyList<double> times, CancellationToken cancel = default)
    {
        await SendAsync("Emulation.setDeviceMetricsOverride", new JsonObject
        {
            ["width"] = block.Width,
            ["height"] = block.Height,
            ["deviceScaleFactor"] = 1,
            ["mobile"] = false,
        }, cancel);

        var url = new Uri(block.Path).AbsoluteUri;
        await SendAsync("Page.navigate", new JsonObject { ["url"] = url }, cancel);
        await ReadyAsync(url, cancel);

        var frames = new List<Frame>(times.Count);
        IReadOnlyList<string> ids = [];
        var span = 0.0;

        foreach (var t in times.OrderBy(t => t))
        {
            var report = await EvaluateJsonAsync(Seek(t), cancel);

            if (report["error"] is { } failure && failure.GetValueKind() != JsonValueKind.Null)
                throw new BrowserException($"seeking to {t.ToString("0.###", CultureInfo.InvariantCulture)}s failed: {failure}");

            if (report["timelines"] is JsonArray found)
            {
                ids = [.. found.Select(n => n?["id"]?.GetValue<string>() ?? "?")];
                span = found.Select(n => n?["duration"]?.GetValue<double>() ?? 0).DefaultIfEmpty(0).Max();
            }

            frames.Add(await ScreenshotAsync(cancel));
        }

        return new Reference(frames, ids, span);
    }

    // ---- the three scripts the whole reference rests on ---------------------------------------

    /// <summary>
    /// The host contract a HyperFrames block is written against, installed before the block's own
    /// script runs.
    ///
    /// <para><c>window.__timelines</c> is not a convention every block creates. 162 of the 187
    /// initialise it themselves; the other 25 - every <c>carousel-*</c> among them - write
    /// <c>window.__timelines[DATA.id] = tl</c> straight into an object they expect their host to
    /// have made. Without it that line throws, the timeline is never registered, and the harness
    /// waited thirty seconds per block to report "not ready" about a page whose only problem was a
    /// missing empty object.</para>
    ///
    /// <para><c>window.__hyperframes</c> is deliberately NOT stubbed. All 44 blocks that read it
    /// guard the access and fall back to their authored defaults, which is the same content the
    /// engine is given - stubbing it would make the two sides diverge on purpose.</para>
    ///
    /// <para>The error list is here because a block whose script throws looks exactly like a block
    /// that is merely slow, and thirty seconds later the harness would have had nothing to say
    /// about which.</para>
    /// </summary>
    private const string Bootstrap = """
        (function () {
          window.__timelines = window.__timelines || {};
          window.__harnessErrors = [];
          // Capture phase, and not for tidiness: a <script> or <img> that fails to load fires an
          // error event that does NOT bubble, so a listener without this argument sees nothing.
          // A block whose GSAP never arrived reported an empty error list and a thirty-second
          // timeout, when what had happened was one failed request with a name.
          window.addEventListener('error', function (e) {
            var target = e && e.target;
            var source = target && (target.src || target.href);
            window.__harnessErrors.push(source
              ? 'failed to load ' + source
              : String((e && (e.message || e.type)) || e));
          }, true);
          window.addEventListener('unhandledrejection', function (e) {
            window.__harnessErrors.push('unhandled rejection: ' + String((e && e.reason) || e));
          });
        })();
        """;

    /// <summary>
    /// Pause everything and place it at <paramref name="t"/>.
    ///
    /// <para>The global timeline is seeked as well as the registered ones. A tween written as a
    /// bare <c>gsap.to(...)</c> never reaches <c>window.__timelines</c>; it plays on the global
    /// timeline from page load, so without this it would sit at "however long the page took to
    /// become ready" in every frame.</para>
    ///
    /// <para>Events are NOT suppressed. Some blocks draw from an <c>onUpdate</c>, and a scrub with
    /// callbacks suppressed leaves their canvas showing the previous sample.</para>
    /// </summary>
    private static string Seek(double t) => """
        (function (t) {
          var report = { timelines: [], error: null };
          try {
            if (typeof gsap === 'undefined') { report.error = 'gsap is not defined'; return JSON.stringify(report); }
            gsap.ticker.lagSmoothing(0);
            var registered = window.__timelines || {};
            for (var id in registered) {
              var tl = registered[id];
              if (!tl || typeof tl.time !== 'function') continue;
              report.timelines.push({ id: id, duration: tl.duration() });
              tl.pause();
              tl.time(t, false);
            }
            gsap.globalTimeline.pause();
            gsap.globalTimeline.time(t, false);
          } catch (e) { report.error = String((e && e.message) || e); }
          return JSON.stringify(report);
        })(TIME)
        """.Replace("TIME", t.ToString("R", CultureInfo.InvariantCulture));

    /// <summary>
    /// Wait for the page the harness asked for - not the one still on screen.
    ///
    /// <para><c>readyState</c> on its own is not enough after a navigate: for the first instants it
    /// describes the PREVIOUS block's complete document, which polls green immediately and is then
    /// screenshotted five times. Hence the href check.</para>
    ///
    /// <para>Fonts and images are waited for too. A frame caught before a web font arrives is laid
    /// out in the fallback face, which is a large diff against an engine that had the font all
    /// along - and a false one.</para>
    /// </summary>
    private async Task ReadyAsync(string url, CancellationToken cancel)
    {
        const string probe = """
            (function () {
              return JSON.stringify({
                href: document.location.href,
                state: document.readyState,
                gsap: typeof gsap,
                timelines: window.__timelines ? Object.keys(window.__timelines).length : 0,
                fonts: document.fonts ? document.fonts.status : 'loaded',
                images: Array.prototype.every.call(document.images, function (i) { return i.complete; }),
                templates: document.querySelectorAll('template').length,
                errors: (window.__harnessErrors || []).slice(0, 4)
              });
            })()
            """;

        // Thirteen blocks - every code-snippet-* - put their entire composition inside a
        // <template>, scripts included. Template content is inert: nothing runs, nothing paints,
        // and the page is blank in any browser until a host clones it in. The host does; so does
        // this, once, and only for a document that finished loading without registering a
        // timeline. Cloned <script> elements have not "already started", so appending them runs
        // them - which is the whole point.
        //
        // The library is loaded first and awaited. Appending the fragment whole does run its
        // scripts, but an external one is fetched asynchronously while the inline script right
        // after it runs immediately, so the first attempt at this produced "Uncaught
        // ReferenceError: gsap is not defined" from a page that had just been handed GSAP.
        const string instantiate = """
            (async function () {
              var report = { instantiated: 0, error: null };
              try {
                var templates = document.querySelectorAll('template');
                for (var i = 0; i < templates.length; i++) {
                  var content = templates[i].content;
                  var external = content.querySelectorAll('script[src]');

                  for (var j = 0; j < external.length; j++) {
                    var url = external[j].src;
                    await new Promise(function (done) {
                      var tag = document.createElement('script');
                      tag.src = url;
                      tag.onload = done;
                      tag.onerror = done;
                      document.head.appendChild(tag);
                    });
                  }

                  document.body.appendChild(content.cloneNode(true));
                  report.instantiated++;
                }
              } catch (e) { report.error = String((e && e.message) || e); }
              return JSON.stringify(report);
            })()
            """;
        var templatesInstantiated = false;

        var started = DateTime.UtcNow;
        var deadline = started + TimeSpan.FromSeconds(30);
        JsonNode? last = null;

        while (DateTime.UtcNow < deadline)
        {
            last = await EvaluateJsonAsync(probe, cancel);

            if (last["href"]?.GetValue<string>() == url
                && last["state"]?.GetValue<string>() == "complete"
                && last["gsap"]?.GetValue<string>() == "object"
                && last["timelines"]?.GetValue<int>() > 0
                && last["fonts"]?.GetValue<string>() == "loaded"
                && last["images"]?.GetValue<bool>() == true)
                return;

            if (!templatesInstantiated
                && last["state"]?.GetValue<string>() == "complete"
                && last["timelines"]?.GetValue<int>() == 0
                && last["templates"]?.GetValue<int>() > 0)
            {
                var cloned = await EvaluateJsonAsync(instantiate, cancel, awaitPromise: true);
                templatesInstantiated = true;

                if (cloned["error"] is { } broke && broke.GetValueKind() != JsonValueKind.Null)
                    throw new BrowserException("instantiating the block's <template> failed: " + broke);

                continue;
            }

            // A finished document whose script has already thrown will not register a timeline a
            // moment later, so there is nothing to wait for. Two seconds of grace first, because
            // eight blocks build their timeline from a setTimeout and a failed image would
            // otherwise cut them off before they got to it.
            if (last["state"]?.GetValue<string>() == "complete"
                && last["timelines"]?.GetValue<int>() == 0
                && last["errors"] is JsonArray { Count: > 0 }
                && DateTime.UtcNow - started > TimeSpan.FromSeconds(2))
                break;

            // GSAP comes from a CDN and its script tag has already been fetched or not by the
            // time the document is complete. Waiting the full thirty seconds for a library that
            // is never going to arrive cost half a minute per block on a flaky network, and the
            // caller retries anyway.
            if (last["state"]?.GetValue<string>() == "complete"
                && last["gsap"]?.GetValue<string>() == "undefined"
                && DateTime.UtcNow - started > TimeSpan.FromSeconds(5))
                break;

            await Task.Delay(100, cancel);
        }

        // Named, not scored. A block whose reference never became ready has no reference, and a
        // number computed against whatever was on screen would be a measurement of nothing.
        var errors = last?["errors"] as JsonArray;
        throw new BrowserException(
            (errors is { Count: > 0 }
                ? "the block's own script failed: " + string.Join(" | ", errors.Select(e => e?.ToString()))
                : $"never became ready within {(DateTime.UtcNow - started).TotalSeconds:0}s")
            + ". State was " + (last?.ToJsonString() ?? "(no reply)")
            + ". gsap 'undefined' usually means no network - every block loads it from a CDN.");
    }

    // ---- protocol -----------------------------------------------------------------------------

    private async Task<Frame> ScreenshotAsync(CancellationToken cancel)
    {
        var result = await SendAsync("Page.captureScreenshot",
            new JsonObject { ["format"] = "png" }, cancel);
        return Frame.Decode(Convert.FromBase64String(result["data"]!.GetValue<string>()));
    }

    private async Task<JsonNode> EvaluateJsonAsync(string expression, CancellationToken cancel,
        bool awaitPromise = false)
    {
        var result = await SendAsync("Runtime.evaluate", new JsonObject
        {
            ["expression"] = expression,
            ["returnByValue"] = true,
            ["awaitPromise"] = awaitPromise,
        }, cancel);

        if (result["exceptionDetails"] is { } thrown)
            throw new BrowserException("the page threw: " + thrown.ToJsonString());

        var value = result["result"]?["value"]?.GetValue<string>()
                    ?? throw new BrowserException("evaluate returned nothing");

        return JsonNode.Parse(value) ?? throw new BrowserException("evaluate returned unparseable JSON");
    }

    private async Task<JsonNode> SendAsync(string method, JsonObject? parameters = null,
        CancellationToken cancel = default)
    {
        var id = ++_id;
        var message = new JsonObject
        {
            ["id"] = id,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };

        await _socket.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()),
            WebSocketMessageType.Text, true, cancel);

        // Replies are matched by id and everything else is dropped. The protocol interleaves
        // events with replies and this harness subscribes to none of them.
        while (true)
        {
            var reply = await ReceiveAsync(cancel);
            if (reply["id"]?.GetValue<int>() != id) continue;
            if (reply["error"] is { } error) throw new BrowserException(method + ": " + error.ToJsonString());
            return reply["result"] ?? new JsonObject();
        }
    }

    private async Task<JsonNode> ReceiveAsync(CancellationToken cancel)
    {
        // A 1920x1080 screenshot arrives base64'd in one logical message and many frames, so the
        // buffer grows rather than being sized once and hoped over.
        var buffer = new byte[64 * 1024];
        using var whole = new MemoryStream();

        while (true)
        {
            var chunk = await _socket.ReceiveAsync(buffer, cancel);
            if (chunk.MessageType == WebSocketMessageType.Close)
                throw new BrowserException("the browser closed the connection");

            whole.Write(buffer, 0, chunk.Count);
            if (chunk.EndOfMessage) break;
        }

        whole.Position = 0;
        return JsonNode.Parse(whole) ?? throw new BrowserException("unparseable reply");
    }

    // ---- launching ----------------------------------------------------------------------------

    /// <summary>
    /// The port the browser actually chose, from the file it writes into the profile.
    ///
    /// <para>The process exiting is NOT a failure here, however much it looks like one. Edge's
    /// <c>msedge.exe</c> is a launcher: it starts the real browser, writes nothing, and returns 0
    /// within a second, so the first version of this reported "the browser exited with 0 before
    /// listening" against a browser that was listening perfectly well. Only the deadline decides.</para>
    /// </summary>
    private static async Task<int> PortAsync(string profile, Process process, CancellationToken cancel)
    {
        var path = Path.Combine(profile, "DevToolsActivePort");
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                // Opened shared, because the browser still holds the file.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                if (int.TryParse((await reader.ReadLineAsync(cancel))?.Trim(), out var port) && port > 0)
                    return port;
            }

            await Task.Delay(100, cancel);
        }

        var exit = process.HasExited
            ? $" (the launcher exited with {process.ExitCode}: {await process.StandardError.ReadToEndAsync(cancel)})"
            : "";
        throw new BrowserException("the browser never wrote DevToolsActivePort" + exit);
    }

    /// <summary>The one page the browser opened at startup, reused for every block. Creating a
    /// target per block leaks them, and a hundred leaked targets is a hundred live renderers.</summary>
    private static async Task<string> PageSocketAsync(int port, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);

        while (DateTime.UtcNow < deadline)
        {
            try
            {
                var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/list", cancel);
                var page = JsonNode.Parse(json)?.AsArray()
                    .FirstOrDefault(t => t?["type"]?.GetValue<string>() == "page");

                if (page?["webSocketDebuggerUrl"]?.GetValue<string>() is { Length: > 0 } socket)
                    return socket;
            }
            catch (HttpRequestException) { /* still coming up */ }

            await Task.Delay(100, cancel);
        }

        throw new BrowserException($"no page target on 127.0.0.1:{port}");
    }

    private static async Task<string> DescribeAsync(int port, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        try
        {
            var json = await http.GetStringAsync($"http://127.0.0.1:{port}/json/version", cancel);
            return JsonNode.Parse(json)?["Browser"]?.GetValue<string>() ?? "unknown";
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            return "unknown";   // a missing version string is not worth failing a run over
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* already gone */ }
        process.Dispose();
    }

    /// <summary>
    /// Ask the browser to quit, over the protocol.
    ///
    /// <para>Asking, rather than killing, because the process this harness started is only a
    /// launcher and is long gone by now - the browser it left behind is not ours to find by
    /// handle. The alternative on Windows is killing every <c>msedge.exe</c> by name, which also
    /// closes whatever the person at the keyboard had open.</para>
    /// </summary>
    private async Task CloseAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var json = await http.GetStringAsync($"http://127.0.0.1:{_port}/json/version");

        if (JsonNode.Parse(json)?["webSocketDebuggerUrl"]?.GetValue<string>() is not { } url) return;

        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await socket.ConnectAsync(new Uri(url), timeout.Token);
        await socket.SendAsync(
            Encoding.UTF8.GetBytes("""{"id":1,"method":"Browser.close"}"""),
            WebSocketMessageType.Text, true, timeout.Token);

        try { await socket.ReceiveAsync(new byte[4096], timeout.Token); }
        catch (OperationCanceledException) { /* it was told; a slow goodbye is not a failure */ }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        catch (WebSocketException) { /* closing anyway */ }

        _socket.Dispose();

        try { await CloseAsync(); }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException
                                       or OperationCanceledException or JsonException)
        {
            Console.Error.WriteLine("cuprilex-harness: the browser did not shut down cleanly: " + ex.Message);
        }

        Kill(_process);

        try { Directory.Delete(_profile, recursive: true); }
        catch (IOException) { /* the browser sometimes still holds a lock; it is under TEMP */ }
        catch (UnauthorizedAccessException) { /* same */ }
    }
}

/// <summary>The reference could not be produced. Never a score of zero: a block with no reference
/// is unmeasured, and calling that "0% correct" would be inventing a measurement.</summary>
public sealed class BrowserException(string message) : Exception(message);
