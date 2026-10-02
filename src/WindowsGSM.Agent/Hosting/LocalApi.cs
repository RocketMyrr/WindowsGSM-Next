using System.Net.Http.Headers;
using WindowsGSM.Agent.Api;
using WindowsGSM.Agent.Security;

namespace WindowsGSM.Agent.Hosting;

/// <summary>
/// The agent's own API, reached over its internal loopback listener. Requests carry the per-process secret and
/// the user to act as, so they go through the normal routes, permission checks, audit log and — on a hub —
/// forwarding to other machines. Used by in-process features that act for someone (the Discord bot).
/// </summary>
public sealed class LocalApi
{
    private readonly AgentContext _ctx;
    private readonly Func<HttpClient> _client;

    public LocalApi(AgentContext ctx, Func<HttpClient> client)
    {
        _ctx = ctx;
        _client = client;
    }

    public HttpClient Client() => _client();

    /// <summary>A request to /api/v2/… acting as <paramref name="user"/> ("via" shows up in the audit log).</summary>
    public HttpRequestMessage Request(HttpMethod method, string path, AgentUser user, string via, object? json = null)
    {
        var req = new HttpRequestMessage(method, "api/v2" + path);
        req.Headers.Add(AgentContext.InternalHeader, _ctx.InternalSecret);
        req.Headers.Add(AgentContext.DelegateHeader, Hub.Delegation.EncodeAs(user, via));
        req.Headers.Add("X-WGSM-CSRF", "1");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (json != null) { req.Content = JsonContent.Create(json); }
        return req;
    }
}
