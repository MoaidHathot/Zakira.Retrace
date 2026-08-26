using System.Numerics.Tensors;

namespace Zakira.Retrace.Core.Embeddings;

/// <summary>Which side of a retrieval pair a text is being embedded for.</summary>
/// <remarks>
/// Asymmetric models score a question against a passage, not a question against a question, and
/// achieve that by prefixing each side differently. Getting this wrong silently costs a large
/// fraction of the model's retrieval quality, so it is a required parameter rather than a default.
/// </remarks>
public enum EmbeddingSide
{
    /// <summary>Indexed content.</summary>
    Document,

    /// <summary>A search query.</summary>
    Query
}

/// <summary>Produces vectors for text.</summary>
public interface IEmbeddingProvider : IDisposable
{
    /// <summary>Identifier of the model in use, recorded in the index to detect a mismatch later.</summary>
    string ModelId { get; }

    /// <summary>Output dimension.</summary>
    int Dimensions { get; }

    /// <summary>Embeds a batch of texts. The result is positionally aligned with the input.</summary>
    ValueTask<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, EmbeddingSide side, CancellationToken cancellationToken);
}

/// <summary>
/// Serialisation and similarity helpers for vectors.
/// </summary>
public static class VectorMath
{
    /// <summary>
    /// Packs a vector as raw little-endian float32.
    /// </summary>
    /// <remarks>
    /// No header and no length prefix: the dimension is fixed per index and recorded once in the
    /// <c>meta</c> table, so repeating it on every row would waste roughly 1% of the vector store
    /// for no benefit. Every runtime Retrace targets is little-endian.
    /// </remarks>
    public static byte[] Serialize(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(vector).CopyTo(bytes);
        return bytes;
    }

    /// <summary>Unpacks a vector written by <see cref="Serialize"/>.</summary>
    public static float[] Deserialize(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length % sizeof(float) != 0)
        {
            return [];
        }

        var vector = new float[bytes.Length / sizeof(float)];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(bytes).CopyTo(vector);
        return vector;
    }

    /// <summary>
    /// Cosine similarity, hardware-accelerated through <see cref="TensorPrimitives"/>.
    /// </summary>
    /// <remarks>
    /// This runs once per candidate vector on every semantic query, so it is the one genuinely hot
    /// loop in the search path. <see cref="TensorPrimitives"/> vectorises it across whatever SIMD
    /// width the machine has, which is roughly an order of magnitude faster than a scalar loop at
    /// the dimensions these models produce.
    /// </remarks>
    public static float CosineSimilarity(ReadOnlySpan<float> left, ReadOnlySpan<float> right)
    {
        if (left.Length == 0 || left.Length != right.Length)
        {
            return 0f;
        }

        return TensorPrimitives.CosineSimilarity(left, right);
    }

    /// <summary>
    /// Dot product, which equals cosine similarity when both operands are already unit length.
    /// Providers normalise their output, so this is the cheaper path.
    /// </summary>
    public static float Dot(ReadOnlySpan<float> left, ReadOnlySpan<float> right) =>
        left.Length == 0 || left.Length != right.Length ? 0f : TensorPrimitives.Dot(left, right);

    /// <summary>Scales a vector to unit length in place. A zero vector is left untouched.</summary>
    public static void NormalizeInPlace(Span<float> vector)
    {
        var norm = TensorPrimitives.Norm(vector);
        if (norm > 0f)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }
    }

    /// <summary>
    /// Averages vectors into a unit-length centroid, used as a session's single representative
    /// vector for the first retrieval tier.
    /// </summary>
    public static float[] Centroid(IReadOnlyList<float[]> vectors)
    {
        if (vectors.Count == 0)
        {
            return [];
        }

        var dimensions = vectors[0].Length;
        var sum = new float[dimensions];

        foreach (var vector in vectors)
        {
            if (vector.Length == dimensions)
            {
                TensorPrimitives.Add(sum, vector, sum);
            }
        }

        TensorPrimitives.Divide(sum, vectors.Count, sum);
        NormalizeInPlace(sum);
        return sum;
    }
}
