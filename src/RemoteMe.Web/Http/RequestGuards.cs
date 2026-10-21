namespace RemoteMe.Web.Http;

public static class RequestGuards
{
    /// <summary>
    /// Refuse les requêtes émises par une autre origine (WebSocket et POST) : sans cela, une page
    /// tierce pourrait ouvrir le tunnel avec le cookie du visiteur.
    /// </summary>
    public static bool IsSameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin))
        {
            // Pas d'en-tête Origin : requête hors navigateur, qui ne peut pas porter le cookie d'un tiers.
            return true;
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            && string.Equals(uri.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}
