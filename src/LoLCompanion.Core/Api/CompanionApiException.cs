namespace LoLCompanion.Core.Api;

public sealed class CompanionApiException : Exception
{
    public CompanionApiException(string message, int statusCode)
        : base(message)
    {
        StatusCode = statusCode;
    }

    public int StatusCode { get; }
}
