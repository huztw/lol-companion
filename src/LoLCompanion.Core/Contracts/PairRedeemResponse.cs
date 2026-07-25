namespace LoLCompanion.Core.Contracts;

public sealed record PairRedeemResponse(
    string SessionToken,
    DateTimeOffset ExpiresAt,
    string DeviceName,
    string DiscordUserId
);
