namespace Zakira.Retrace.Core.Embeddings;

/// <summary>
/// Pooling and prefix family of an embedding model.
/// </summary>
/// <remarks>
/// Getting this wrong is silent: the model still returns a vector of the right shape, it is just a
/// materially worse one. BGE models are trained with CLS pooling and mean-pooling them measurably
/// degrades retrieval; E5 models expect explicit <c>query: </c> and <c>passage: </c> prefixes and
/// lose their asymmetry without them.
/// </remarks>
public enum EmbeddingModelKind
{
    /// <summary>Generic BERT/MiniLM: mean pooling over the attention mask, no prefixes.</summary>
    Bert,

    /// <summary>BGE and Snowflake arctic-embed: CLS pooling, instruction prefix on queries only.</summary>
    Bge,

    /// <summary>E5 family: mean pooling, distinct query and passage prefixes.</summary>
    E5
}

/// <summary>One file that makes up a downloadable model.</summary>
public sealed record ModelFile(string LocalName, string RemotePath);

/// <summary>A model Retrace knows how to download and configure.</summary>
public sealed record KnownEmbeddingModel
{
    /// <summary>Identifier used in config and stored in the index.</summary>
    public required string Id { get; init; }

    /// <summary>Base URL the files are fetched from.</summary>
    public required string RepositoryBaseUrl { get; init; }

    /// <summary>Pooling and prefix family.</summary>
    public required EmbeddingModelKind Kind { get; init; }

    /// <summary>Output dimension.</summary>
    public required int Dimensions { get; init; }

    /// <summary>Maximum token count the export accepts.</summary>
    public required int MaxSequenceLength { get; init; }

    /// <summary>
    /// Tokenizer file name. A <c>.txt</c> file is a WordPiece vocabulary; a <c>.model</c> file is a
    /// SentencePiece model, which selects a different tokenizer at load time.
    /// </summary>
    public required string TokenizerFileName { get; init; }

    /// <summary>Files to download.</summary>
    public required IReadOnlyList<ModelFile> Files { get; init; }

    /// <summary>Approximate download size, for the confirmation message.</summary>
    public required int ApproximateSizeMegabytes { get; init; }
}

/// <summary>The set of models Retrace can provision automatically.</summary>
public static class KnownEmbeddingModels
{
    /// <summary>English, 384 dimensions, roughly 33 MB quantised. The default.</summary>
    public const string BgeSmallEnV15 = "bge-small-en-v1.5";

    /// <summary>Snowflake's small English model. Same architecture and size as BGE.</summary>
    public const string SnowflakeArcticEmbedS = "snowflake-arctic-embed-s";

    /// <summary>Multilingual, 384 dimensions, roughly 118 MB. Uses a SentencePiece tokenizer.</summary>
    public const string MultilingualE5Small = "multilingual-e5-small";

    /// <summary>Model used when config does not specify one.</summary>
    public const string Default = BgeSmallEnV15;

    private static readonly Dictionary<string, KnownEmbeddingModel> Registry = new(StringComparer.OrdinalIgnoreCase)
    {
        [BgeSmallEnV15] = new KnownEmbeddingModel
        {
            Id = BgeSmallEnV15,
            RepositoryBaseUrl = "https://huggingface.co/Xenova/bge-small-en-v1.5/resolve/main",
            Kind = EmbeddingModelKind.Bge,
            Dimensions = 384,
            MaxSequenceLength = 512,
            TokenizerFileName = "vocab.txt",
            ApproximateSizeMegabytes = 33,
            Files =
            [
                // The quantised export is a third of the size at a negligible retrieval cost, which
                // is the right trade for a tool that downloads this on first use.
                new ModelFile("model.onnx", "onnx/model_quantized.onnx"),
                new ModelFile("vocab.txt", "vocab.txt"),
                new ModelFile("tokenizer_config.json", "tokenizer_config.json"),
                new ModelFile("config.json", "config.json")
            ]
        },

        [SnowflakeArcticEmbedS] = new KnownEmbeddingModel
        {
            Id = SnowflakeArcticEmbedS,
            // Snowflake does not publish ONNX exports themselves; onnx-community is the canonical
            // mirror that transformers.js consumers use.
            RepositoryBaseUrl = "https://huggingface.co/onnx-community/snowflake-arctic-embed-s/resolve/main",
            Kind = EmbeddingModelKind.Bge,
            Dimensions = 384,
            MaxSequenceLength = 512,
            TokenizerFileName = "vocab.txt",
            ApproximateSizeMegabytes = 33,
            Files =
            [
                new ModelFile("model.onnx", "onnx/model_quantized.onnx"),
                new ModelFile("vocab.txt", "vocab.txt"),
                new ModelFile("tokenizer_config.json", "tokenizer_config.json"),
                new ModelFile("config.json", "config.json")
            ]
        },

        [MultilingualE5Small] = new KnownEmbeddingModel
        {
            Id = MultilingualE5Small,
            RepositoryBaseUrl = "https://huggingface.co/Xenova/multilingual-e5-small/resolve/main",
            Kind = EmbeddingModelKind.E5,
            Dimensions = 384,
            MaxSequenceLength = 512,
            TokenizerFileName = "sentencepiece.bpe.model",
            ApproximateSizeMegabytes = 118,
            Files =
            [
                new ModelFile("model.onnx", "onnx/model_quantized.onnx"),
                new ModelFile("sentencepiece.bpe.model", "sentencepiece.bpe.model"),
                new ModelFile("tokenizer_config.json", "tokenizer_config.json"),
                new ModelFile("config.json", "config.json")
            ]
        }
    };

    /// <summary>Every known model id.</summary>
    public static IReadOnlyCollection<string> Ids => Registry.Keys;

    /// <summary>Every known model.</summary>
    public static IReadOnlyCollection<KnownEmbeddingModel> All => Registry.Values;

    /// <summary>Looks up a model by id.</summary>
    public static bool TryGet(string? id, out KnownEmbeddingModel model)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            return Registry.TryGetValue(id, out model!);
        }

        model = Registry[Default];
        return true;
    }

    /// <summary>
    /// Infers the pooling and prefix family from a model id, honouring an explicit override.
    /// </summary>
    public static EmbeddingModelKind ResolveKind(string? modelId, string? explicitKind = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitKind))
        {
            return ParseKind(explicitKind);
        }

        if (string.IsNullOrWhiteSpace(modelId))
        {
            return EmbeddingModelKind.Bert;
        }

        if (modelId.Contains("bge", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("arctic-embed", StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingModelKind.Bge;
        }

        if (modelId.StartsWith("e5-", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("multilingual-e5", StringComparison.OrdinalIgnoreCase)
            || modelId.Contains("/e5-", StringComparison.OrdinalIgnoreCase))
        {
            return EmbeddingModelKind.E5;
        }

        return EmbeddingModelKind.Bert;
    }

    /// <summary>Parses a model-kind string.</summary>
    public static EmbeddingModelKind ParseKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "bert" or "minilm" or "default" => EmbeddingModelKind.Bert,
        "bge" or "arctic" or "snowflake" => EmbeddingModelKind.Bge,
        "e5" or "multilingual-e5" => EmbeddingModelKind.E5,
        _ => throw new Abstractions.RetraceException(
            $"Unknown embedding model kind '{value}'. Expected one of: bert, bge, e5.")
    };
}
