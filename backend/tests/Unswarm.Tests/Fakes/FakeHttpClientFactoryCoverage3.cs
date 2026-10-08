using Unswarm.Core.Contracts;

namespace Unswarm.Tests.Fakes;

/// <summary>
/// HttpClient factory backed by a scriptable handler for ChatGPT-subscription
/// forwarding tests. <see cref="Handler"/> returns a response synchronously;
/// <see cref="HandlerAsync"/> supports throwing TaskCanceled/HttpRequest
/// exceptions from inside the async pipeline.
/// </summary>
public sealed class FakeHttpClientFactoryCoverage3 : IHttpClientFactory
{
    public Func<HttpRequestMessage, HttpResponseMessage>? Handler { get; set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? HandlerAsync { get; set; }

    public List<HttpRequestMessage> Requests { get; } = [];

    public HttpClient CreateClient(string name)
    {
        var messageHandler = new ScriptedHandler(this);
        return new HttpClient(messageHandler, disposeHandler: false);
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly FakeHttpClientFactoryCoverage3 _factory;

        public ScriptedHandler(FakeHttpClientFactoryCoverage3 factory) => _factory = factory;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _factory.Requests.Add(request);
            if (_factory.HandlerAsync is not null)
                return _factory.HandlerAsync(request, cancellationToken);
            return Task.FromResult(_factory.Handler!(request));
        }
    }
}
