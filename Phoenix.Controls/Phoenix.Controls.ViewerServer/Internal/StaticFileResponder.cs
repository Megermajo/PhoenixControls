using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading.Tasks;

namespace Phoenix.Controls.ViewerServer.Internal;

/// <summary>
/// Serves the static web bundle (HTML / CSS / JS / fonts / phoenix-mark)
/// from <see cref="ViewerServerOptions.AssetsRoot"/>. Path traversal is
/// blocked by canonicalising the resolved path and checking it stays
/// inside the root before any IO happens.
/// </summary>
internal static class StaticFileResponder
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"]  = "text/html; charset=utf-8",
        [".css"]  = "text/css; charset=utf-8",
        [".js"]   = "application/javascript; charset=utf-8",
        [".mjs"]  = "application/javascript; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".svg"]  = "image/svg+xml",
        [".png"]  = "image/png",
        [".jpg"]  = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"]  = "image/gif",
        [".webp"] = "image/webp",
        [".ico"]  = "image/x-icon",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".ttf"]  = "font/ttf",
        [".otf"]  = "font/otf",
    };

    public static async Task ServeAsync(HttpListenerResponse resp, string assetsRoot, string relativePath)
    {
        // Block traversal: only allow forward-slash relative paths, no
        // ..\, no rooted segments, no drive letters.
        if (string.IsNullOrEmpty(relativePath) ||
            relativePath.Contains("..", StringComparison.Ordinal) ||
            Path.IsPathRooted(relativePath))
        {
            await WriteStatusAsync(resp, HttpStatusCode.BadRequest, "bad path").ConfigureAwait(false);
            return;
        }

        string fullRoot = Path.GetFullPath(assetsRoot);
        string candidate = Path.GetFullPath(Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            await WriteStatusAsync(resp, HttpStatusCode.BadRequest, "bad path").ConfigureAwait(false);
            return;
        }

        if (!File.Exists(candidate))
        {
            await WriteStatusAsync(resp, HttpStatusCode.NotFound, "not found").ConfigureAwait(false);
            return;
        }

        string ext = Path.GetExtension(candidate);
        resp.ContentType = ContentTypes.TryGetValue(ext, out var ct) ? ct : "application/octet-stream";
        resp.StatusCode = (int)HttpStatusCode.OK;

        // Cache-Control kept conservative: the dev iteration loop
        // benefits from no-cache, and a static viewer bundle is
        // measured in tens of KB so revalidation cost is negligible.
        resp.AddHeader("Cache-Control", "no-cache");

        // Defence-in-depth headers for the HTML shell (QC27-04).
        // The viewer SPA is fully self-hosted — no third-party
        // scripts, fonts, or styles — so a strict policy holds.
        // Verified before locking down: WebViewer/dist contains no
        // inline <style>, no style="…" attributes, no element.style.*
        // assignments, so 'unsafe-inline' is NOT required for styles.
        // Connect-src includes ws/wss so the bearer-subprotocol
        // WebSocket to /v/ws on this same origin can open.
        if (string.Equals(ext, ".html", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ext, ".htm",  StringComparison.OrdinalIgnoreCase))
        {
            resp.AddHeader("Content-Security-Policy",
                "default-src 'self'; " +
                "script-src 'self'; " +
                "style-src 'self'; " +
                "img-src 'self' data:; " +
                "font-src 'self'; " +
                "connect-src 'self' ws: wss:; " +
                "object-src 'none'; " +
                "base-uri 'self'; " +
                "form-action 'self'; " +
                "frame-ancestors 'none'");
            resp.AddHeader("X-Content-Type-Options", "nosniff");
            resp.AddHeader("Referrer-Policy", "no-referrer");
            resp.AddHeader("X-Frame-Options", "DENY");
        }

        using var fs = File.OpenRead(candidate);
        resp.ContentLength64 = fs.Length;
        await fs.CopyToAsync(resp.OutputStream).ConfigureAwait(false);
    }

    public static async Task WriteStatusAsync(HttpListenerResponse resp, HttpStatusCode status, string body)
    {
        resp.StatusCode = (int)status;
        resp.ContentType = "text/plain; charset=utf-8";
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        resp.ContentLength64 = bytes.Length;
        await resp.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
    }
}
