using System.Text.Json.Serialization;

namespace Zakira.Retrace.Core.Configuration;

/// <summary>
/// The contents of <c>retrace.json</c>.
/// </summary>
/// <remarks>
/// Every property has a usable default, so an absent config file behaves exactly like a freshly
/// generated one. When Retrace writes the file it serialises <em>all</em> properties, including
/// those still at their defaults and those that are null, so the file doubles as documentation of
/// the full option surface.
/// </remarks>
public sealed class RetraceConfig
{
    /// <summary>JSON Schema reference, for editor completion.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; } = "https://raw.githubusercontent.com/MoaidHathot/Zakira.Retrace/main/schemas/retrace.schema.json";

    /// <summary>Config format version. Bumped only for breaking layout changes.</summary>
    [JsonPropertyName("version")]
    public int Version { get; set; } = 1;

    /// <summary>Per-source settings.</summary>
    [JsonPropertyName("sources")]
    public SourcesConfig Sources { get; set; } = new();

    /// <summary>Index location and chunking behaviour.</summary>
    [JsonPropertyName("index")]
    public IndexConfig Index { get; set; } = new();

    /// <summary>Retrieval and ranking behaviour.</summary>
    [JsonPropertyName("search")]
    public SearchConfig Search { get; set; } = new();

    /// <summary>Local embedding model settings.</summary>
    [JsonPropertyName("embeddings")]
    public EmbeddingsConfig Embeddings { get; set; } = new();

    /// <summary>Console rendering defaults.</summary>
    [JsonPropertyName("output")]
    public OutputConfig Output { get; set; } = new();
}

/// <summary>Per-source settings, keyed by the source ids used everywhere else.</summary>
public sealed class SourcesConfig
{
    /// <summary>OpenCode.</summary>
    [JsonPropertyName("opencode")]
    public OpenCodeSourceConfig OpenCode { get; set; } = new();

    /// <summary>GitHub Copilot CLI.</summary>
    [JsonPropertyName("copilot-cli")]
    public CopilotCliSourceConfig CopilotCli { get; set; } = new();

    /// <summary>GitHub Copilot Chat inside VS Code.</summary>
    [JsonPropertyName("copilot-vscode")]
    public CopilotVsCodeSourceConfig CopilotVsCode { get; set; } = new();
}

/// <summary>Settings shared by every source.</summary>
public abstract class SourceConfigBase
{
    /// <summary>Whether this source participates in listing, search, and indexing.</summary>
    /// <remarks>
    /// Explicit ordering: System.Text.Json emits derived-type properties before inherited ones, so
    /// without this the generated file would bury `enabled` under each source's specific options.
    /// </remarks>
    [JsonPropertyName("enabled")]
    [JsonPropertyOrder(-2)]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Override the harness's data directory. <see langword="null"/> means auto-detect from the
    /// usual per-platform location. Supports <c>$XDG_CONFIG_HOME</c>, <c>~</c>, and environment
    /// variable syntax.
    /// </summary>
    [JsonPropertyName("dataDirectory")]
    [JsonPropertyOrder(-1)]
    public string? DataDirectory { get; set; }
}

/// <summary>OpenCode-specific settings.</summary>
public sealed class OpenCodeSourceConfig : SourceConfigBase
{
    /// <summary>
    /// Read the legacy JSON file tree under <c>storage/</c> instead of <c>opencode.db</c>.
    /// </summary>
    /// <remarks>
    /// OpenCode migrated to SQLite and now writes both stores for a transition period. SQLite is
    /// authoritative and far faster, so it is preferred automatically; this flag exists for the
    /// case where the database is missing, unreadable, or lagging behind the JSON tree.
    /// </remarks>
    [JsonPropertyName("preferLegacyJsonStore")]
    public bool PreferLegacyJsonStore { get; set; }

    /// <summary>Include sub-agent and forked child sessions in listings.</summary>
    [JsonPropertyName("includeChildSessions")]
    public bool IncludeChildSessions { get; set; }
}

/// <summary>Copilot CLI-specific settings.</summary>
public sealed class CopilotCliSourceConfig : SourceConfigBase
{
    /// <summary>
    /// Skip sessions with fewer than this many turns. Copilot CLI creates a session record for
    /// every launch, so a machine with heavy usage accumulates tens of thousands of empty shells
    /// that would otherwise dominate every listing.
    /// </summary>
    [JsonPropertyName("minTurns")]
    public int MinTurns { get; set; } = 1;

    /// <summary>
    /// Use the harness's own FTS5 index for <c>--live</c> searches instead of scanning turn text.
    /// </summary>
    [JsonPropertyName("useNativeSearchIndex")]
    public bool UseNativeSearchIndex { get; set; } = true;
}

/// <summary>Copilot in VS Code-specific settings.</summary>
public sealed class CopilotVsCodeSourceConfig : SourceConfigBase
{
    /// <summary>
    /// Which VS Code installations to scan. Recognised values are <c>stable</c>, <c>insiders</c>,
    /// and <c>exploration</c>.
    /// </summary>
    [JsonPropertyName("editors")]
    public List<string> Editors { get; set; } = ["stable", "insiders"];

    /// <summary>Include chat sessions that contain no requests.</summary>
    [JsonPropertyName("includeEmptySessions")]
    public bool IncludeEmptySessions { get; set; }
}

/// <summary>Index location and chunking behaviour.</summary>
public sealed class IndexConfig
{
    /// <summary>
    /// Full path to the index database. <see langword="null"/> means
    /// <c>&lt;data directory&gt;/index.db</c>.
    /// </summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    /// <summary>
    /// Refresh a stale index automatically before a query, rather than returning stale results.
    /// </summary>
    /// <remarks>
    /// Retrace never runs in the background. This refresh happens inline, inside the command or MCP
    /// call that needed it, and only when the source watermarks have actually moved.
    /// </remarks>
    [JsonPropertyName("autoRefresh")]
    public bool AutoRefresh { get; set; } = true;

    /// <summary>
    /// Skip the staleness check when the index was refreshed less than this many minutes ago, so a
    /// burst of queries does not re-probe every source each time.
    /// </summary>
    [JsonPropertyName("autoRefreshMinIntervalMinutes")]
    public int AutoRefreshMinIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Hard ceiling, in seconds, on how long an automatic refresh may delay a query.
    /// </summary>
    /// <remarks>
    /// A query must never become slow because indexing work happens to be outstanding. When this
    /// budget expires the refresh stops where it is, the query answers from whatever the index
    /// already holds, and the shortfall is reported rather than paid for. Set to <c>0</c> to remove
    /// the ceiling, which is only sensible in a script that wants a fully current index.
    /// </remarks>
    [JsonPropertyName("autoRefreshMaxSeconds")]
    public int AutoRefreshMaxSeconds { get; set; } = 5;

    /// <summary>
    /// Compute vectors during an automatic refresh.
    /// </summary>
    /// <remarks>
    /// Off by default, and deliberately so: inference is by far the most expensive step, and
    /// putting it in front of an interactive query defeats the point of maintaining an index at
    /// all. New sessions still become keyword-searchable immediately; their vectors are added by
    /// an explicit <c>retrace index refresh</c>.
    /// </remarks>
    [JsonPropertyName("autoRefreshEmbed")]
    public bool AutoRefreshEmbed { get; set; }

    /// <summary>Index tool names, arguments, and captured output for keyword search.</summary>
    [JsonPropertyName("includeToolOutput")]
    public bool IncludeToolOutput { get; set; } = true;

    /// <summary>Index model reasoning blocks.</summary>
    [JsonPropertyName("includeReasoning")]
    public bool IncludeReasoning { get; set; } = true;

    /// <summary>Target chunk size in characters.</summary>
    [JsonPropertyName("chunkCharacters")]
    public int ChunkCharacters { get; set; } = 1200;

    /// <summary>Characters repeated between adjacent chunks so boundary-straddling matches survive.</summary>
    [JsonPropertyName("chunkOverlapCharacters")]
    public int ChunkOverlapCharacters { get; set; } = 150;

    /// <summary>Discard chunks shorter than this.</summary>
    [JsonPropertyName("minChunkCharacters")]
    public int MinChunkCharacters { get; set; } = 16;

    /// <summary>Truncate a single tool output to this many characters before indexing it.</summary>
    [JsonPropertyName("maxToolOutputCharacters")]
    public int MaxToolOutputCharacters { get; set; } = 4000;
}

/// <summary>Retrieval and ranking behaviour.</summary>
public sealed class SearchConfig
{
    /// <summary>Default result count when <c>--top</c> is not given.</summary>
    [JsonPropertyName("defaultTop")]
    public int DefaultTop { get; set; } = 20;

    /// <summary>Default snippets attached to each result.</summary>
    [JsonPropertyName("snippetsPerSession")]
    public int SnippetsPerSession { get; set; } = 3;

    /// <summary>
    /// Default retrieval mode: <c>hybrid</c>, <c>lexical</c>, or <c>semantic</c>. Hybrid degrades to
    /// lexical automatically when no embedding model is installed.
    /// </summary>
    [JsonPropertyName("mode")]
    public string Mode { get; set; } = "hybrid";

    /// <summary>
    /// The <c>k</c> constant in reciprocal rank fusion, <c>score = weight / (k + rank)</c>. Larger
    /// values flatten the contribution of top ranks; 60 is the value from the original RRF paper.
    /// </summary>
    [JsonPropertyName("rrfK")]
    public int RrfK { get; set; } = 60;

    /// <summary>Weight of the keyword signal during fusion.</summary>
    [JsonPropertyName("lexicalWeight")]
    public double LexicalWeight { get; set; } = 1.0;

    /// <summary>Weight of the vector signal during fusion.</summary>
    [JsonPropertyName("semanticWeight")]
    public double SemanticWeight { get; set; } = 1.0;

    /// <summary>
    /// How strongly recency lifts a result, as a fraction of the top fused score. <c>0</c> disables it.
    /// </summary>
    [JsonPropertyName("recencyBoost")]
    public double RecencyBoost { get; set; } = 0.15;

    /// <summary>Age at which the recency boost has decayed to half its value.</summary>
    [JsonPropertyName("recencyHalfLifeDays")]
    public double RecencyHalfLifeDays { get; set; } = 30;

    /// <summary>
    /// How strongly a session lifts when its working directory matches the current one. <c>0</c>
    /// disables it.
    /// </summary>
    [JsonPropertyName("workspaceBoost")]
    public double WorkspaceBoost { get; set; } = 0.10;

    /// <summary>Keyword candidates retrieved per requested result, before fusion.</summary>
    [JsonPropertyName("lexicalCandidateMultiplier")]
    public int LexicalCandidateMultiplier { get; set; } = 20;

    /// <summary>Hard ceiling on keyword candidates.</summary>
    [JsonPropertyName("lexicalCandidateCap")]
    public int LexicalCandidateCap { get; set; } = 500;

    /// <summary>
    /// Sessions kept after the first, session-level vector pass. Chunk vectors are then loaded only
    /// for these, which is what keeps semantic search off a full scan of the vector table.
    /// </summary>
    [JsonPropertyName("semanticSessionShortlist")]
    public int SemanticSessionShortlist { get; set; } = 50;
}

/// <summary>Local embedding model settings.</summary>
public sealed class EmbeddingsConfig
{
    /// <summary>
    /// Whether to compute and use vectors at all. When disabled, indexing is faster and smaller and
    /// search silently runs in lexical mode.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Known model id: <c>bge-small-en-v1.5</c>, <c>snowflake-arctic-embed-s</c>, or
    /// <c>multilingual-e5-small</c>. Also accepts an arbitrary id when <c>modelPath</c> and
    /// <c>tokenizerPath</c> are both set.
    /// </summary>
    [JsonPropertyName("model")]
    public string Model { get; set; } = "bge-small-en-v1.5";

    /// <summary>
    /// Pooling and prefix family: <c>bert</c>, <c>bge</c>, or <c>e5</c>. <see langword="null"/>
    /// infers it from the model id.
    /// </summary>
    [JsonPropertyName("modelKind")]
    public string? ModelKind { get; set; }

    /// <summary>Explicit path to a <c>.onnx</c> file, for models outside the known set.</summary>
    [JsonPropertyName("modelPath")]
    public string? ModelPath { get; set; }

    /// <summary>
    /// Explicit path to the tokenizer file: <c>vocab.txt</c> for WordPiece models,
    /// <c>sentencepiece.bpe.model</c> for XLM-R models.
    /// </summary>
    [JsonPropertyName("tokenizerPath")]
    public string? TokenizerPath { get; set; }

    /// <summary>
    /// Directory holding downloaded models, one subdirectory per model id.
    /// <see langword="null"/> means <c>&lt;data directory&gt;/models</c>.
    /// </summary>
    [JsonPropertyName("modelsDirectory")]
    public string? ModelsDirectory { get; set; }

    /// <summary>Token budget per chunk. Longer chunks are truncated, never split further.</summary>
    [JsonPropertyName("maxSequenceLength")]
    public int MaxSequenceLength { get; set; } = 512;

    /// <summary>
    /// Chunks per inference batch. Batching is what makes a first index build over a large session
    /// store finish in minutes rather than hours.
    /// </summary>
    [JsonPropertyName("batchSize")]
    public int BatchSize { get; set; } = 32;

    /// <summary>
    /// Maximum CPU threads ONNX Runtime may use per inference. <c>0</c> means half the logical
    /// processors.
    /// </summary>
    /// <remarks>
    /// ONNX Runtime defaults to every core, which on a large machine turns an index build into
    /// something that makes the whole system unresponsive rather than merely busy. Halving it
    /// costs some throughput on a build nobody is watching and buys back a usable machine.
    /// </remarks>
    [JsonPropertyName("maxThreads")]
    public int MaxThreads { get; set; }

    /// <summary>Download a known model automatically the first time it is needed.</summary>
    [JsonPropertyName("autoDownload")]
    public bool AutoDownload { get; set; } = true;
}

/// <summary>Console rendering defaults.</summary>
public sealed class OutputConfig
{
    /// <summary>ANSI colour: <c>auto</c>, <c>always</c>, or <c>never</c>.</summary>
    [JsonPropertyName("color")]
    public string Color { get; set; } = "auto";

    /// <summary>Default output format: <c>text</c>, <c>json</c>, or <c>ndjson</c>.</summary>
    [JsonPropertyName("defaultFormat")]
    public string DefaultFormat { get; set; } = "text";

    /// <summary>Timestamp rendering: <c>relative</c> ("3 days ago") or <c>absolute</c> (ISO 8601).</summary>
    [JsonPropertyName("dateFormat")]
    public string DateFormat { get; set; } = "relative";

    /// <summary>Characters of preview text shown per row in list and search output.</summary>
    [JsonPropertyName("previewCharacters")]
    public int PreviewCharacters { get; set; } = 120;
}
