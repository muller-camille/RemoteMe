using System.Globalization;
using System.Text.RegularExpressions;

namespace RemoteMe.Web.Guacamole;

/// <summary>
/// Caractéristiques d'affichage envoyées par le navigateur. Ce sont les seules valeurs que le
/// navigateur peut influencer : serveur, compte et programme sont fixés côté serveur.
/// </summary>
public sealed partial class ClientInfo
{
    private static readonly string[] AllowedAudio = { "audio/L8", "audio/L16" };
    private static readonly string[] AllowedImages = { "image/png", "image/jpeg", "image/webp" };

    public int Width { get; init; } = 1024;

    public int Height { get; init; } = 768;

    public int Dpi { get; init; } = 96;

    public string? Timezone { get; init; }

    public IReadOnlyList<string> AudioMimetypes { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> ImageMimetypes { get; init; } = new[] { "image/png", "image/jpeg" };

    public static ClientInfo FromQuery(IQueryCollection query)
    {
        var images = Allowed(query["image"], AllowedImages);
        var timezone = query["tz"].FirstOrDefault();

        return new ClientInfo
        {
            Width = ParseClamped(query["width"].FirstOrDefault(), 1024, 320, 7680),
            Height = ParseClamped(query["height"].FirstOrDefault(), 768, 240, 4320),
            Dpi = ParseClamped(query["dpi"].FirstOrDefault(), 96, 48, 480),
            Timezone = timezone is not null && TimezonePattern().IsMatch(timezone) ? timezone : null,
            AudioMimetypes = Allowed(query["audio"], AllowedAudio),
            ImageMimetypes = images.Length > 0 ? images : new[] { "image/png", "image/jpeg" },
        };
    }

    private static string[] Allowed(IEnumerable<string?> requested, string[] allowed) =>
        allowed.Where(type => requested.Contains(type, StringComparer.Ordinal)).ToArray();

    private static int ParseClamped(string? value, int fallback, int min, int max) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            ? Math.Clamp(parsed, min, max)
            : fallback;

    [GeneratedRegex("^[A-Za-z0-9_+\\-]+(/[A-Za-z0-9_+\\-]+){0,2}$")]
    private static partial Regex TimezonePattern();
}
