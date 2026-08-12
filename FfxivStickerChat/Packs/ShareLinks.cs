using System;
using System.Text.RegularExpressions;
using System.Web;

namespace FfxivStickerChat.Packs;

/// <summary>
/// Turns cloud-drive share links into direct download links.
/// </summary>
/// <remarks>
/// A share link opens a web page, not a file — pasting one into a downloader fetches HTML. Most services
/// have a documented direct-download form of the same URL, so the link people naturally copy is rewritten
/// into the one that actually returns bytes.
/// </remarks>
public static partial class ShareLinks
{
    /// <summary>Outcome of inspecting a link.</summary>
    /// <param name="Url">The URL to fetch. Unchanged when no rewrite applied.</param>
    /// <param name="Note">A human explanation when something was changed or is unsupported.</param>
    /// <param name="Supported">False when the host cannot serve a file to a program at all.</param>
    public sealed record Result(string Url, string Note, bool Supported = true);

    public static Result Resolve(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return new Result(url, string.Empty);

        var host = uri.Host.ToLowerInvariant();

        // Proton Drive share links are end-to-end encrypted: the decryption key lives in the URL
        // fragment, which browsers never send to the server, and decryption happens in their web app.
        // There is no direct-download form to rewrite to — this is a property of the encryption, not a
        // gap that more code would close.
        if (host.EndsWith("proton.me", StringComparison.Ordinal))
        {
            return new Result(
                url,
                "Proton Drive links can't be downloaded by a program — they're end-to-end encrypted and " +
                "only their web app can decrypt them. Use a GitHub release, Google Drive, Dropbox or " +
                "OneDrive link instead.",
                Supported: false);
        }

        if (host is "drive.google.com" or "docs.google.com")
            return ResolveGoogleDrive(uri, url);

        if (host.EndsWith("dropbox.com", StringComparison.Ordinal))
            return ResolveDropbox(uri, url);

        if (host is "1drv.ms" or "onedrive.live.com" || host.EndsWith("sharepoint.com", StringComparison.Ordinal))
            return ResolveOneDrive(uri, url);

        return new Result(url, string.Empty);
    }

    private static Result ResolveGoogleDrive(Uri uri, string original)
    {
        // Already the direct form.
        if (uri.AbsolutePath.Equals("/uc", StringComparison.OrdinalIgnoreCase))
            return new Result(original, string.Empty);

        var id = GoogleFileId().Match(uri.AbsolutePath) is { Success: true } m
            ? m.Groups[1].Value
            : HttpUtility.ParseQueryString(uri.Query).Get("id");

        if (string.IsNullOrEmpty(id))
            return new Result(original, string.Empty);

        return new Result(
            $"https://drive.google.com/uc?export=download&id={id}",
            "Rewrote the Google Drive share link to its direct download form.");
    }

    private static Result ResolveDropbox(Uri uri, string original)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);

        // dl=1 is Dropbox's documented direct-download switch; the share link ships with dl=0.
        query.Set("dl", "1");

        var builder = new UriBuilder(uri) { Query = query.ToString() };

        return new Result(
            builder.Uri.ToString(),
            "Rewrote the Dropbox share link to its direct download form.");
    }

    private static Result ResolveOneDrive(Uri uri, string original)
    {
        var query = HttpUtility.ParseQueryString(uri.Query);

        if (string.Equals(query.Get("download"), "1", StringComparison.Ordinal))
            return new Result(original, string.Empty);

        query.Set("download", "1");
        var builder = new UriBuilder(uri) { Query = query.ToString() };

        return new Result(
            builder.Uri.ToString(),
            "Rewrote the OneDrive share link to its direct download form.");
    }

    [GeneratedRegex(@"/file/d/([A-Za-z0-9_-]+)")]
    private static partial Regex GoogleFileId();
}
