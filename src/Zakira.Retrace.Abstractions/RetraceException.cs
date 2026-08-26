namespace Zakira.Retrace.Abstractions;

/// <summary>
/// Base type for every error Retrace raises deliberately. The CLI renders these as a one-line
/// message and exits non-zero; anything else is reported as an unexpected error with a stack trace.
/// </summary>
public class RetraceException : Exception
{
    /// <summary>Creates an exception with a message.</summary>
    public RetraceException(string message) : base(message)
    {
    }

    /// <summary>Creates an exception with a message and an inner cause.</summary>
    public RetraceException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates an exception with no message. Present to satisfy the standard constructor set.</summary>
    public RetraceException()
    {
    }
}

/// <summary>Raised when a session id cannot be resolved to exactly one session.</summary>
public sealed class SessionNotFoundException : RetraceException
{
    /// <summary>Creates the exception for an id that matched nothing.</summary>
    public SessionNotFoundException(string identifier)
        : base($"No session matched '{identifier}'. Run `retrace list` to see available sessions, or `retrace index refresh` if the index is stale.")
    {
        Identifier = identifier;
    }

    /// <summary>Creates the exception for an id prefix that matched more than one session.</summary>
    public SessionNotFoundException(string identifier, IReadOnlyList<string> candidates)
        : base($"'{identifier}' is ambiguous and matched {candidates.Count} sessions: {string.Join(", ", candidates.Take(5))}{(candidates.Count > 5 ? ", ..." : string.Empty)}. Use a longer prefix or the full retrace:// URI.")
    {
        Identifier = identifier;
        Candidates = candidates;
    }

    /// <summary>The identifier that failed to resolve.</summary>
    public string Identifier { get; }

    /// <summary>Matching candidates, when the failure was ambiguity rather than absence.</summary>
    public IReadOnlyList<string> Candidates { get; } = [];
}

/// <summary>Raised when a source's data store exists but cannot be read.</summary>
public sealed class SourceUnavailableException : RetraceException
{
    /// <summary>Creates the exception.</summary>
    public SourceUnavailableException(string sourceId, string reason)
        : base($"Source '{sourceId}' is unavailable: {reason}")
    {
        SourceId = sourceId;
    }

    /// <summary>Creates the exception with an inner cause.</summary>
    public SourceUnavailableException(string sourceId, string reason, Exception innerException)
        : base($"Source '{sourceId}' is unavailable: {reason}", innerException)
    {
        SourceId = sourceId;
    }

    /// <summary>The source that could not be read.</summary>
    public string SourceId { get; }
}

/// <summary>
/// Raised when the index was built with one embedding model and is being queried with another.
/// </summary>
/// <remarks>
/// Vectors from different models are not comparable even when their dimensions match, so a query
/// under the wrong model returns confident nonsense. Failing loudly is the only safe option.
/// </remarks>
public sealed class EmbeddingModelMismatchException : RetraceException
{
    /// <summary>Diagnostic code, stable across versions.</summary>
    public const string Code = "RETRACE_EMBEDDING_MODEL_MISMATCH";

    /// <summary>Creates the exception.</summary>
    public EmbeddingModelMismatchException(string indexedModel, string runtimeModel)
        : base($"{Code}: the index was built with '{indexedModel}' but the configured model is '{runtimeModel}'. " +
               "These vector spaces are not interchangeable. Either rebuild the index with " +
               $"`retrace index build --force`, or pin the original model with `--onnx-model {indexedModel}`.")
    {
        IndexedModel = indexedModel;
        RuntimeModel = runtimeModel;
    }

    /// <summary>Model the index was built with.</summary>
    public string IndexedModel { get; }

    /// <summary>Model currently configured.</summary>
    public string RuntimeModel { get; }
}

/// <summary>Raised when an operation needs the index but it has not been built.</summary>
public sealed class IndexNotBuiltException : RetraceException
{
    /// <summary>Creates the exception.</summary>
    public IndexNotBuiltException(string indexPath)
        : base($"No index at '{indexPath}'. Run `retrace index build` first, or add --live to search the sources directly.")
    {
        IndexPath = indexPath;
    }

    /// <summary>Where the index was expected.</summary>
    public string IndexPath { get; }
}
