using System.Net;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace DotnetFleet.Api.Client;

/// <summary>
/// HTTP message handler that surfaces 401 Unauthorized responses as a reactive signal.
/// Lets the GUI bounce the user back to login when a stored token has become invalid
/// (e.g. server secret rotated, token expired).
/// </summary>
public sealed class UnauthorizedHandler : DelegatingHandler
{
    private readonly Subject<Unit> _unauthorized = new();

    public UnauthorizedHandler() : base(new HttpClientHandler())
    {
    }

    public IObservable<Unit> Unauthorized => _unauthorized.AsObservable();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            _unauthorized.OnNext(Unit.Default);
        }
        return response;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _unauthorized.Dispose();
        }
        base.Dispose(disposing);
    }
}
