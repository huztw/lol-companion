namespace LoLCompanion.Core.Contracts;

public sealed record CurrentSessionResponse(
    string DiscordUserId,
    string DeviceName,
    DateTimeOffset ExpiresAt
);
