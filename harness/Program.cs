using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using AngleSharp;
using CupriLex.Compiler;

namespace CupriLex.Harness;

/// <summary>How one sample time came out.</summary>
/// <param name="ContentDiffering">The share of the pixels the browser paints on that are wrong.
/// The headline, because the frame-wide share is dominated by empty background.</param>
/// <param name="ErrorWhenWrong">How far the wrong pixels are wrong, as a fraction of full scale.
/// The axis that separates "rasterised differently" from "missing".</param>
/// <param name="Severe">The share of the frame off by more than half of full scale.</param>
public sealed record Sample(
    double Time, double Similarity, double Differing,
    double ContentDiffering, double ErrorWhenWrong, double Severe);

/// <summary>One block's score, and enough beside it to know whether to believe the score.</summary>
/// <param name="Matching">The headline: the share of pixels that are not visibly different, mean
/// over the samples.</param>
/// <param name="Similarity">Mean absolute error, as a fraction. Reported second because it is far
/// too kind: <c>carousel-circle-1</c> renders as an empty grey rectangle in the engine - not one
/// of its cards appears - and scores 99.4% here, because white cards on light grey are a small
/// per-pixel difference over a tenth of the frame. Both numbers are generous to a mostly-empty
/// composition, which is what the diff image and <see cref="ReferenceMoves"/> are for.</param>
/// <param name="Worst">The worst sample, by matching. Usually the end of a composition, where the
/// browser has finished animating and the engine has not started.</param>
/// <param name="ReferenceMoves">How much the browser's own frames differ from its first one. The
/// calibration that makes the rest readable: a block whose reference barely moves cannot be
/// evidence that motion was carried, however well it scores.</param>
/// <param name="EngineMoves">The same for the engine's frames. Zero means the engine rendered the
/// same image at every time - no motion at all, which is the expected baseline before the
/// compiler.</param>
/// <param name="ReferenceInk">The same measure taken of the browser, so the engine's can be
/// read. 3% ink is a sparse composition, not a broken render, and only the pair says which.</param>
/// <param name="EngineInk">How much of the frame the engine PAINTED, mean over the samples,
/// measured against the engine's own background rather than the browser's frame. The one number
/// here that is not a comparison, and it separates the two failures a comparison confuses: drawing
/// the wrong thing, and drawing nothing at all.</param>
/// <param name="Failure">Set when there is no score: no reference, or a document the engine
/// refused. Never scored as zero - unmeasured is not the same as wrong.</param>
public sealed record Score(
    string Block,
    int Width,
    int Height,
    double Duration,
    string DurationSource,
    double TimelineSeconds,
    double Content,
    double Matching,
    double Similarity,
    double ErrorWhenWrong,
    double Severe,
    double Worst,
    double ReferenceMoves,
    double EngineMoves,
    double ReferenceInk,
    double EngineInk,
    double Seconds,
    IReadOnlyList<Sample> Samples,
    IReadOnlyList<string> Refusals,
    IReadOnlyList<string> Diagnostics,
    string? Failure)
{
    /// <summary>The folder this block's images went into: the name as one path segment.</summary>
    public string Slug() => Block.Replace('/', '_');
}

/// <summary>
/// The instrument this project is steered by: how close the engine's frames are to a browser's,
/// for one block or for all of them.
///
///     dotnet run --project harness -- bar-chart-race
///     dotnet run --project harness -- --all --out harness/out
///
/// <para>Progress is this number and nothing else - not how many rewrite rules exist, not how many
/// blocks parse without an error. See docs/HARNESS.md.</para>
/// </summary>
public static class Program
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            var samples = int.Parse(Option(args, "--samples") ?? "5", CultureInfo.InvariantCulture);
            var output = Option(args, "--out") ?? Path.Combine("harness", "out");
            var limit = int.Parse(Option(args, "--limit") ?? "0", CultureInfo.InvariantCulture);
            var keepFrames = args.Contains("--frames");
            var gallery = args.Contains("--gallery");
            var align = args.Contains("--align");

            // Re-read a finished run instead of producing one. A corpus run is a quarter of an
            // hour of rendering, and a better way of summarising it should not cost that again.
            if (Option(args, "--report") is { Length: > 0 } finished)
            {
                Summarise(Read(finished).Blocks);
                return 0;
            }

            var named = args.FirstOrDefault(a => !a.StartsWith("--")
                                                 && !IsOptionValue(args, a));

            var fast = args.Contains("--fast");

            if (!args.Contains("--all") && !fast && named is null)
            {
                Usage();
                return 2;
            }

            IReadOnlyList<Block> blocks = args.Contains("--all") ? Corpus.All()
                : fast ? Corpus.Fast()
                : [Corpus.Find(named!)];

            if (align) return await AlignAsync(blocks[0], samples, Engine.FindFonts());

            // Packaging renders nothing, so it comes before the browser is launched.
            if (Option(args, "--package") is { Length: > 0 } into)
                return await PackagingAsync(blocks, into, Option(args, "--download"));

            if (limit > 0) blocks = [.. blocks.Take(limit)];

            if (fast) Canaries();

            return await RunAsync(blocks, samples, output, keepFrames, gallery, fast);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("cuprilex-harness: " + ex.Message);
            return 1;
        }
    }

    /// <summary>
    /// Translated blocks written out as <c>.cutpkg</c> files.
    ///
    /// <para>Here rather than in the compiler because a package needs facts the compiler has no
    /// business knowing: how wide a block is, how long it runs, and where its assets sit. That is
    /// corpus metadata, and this is the project that reads it. The format itself stays in
    /// <see cref="Package"/> - the compiler owns what a translation becomes, and the harness owns
    /// what a block is.</para>
    ///
    /// <para>Nothing is rendered. A package is the end of the pipeline and not a measurement, so
    /// it costs no browser and no engine.</para>
    /// </summary>
    private static async Task<int> PackagingAsync(
        IReadOnlyList<Block> blocks, string into, string? download)
    {
        Directory.CreateDirectory(into);

        var translations = blocks.ToDictionary(
            block => block.Name,
            block => Translator.Of(File.ReadAllText(block.Path)));

        // Asked once, for the whole run, BEFORE anything is fetched or written. A prompt that
        // arrived block by block would be a hundred and eighty-seven decisions nobody can make.
        var consent = Decide(translations.Values, download);

        Console.WriteLine($"packaging {blocks.Count} block(s) into {into}");
        Console.WriteLine();

        using var fetch = new Http();
        long total = 0;
        var refused = 0;
        var missing = 0;
        var fetched = 0;

        foreach (var block in blocks)
        {
            var translated = translations[block.Name];

            var document = new AngleSharp.Html.Parser.HtmlParser()
                .ParseDocument(translated.Html);

            var gathered = await WebFonts.GatherAsync(document, consent, fetch);
            fetched += gathered.Faces.Count;

            var composition = new Composition(
                block.Name, document.ToHtml(), translated.Motion, translated.Refusals,
                block.Width, block.Height, block.Duration,
                Description: $"Translated from {Path.GetFileName(block.Path)} by CupriLex.");

            var written = Package.Write(composition, block.Directory,
                Path.Combine(into, block.Slug + Package.Extension),
                [.. gathered.Faces.Select(f => new Asset(f.Key, f.From, f.Bytes))]);

            total += written.Bytes;
            refused += translated.Refusals.Count;
            missing += written.Missing.Count;

            Console.WriteLine($"{block.Name,-34}  {written.Bytes / 1024.0,8:n0} KB  "
                              + $"{written.Assets.Count,3} asset(s)  "
                              + $"{translated.Motion.Elements,3} animated  "
                              + $"{translated.Refusals.Count,3} refused"
                              + (gathered.Faces.Count > 0 ? $"  {gathered.Faces.Count} fetched" : "")
                              + (written.Missing.Count > 0 ? $"  {written.Missing.Count} MISSING" : ""));
        }

        Console.WriteLine();
        Console.WriteLine($"{blocks.Count} package(s), {total / 1024.0 / 1024.0:n1} MB, "
                          + $"{refused} refusal(s) recorded in them");

        if (fetched > 0)
            Console.WriteLine($"{fetched} font file(s) were fetched and are carried inside the "
                              + "packages, so nothing reaches for a network when they render.");

        if (missing > 0)
            Console.WriteLine($"{missing} reference(s) named a file that was not beside the block. "
                              + "Each package names its own; none were silently dropped.");

        return 0;
    }

    /// <summary>
    /// What the person running this agreed to let off the machine.
    ///
    /// <para>Nothing is fetched by default. A tool that reaches the network unless told not to has
    /// made the decision for whoever is running it, and "it only downloads fonts" is a sentence
    /// about this version rather than about the design.</para>
    ///
    /// <para><c>--download all</c> approves everything. <c>--download none</c>, or no flag at all,
    /// approves nothing. Anything else prints the full list of requests and reads an answer, which
    /// is the shape a settings screen or a first-run prompt wants: the same list, the same
    /// decision, somewhere with buttons.</para>
    /// </summary>
    private static IConsent Decide(IEnumerable<Translated> translations, string? download)
    {
        if (string.Equals(download, "none", StringComparison.OrdinalIgnoreCase)) return Consent.None;
        if (string.Equals(download, "all", StringComparison.OrdinalIgnoreCase)) return Consent.All;

        var parser = new AngleSharp.Html.Parser.HtmlParser();
        var requests = translations
            .SelectMany(t => External.Of(parser.ParseDocument(t.Html)))
            .Where(r => r.Kind is Fetches.Stylesheet or Fetches.Font)
            .GroupBy(r => r.Url, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Request: g.First(), Count: g.Count()))
            .OrderByDescending(r => r.Count)
            .ToArray();

        if (requests.Length == 0) return Consent.None;
        if (download is null && Console.IsInputRedirected) return Consent.None;

        Console.WriteLine();
        Console.WriteLine($"These {requests.Length} request(s) would be made off this machine:");
        Console.WriteLine();

        foreach (var (request, count) in requests)
            Console.WriteLine($"  [{count,3} block(s)] {request.Url}");

        Console.WriteLine();
        Console.WriteLine("Hosts: " + string.Join(", ",
            requests.Select(r => r.Request.Host).Distinct().Order()));
        Console.WriteLine();
        Console.Write("Fetch them? [a]ll / [n]one / list hosts to allow: ");

        var answer = (Console.ReadLine() ?? string.Empty).Trim();

        if (answer.StartsWith('a')) return Consent.All;
        if (answer.Length == 0 || answer.StartsWith('n')) return Consent.None;

        return Consent.Hosts(answer.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>
    /// Is the engine's clock the same clock as the browser's?
    ///
    /// <para>Renders the browser at each sample time and the engine at that time plus a sweep of
    /// offsets, then reports which offset makes the frames agree best. If the two clocks are
    /// aligned the answer is zero at every sample; a consistent non-zero answer is a systematic
    /// lead or lag, and every score in this repository would be measuring it.</para>
    ///
    /// <para>Worth having as a standing diagnostic rather than a one-off, because the symptom that
    /// prompted it - the worst sample of a block usually falling near a transition - has an
    /// innocent explanation as well as a guilty one. A transition is where the frame changes
    /// fastest, so it is where ANY timing error costs the most pixels, and it is also where the
    /// most content is in flight for reasons that have nothing to do with the clock. Only the
    /// sweep separates those.</para>
    /// </summary>
    private static async Task<int> AlignAsync(Block block, int samples, string? fonts)
    {
        await using var browser = await Browser.LaunchAsync();

        var times = Times(block.Duration, samples).OrderBy(t => t).ToArray();
        var reference = await browser.RenderAsync(block, times);
        var html = Translation.Of(block).Html;

        // A tenth of a second either way, then a coarse second, which is wide enough to catch a
        // whole-tween lag and fine enough to see a frame's worth.
        double[] offsets =
        [
            -1.0, -0.75, -0.5, -0.4, -0.3, -0.2, -0.15, -0.1, -0.05,
            0,
            0.05, 0.1, 0.15, 0.2, 0.3, 0.4, 0.5, 0.75, 1.0,
        ];

        Console.WriteLine($"{block.Name}: {block.Width}x{block.Height}, "
                          + $"{block.Duration.ToString("0.###", CultureInfo.InvariantCulture)}s, "
                          + $"{samples} samples");
        Console.WriteLine();
        Console.WriteLine($"  {"offset",8}  {"content wrong",14}");

        var best = (Offset: 0.0, Wrong: double.MaxValue);
        var atZero = 0.0;
        var curve = new List<(double Offset, double Wrong)>();

        foreach (var offset in offsets)
        {
            var shifted = times.Select(t => Math.Max(0, t + offset)).ToArray();
            var rendered = Engine.Render(block, html, shifted, fonts);

            var wrong = 0.0;
            for (var i = 0; i < times.Length; i++)
                wrong += Comparison.Of(reference.Frames[i], rendered.Frames[i]).ContentDiffering;
            wrong /= times.Length;

            curve.Add((offset, wrong));
            if (offset == 0) atZero = wrong;

            // Ties go to the smaller shift, and zero beats everything it ties with. Without that
            // the sweep "finds" a 50ms lead on any block whose curve is flat, which is most of
            // them once the motion has finished.
            if (wrong < best.Wrong - 1e-9
                || (Math.Abs(wrong - best.Wrong) <= 1e-9 && Math.Abs(offset) < Math.Abs(best.Offset)))
                best = (offset, wrong);

            Console.WriteLine($"  {offset,8:+0.00;-0.00;0.00}  {Percent(wrong),14}"
                              + (offset == 0 ? "   <- no offset" : ""));
        }

        Console.WriteLine();
        Console.WriteLine($"best offset {best.Offset:+0.00;-0.00;0.00}s at {Percent(best.Wrong)} wrong, "
                          + $"against {Percent(atZero)} with no offset");
        // A real lead or lag is a valley: the offsets either side of the best one also beat zero,
        // because shifting a little less still helps a little. A single offset that beats zero
        // while its neighbours do not is one sample happening to line up, and it moves when the
        // sample count changes - which is exactly what the first version of this reported as a
        // 0.75s lag on transitions-grid.
        // The neighbours have to beat zero by a real margin, not by rounding noise. A quarter of
        // the best improvement is arbitrary and it is enough: on transitions-grid the neighbours
        // beat zero by 0.1% against a 3.4% dip, which is a spike, and this rejects it.
        var gain = atZero - best.Wrong;
        var at = curve.FindIndex(p => p.Offset == best.Offset);
        var valley = at > 0 && at < curve.Count - 1
                     && curve[at - 1].Wrong < atZero - gain / 4
                     && curve[at + 1].Wrong < atZero - gain / 4;

        Console.WriteLine(Math.Abs(best.Offset) < 1e-9 || atZero - best.Wrong < 0.005 || !valley
            ? "The clocks agree. No shift recovers half a percent of content with its neighbours "
              + "agreeing, which is what a real lead or lag looks like."
            : $"Shifting the engine by {best.Offset:+0.00;-0.00}s recovers "
              + $"{Percent(atZero - best.Wrong)} of content, and the offsets either side agree. "
              + "That is a systematic lead or lag and no score should be trusted until it is "
              + "explained.");

        return 0;
    }

    private static async Task<int> RunAsync(
        IReadOnlyList<Block> blocks, int samples, string output, bool keepFrames, bool gallery,
        bool fast = false)
    {
        var fonts = Engine.FindFonts();
        Directory.CreateDirectory(output);

        await using var browser = await Browser.LaunchAsync();

        Console.WriteLine($"engine   CupriFace {Engine.Version}");
        Console.WriteLine($"browser  {browser.Version}");
        Console.WriteLine($"fonts    {fonts ?? "(none found - text will be scored against whatever is installed)"}");
        Console.WriteLine($"blocks   {blocks.Count}, {samples} samples each");
        Console.WriteLine();

        // Appended as each block finishes. A run over the whole corpus is minutes long and a
        // process that dies at block 150 should not cost the 149 measurements before it.
        var incremental = Path.Combine(output, "blocks.jsonl");
        File.Delete(incremental);

        var scores = new List<Score>(blocks.Count);
        var started = Stopwatch.StartNew();

        foreach (var block in blocks)
        {
            var score = await ScoreAsync(browser, block, samples, output, keepFrames, fonts);
            scores.Add(score);

            await File.AppendAllTextAsync(incremental,
                JsonSerializer.Serialize(score, new JsonSerializerOptions(Json) { WriteIndented = false })
                + Environment.NewLine);

            Report(score, blocks.Count == 1);
        }

        var report = new Baseline(Engine.Version, browser.Version, Environment.OSVersion.VersionString,
            DateTimeOffset.UtcNow, samples, Math.Round(started.Elapsed.TotalSeconds, 1), scores);

        var path = Path.Combine(output, "baseline.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(report, Json));

        if (blocks.Count > 1) Summarise(scores);

        // The one thing the fast set must never be allowed to do is read like the corpus. Its
        // blocks were chosen for being unusually alive, so its mean sits far above the real one,
        // and a number quoted from here into a commit message would be a lie told by omission.
        if (fast)
        {
            Console.WriteLine();
            Console.WriteLine("This is the FAST SET, not the corpus. These nine blocks were chosen "
                              + "for being the first");
            Console.WriteLine("place a change of each kind would show, which makes the mean above "
                              + "unrepresentative on");
            Console.WriteLine("purpose. Quote --all. Use this to decide whether --all is worth "
                              + "running.");
        }

        Console.WriteLine();
        Console.WriteLine($"wrote {path}");

        if (gallery)
            Console.WriteLine("wrote " + Gallery.Write(
                output, scores, Engine.Version, browser.Version, report.Measured));

        return 0;
    }

    private sealed record Baseline(
        string Engine, string Browser, string Platform, DateTimeOffset Measured,
        int Samples, double Seconds, IReadOnlyList<Score> Blocks);

    private static Baseline Read(string path)
    {
        var loaded = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(path), Json)
                     ?? throw new InvalidOperationException($"'{path}' is not a baseline.");

        Console.WriteLine($"CupriFace {loaded.Engine}, {loaded.Browser}, measured "
                          + loaded.Measured.ToString("u", CultureInfo.InvariantCulture));
        return loaded;
    }

    // ---- one block ----------------------------------------------------------------------------

    private static async Task<Score> ScoreAsync(Browser browser, Block block, int samples,
        string output, bool keepFrames, string? fonts)
    {
        // Sorted here, once. Both renderers return frames in ascending time order, so an unsorted
        // list would pair frame i with a label that belongs to a different instant - and every
        // number would be right while every row said the wrong time.
        var times = Times(block.Duration, samples).OrderBy(t => t).ToArray();
        var clock = Stopwatch.StartNew();

        Score Failed(string why) => new(block.Name, block.Width, block.Height, block.Duration,
            block.DurationSource, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            Math.Round(clock.Elapsed.TotalSeconds, 2),
            [], [], [], why);

        Reference reference;
        try
        {
            reference = await Retried(() => browser.RenderAsync(block, times));
        }
        catch (BrowserException ex)
        {
            return Failed("no reference, twice: " + ex.Message);
        }

        var translated = Translation.Of(block);

        Rendered rendered;
        try
        {
            rendered = Engine.Render(block, translated.Html, times, fonts);
        }
        catch (Exception ex)
        {
            // An engine that throws on a real document is a finding, not a footnote. The report
            // names it and the whole stack goes on disk beside the block, because the next
            // question is always "where", and the answer belongs in the sibling repository's
            // issue list rather than in a console buffer that scrolled away.
            var crash = Path.Combine(output, block.Slug, "engine-crash.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(crash)!);
            await File.WriteAllTextAsync(crash, ex.ToString());

            return Failed($"the engine threw {ex.GetType().Name}: {OneLine(ex.Message)} "
                          + $"(stack: {crash})");
        }

        var comparisons = new List<Sample>(times.Length);
        for (var i = 0; i < times.Length; i++)
        {
            var c = Comparison.Of(reference.Frames[i], rendered.Frames[i]);
            comparisons.Add(new Sample(Math.Round(times[i], 3),
                Math.Round(c.Similarity, 5), Math.Round(c.Differing, 5),
                Math.Round(c.ContentDiffering, 5), Math.Round(c.ErrorWhenWrong, 5),
                Math.Round(c.Severe, 5)));
        }

        var worstIndex = comparisons.IndexOf(comparisons.MaxBy(s => s.ContentDiffering)!);
        var folder = Path.Combine(output, block.Slug);

        // The worst sample always, because it is the one that says what went wrong. Every sample
        // when asked, because a single frame can be unrepresentative in both directions.
        if (keepFrames)
        {
            for (var i = 0; i < times.Length; i++)
            {
                Diff.Write(reference.Frames[i], rendered.Frames[i],
                    Path.Combine(folder, $"{i:00}-t{times[i]:0.###}.png"),
                    Caption(block, comparisons[i]));
                reference.Frames[i].Save(Path.Combine(folder, "full", $"{i:00}-browser.png"));
                rendered.Frames[i].Save(Path.Combine(folder, "full", $"{i:00}-engine.png"));
            }
        }
        else
        {
            Diff.Write(reference.Frames[worstIndex], rendered.Frames[worstIndex],
                Path.Combine(folder, $"worst-t{times[worstIndex]:0.###}.png"),
                Caption(block, comparisons[worstIndex]));
        }

        return new Score(
            block.Name, block.Width, block.Height, block.Duration, block.DurationSource,
            Math.Round(reference.TimelineSeconds, 3),
            Math.Round(comparisons.Average(s => 1 - s.ContentDiffering), 5),
            Math.Round(comparisons.Average(s => 1 - s.Differing), 5),
            Math.Round(comparisons.Average(s => s.Similarity), 5),
            Math.Round(comparisons.Average(s => s.ErrorWhenWrong), 5),
            Math.Round(comparisons.Average(s => s.Severe), 5),
            Math.Round(1 - comparisons.Max(s => s.ContentDiffering), 5),
            Math.Round(Movement(reference.Frames), 5),
            Math.Round(Movement(rendered.Frames), 5),
            Math.Round(reference.Frames.Average(f => f.Ink()), 5),
            Math.Round(rendered.Frames.Average(f => f.Ink()), 5),
            Math.Round(clock.Elapsed.TotalSeconds, 2),
            comparisons, translated.Refusals, rendered.Diagnostics, null);
    }

    /// <summary>
    /// One more attempt at the reference, after a pause.
    ///
    /// <para>Every block in the corpus pulls GSAP from the same CDN, and a corpus run asks for it
    /// 187 times in a few minutes. In the first full run one block came back with the document
    /// complete and <c>gsap</c> undefined - a single request that did not arrive - and was
    /// reported as unmeasured. A block the network dropped once is not a finding about the
    /// engine, and leaving it in the report as though it were would be worse than the ten seconds
    /// this costs.</para>
    /// </summary>
    private static async Task<Reference> Retried(Func<Task<Reference>> render)
    {
        try
        {
            return await render();
        }
        catch (BrowserException)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            return await render();
        }
    }

    /// <summary>
    /// The share of the frame a renderer's own samples change, against its first one. Zero means
    /// every sample came out identical - nothing moved.
    ///
    /// <para>Measured in differing pixels rather than mean error, because mean error over a frame
    /// that is four fifths flat background reports a composition rebuilding itself completely as
    /// "2% of movement", which reads like a still.</para>
    /// </summary>
    private static double Movement(IReadOnlyList<Frame> frames) =>
        frames.Count < 2 ? 0 : frames.Skip(1).Average(f => Comparison.Of(frames[0], f).Differing);

    /// <summary>Evenly spaced over the declared duration, both ends included. The ends are where
    /// the disagreement usually is: <c>t=0</c> catches an opening state the engine never applied,
    /// and <c>t=end</c> catches a composition that finished somewhere the engine never went.</summary>
    public static IReadOnlyList<double> Times(double duration, int samples)
    {
        if (samples < 1) throw new ArgumentOutOfRangeException(nameof(samples), "at least one sample");
        if (samples == 1) return [duration / 2];
        return [.. Enumerable.Range(0, samples).Select(i => duration * i / (samples - 1))];
    }

    // ---- reporting ----------------------------------------------------------------------------

    private static void Report(Score score, bool verbose)
    {
        if (score.Failure is { } failure)
        {
            Console.WriteLine($"{score.Block,-38}  ----   {failure}");
            return;
        }

        var frozen = score.EngineMoves == 0 ? "  engine still" : "";
        Console.WriteLine($"{score.Block,-34}  {Percent(score.Content),7} of content  "
                          + $"{Percent(score.Matching),7} of frame  off by {Percent(score.ErrorWhenWrong),6}"
                          + $"  severe {Percent(score.Severe),6}{frozen}");

        if (!verbose) return;

        Console.WriteLine();
        Console.WriteLine($"  {score.Width}x{score.Height}, {score.Duration:0.###}s declared "
                          + $"({score.DurationSource}), timeline spans {score.TimelineSeconds:0.###}s");

        if (score.TimelineSeconds > 0 && Math.Abs(score.TimelineSeconds - score.Duration) > 0.25)
            Console.WriteLine("  NOTE the timeline and the declared duration disagree, so some "
                              + "samples land where the reference is already a still frame.");

        Console.WriteLine();
        Console.WriteLine($"  {"time",8}  {"content",9}  {"frame",9}  {"off by",9}  {"severe",8}");
        foreach (var s in score.Samples)
            Console.WriteLine($"  {s.Time,8:0.###}  {Percent(1 - s.ContentDiffering),9}  "
                              + $"{Percent(1 - s.Differing),9}  {Percent(s.ErrorWhenWrong),9}  "
                              + $"{Percent(s.Severe),8}");

        if (score.Refusals.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  not carried:");
            foreach (var r in score.Refusals) Console.WriteLine("    - " + r);
        }

        if (score.Diagnostics.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  the engine said:");
            foreach (var d in score.Diagnostics.Take(8)) Console.WriteLine("    - " + Clip(d, 110));
            if (score.Diagnostics.Count > 8)
                Console.WriteLine($"    ... and {score.Diagnostics.Count - 8} more codes");
        }
    }

    private static void Summarise(IReadOnlyList<Score> scores)
    {
        var scored = scores.Where(s => s.Failure is null).ToArray();
        var failed = scores.Where(s => s.Failure is not null).ToArray();

        Console.WriteLine();
        Console.WriteLine($"{scored.Length} scored, {failed.Length} unmeasured");

        if (scored.Length == 0) return;

        // The content share leads, because it is the one that does not reward losing a
        // composition's text on an empty background. The frame share is beside it so the two can
        // be compared, and the gap between them is a measure of how empty the corpus is.
        Console.WriteLine($"mean of content  {Percent(scored.Average(s => s.Content))}   <- the number to beat");
        Console.WriteLine($"median           {Percent(Median([.. scored.Select(s => s.Content)]))}");
        Console.WriteLine($"worst block      {Percent(scored.Min(s => s.Content))}  "
                          + $"({scored.MinBy(s => s.Content)!.Block})");
        Console.WriteLine($"best block       {Percent(scored.Max(s => s.Content))}  "
                          + $"({scored.MaxBy(s => s.Content)!.Block})");
        Console.WriteLine();
        Console.WriteLine($"mean of frame    {Percent(scored.Average(s => s.Matching))} "
                          + "- the same thing counted over empty background too");
        Console.WriteLine($"off by           {Percent(scored.Average(s => s.ErrorWhenWrong))} "
                          + "where it is wrong at all");
        Console.WriteLine($"severe           {Percent(scored.Average(s => s.Severe))} "
                          + "of the frame replaced rather than shifted");
        Console.WriteLine($"engine still     {scored.Count(s => s.EngineMoves == 0)} of {scored.Length} "
                          + "blocks rendered the same frame at every time");

        // The line that says which KIND of failure this corpus has, and the only one here that
        // does not involve the browser at all. A block the engine paints almost nothing in cannot
        // be improved by any amount of fidelity work - not a typeface, not an easing curve - and
        // for two releases running a correct fix moved the mean by nothing because of it.
        var blank = scored.Where(s => s.EngineInk < 0.005).ToArray();
        Console.WriteLine($"engine blank     {blank.Length} of {scored.Length} "
                          + "blocks paint under 0.5% of their own frame: nothing to compare");
        Console.WriteLine($"engine ink       {Percent(scored.Average(s => s.EngineInk))} "
                          + $"of the frame painted, against {Percent(scored.Average(s => s.ReferenceInk))} "
                          + "in the browser");

        // The qualification that keeps the headline honest. editorial-flash-overlay scores 100%
        // and its reference does not change by a single pixel across the whole composition: the
        // two renderers agree about a still, which is not evidence that anything was carried.
        var animated = scored.Where(s => s.ReferenceMoves >= 0.01).ToArray();
        Console.WriteLine($"still reference  {scored.Length - animated.Length} of {scored.Length} "
                          + "blocks change under 1% of their pixels over time; their score says little");

        if (animated.Length > 0)
            Console.WriteLine($"mean of content  {Percent(animated.Average(s => s.Content))} "
                              + $"over the {animated.Length} blocks whose reference actually moves");

        if (failed.Length == 0) return;

        // Grouped by cause, because thirty-six blocks failing for one reason and thirty-six
        // failing for thirty-six reasons are very different pieces of news, and a flat list of
        // near-identical lines hides which one it is.
        Console.WriteLine();
        Console.WriteLine("unmeasured, by cause:");

        foreach (var cause in failed.GroupBy(f => Cause(f.Failure!)).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {cause.Count(),3}  {Clip(cause.Key, 100)}");
            Console.WriteLine($"       {Clip(string.Join(", ", cause.Select(f => f.Block)), 100)}");
        }
    }

    /// <summary>A failure reason with the block-specific tail cut off, so identical causes group.
    /// The stack path and the state dump differ per block; the sentence in front of them does
    /// not.</summary>
    private static string Cause(string failure)
    {
        var cut = failure.IndexOf(" (stack:", StringComparison.Ordinal);
        if (cut < 0) cut = failure.IndexOf(". State was", StringComparison.Ordinal);
        return (cut < 0 ? failure : failure[..cut]).Trim();
    }

    private static double Median(double[] values)
    {
        Array.Sort(values);
        return values.Length % 2 == 1
            ? values[values.Length / 2]
            : (values[values.Length / 2 - 1] + values[values.Length / 2]) / 2;
    }

    // ---- plumbing -----------------------------------------------------------------------------

    /// <summary>What each block in the fast set is there to catch, printed before the run.
    ///
    /// <para>Printed rather than left in the source, because a subset whose reasons are invisible
    /// decays into a list of nine arbitrary names within a release or two, and then into a subset
    /// nobody trusts.</para>
    /// </summary>
    private static void Canaries()
    {
        Console.WriteLine("the fast set, and what each one is watching:");
        Console.WriteLine();
        foreach (var (block, why) in Corpus.Quick)
        {
            Console.WriteLine($"  {block}");
            foreach (var line in Wrap(why, 88)) Console.WriteLine($"      {line}");
        }
        Console.WriteLine();
    }

    /// <summary>Word wrap, so a reason can be written as a sentence rather than as a column.</summary>
    private static IEnumerable<string> Wrap(string text, int width)
    {
        var line = new System.Text.StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }
            if (line.Length > 0) line.Append(' ');
            line.Append(word);
        }
        if (line.Length > 0) yield return line.ToString();
    }

    private static void Usage()
    {
        Console.WriteLine("""
            How close is the engine to a browser, for one block or for all of them?

                dotnet run --project harness -- <block>        score one block
                dotnet run --project harness -- --fast         score the nine canaries
                dotnet run --project harness -- --all          score the corpus

                --samples N    times to sample across the declared duration (default 5)
                --out DIR      where frames and baseline.json go (default harness/out)
                --frames       keep every sample's images, not only the worst
                --limit N      stop after N blocks, for a quick look
                --fast         the nine blocks each change shows up in first, about 30s.
                               For the loop, not for the record: its mean is not the corpus

                --gallery      also write index.html: every block, worst first, with its images
                --align        sweep the engine's clock against the browser's, for one block
                --report FILE  re-summarise a finished run's baseline.json, rendering nothing
                --package DIR  write each block as a .cutpkg and render nothing: one file with
                               its assets, its fonts and its report inside
                --download W   what may be fetched off this machine while packaging:
                               "all", "none" (the default), or omit it to be shown
                               every request and asked

            Needs the corpus: python tools/fetch-corpus.py
            """);
    }

    private static string Caption(Block block, Sample sample) =>
        $"{block.Name}   t={sample.Time.ToString("0.###", CultureInfo.InvariantCulture)}s   "
        + $"{Percent(1 - sample.ContentDiffering)} of content   "
        + $"{Percent(1 - sample.Differing)} of frame   "
        + $"off by {Percent(sample.ErrorWhenWrong)}";

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    /// <summary>A multi-line exception message ruins a table, and every failure here is a row.</summary>
    private static string OneLine(string s) =>
        string.Join(' ', s.Split(['\n', '\r'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Whether this argument is some option's value rather than the block name.</summary>
    private static bool IsOptionValue(string[] args, string argument)
    {
        var i = Array.IndexOf(args, argument);
        return i > 0 && args[i - 1].StartsWith("--");
    }
}
