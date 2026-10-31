using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Requests.Web;

/// <summary>
/// Adds the client script tag to jellyfin-web's index.html as it is served.
/// Jellyfin 12 has no script-injection hook, and rewriting index.html on disk fails on
/// read-only installs and is undone by web updates, so this runs as middleware instead.
/// Any failure serves the original page untouched.
/// </summary>
public sealed class ScriptInjectionStartupFilter : IStartupFilter
{
    private const string Marker = "/" + Plugin.RoutePrefix + "/client.js";

    private readonly ILogger<ScriptInjectionStartupFilter> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="ScriptInjectionStartupFilter"/> class.
    /// </summary>
    /// <param name="logger">Logger.</param>
    public ScriptInjectionStartupFilter(ILogger<ScriptInjectionStartupFilter> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InvokeAsync);
            next(app);
        };
    }

    private async Task InvokeAsync(HttpContext context, Func<Task> next)
    {
        if (!HttpMethods.IsGet(context.Request.Method) || !IsIndexRequest(context.Request.Path.Value) || Plugin.Instance is not { } plugin)
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Ask for a plain, complete body we can rewrite.
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("Range");
        context.Request.Headers.Remove("If-Range");

        var originalBody = context.Response.Body;
        using var buffer = new MemoryStream();
        context.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Seek(0, SeekOrigin.Begin);
        var isHtml = context.Response.StatusCode == StatusCodes.Status200OK
            && (context.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false);
        if (!isHtml)
        {
            await buffer.CopyToAsync(originalBody).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        try
        {
            var bodyClose = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyClose >= 0 && !html.Contains(Marker, StringComparison.OrdinalIgnoreCase))
            {
                var label = WebUtility.HtmlEncode(plugin.Configuration.NavLabel);
                var tag = $"<script plugin=\"JellyfinRequests\" data-label=\"{label}\" src=\"..{Marker}?v={plugin.ScriptCacheKey}\" defer></script>";
                html = string.Concat(html.AsSpan(0, bodyClose), tag, "\n", html.AsSpan(bodyClose));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Script injection failed; serving index.html unchanged");
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.Headers.Remove("Accept-Ranges");
        await originalBody.WriteAsync(bytes).ConfigureAwait(false);
    }

    // EndsWith keeps this working under a base URL such as /jellyfin/web/.
    private static bool IsIndexRequest(string? path)
        => path is not null
            && (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/web", StringComparison.OrdinalIgnoreCase));
}
