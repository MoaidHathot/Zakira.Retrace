using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Zakira.Retrace.Abstractions;

namespace Zakira.Retrace.Core.Embeddings;

/// <summary>
/// Runs a local ONNX sentence-embedding model.
/// </summary>
/// <remarks>
/// <para>
/// Everything is local and CPU-only: no execution-provider configuration, no network at inference
/// time. The model is loaded once and reused for the lifetime of the instance, which matters most
/// during an index build where the same session is used for tens of thousands of passages.
/// </para>
/// <para>
/// Batching is the single largest performance lever here. Session stores routinely contain hundreds
/// of thousands of passages, and running inference one text at a time turns a multi-minute build
/// into a multi-hour one. Sequence length is also computed per batch rather than padded to the
/// model maximum, because most conversation passages are far shorter than 512 tokens and padding
/// them all to that length wastes the majority of every matrix multiply.
/// </para>
/// </remarks>
public sealed class OnnxEmbeddingProvider : IEmbeddingProvider
{
    private const string BgeQueryPrefix = "Represent this sentence for searching relevant passages: ";
    private const string E5QueryPrefix = "query: ";
    private const string E5PassagePrefix = "passage: ";

    private readonly InferenceSession session;
    private readonly Tokenizer tokenizer;
    private readonly EmbeddingModelKind kind;
    private readonly int maxSequenceLength;
    private readonly int padId;
    private readonly int startId;
    private readonly int endId;
    private readonly Lock inferenceGate = new();

    /// <summary>Loads a model.</summary>
    public OnnxEmbeddingProvider(
        string modelPath,
        string tokenizerPath,
        string modelId,
        EmbeddingModelKind kind,
        int dimensions,
        int maxSequenceLength,
        int maxThreads = 0)
    {
        if (!File.Exists(modelPath))
        {
            throw new RetraceException($"ONNX embedding model not found: {modelPath}. Run `retrace deps install onnx`.");
        }

        if (!File.Exists(tokenizerPath))
        {
            throw new RetraceException($"Embedding tokenizer not found: {tokenizerPath}. Run `retrace deps install onnx`.");
        }

        // ONNX Runtime otherwise takes every logical processor. On a 32-core machine that turns an
        // index build into something that saturates the whole system rather than merely occupying
        // it, which is a poor trade for work nobody is watching. Half the cores keeps the machine
        // usable at a modest cost in throughput.
        using var sessionOptions = new SessionOptions
        {
            IntraOpNumThreads = maxThreads > 0 ? maxThreads : Math.Max(1, Environment.ProcessorCount / 2),
            InterOpNumThreads = 1
        };

        session = new InferenceSession(modelPath, sessionOptions);
        (tokenizer, padId, startId, endId) = LoadTokenizer(tokenizerPath, kind);

        ModelId = modelId;
        Dimensions = dimensions;
        this.kind = kind;
        this.maxSequenceLength = Math.Max(16, maxSequenceLength);
    }

    /// <inheritdoc />
    public string ModelId { get; }

    /// <inheritdoc />
    public int Dimensions { get; }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingSide side, CancellationToken cancellationToken)
    {
        if (texts.Count == 0)
        {
            return ValueTask.FromResult<IReadOnlyList<float[]>>([]);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var encoded = new List<int[]>(texts.Count);
        foreach (var text in texts)
        {
            encoded.Add(Encode(ApplyPrefix(text, side)));
        }

        // Pad to the longest sequence in this batch rather than to the model maximum. Conversation
        // passages are usually well under the limit, so this typically halves the work.
        var sequenceLength = Math.Min(encoded.Max(ids => ids.Length), maxSequenceLength);
        var batchSize = texts.Count;

        var inputIds = new long[batchSize * sequenceLength];
        var attentionMask = new long[batchSize * sequenceLength];
        var tokenTypeIds = new long[batchSize * sequenceLength];

        for (var row = 0; row < batchSize; row++)
        {
            var ids = encoded[row];
            for (var column = 0; column < sequenceLength; column++)
            {
                var offset = (row * sequenceLength) + column;
                if (column < ids.Length)
                {
                    inputIds[offset] = ids[column];
                    attentionMask[offset] = 1;
                }
                else
                {
                    inputIds[offset] = padId;
                    attentionMask[offset] = 0;
                }
            }
        }

        // InferenceSession.Run is not documented as thread-safe for concurrent calls on the same
        // session, and a single provider instance is shared by the indexer and the searcher.
        List<float[]> vectors;
        lock (inferenceGate)
        {
            var inputs = BuildInputs(inputIds, attentionMask, tokenTypeIds, batchSize, sequenceLength);
            using var outputs = session.Run(inputs);
            vectors = Pool(outputs, attentionMask, batchSize, sequenceLength);
        }

        return ValueTask.FromResult<IReadOnlyList<float[]>>(vectors);
    }

    private string ApplyPrefix(string text, EmbeddingSide side) => (kind, side) switch
    {
        (EmbeddingModelKind.Bge, EmbeddingSide.Query) => BgeQueryPrefix + text,
        (EmbeddingModelKind.E5, EmbeddingSide.Query) => E5QueryPrefix + text,
        (EmbeddingModelKind.E5, EmbeddingSide.Document) => E5PassagePrefix + text,
        _ => text
    };

    private int[] Encode(string text)
    {
        // Two slots are reserved for the start and end markers so a truncated sequence is still
        // well-formed rather than ending mid-content.
        var budget = maxSequenceLength - 2;

        IReadOnlyList<int> raw = tokenizer switch
        {
            BertTokenizer bert => bert.EncodeToIds(text, addSpecialTokens: false, considerPreTokenization: true),
            SentencePieceTokenizer sentencePiece => sentencePiece.EncodeToIds(
                text,
                addBeginningOfSentence: false,
                addEndOfSentence: false,
                considerNormalization: true,
                considerPreTokenization: false),
            _ => tokenizer.EncodeToIds(text)
        };

        var count = Math.Min(raw.Count, budget);
        var ids = new int[count + 2];
        ids[0] = startId;
        for (var index = 0; index < count; index++)
        {
            ids[index + 1] = raw[index];
        }

        ids[^1] = endId;
        return ids;
    }

    private List<NamedOnnxValue> BuildInputs(long[] inputIds, long[] attentionMask, long[] tokenTypeIds, int batchSize, int sequenceLength)
    {
        var inputs = new List<NamedOnnxValue>();

        // Input names and element types vary between exports, so they are read from the model's own
        // metadata instead of being assumed.
        foreach (var (name, metadata) in session.InputMetadata)
        {
            var source = name switch
            {
                _ when name.Contains("input_ids", StringComparison.OrdinalIgnoreCase) => inputIds,
                _ when name.Contains("attention", StringComparison.OrdinalIgnoreCase) => attentionMask,
                _ when name.Contains("token_type", StringComparison.OrdinalIgnoreCase) => tokenTypeIds,
                _ when name.Contains("segment", StringComparison.OrdinalIgnoreCase) => tokenTypeIds,
                _ => null
            };

            if (source is null)
            {
                continue;
            }

            if (metadata.ElementDataType == TensorElementType.Int32)
            {
                var narrowed = new int[source.Length];
                for (var index = 0; index < source.Length; index++)
                {
                    narrowed[index] = (int)source[index];
                }

                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<int>(narrowed, [batchSize, sequenceLength])));
            }
            else
            {
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(source, [batchSize, sequenceLength])));
            }
        }

        if (inputs.Count == 0)
        {
            throw new RetraceException(
                $"The ONNX model at '{ModelId}' exposes no recognisable text inputs (expected input_ids and attention_mask).");
        }

        return inputs;
    }

    private List<float[]> Pool(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        long[] attentionMask,
        int batchSize,
        int sequenceLength)
    {
        // Some exports emit an already-pooled vector. When they do, honour it: the pooling is baked
        // into the graph and re-deriving it from hidden states would be both slower and different.
        var pooled = outputs.FirstOrDefault(output =>
            output.Name.Equals("sentence_embedding", StringComparison.OrdinalIgnoreCase)
            || output.Name.Equals("pooler_output", StringComparison.OrdinalIgnoreCase)
            || output.Name.Equals("embeddings", StringComparison.OrdinalIgnoreCase));

        var tensor = (pooled ?? outputs.ElementAt(0)).AsTensor<float>();

        if (tensor.Dimensions.Length == 2)
        {
            return Split(tensor, batchSize, tensor.Dimensions[1]);
        }

        if (tensor.Dimensions.Length != 3)
        {
            throw new RetraceException($"Unexpected embedding output rank {tensor.Dimensions.Length} from model '{ModelId}'.");
        }

        var hiddenSize = tensor.Dimensions[2];
        var results = new List<float[]>(batchSize);

        for (var row = 0; row < batchSize; row++)
        {
            var vector = new float[hiddenSize];

            if (kind == EmbeddingModelKind.Bge)
            {
                // BGE and arctic-embed are trained against the CLS position; mean pooling them
                // measurably degrades retrieval quality.
                for (var hidden = 0; hidden < hiddenSize; hidden++)
                {
                    vector[hidden] = tensor[row, 0, hidden];
                }
            }
            else
            {
                var counted = 0;
                for (var token = 0; token < sequenceLength; token++)
                {
                    if (attentionMask[(row * sequenceLength) + token] == 0)
                    {
                        continue;
                    }

                    counted++;
                    for (var hidden = 0; hidden < hiddenSize; hidden++)
                    {
                        vector[hidden] += tensor[row, token, hidden];
                    }
                }

                if (counted > 0)
                {
                    for (var hidden = 0; hidden < hiddenSize; hidden++)
                    {
                        vector[hidden] /= counted;
                    }
                }
            }

            VectorMath.NormalizeInPlace(vector);
            results.Add(vector);
        }

        return results;
    }

    private static List<float[]> Split(Tensor<float> tensor, int batchSize, int dimensions)
    {
        var results = new List<float[]>(batchSize);
        for (var row = 0; row < batchSize; row++)
        {
            var vector = new float[dimensions];
            for (var column = 0; column < dimensions; column++)
            {
                vector[column] = tensor[row, column];
            }

            VectorMath.NormalizeInPlace(vector);
            results.Add(vector);
        }

        return results;
    }

    private static (Tokenizer Tokenizer, int PadId, int StartId, int EndId) LoadTokenizer(string tokenizerPath, EmbeddingModelKind kind)
    {
        var extension = Path.GetExtension(tokenizerPath).ToLowerInvariant();

        // Selection is by file shape first: a SentencePiece model is a binary .model file, a
        // WordPiece vocabulary is a plain-text .txt. The model kind is only a tiebreaker for
        // non-canonical file names.
        if (extension == ".model" || (kind == EmbeddingModelKind.E5 && extension != ".txt"))
        {
            using var stream = File.OpenRead(tokenizerPath);
            var sentencePiece = SentencePieceTokenizer.Create(stream, addBeginningOfSentence: false, addEndOfSentence: false);

            // Canonical XLM-R ids: <s>=0, <pad>=1, </s>=2, <unk>=3.
            var pad = sentencePiece.SpecialTokens?.TryGetValue("<pad>", out var padValue) == true ? padValue : 1;
            return (sentencePiece, pad, sentencePiece.BeginningOfSentenceId, sentencePiece.EndOfSentenceId);
        }

        var options = new BertOptions
        {
            // BGE ships an uncased vocabulary. Arctic-embed is cased, so lower-casing it is a small
            // fidelity loss; it is accepted here because the alternative is parsing tokenizer_config
            // for do_lower_case, and the measured retrieval difference is negligible at this size.
            LowerCaseBeforeTokenization = true,
            ApplyBasicTokenization = true
        };

        var bert = BertTokenizer.Create(tokenizerPath, options);
        return (bert, bert.PaddingTokenId, bert.ClassificationTokenId, bert.SeparatorTokenId);
    }

    /// <inheritdoc />
    public void Dispose() => session.Dispose();
}
