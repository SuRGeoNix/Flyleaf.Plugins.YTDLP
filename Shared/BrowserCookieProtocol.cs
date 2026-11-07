namespace Flyleaf.BrowserCookies;

internal static class BrowserCookieProtocol
{
    internal const int Version = 3;
    internal const string HostName = "com.flyleaf.browsercookies";
    internal const string ExtensionOrigin = "chrome-extension://eobcoanhdnhafpehnflliekjejifliop/";
    internal static string PipePrefix => "Flyleaf.BrowserCookies.v" + Version + ".";
}
