namespace LoLCompanion.Core.Lcu;

public sealed class LcuException : Exception
{
    public LcuException(string category, string message, bool isRecoverable, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        IsRecoverable = isRecoverable;
        StatusCode = statusCode;
    }

    public string Category { get; }

    public bool IsRecoverable { get; }

    public int? StatusCode { get; }

    public override string ToString() =>
        $"{GetType().Name} {{ Category = {Category}, Message = {Message}, Recoverable = {IsRecoverable}, StatusCode = {StatusCode?.ToString() ?? "none"} }}";
}
