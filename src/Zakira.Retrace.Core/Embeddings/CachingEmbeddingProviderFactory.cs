namespace Zakira.Retrace.Core.Embeddings;

/// <summary>
/// Keeps one embedding provider alive for the lifetime of the process and hands it out to every
/// caller, for long-lived front ends such as the interactive browser.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OnnxEmbeddingProviderFactory"/> deliberately creates a fresh provider per operation:
/// a one-shot CLI command should not pin an ONNX session it will use once. The browser is the
/// opposite case. It runs a search on every keystroke, and loading the model and tokenizer from
/// disk each time cost more than the search itself. This decorator moves that cost to the first
/// call, and <see cref="WarmUpAsync"/> lets the browser pay it before the user has typed anything.
/// </para>
/// <para>
/// Callers dispose what they are given, as they always have, so what they are given is a handle
/// whose <see cref="IDisposable.Dispose"/> does nothing. The real provider is released when the
/// factory itself is disposed, which the service container does on shutdown.
/// </para>
/// </remarks>
public sealed class CachingEmbeddingProviderFactory(IEmbeddingProviderFactory inner) : IEmbeddingProviderFactory, IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private IEmbeddingProvider? provider;
    private bool disposed;

    /// <inheritdoc />
    public string ModelId => inner.ModelId;

    /// <inheritdoc />
    public bool IsAvailable => inner.IsAvailable;

    /// <summary>Whether the provider has already been created.</summary>
    public bool IsWarm => provider is not null;

    /// <inheritdoc />
    public async Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (provider is { } existing)
        {
            return new SharedHandle(existing);
        }

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            provider ??= await inner.CreateAsync(cancellationToken).ConfigureAwait(false);
            return new SharedHandle(provider);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Loads the model now, so the first query does not have to. A no-op when embeddings are unavailable.</summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        if (!IsAvailable || IsWarm)
        {
            return;
        }

        using var handle = await CreateAsync(cancellationToken).ConfigureAwait(false);

        // One tiny inference also pays for the runtime's lazy first-run setup, which is otherwise
        // added to the first real query.
        await handle.EmbedAsync(["warm up"], EmbeddingSide.Query, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        provider?.Dispose();
        provider = null;
        gate.Dispose();
    }

    /// <summary>A view over the shared provider that ignores disposal.</summary>
    private sealed class SharedHandle(IEmbeddingProvider inner) : IEmbeddingProvider
    {
        public string ModelId => inner.ModelId;

        public int Dimensions => inner.Dimensions;

        public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingSide side, CancellationToken cancellationToken) =>
            inner.EmbedAsync(texts, side, cancellationToken);

        public void Dispose()
        {
        }
    }
}
