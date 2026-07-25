using System.Net;

namespace LoLCompanion.Core.Lcu;

public sealed class LcuHttpClientFactory
{
    private readonly TimeSpan _timeout;
    private readonly Func<LcuCredential, HttpMessageHandler>? _handlerFactory;

    public LcuHttpClientFactory(TimeSpan timeout, Func<LcuCredential, HttpMessageHandler>? handlerFactory = null)
    {
        _timeout = timeout;
        _handlerFactory = handlerFactory;
    }

    public HttpClient Create(LcuCredential credential)
    {
        if (!IsLoopbackHost(credential.Host))
        {
            throw new LcuException("loopback_only", "LCU host must be loopback.", isRecoverable: false);
        }

        var handler = _handlerFactory?.Invoke(credential) ?? CreateDefaultHandler(credential);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            BaseAddress = new Uri($"{credential.Protocol}://{credential.Host}:{credential.Port}/"),
            Timeout = _timeout
        };
        client.DefaultRequestHeaders.Authorization = credential.CreateAuthorizationHeader();
        return client;
    }

    private static HttpClientHandler CreateDefaultHandler(LcuCredential credential)
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                IsTrustedLoopbackUri(request.RequestUri, credential)
        };
    }

    public static bool IsTrustedLoopbackUri(Uri? uri, LcuCredential credential)
    {
        return uri is not null &&
               string.Equals(uri.Scheme, "https", StringComparison.OrdinalIgnoreCase) &&
               uri.Port == credential.Port &&
               IsLoopbackHost(uri.Host);
    }

    private static bool IsLoopbackHost(string host)
    {
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
