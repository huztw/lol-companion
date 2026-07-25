using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using LoLCompanion.Core.Contracts;

namespace LoLCompanion.Core.Api;

public sealed class CompanionApiClient
{
    private const int MaxErrorBytes = 4096;
    private const int MaxErrorMessageLength = 256;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    public CompanionApiClient(HttpClient httpClient)
    {
        if (httpClient.BaseAddress is null || !string.Equals(httpClient.BaseAddress.Scheme, "https", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Companion API base address must use HTTPS.", nameof(httpClient));
        }

        _httpClient = httpClient;
    }

    public async Task<PairRedeemResponse> RedeemPairCodeAsync(PairRedeemRequest request, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.PairCode))
        {
            throw new ArgumentException("Pair code is required.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.DeviceName))
        {
            throw new ArgumentException("Device name is required.", nameof(request));
        }

        using var response = await _httpClient.PostAsJsonAsync(
            "companion/pair/redeem",
            request,
            JsonOptions,
            cancellationToken
        );

        return await ReadRequiredJsonAsync<PairRedeemResponse>(response, cancellationToken);
    }

    public async Task<CurrentSessionResponse> GetCurrentSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Get, "companion/sessions/current", sessionToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadRequiredJsonAsync<CurrentSessionResponse>(response, cancellationToken);
    }

    public async Task RevokeCurrentSessionAsync(string sessionToken, CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Delete, "companion/sessions/current", sessionToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return;
        }

        throw await BuildApiExceptionAsync(response, cancellationToken);
    }

    public async Task<CompanionAnalysisSubmitResponse> SubmitAnalysisAsync(string sessionToken, byte[] utf8Body, CancellationToken cancellationToken = default)
    {
        using var request = CreateAuthorizedRequest(HttpMethod.Post, "companion/analyses", sessionToken);
        request.Content = new ByteArrayContent(utf8Body);
        request.Content.Headers.ContentType = new("application/json")
        {
            CharSet = "utf-8"
        };

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadRequiredJsonAsync<CompanionAnalysisSubmitResponse>(response, cancellationToken);
    }

    public async Task<CompanionAnalysisStatusDtoV1> GetAnalysisStatusAsync(string sessionToken, string jobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            throw new ArgumentException("Job id is required.", nameof(jobId));
        }

        using var request = CreateAuthorizedRequest(HttpMethod.Get, $"companion/analyses/{Uri.EscapeDataString(jobId)}", sessionToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return await ReadRequiredJsonAsync<CompanionAnalysisStatusDtoV1>(response, cancellationToken);
    }

    public async Task<CompanionVersionDtoV1> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync("companion/version", cancellationToken);
        return await ReadRequiredJsonAsync<CompanionVersionDtoV1>(response, cancellationToken);
    }

    private static HttpRequestMessage CreateAuthorizedRequest(HttpMethod method, string path, string sessionToken)
    {
        if (string.IsNullOrWhiteSpace(sessionToken))
        {
            throw new ArgumentException("Session token is required.", nameof(sessionToken));
        }

        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new("Bearer", sessionToken);
        return request;
    }

    private async Task<TResponse> ReadRequiredJsonAsync<TResponse>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw await BuildApiExceptionAsync(response, cancellationToken);
        }

        var result = await response.Content.ReadFromJsonAsync<TResponse>(JsonOptions, cancellationToken);
        if (result is null)
        {
            throw new CompanionApiException("Companion API returned an empty response.", (int)response.StatusCode);
        }

        return result;
    }

    private static async Task<CompanionApiException> BuildApiExceptionAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var message = await ReadErrorMessageAsync(response, cancellationToken);
        return new CompanionApiException(message, (int)response.StatusCode);
    }

    private static async Task<string> ReadErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is long contentLength && contentLength > MaxErrorBytes)
        {
            return "Companion API request failed.";
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[1024];
        while (buffer.Length < MaxErrorBytes)
        {
            var bytesRead = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxErrorBytes - (int)buffer.Length)), cancellationToken);
            if (bytesRead == 0)
            {
                break;
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        if (buffer.Length == 0)
        {
            return "Companion API request failed.";
        }

        try
        {
            using var document = JsonDocument.Parse(buffer.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.String)
            {
                var error = errorElement.GetString();
                if (!string.IsNullOrWhiteSpace(error))
                {
                    return error.Length > MaxErrorMessageLength ? error[..MaxErrorMessageLength] : error;
                }
            }
        }
        catch (JsonException)
        {
        }

        return "Companion API request failed.";
    }
}
