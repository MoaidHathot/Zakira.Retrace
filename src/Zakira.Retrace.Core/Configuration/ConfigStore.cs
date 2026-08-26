using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Zakira.Retrace.Core.Configuration;

/// <summary>
/// Loads, creates, and mutates <c>retrace.json</c>.
/// </summary>
public sealed class ConfigStore
{
    private readonly RetracePaths paths;

    /// <summary>
    /// Serialiser settings for the on-disk file.
    /// </summary>
    /// <remarks>
    /// <see cref="JsonIgnoreCondition.Never"/> is deliberate. A generated config lists every
    /// option at its default value, including the ones whose default is null, so the file itself
    /// tells the user what can be tuned without a trip to the documentation.
    /// </remarks>
    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Creates a store for an explicit path, or the resolved default when null.</summary>
    public ConfigStore(RetracePaths paths, string? configPath = null)
    {
        this.paths = paths;
        ConfigPath = string.IsNullOrWhiteSpace(configPath) ? paths.ConfigFilePath : paths.ExpandPath(configPath);
    }

    /// <summary>Full path to the file this store reads and writes.</summary>
    public string ConfigPath { get; }

    /// <summary>Whether the file exists on disk.</summary>
    public bool Exists => File.Exists(ConfigPath);

    /// <summary>
    /// Loads the config, falling back to defaults when the file is absent. Never writes.
    /// </summary>
    public async Task<RetraceConfig> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ConfigPath))
        {
            return new RetraceConfig();
        }

        try
        {
            await using var stream = File.OpenRead(ConfigPath);
            var config = await JsonSerializer.DeserializeAsync<RetraceConfig>(stream, ReadOptions, cancellationToken).ConfigureAwait(false);
            return config ?? new RetraceConfig();
        }
        catch (JsonException ex)
        {
            throw new Abstractions.RetraceException($"Failed to parse '{ConfigPath}': {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Loads the config, creating the file with fully populated defaults when it does not exist.
    /// </summary>
    public async Task<RetraceConfig> EnsureExistsAsync(CancellationToken cancellationToken)
    {
        if (File.Exists(ConfigPath))
        {
            return await LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        var config = new RetraceConfig();
        await SaveAsync(config, cancellationToken).ConfigureAwait(false);
        return config;
    }

    /// <summary>Writes the config, creating the directory when needed.</summary>
    public async Task SaveAsync(RetraceConfig config, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a sibling temp file and move into place so an interrupted write cannot leave a
        // truncated config behind. The user's dotfiles are usually under version control and a
        // half-written file there is worse than no file at all.
        var tempPath = ConfigPath + ".tmp";
        await using (var stream = File.Create(tempPath))
        {
            await JsonSerializer.SerializeAsync(stream, config, WriteOptions, cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, ConfigPath, overwrite: true);
    }

    /// <summary>
    /// Flattens the config into dotted keys for <c>retrace config list</c> and <c>get</c>.
    /// </summary>
    public static IReadOnlyDictionary<string, string?> Flatten(RetraceConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, WriteOptions)!.AsObject();
        var result = new SortedDictionary<string, string?>(StringComparer.Ordinal);
        Flatten(node, prefix: string.Empty, result);
        return result;
    }

    private static void Flatten(JsonNode? node, string prefix, IDictionary<string, string?> into)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    // $schema is metadata for editors, not a tunable option.
                    if (key.StartsWith('$'))
                    {
                        continue;
                    }

                    Flatten(value, prefix.Length == 0 ? key : $"{prefix}.{key}", into);
                }

                break;

            case JsonArray array:
                into[prefix] = string.Join(",", array.Select(item => item?.ToString() ?? string.Empty));
                break;

            case null:
                into[prefix] = null;
                break;

            default:
                into[prefix] = node.ToString();
                break;
        }
    }

    /// <summary>Reads one dotted key. Returns <see langword="false"/> when the key does not exist.</summary>
    public static bool TryGet(RetraceConfig config, string key, out string? value) =>
        Flatten(config).TryGetValue(key, out value);

    /// <summary>
    /// Sets one dotted key, parsing the string into the JSON type the existing value already has.
    /// </summary>
    /// <remarks>
    /// Typing from the current value rather than from a schema keeps <c>config set</c> honest:
    /// <c>search.rrfK 60</c> stays a number, <c>index.autoRefresh false</c> stays a boolean, and an
    /// unknown key is rejected instead of silently adding dead configuration.
    /// </remarks>
    public static RetraceConfig Set(RetraceConfig config, string key, string? value)
    {
        var root = JsonSerializer.SerializeToNode(config, WriteOptions)!.AsObject();
        var segments = key.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            throw new Abstractions.RetraceException("A configuration key is required.");
        }

        JsonObject cursor = root;
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (cursor[segments[i]] is not JsonObject child)
            {
                throw new Abstractions.RetraceException(
                    $"Unknown configuration key '{key}'. Run `retrace config list` to see valid keys.");
            }

            cursor = child;
        }

        var leaf = segments[^1];
        if (!cursor.ContainsKey(leaf))
        {
            throw new Abstractions.RetraceException(
                $"Unknown configuration key '{key}'. Run `retrace config list` to see valid keys.");
        }

        cursor[leaf] = CoerceToExistingShape(cursor[leaf], value, key);

        var updated = root.Deserialize<RetraceConfig>(ReadOptions)
            ?? throw new Abstractions.RetraceException($"Setting '{key}' produced an invalid configuration.");
        return updated;
    }

    private static JsonNode? CoerceToExistingShape(JsonNode? existing, string? value, string key)
    {
        if (value is null or "null")
        {
            return null;
        }

        // An array-valued option is written as a comma-separated list on the command line.
        if (existing is JsonArray)
        {
            var array = new JsonArray();
            foreach (var item in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                array.Add(JsonValue.Create(item));
            }

            return array;
        }

        if (existing is JsonValue existingValue)
        {
            if (existingValue.TryGetValue<bool>(out _))
            {
                return bool.TryParse(value, out var parsed)
                    ? JsonValue.Create(parsed)
                    : throw new Abstractions.RetraceException($"'{key}' expects true or false, got '{value}'.");
            }

            if (existingValue.TryGetValue<int>(out _))
            {
                return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
                    ? JsonValue.Create(parsed)
                    : throw new Abstractions.RetraceException($"'{key}' expects an integer, got '{value}'.");
            }

            if (existingValue.TryGetValue<double>(out _))
            {
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                    ? JsonValue.Create(parsed)
                    : throw new Abstractions.RetraceException($"'{key}' expects a number, got '{value}'.");
            }
        }

        return JsonValue.Create(value);
    }
}
