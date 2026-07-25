namespace LoLCompanion.Core.Analysis;

public sealed class CompanionAnalysisException : Exception
{
    public CompanionAnalysisException(string category, string message, bool isRecoverable = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Category = category;
        IsRecoverable = isRecoverable;
    }

    public string Category { get; }

    public bool IsRecoverable { get; }
}
