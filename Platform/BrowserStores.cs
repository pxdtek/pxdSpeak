namespace pxdSpeak.Platform;

/// <summary>
/// Where the pxd Speak extension is listed. Empty until the listings are live: the "Add to Chrome / Edge" buttons stay
/// hidden while a link is empty, and "load unpacked" is shown instead.
/// </summary>
public static class BrowserStores
{
    /// <summary>e.g. https://chromewebstore.google.com/detail/pxd-speak/&lt;id&gt;</summary>
    public const string Chrome = "";

    /// <summary>e.g. https://microsoftedge.microsoft.com/addons/detail/pxd-speak/&lt;id&gt;</summary>
    public const string Edge = "";

    public static bool Listed => Chrome.Length > 0 || Edge.Length > 0;

    /// <summary>The store for the browser most people here use: Edge on Windows if listed, else Chrome; null if neither is.</summary>
    public static string? Preferred() => Edge.Length > 0 ? Edge : Chrome.Length > 0 ? Chrome : null;
}
