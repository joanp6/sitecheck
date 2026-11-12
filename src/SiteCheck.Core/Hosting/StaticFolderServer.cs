using System.Net;
using System.Net.Sockets;

namespace SiteCheck.Hosting;

/// <summary>
/// Serves a folder of built pages on this machine for as long as an audit of it lasts.
/// </summary>
/// <remarks>
/// <para>
/// The checks audit a site over HTTP, and a site that has not been published yet has no
/// address. Rather than teach every check to read files, the folder is given one: a small web
/// server on a free local port, there for the length of the run. The checks then see the site
/// as a visitor's browser will, relative links and all.
/// </para>
/// <para>
/// It answers the way static hosts (Cloudflare Pages, Netlify, GitHub Pages) do, so that what
/// passes here passes there: a folder serves its <c>index.html</c>, <c>/about</c> serves
/// <c>about.html</c>, and anything else is a 404, with the folder's own <c>404.html</c> as the
/// page when it has one.
/// </para>
/// <para>
/// It listens on the loopback address only, so nothing else on the network can reach it, and it
/// serves nothing outside the folder: an address that climbs out with <c>..</c> is a 404 like
/// any other file that is not there.
/// </para>
/// </remarks>
public sealed class StaticFolderServer : IAsyncDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8", [".htm"] = "text/html; charset=utf-8",
        [".css"] = "text/css; charset=utf-8", [".js"] = "text/javascript; charset=utf-8", [".mjs"] = "text/javascript; charset=utf-8",
        [".json"] = "application/json", [".xml"] = "application/xml", [".txt"] = "text/plain; charset=utf-8",
        [".svg"] = "image/svg+xml", [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp", [".avif"] = "image/avif", [".ico"] = "image/x-icon",
        [".woff"] = "font/woff", [".woff2"] = "font/woff2", [".pdf"] = "application/pdf",
        [".webmanifest"] = "application/manifest+json", [".mp4"] = "video/mp4", [".webm"] = "video/webm",
    };

    private readonly string _root;
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _serving;

    private StaticFolderServer(string root, HttpListener listener, Uri address)
    {
        _root = root;
        _listener = listener;
        Address = address;
        _serving = ServeAsync();
    }

    /// <summary>Where the folder is being served, such as <c>http://127.0.0.1:51234/</c>.</summary>
    public Uri Address { get; }

    /// <summary>Starts serving <paramref name="folder"/> on a free port.</summary>
    /// <exception cref="DirectoryNotFoundException"><paramref name="folder"/> does not exist.</exception>
    public static StaticFolderServer Start(string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        var root = Path.GetFullPath(folder);

        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException($"There is no folder at {root}.");
        }

        // A port can be taken between asking for a free one and listening on it, so try again.
        for (var attempt = 1; ; attempt++)
        {
            var address = new Uri($"http://127.0.0.1:{FreePort()}/");
            var listener = new HttpListener();
            listener.Prefixes.Add(address.AbsoluteUri);

            try
            {
                listener.Start();
                return new StaticFolderServer(root, listener, address);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
                listener.Close();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        _listener.Close();

        try
        {
            await _serving.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
            // Closing the listener is how the loop is told to stop.
        }

        _stopping.Dispose();
    }

    /// <summary>
    /// The file an address leads to, or <see langword="null"/> when there is none in the folder.
    /// </summary>
    /// <param name="root">The folder being served, as a full path.</param>
    /// <param name="path">The path part of the address, still percent-encoded, starting with <c>/</c>.</param>
    public static string? Resolve(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);

        string relative;

        try
        {
            relative = Uri.UnescapeDataString(path).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        }
        catch (UriFormatException)
        {
            return null;
        }

        // A null byte or a drive letter has no business in a web address.
        if (relative.Contains('\0', StringComparison.Ordinal) || Path.IsPathRooted(relative))
        {
            return null;
        }

        var boundary = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(boundary, relative));

        // GetFullPath has resolved every "..": whatever is left either sits inside the folder or
        // has climbed out of it, and only the first is served.
        if (!(full + Path.DirectorySeparatorChar).StartsWith(boundary, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (Directory.Exists(full))
        {
            full = Path.Combine(full, "index.html");
        }
        else if (!File.Exists(full) && !Path.HasExtension(full))
        {
            // /about for about.html, as static hosts serve it.
            full += ".html";
        }

        return File.Exists(full) ? full : null;
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private async Task ServeAsync()
    {
        while (!_stopping.IsCancellationRequested)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            // Not awaited: one slow file must not hold up the other requests of the same page.
            _ = AnswerAsync(context);
        }
    }

    private async Task AnswerAsync(HttpListenerContext context)
    {
        var response = context.Response;

        try
        {
            var method = context.Request.HttpMethod;

            if (method is not ("GET" or "HEAD"))
            {
                response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                return;
            }

            var file = Resolve(_root, context.Request.Url?.AbsolutePath ?? "/");

            if (file is null)
            {
                response.StatusCode = (int)HttpStatusCode.NotFound;
                file = Resolve(_root, "/404.html");

                if (file is null)
                {
                    return;
                }
            }

            response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(file), "application/octet-stream");

            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);

            await using (stream.ConfigureAwait(false))
            {
                response.ContentLength64 = stream.Length;

                if (method == "GET")
                {
                    await stream.CopyToAsync(response.OutputStream, _stopping.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpListenerException or ObjectDisposedException or OperationCanceledException or UnauthorizedAccessException)
        {
            // The client went away, or the file did. Either way there is nobody to tell.
        }
        finally
        {
            try
            {
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                // Already gone.
            }
        }
    }
}
