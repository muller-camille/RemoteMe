using System.Security.Cryptography;

namespace RemoteMe.Web.Http;

/// <summary>
/// Identité anonyme du visiteur : un jeton aléatoire de 256 bits dans un cookie HttpOnly.
/// Pas de compte ni de mot de passe : le cookie suffit à retrouver « son » environnement.
/// </summary>
public static class VisitorCookie
{
    public const string Name = "RemoteMe.Visitor";

    public static string? Read(HttpRequest request)
    {
        var value = request.Cookies[Name];
        return IsWellFormed(value) ? value : null;
    }

    /// <summary>Renvoie l'identifiant du visiteur, en lui en créant un s'il n'en a pas.</summary>
    public static string Ensure(HttpContext context)
    {
        var existing = Read(context.Request);
        if (existing is not null)
        {
            return existing;
        }

        var created = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        context.Response.Cookies.Append(Name, created, new CookieOptions
        {
            HttpOnly = true,
            Secure = context.Request.IsHttps,
            SameSite = SameSiteMode.Lax,
            IsEssential = true,
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
        });
        return created;
    }

    public static void Delete(HttpContext context) =>
        context.Response.Cookies.Delete(Name, new CookieOptions
        {
            Path = context.Request.PathBase.HasValue ? context.Request.PathBase.Value : "/",
        });

    private static bool IsWellFormed(string? value) =>
        value is { Length: 43 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
