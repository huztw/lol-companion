using LoLCompanion.Core.Analysis;
using LoLCompanion.Core.Contracts;

namespace LoLCompanion.Core.Api;

public interface ICompanionSessionManager
{
    CompanionSessionSnapshot? GetActiveSession();
    Task<CompanionSessionSnapshot> RedeemAsync(CompanionApiClient apiClient, PairRedeemRequest request, CancellationToken cancellationToken = default);
    Task RevokeAsync(CompanionApiClient apiClient, CancellationToken cancellationToken = default);
    void Clear();
    void ClearIfExpired();
    void ClearUnauthorized();
    string GetRequiredSessionToken();
}

public sealed class InMemoryCompanionSessionManager : ICompanionSessionManager
{
    private readonly Func<DateTimeOffset> _now;
    private StoredSession? _session;

    public InMemoryCompanionSessionManager(Func<DateTimeOffset>? now = null)
    {
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public CompanionSessionSnapshot? GetActiveSession()
    {
        ClearIfExpired();
        return _session?.Snapshot;
    }

    public async Task<CompanionSessionSnapshot> RedeemAsync(CompanionApiClient apiClient, PairRedeemRequest request, CancellationToken cancellationToken = default)
    {
        var response = await apiClient.RedeemPairCodeAsync(request, cancellationToken);
        _session = new StoredSession(response.SessionToken, response.ExpiresAt, response.DeviceName, response.DiscordUserId);
        return _session.Snapshot;
    }

    public async Task RevokeAsync(CompanionApiClient apiClient, CancellationToken cancellationToken = default)
    {
        ClearIfExpired();
        if (_session is null)
        {
            return;
        }

        try
        {
            await apiClient.RevokeCurrentSessionAsync(_session.Token, cancellationToken);
        }
        catch (CompanionApiException exception) when (exception.StatusCode == 401)
        {
            ClearUnauthorized();
            return;
        }

        Clear();
    }

    public void Clear()
    {
        _session = null;
    }

    public void ClearIfExpired()
    {
        if (_session is not null && _session.ExpiresAt <= _now())
        {
            _session = null;
        }
    }

    public void ClearUnauthorized()
    {
        _session = null;
    }

    public string GetRequiredSessionToken()
    {
        ClearIfExpired();
        if (_session is null)
        {
            throw new CompanionAnalysisException("session_missing", "A valid Companion session is required.");
        }

        return _session.Token;
    }

    private sealed class StoredSession
    {
        public StoredSession(string token, DateTimeOffset expiresAt, string deviceName, string discordUserId)
        {
            Token = token;
            ExpiresAt = expiresAt;
            Snapshot = new CompanionSessionSnapshot(expiresAt, deviceName, discordUserId);
        }

        public string Token { get; }

        public DateTimeOffset ExpiresAt { get; }

        public CompanionSessionSnapshot Snapshot { get; }

        public override string ToString() => $"StoredSession {{ ExpiresAt = {ExpiresAt:O}, DeviceName = {Snapshot.DeviceName}, DiscordUserId = {Snapshot.DiscordUserId}, Token = [redacted] }}";
    }
}

public sealed record CompanionSessionSnapshot(
    DateTimeOffset ExpiresAt,
    string DeviceName,
    string DiscordUserId
);
