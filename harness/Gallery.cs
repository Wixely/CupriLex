using System.Globalization;
using System.Text;

namespace CupriLex.Harness;

/// <summary>
/// One page showing where the corpus actually stands, worst first.
///
/// <para>Numbers in a table are not enough to steer by. A block scoring 67% of content can be a
/// composition with one missing caption or one that drew nothing but its background, and the two
/// need completely different work. The triptychs already exist after a run; this puts them in one
/// place, beside the numbers, in an order that puts the worst thing first.</para>
///
/// <para>Deliberately a single self-contained file with no stylesheet, no script and no fonts: it
/// is written into an ignored directory and opened from disk, and anything it depended on would
/// have to be fetched over a network that may not be there.</para>
/// </summary>
public static class Gallery
{
    public static string Write(string directory, IReadOnlyList<Score> scores, string engine,
        string browser, DateTimeOffset measured)
    {
        var scored = scores.Where(s => s.Failure is null).OrderBy(s => s.Content).ToArray();
        var failed = scores.Where(s => s.Failure is not null).ToArray();

        var html = new StringBuilder();
        html.Append($$"""
            <!doctype html>
            <meta charset="utf-8">
            <title>CupriLex — {{scored.Length}} blocks, worst first</title>
            <style>
              :root { color-scheme: dark; }
              body { background:#101014; color:#d8d8e0; margin:0; padding:2rem 2.5rem;
                     font:14px/1.5 "Segoe UI",system-ui,sans-serif; }
              h1 { font-size:1.3rem; margin:0 0 .3rem; }
              .sub { color:#8a8a98; margin-bottom:2rem; }
              .sub code { color:#c8c8d4; }
              .block { margin:0 0 2.5rem; border-top:1px solid #26262e; padding-top:1rem; }
              .head { display:flex; align-items:baseline; gap:1rem; flex-wrap:wrap; }
              .name { font-size:1.05rem; font-weight:600; }
              .n { font-variant-numeric:tabular-nums; }
              .bad { color:#e5646e; } .mid { color:#e2b45c; } .ok { color:#5cc98b; }
              .meta { color:#7a7a88; font-size:.85rem; }
              img { display:block; width:100%; margin:.8rem 0 .5rem; border:1px solid #26262e; }
              details { margin-top:.4rem; }
              summary { cursor:pointer; color:#8a8a98; }
              li { color:#a6a6b4; margin:.15rem 0; }
              .failed li { color:#e5646e; }
            </style>
            <h1>CupriLex — the current state of the comparison</h1>
            <div class="sub">
              CupriFace {{engine}} · {{browser}} ·
              measured {{measured.ToString("u", CultureInfo.InvariantCulture)}} ·
              {{scored.Length}} scored, {{failed.Length}} unmeasured.<br>
              Each row is one block at its worst sample. Left is the browser, middle is CupriFace,
              right is where they differ. <b>Worst first.</b><br>
              <code>of content</code> counts only the pixels the browser paints on ·
              <code>of frame</code> counts the empty background too ·
              <code>off by</code> is how far wrong the wrong pixels are ·
              <code>severe</code> is what was replaced rather than shifted.
            </div>

            """);

        foreach (var score in scored)
        {
            var worst = score.Samples.MaxBy(s => s.ContentDiffering);
            var image = $"{score.Slug()}/worst-t{worst?.Time.ToString("0.###", CultureInfo.InvariantCulture)}.png";

            html.Append($$"""
                <div class="block">
                  <div class="head">
                    <span class="name">{{Escape(score.Block)}}</span>
                    <span class="n {{Band(score.Content)}}">{{Percent(score.Content)}} of content</span>
                    <span class="n meta">{{Percent(score.Matching)}} of frame</span>
                    <span class="n meta">off by {{Percent(score.ErrorWhenWrong)}}</span>
                    <span class="n meta">severe {{Percent(score.Severe)}}</span>
                    <span class="meta">{{score.Width}}×{{score.Height}} ·
                      {{score.Duration.ToString("0.##", CultureInfo.InvariantCulture)}}s ·
                      reference moves {{Percent(score.ReferenceMoves)}}
                      {{(score.EngineMoves == 0 ? "· <b>engine still</b>" : "")}}</span>
                  </div>
                  <img loading="lazy" src="{{image}}" alt="{{Escape(score.Block)}}">

                """);

            if (score.Refusals.Count > 0)
            {
                html.Append($"""
                      <details><summary>{score.Refusals.Count} not carried</summary><ul>
                    """);
                foreach (var refusal in score.Refusals.Take(40))
                    html.Append($"<li>{Escape(refusal)}</li>");
                if (score.Refusals.Count > 40)
                    html.Append($"<li>… and {score.Refusals.Count - 40} more</li>");
                html.Append("</ul></details>");
            }

            html.Append("</div>\n");
        }

        if (failed.Length > 0)
        {
            html.Append("<div class=\"block failed\"><div class=\"name\">Unmeasured</div><ul>");
            foreach (var f in failed)
                html.Append($"<li><b>{Escape(f.Block)}</b> — {Escape(f.Failure!)}</li>");
            html.Append("</ul></div>");
        }

        var path = Path.Combine(directory, "index.html");
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, html.ToString());
        return path;
    }

    /// <summary>Three bands, so a glance down the page finds the cliff. Not a grade: the
    /// thresholds are round numbers, chosen to separate "nearly right" from "not there".</summary>
    private static string Band(double content) => content switch
    {
        < 0.5 => "bad",
        < 0.85 => "mid",
        _ => "ok",
    };

    private static string Percent(double fraction) =>
        (fraction * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Escape(string text) => text
        .Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
