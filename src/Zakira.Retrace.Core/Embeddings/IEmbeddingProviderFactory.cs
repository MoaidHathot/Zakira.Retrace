using Microsoft.Extensions.Logging;
using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Core.Embeddings;

/// <summary>Creates embedding providers on demand.</summary>
public interface IEmbeddingProviderFactory
{
    /// <summary>The model that would be used.</summary>
    string ModelId { get; }

    /// <summary>
    /// Whether a provider can be created right now: embeddings enabled in config, and model files
    /// present on disk. Checked before every semantic query so a missing model degrades search to
    /// keyword-only rather than failing it.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Creates a provider, downloading the model first when configured to.</summary>
    Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds <see cref="OnnxEmbeddingProvider"/> instances from configuration.
/// </summary>
/// <remarks>
/// Providers are created per operation rather than held as a singleton. An ONNX session pins tens
/// of megabytes of native memory for the lifetime of the object, and a CLI process that only ran
/// <c>retrace list</c> has no reason to be paying for that.
/// </remarks>
public sealed class OnnxEmbeddingProviderFactory(
    RetraceConfig config,
    EmbeddingModelProvisioner provisioner,
    ILogger<OnnxEmbeddingProviderFactory> logger) : IEmbeddingProviderFactory
{
    /// <inheritdoc />
    public string ModelId => string.IsNullOrWhiteSpace(config.Embeddings.Model)
        ? KnownEmbeddingModels.Default
        : config.Embeddings.Model;

    /// <inheritdoc />
    public bool IsAvailable => config.Embeddings.Enabled && provisioner.IsInstalled(ModelId);

    /// <inheritdoc />
    public async Task<IEmbeddingProvider> CreateAsync(CancellationToken cancellationToken)
    {
        if (!config.Embeddings.Enabled)
        {
            throw new Abstractions.RetraceException(
                "Embeddings are disabled. Set embeddings.enabled to true, or use --lexical-only.");
        }

        if (!provisioner.IsInstalled(ModelId))
        {
            if (!config.Embeddings.AutoDownload)
            {
                throw new Abstractions.RetraceException(
                    $"Embedding model '{ModelId}' is not installed and embeddings.autoDownload is false. "
                    + "Run `retrace deps install onnx` or set embeddings.enabled to false.");
            }

            logger.LogInformation("Downloading embedding model {ModelId}...", ModelId);
            await provisioner.InstallAsync(ModelId, force: false, progress: null, cancellationToken).ConfigureAwait(false);
        }

        var known = KnownEmbeddingModels.TryGet(ModelId, out var model) ? model : null;

        var dimensions = known?.Dimensions ?? 384;
        var maxSequenceLength = config.Embeddings.MaxSequenceLength > 0
            ? config.Embeddings.MaxSequenceLength
            : known?.MaxSequenceLength ?? 512;

        var kind = KnownEmbeddingModels.ResolveKind(ModelId, config.Embeddings.ModelKind);

        return new OnnxEmbeddingProvider(
            provisioner.GetModelPath(ModelId),
            provisioner.GetTokenizerPath(ModelId),
            ModelId,
            kind,
            dimensions,
            maxSequenceLength,
            config.Embeddings.MaxThreads);
    }
}
