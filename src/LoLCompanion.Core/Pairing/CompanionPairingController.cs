using LoLCompanion.Core.Api;
using LoLCompanion.Core.Contracts;

namespace LoLCompanion.Core.Pairing;

public enum CompanionPairingState
{
    Paired,
    ValidationFailed,
    InvalidOrExpiredCode,
    CodeAlreadyUsed,
    RateLimited,
    NetworkUnavailable,
    TimedOut,
    ServiceUnavailable
}

public sealed record CompanionPairingResult(
    CompanionPairingState State,
    string Message,
    CompanionSessionSnapshot? Session
);

public sealed class CompanionPairingController
{
    private readonly CompanionApiClient _apiClient;
    private readonly ICompanionSessionManager _sessionManager;

    public CompanionPairingController(CompanionApiClient apiClient, ICompanionSessionManager sessionManager)
    {
        _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
        _sessionManager = sessionManager ?? throw new ArgumentNullException(nameof(sessionManager));
    }

    public async Task<CompanionPairingResult> PairAsync(
        string pairCode,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        var normalizedPairCode = pairCode?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedPairCode))
        {
            return new CompanionPairingResult(
                CompanionPairingState.ValidationFailed,
                "配對碼不能是空白。",
                null);
        }

        var normalizedDeviceName = deviceName?.Trim();
        if (string.IsNullOrWhiteSpace(normalizedDeviceName))
        {
            return new CompanionPairingResult(
                CompanionPairingState.ValidationFailed,
                "裝置名稱不能是空白。",
                null);
        }

        if (normalizedDeviceName.Length > 40)
        {
            return new CompanionPairingResult(
                CompanionPairingState.ValidationFailed,
                "裝置名稱最多 40 個字元。",
                null);
        }

        try
        {
            var snapshot = await _sessionManager.RedeemAsync(
                _apiClient,
                new PairRedeemRequest(normalizedPairCode, normalizedDeviceName),
                cancellationToken);

            return new CompanionPairingResult(
                CompanionPairingState.Paired,
                "已完成配對。",
                snapshot);
        }
        catch (CompanionApiException exception) when (exception.StatusCode == 400)
        {
            return new CompanionPairingResult(
                CompanionPairingState.InvalidOrExpiredCode,
                "配對碼無效或已過期。",
                null);
        }
        catch (CompanionApiException exception) when (exception.StatusCode == 409)
        {
            return new CompanionPairingResult(
                CompanionPairingState.CodeAlreadyUsed,
                "這組配對碼已經被使用。",
                null);
        }
        catch (CompanionApiException exception) when (exception.StatusCode == 429)
        {
            return new CompanionPairingResult(
                CompanionPairingState.RateLimited,
                "操作太頻繁，請稍後再試。",
                null);
        }
        catch (CompanionApiException)
        {
            return new CompanionPairingResult(
                CompanionPairingState.ServiceUnavailable,
                "配對服務暫時無法使用。",
                null);
        }
        catch (HttpRequestException)
        {
            return new CompanionPairingResult(
                CompanionPairingState.NetworkUnavailable,
                "無法連線到配對服務。",
                null);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CompanionPairingResult(
                CompanionPairingState.TimedOut,
                "配對逾時，請再試一次。",
                null);
        }
    }
}
