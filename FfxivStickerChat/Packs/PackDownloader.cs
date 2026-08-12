using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace FfxivStickerChat.Packs;

/// <summary>
/// Fetches a sticker pack zip from a URL.
/// </summary>
/// <remarks>
/// <para>
/// The URL is untrusted: it is typed by the user or, once pack sync exists, advertised by another player.
/// So the fetch is constrained rather than convenient — HTTPS only, redirects followed manually and
/// re-checked at every hop, private and loopback addresses refused, a hard byte ceiling enforced while
/// streaming, and the payload confirmed to be a zip before anything looks inside it.
/// </para>
/// <para>
/// Passing those checks only gets the bytes to <see cref="PackArchive.Import"/>, which independently
/// re-validates every entry. This class decides whether we are willing to fetch something; that one
/// decides whether we are willing to keep it.
/// </para>
/// </remarks>
public static class PackDownloader
{
    /// <summary>Largest archive accepted over the network.</summary>
    public static readonly long MaxDownloadBytes = PackArchive.MaxTotalBytes;

    private const int MaxRedirects = 5;

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    /// <summary>Downloads and imports a pack, returning the same result shape as a local import.</summary>
    /// <param name="expectedHash">
    /// SHA-256 of the archive, lowercase hex, or null to skip the check. Supplied by pack sync so a
    /// tampered or truncated download is rejected rather than imported.
    /// </param>
    public static async Task<PackTransferResult> DownloadAndImportAsync(
        string url,
        PackStore store,
        string? expectedHash = null,
        CancellationToken cancellationToken = default)
    {
        // A share link points at a web page, not a file. Rewrite it to the direct form before anything
        // else, so the URL people naturally copy actually works.
        var resolved = ShareLinks.Resolve(url);

        if (!resolved.Supported)
            return new PackTransferResult(false, resolved.Note);

        if (!string.IsNullOrEmpty(resolved.Note))
            Services.Log.Information(resolved.Note);

        if (!TryValidateUrl(resolved.Url, out var uri, out var reason))
            return new PackTransferResult(false, reason);

        string? tempPath = null;

        try
        {
            var bytes = await FetchAsync(uri, cancellationToken).ConfigureAwait(false);

            if (bytes.Length < 4 || bytes[0] != 'P' || bytes[1] != 'K' || bytes[2] != 0x03 || bytes[3] != 0x04)
            {
                // Even after rewriting, a link can land on a sign-in wall or a "file too large to scan"
                // page. Saying which is far more useful than the importer's "not a readable zip".
                var looksLikeHtml = bytes.Length > 1 && bytes[0] == '<';

                return new PackTransferResult(false, looksLikeHtml
                    ? "That URL returned a web page, not a zip — the file is probably not public, or " +
                      "needs sign-in."
                    : "That URL did not return a zip.");
            }

            if (!string.IsNullOrEmpty(expectedHash))
            {
                var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                    return new PackTransferResult(false, "Downloaded file does not match its expected hash.");
            }

            tempPath = Path.Combine(Path.GetTempPath(), $"stickerpack_{Guid.NewGuid():N}.zip");
            await File.WriteAllBytesAsync(tempPath, bytes, cancellationToken).ConfigureAwait(false);

            var result = PackArchive.Import(tempPath, store);

            if (result.Success && result.Pack is not null)
            {
                // Remember where it came from so it can be refreshed later without re-entering the URL.
                result.Pack.SourceUrl = url;
                store.Save(result.Pack);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            return new PackTransferResult(false, "Download cancelled.");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Pack download failed for {url}");
            return new PackTransferResult(false, $"Download failed: {ex.Message}");
        }
        finally
        {
            if (tempPath is not null && File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception ex)
                {
                    Services.Log.Warning(ex, $"Could not remove {tempPath}");
                }
            }
        }
    }

    /// <summary>Rejects anything that is not a plain HTTPS URL.</summary>
    public static bool TryValidateUrl(string url, out Uri uri, out string reason)
    {
        uri = null!;
        reason = string.Empty;

        if (string.IsNullOrWhiteSpace(url))
        {
            reason = "No URL given.";
            return false;
        }

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed))
        {
            reason = "That is not a valid URL.";
            return false;
        }

        if (!parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            reason = "Only https URLs are allowed.";
            return false;
        }

        uri = parsed;
        return true;
    }

    private static async Task<byte[]> FetchAsync(Uri uri, CancellationToken cancellationToken)
    {
        // Redirects are followed by hand so every hop gets the same scrutiny as the first. Letting
        // HttpClient follow them automatically would allow a public URL to bounce to a private address.
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var client = new HttpClient(handler) { Timeout = Timeout };

        // Deliberately generic. Naming the plugin would tell every host a pack is fetched from that the
        // requester runs a third-party FFXIV addon, which is not theirs to learn.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        var current = uri;

        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            await EnsureHostIsPublicAsync(current, cancellationToken).ConfigureAwait(false);

            using var response = await client
                .GetAsync(current, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location
                    ?? throw new InvalidOperationException("Redirect without a destination.");

                current = location.IsAbsoluteUri ? location : new Uri(current, location);

                if (!current.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Redirected to a non-https URL.");

                continue;
            }

            response.EnsureSuccessStatusCode();

            // Trust the declared length only to fail early; the real limit is enforced while reading.
            if (response.Content.Headers.ContentLength is > 0 and var declared &&
                declared > MaxDownloadBytes)
            {
                throw new InvalidOperationException(
                    $"Archive is {declared / (1024 * 1024)} MB; the limit is {MaxDownloadBytes / (1024 * 1024)} MB.");
            }

            return await ReadCappedAsync(response, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("Too many redirects.");
    }

    private static async Task<byte[]> ReadCappedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();

        var chunk = new byte[81920];
        int read;

        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            buffer.Write(chunk, 0, read);

            // A server can lie about Content-Length, so the ceiling is enforced against what arrives.
            if (buffer.Length > MaxDownloadBytes)
            {
                throw new InvalidOperationException(
                    $"Archive exceeds the {MaxDownloadBytes / (1024 * 1024)} MB limit.");
            }
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Refuses hosts that resolve to a loopback, private or link-local address.
    /// </summary>
    /// <remarks>
    /// Without this a shared pack URL could point at something on the importer's own machine or network.
    /// </remarks>
    private static async Task EnsureHostIsPublicAsync(Uri uri, CancellationToken cancellationToken)
    {
        IPAddress[] addresses;

        try
        {
            addresses = await Dns.GetHostAddressesAsync(uri.Host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Could not resolve {uri.Host}.", ex);
        }

        if (addresses.Length == 0)
            throw new InvalidOperationException($"Could not resolve {uri.Host}.");

        if (addresses.Any(IsPrivate))
            throw new InvalidOperationException($"{uri.Host} resolves to a private address.");
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
            return true;

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6UniqueLocal;

        var b = address.GetAddressBytes();

        return b[0] switch
        {
            10 => true,                                  // 10.0.0.0/8
            127 => true,                                 // loopback
            169 when b[1] == 254 => true,                // link-local
            172 when b[1] >= 16 && b[1] <= 31 => true,   // 172.16.0.0/12
            192 when b[1] == 168 => true,                // 192.168.0.0/16
            0 => true,                                   // this network
            _ => false,
        };
    }

    private static bool IsRedirect(HttpStatusCode status) => status is
        HttpStatusCode.MovedPermanently or
        HttpStatusCode.Found or
        HttpStatusCode.SeeOther or
        HttpStatusCode.TemporaryRedirect or
        HttpStatusCode.PermanentRedirect;
}
