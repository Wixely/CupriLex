namespace CupriLex.Compiler;

/// <summary>
/// Who decides whether a request off this machine is made.
///
/// <para><b>The library never asks and never assumes.</b> A host - a CLI that prompts, a settings
/// screen with "allow all", a CI job with a fixed allow-list - answers for every URL, including
/// the ones found only after an approved stylesheet came back. Denying is always a valid answer
/// and never an error: a package built without a font is a package that says so in its report.</para>
///
/// <para>Per URL rather than per run, because a font stylesheet reveals further requests to
/// another host when it is fetched. A person who approved "a stylesheet from fonts.googleapis.com"
/// has not thereby approved the files it turns out to name, and a consent model that could not
/// tell those apart would be approving things nobody was shown.</para>
/// </summary>
public interface IConsent
{
    /// <summary>Whether this request may be made.</summary>
    bool Allows(Request request);
}

/// <summary>The decisions a host can make without writing one.</summary>
public static class Consent
{
    /// <summary>Nothing leaves this machine. The default everywhere: a tool that fetches unless
    /// told not to has already made the decision for whoever is running it.</summary>
    public static IConsent None { get; } = new Nothing();

    /// <summary>Every request, including the ones discovered on the way. For "--download all", for
    /// a settings box that says so, and for a test.</summary>
    public static IConsent All { get; } = new Everything();

    /// <summary>Every request to these hosts, and nothing else. The shape a settings screen wants:
    /// a person approves a SITE once rather than a list of URLs they cannot evaluate.</summary>
    public static IConsent Hosts(params string[] hosts) =>
        new ByHost(new HashSet<string>(hosts, StringComparer.OrdinalIgnoreCase));

    /// <summary>Exactly these URLs, and anything discovered from them on the same hosts. The shape
    /// a prompt wants: the person saw a list and ticked some of it.</summary>
    public static IConsent Urls(IEnumerable<string> urls)
    {
        var chosen = new HashSet<string>(urls, StringComparer.OrdinalIgnoreCase);
        var hosts = chosen
            .Select(u => Uri.TryCreate(u, UriKind.Absolute, out var uri) ? uri.Host : null)
            .Where(h => h is { Length: > 0 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        return new ByUrl(chosen, hosts!);
    }

    private sealed class Nothing : IConsent
    {
        public bool Allows(Request request) => false;
    }

    private sealed class Everything : IConsent
    {
        public bool Allows(Request request) => true;
    }

    private sealed class ByHost(HashSet<string> hosts) : IConsent
    {
        public bool Allows(Request request) => hosts.Contains(request.Host);
    }

    private sealed class ByUrl(HashSet<string> urls, HashSet<string> hosts) : IConsent
    {
        // A URL the person picked, or one found by fetching it. The second half matters: a
        // stylesheet is approved in order to get the fonts it names, and approving it while
        // refusing its contents would fetch something and then throw it away.
        public bool Allows(Request request) =>
            urls.Contains(request.Url) || (request.Discovered && hosts.Contains(request.Host));
    }
}
