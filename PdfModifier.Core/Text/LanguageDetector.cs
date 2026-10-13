using System.Text;

namespace PdfModifier.Core.Text;

/// <summary>
/// Identifies which of the supported languages a text is written in. The script settles Russian and Japanese;
/// the Latin-script languages are told apart by their most frequent words and their characteristic letters.
/// </summary>
public static class LanguageDetector
{
    private static readonly Dictionary<string, HashSet<string>> CommonWords = new()
    {
        ["en"] = Words("the of and to in is that it was for on are as with his they at be this have from or one had by but not what all were we when your can said there use an each which she do how their if will"),
        ["es"] = Words("de la que el en y a los del se las por un para con no una su al lo como más pero sus le ya o este sí porque esta entre cuando muy sin sobre también me hasta hay donde quien desde todo nos durante todos uno les ni contra otros ese eso ante ellos esto"),
        ["de"] = Words("der die und in den von zu das mit sich des auf für ist im dem nicht ein eine als auch es an werden aus er hat dass sie nach wird bei einer um am sind noch wie einem über einen so zum war haben nur oder aber vor zur bis mehr durch man sein wurde"),
        ["fr"] = Words("de la le et les des en un du une que est pour qui dans a par plus pas au sur ne se ce il sont la les mais comme ou si leur y dont elle ont cette été aux avec ses tout nous sa fait son aussi être ces deux même très où sans peut entre"),
        ["pt"] = Words("de a o que e do da em um para é com não uma os no se na por mais as dos como mas foi ao ele das tem à seu sua ou ser quando muito há nos já está eu também só pelo pela até isso ela entre era depois sem mesmo aos ter seus quem nas me esse eles estão você"),
        ["tr"] = Words("bir ve bu da de için ile olarak çok daha gibi en ne o ama kadar olan var ya ise her sonra ki değil veya göre ancak yok ben sen biz onlar şu mi mı mu mü ilk iki üç büyük yeni tarafından üzerinde içinde arasında olduğu olduğunu etmek yapılan türkiye")
    };

    // Letters that are common in one language and rare or absent in the others.
    private static readonly Dictionary<string, string> Signature = new()
    {
        ["es"] = "ñ¿¡",
        ["de"] = "ßäöü",
        ["fr"] = "èêëàâçùûœîï",
        ["pt"] = "ãõçâêôà",
        ["tr"] = "ğışİçöü"
    };

    /// <summary>
    /// Scores the supported languages for a text, best first, with scores normalised so the best is 1.
    /// Languages with no evidence are left out. <paramref name="candidates"/> restricts the answer to those codes.
    /// </summary>
    public static IReadOnlyList<(string Code, double Score)> Detect(string? text, IEnumerable<string>? candidates = null)
    {
        var allowed = (candidates ?? Languages.All.Select(l => l.Code)).Select(c => c.ToLowerInvariant()).ToHashSet();
        var scores = new Dictionary<string, double>();
        if (string.IsNullOrWhiteSpace(text)) return [];

        int cyrillic = 0, japanese = 0, kana = 0, latin = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var v = rune.Value;
            if (v is >= 0x0400 and <= 0x04FF) cyrillic++;
            else if (v is >= 0x3040 and <= 0x30FF)
            {
                kana++;
                japanese++;
            }
            else if (v is (>= 0x4E00 and <= 0x9FFF) or (>= 0xFF66 and <= 0xFF9F)) japanese++;
            else if (Rune.IsLetter(rune) && v < 0x250) latin++;
        }

        var letters = cyrillic + japanese + latin;
        if (letters == 0) return [];
        if (cyrillic > 0) scores["ru"] = (double)cyrillic / letters;
        // Han characters alone could be Chinese, but Japanese is the only CJK language on offer.
        if (japanese > 0) scores["ja"] = (double)japanese / letters * (kana > 0 ? 1 : 0.8);

        if (latin > 0)
        {
            var words = text.ToLowerInvariant()
                .Split(text.Where(c => !char.IsLetter(c)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries)
                .Where(w => w[0] < 0x250)
                .ToArray();
            var raw = new Dictionary<string, double>();
            foreach (var (code, common) in CommonWords)
            {
                if (!allowed.Contains(code)) continue;
                double hits = words.Count(common.Contains);
                var signature = Signature.TryGetValue(code, out var chars) ? text.Count(c => chars.Contains(c)) : 0;
                var score = (words.Length == 0 ? 0 : hits / words.Length) + Math.Min(0.25, signature * 4.0 / latin);
                if (score > 0) raw[code] = score;
            }

            // The Latin-script languages share the Latin part of the text. Related languages (Spanish/Portuguese) have
            // many words in common, so anything clearly behind the best match is demoted rather than reported as present.
            var latinShare = (double)latin / letters;
            var bestRaw = raw.Count == 0 ? 0 : raw.Values.Max();
            foreach (var (code, score) in raw)
            {
                var relative = score / bestRaw;
                scores[code] = latinShare * (relative >= 0.7 ? relative : relative * 0.5);
            }
        }

        var ranked = scores.Where(s => allowed.Contains(s.Key) && s.Value > 0).OrderByDescending(s => s.Value).ToList();
        if (ranked.Count == 0) return [];
        var best = ranked[0].Value;
        return ranked.Select(s => (s.Key, s.Value / best)).ToList();
    }

    /// <summary>The single most likely language, or null when the text gives no evidence.</summary>
    public static string? DetectBest(string? text, IEnumerable<string>? candidates = null) =>
        Detect(text, candidates) is { Count: > 0 } ranked ? ranked[0].Code : null;

    private static HashSet<string> Words(string list) => [..list.Split(' ')];
}
