using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Core.Embeddings;

/// <summary>Outcome of a model provisioning request.</summary>
public sealed record ModelInstallResult
{
    /// <summary>The model that was requested.</summary>
    public required string ModelId { get; init; }

    /// <summary>Where its files live.</summary>
    public required string Directory { get; init; }

    /// <summary>Whether anything was actually downloaded.</summary>
    public bool Downloaded { get; init; }

    /// <summary>Per-file notes, for the CLI to render.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];
}

/// <summary>
/// Downloads and locates local embedding models.
/// </summary>
/// <remarks>
/// Models are stored one directory per model id so several can coexist and the configured model can
/// be switched without re-downloading. Downloads land on a temporary path and are moved into place
/// only after a successful integrity check, so an interrupted or truncated transfer can never leave
/// a file that looks installed but fails at load time with an opaque ONNX error.
/// </remarks>
public sealed class EmbeddingModelProvisioner(
    RetracePaths paths,
    RetraceConfig config,
    HttpClient httpClient,
    ILogger<EmbeddingModelProvisioner> logger)
{
    /// <summary>Root directory holding every downloaded model.</summary>
    public string ModelsRoot => string.IsNullOrWhiteSpace(config.Embeddings.ModelsDirectory)
        ? paths.ModelsDirectory
        : paths.ExpandPath(config.Embeddings.ModelsDirectory);

    /// <summary>Directory for a specific model.</summary>
    public string GetModelDirectory(string modelId) => Path.Combine(ModelsRoot, Sanitize(modelId));

    /// <summary>Path to the ONNX graph for a model.</summary>
    public string GetModelPath(string modelId) => string.IsNullOrWhiteSpace(config.Embeddings.ModelPath)
        ? Path.Combine(GetModelDirectory(modelId), "model.onnx")
        : paths.ExpandPath(config.Embeddings.ModelPath);

    /// <summary>Path to the tokenizer file for a model.</summary>
    public string GetTokenizerPath(string modelId)
    {
        if (!string.IsNullOrWhiteSpace(config.Embeddings.TokenizerPath))
        {
            return paths.ExpandPath(config.Embeddings.TokenizerPath);
        }

        var fileName = KnownEmbeddingModels.TryGet(modelId, out var known) ? known.TokenizerFileName : "vocab.txt";
        return Path.Combine(GetModelDirectory(modelId), fileName);
    }

    /// <summary>Whether a model's files are all present locally.</summary>
    public bool IsInstalled(string modelId) => File.Exists(GetModelPath(modelId)) && File.Exists(GetTokenizerPath(modelId));

    /// <summary>
    /// Downloads a model when it is not already installed.
    /// </summary>
    public async Task<ModelInstallResult> InstallAsync(string? modelId, bool force, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? config.Embeddings.Model : modelId;

        if (!KnownEmbeddingModels.TryGet(id, out var model))
        {
            throw new RetraceException(
                $"'{id}' is not a known embedding model ({string.Join(", ", KnownEmbeddingModels.Ids)}). "
                + "For a custom model, set embeddings.modelPath and embeddings.tokenizerPath instead and skip this command.");
        }

        var directory = GetModelDirectory(model.Id);
        Directory.CreateDirectory(directory);

        var downloaded = false;
        var notes = new List<string>();

        foreach (var file in model.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destination = Path.Combine(directory, file.LocalName);
            if (!force && File.Exists(destination) && new FileInfo(destination).Length > 0)
            {
                notes.Add($"{file.LocalName}: already present");
                continue;
            }

            var url = $"{model.RepositoryBaseUrl}/{file.RemotePath}?download=true";
            progress?.Report($"Downloading {file.LocalName} from {model.RepositoryBaseUrl}");

            var bytes = await DownloadAsync(url, destination, cancellationToken).ConfigureAwait(false);
            notes.Add($"{file.LocalName}: downloaded ({bytes / 1024.0 / 1024.0:F1} MB)");
            downloaded = true;
        }

        return new ModelInstallResult
        {
            ModelId = model.Id,
            Directory = directory,
            Downloaded = downloaded,
            Files = notes
        };
    }

    private async Task<long> DownloadAsync(string url, string destination, CancellationToken cancellationToken)
    {
        var temporary = destination + ".download";

        try
        {
            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var expectedLength = response.Content.Headers.ContentLength;

            long written;
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = File.Create(temporary))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                written = output.Length;
            }

            // A truncated response is the realistic failure mode on a flaky connection, and it
            // produces a file that ONNX Runtime rejects with an unhelpful protobuf error much later.
            // Checking the declared length here turns that into a clear, actionable failure.
            if (expectedLength is { } expected && written != expected)
            {
                throw new RetraceException(
                    $"Download of '{url}' was truncated: expected {expected:N0} bytes, received {written:N0}. Re-run `retrace deps install onnx`.");
            }

            File.Move(temporary, destination, overwrite: true);
            logger.LogDebug("Downloaded {Url} to {Destination} ({Bytes} bytes).", url, destination, written);
            return written;
        }
        catch (HttpRequestException ex)
        {
            throw new RetraceException($"Could not download '{url}': {ex.Message}", ex);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                try
                {
                    File.Delete(temporary);
                }
                catch (IOException)
                {
                    // A stray .download file is harmless and will be overwritten next time.
                }
            }
        }
    }

    /// <summary>Computes a file's SHA-256, for `doctor` output.</summary>
    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(character => invalid.Contains(character) ? '-' : character));
    }
}
