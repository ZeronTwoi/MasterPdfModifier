namespace PdfModifier.Core;

/// <summary>Fetching a file from the internet.</summary>
internal static class Download
{
    /// <summary>
    /// Writes what the first address that answers holds into the target file; the later addresses are tried when an
    /// earlier one cannot be reached.
    /// </summary>
    public static async Task ToFileAsync(IReadOnlyList<string> sources, string target, IProgress<double>? progress = null,
        CancellationToken cancel = default)
    {
        var partial = target + ".part";
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await client.GetAsync(sources[attempt], HttpCompletionOption.ResponseHeadersRead, cancel);
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var input = await response.Content.ReadAsStreamAsync(cancel);
                await using var output = File.Create(partial);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancel)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancel);
                    done += read;
                    if (total is > 0) progress?.Report((double)done / total.Value);
                }

                break;
            }
            catch (Exception e) when (e is HttpRequestException or IOException && attempt + 1 < sources.Count)
            {
                // Fall through to the next source.
            }
        }

        // Written under a temporary name so an interrupted download never looks like a complete file.
        File.Move(partial, target, true);
        progress?.Report(1);
    }
}
