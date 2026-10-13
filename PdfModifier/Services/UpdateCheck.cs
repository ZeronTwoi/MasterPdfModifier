using System.Text.RegularExpressions;

namespace PdfModifier.Services;

/// <summary>
/// Looks whether a newer version of the app has been published. It does so once, when the app has started, and only
/// for a user who said they want to be told; for anyone else nothing here ever goes near the network.
///
/// The look is as small as one can be: the address of the newest version is asked for its headers only. GitHub
/// answers with a redirect to the page of that version, whose address ends in its number, and no page is fetched at
/// all. Nothing is sent but the request itself, and whatever goes wrong is not worth a word to the user: no news is
/// then simply no news.
/// </summary>
public sealed partial class UpdateCheck
{
    private bool _looked;
    private string? _found;

    /// <summary>
    /// The page of the newer version that was found, with what is new in it and its downloads. It is where GitHub
    /// pointed, so it stays right when the project moves.
    /// </summary>
    public string? Page { get; private set; }

    /// <summary>
    /// Asks an address for its headers and returns where it redirects to, or null when it does not. The tests put
    /// their own answers here; this is also where they count that nothing was asked.
    /// </summary>
    internal Func<string, CancellationToken, Task<string?>> Redirect { get; set; } = RedirectOf;

    /// <summary>The number of a version newer than <paramref name="current"/>, if one has been published.</summary>
    public async Task<string?> LookOnce(Settings settings, string current, CancellationToken cancel = default)
    {
        // Not wanted, or not decided yet: no connection is tried.
        if (settings.UpdateNotice != true) return null;
        if (_looked) return _found;
        _looked = true;
        try
        {
            // A repository that was renamed passes the question on once or twice before the answer comes.
            var address = AppInfo.LatestVersionPage;
            for (var hop = 0; hop < 3; hop++)
            {
                if (await Redirect(address, cancel) is not { } target || !target.StartsWith("https://github.com/", StringComparison.Ordinal)) break;
                if (VersionIn(target) is { } version)
                {
                    if (IsNewer(version, current)) (_found, Page) = (version, target);
                    break;
                }

                address = target;
            }
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException or UriFormatException)
        {
            // Offline, or GitHub out of reach: there is nothing to tell.
        }

        return _found;
    }

    /// <summary>The version an address of the form …/releases/tag/v1.2.3 names.</summary>
    internal static string? VersionIn(string address) =>
        TagAddress().Match(address) is { Success: true } match ? match.Groups[1].Value : null;

    internal static bool IsNewer(string found, string current) =>
        Version.TryParse(found, out var theirs) && Version.TryParse(current, out var ours) && theirs > ours;

    private static async Task<string?> RedirectOf(string address, CancellationToken cancel)
    {
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Head, address);
        request.Headers.UserAgent.ParseAdd("MasterPdfModifier/" + AppInfo.Version);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is not { } location) return null;
        return (location.IsAbsoluteUri ? location : new Uri(new Uri(address), location)).ToString();
    }

    [GeneratedRegex(@"/releases/tag/v?(\d+(?:\.\d+){1,3})$")]
    private static partial Regex TagAddress();
}
