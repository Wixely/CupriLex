using System.Net.Http.Headers;

namespace CupriLex.Compiler;

/// <summary>What came back, or why nothing did.</summary>
public sealed record Fetched(byte[] Bytes, string? ContentType)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Bytes);
}

/// <summary>
/// How a host makes a request that consent has already allowed.
///
/// <para>An interface rather than a hard-wired <c>HttpClient</c> so a host can route through its
/// own stack - a proxy, a cache, an offline mirror, a test that answers from memory - and so this
/// library is inert by construction: given no fetcher it cannot reach anything, whatever a consent
/// object says.</para>
/// </summary>
public interface IFetch
{
    Task<Fetched?> GetAsync(string url, CancellationToken cancel = default);
}

/// <summary>
/// The ordinary one: an <c>HttpClient</c>, with the two headers a font service needs.
///
/// <para>The user agent is not decoration. Google Fonts serves a DIFFERENT stylesheet depending on
/// what asks: an old browser string gets TTF, a modern one gets WOFF 2. Asking as something modern
/// is how the smaller, better-supported file arrives, and CupriFace has read WOFF 2 since
/// 0.28.1.</para>
/// </summary>
public sealed class Http : IFetch, IDisposable
{
    /// <summary>A current Chrome on Windows, which is what gets WOFF 2 out of a font service.
    /// Stated rather than sniffed: the string decides the format of the reply, so it is part of
    /// what this class does and not an implementation detail.</summary>
    private const string Agent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) "
        + "Chrome/131.0.0.0 Safari/537.36";

    private readonly HttpClient _client;
    private readonly bool _owned;

    public Http(HttpClient? client = null, TimeSpan? timeout = null)
    {
        _owned = client is null;
        _client = client ?? new HttpClient();

        if (_owned) _client.Timeout = timeout ?? TimeSpan.FromSeconds(30);

        if (!_client.DefaultRequestHeaders.UserAgent.TryParseAdd(Agent))
            _client.DefaultRequestHeaders.Add("User-Agent", Agent);
    }

    public async Task<Fetched?> GetAsync(string url, CancellationToken cancel = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));

            using var response = await _client.SendAsync(request, cancel);
            if (!response.IsSuccessStatusCode) return null;

            return new Fetched(
                await response.Content.ReadAsByteArrayAsync(cancel),
                response.Content.Headers.ContentType?.MediaType);
        }
        catch (Exception)
        {
            // A request that fails is a font the package does not carry, which the report already
            // knows how to say. It is never a reason to fail a translation.
            return null;
        }
    }

    public void Dispose()
    {
        if (_owned) _client.Dispose();
    }
}
