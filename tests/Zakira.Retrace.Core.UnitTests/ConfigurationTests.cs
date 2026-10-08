using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Configuration;

namespace Zakira.Retrace.Core.UnitTests;

/// <summary>
/// Covers path resolution and the config file's read/write/mutate cycle.
/// </summary>
/// <remarks>
/// Path resolution is tested against a scripted environment, but it still runs through the host's
/// <see cref="Path"/> APIs, so the fake roots follow the host's conventions: drive letters and
/// backslashes on Windows, a <c>/home</c> tree elsewhere. The rules under test are the same on
/// every platform; only the shape of the strings differs.
/// </remarks>
public sealed class ConfigurationTests
{
    /// <summary>A fully scriptable environment, so path tests never touch the real machine.</summary>
    private sealed class FakeEnvironment : ISystemEnvironment
    {
        public Dictionary<string, string> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<Environment.SpecialFolder, string> Folders { get; } = [];

        public string TempPath { get; set; } = Path.Combine(Path.GetTempPath(), "fake");

        public bool IsWindows { get; set; } = OperatingSystem.IsWindows();

        public bool IsMacOs { get; set; } = OperatingSystem.IsMacOS();

        public string? GetEnvironmentVariable(string name) => Variables.GetValueOrDefault(name);

        public string GetFolderPath(Environment.SpecialFolder folder) => Folders.GetValueOrDefault(folder, Fallback);

        public string GetTempPath() => TempPath;
    }

    private static readonly bool OnWindows = OperatingSystem.IsWindows();

    private static string Fallback => OnWindows ? @"C:\fallback" : "/fallback";

    private static string Profile => OnWindows ? @"C:\Users\test" : "/home/test";

    private static string Roaming => OnWindows ? @"C:\Users\test\AppData\Roaming" : "/home/test/.config";

    private static string LocalAppData => OnWindows ? @"C:\Users\test\AppData\Local" : "/home/test/.local/share";

    private static string Dotfiles => OnWindows ? @"P:\dotfiles\config" : "/dotfiles/config";

    private static string DataRoot => OnWindows ? @"P:\data" : "/data";

    private static string CustomConfig => OnWindows ? @"D:\custom\my-retrace.json" : "/custom/my-retrace.json";

    private static string Cache => OnWindows ? @"E:\retrace-cache" : "/retrace-cache";

    /// <summary>Where the platform puts config and data when nothing overrides it.</summary>
    private static string PlatformConfigRoot => OnWindows
        ? Roaming
        : OperatingSystem.IsMacOS() ? Path.Combine(Profile, "Library", "Application Support") : Path.Combine(Profile, ".config");

    private static string PlatformDataRoot => OnWindows
        ? LocalAppData
        : OperatingSystem.IsMacOS() ? Path.Combine(Profile, "Library", "Application Support") : Path.Combine(Profile, ".local", "share");

    private static FakeEnvironment HostEnvironment() => new()
    {
        Folders =
        {
            [Environment.SpecialFolder.ApplicationData] = Roaming,
            [Environment.SpecialFolder.LocalApplicationData] = LocalAppData,
            [Environment.SpecialFolder.UserProfile] = Profile
        }
    };

    [Fact]
    public void Config_lands_in_XDG_CONFIG_HOME_when_it_is_set()
    {
        var environment = HostEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = Dotfiles;

        var paths = new RetracePaths(environment);

        // The whole point of preferring XDG here is that the config file ends up in the user's
        // versioned dotfiles alongside the other tools' configuration.
        paths.ConfigFilePath.Should().Be(Path.Combine(Dotfiles, "Zakira.Retrace", "retrace.json"));
        paths.ConfigRoot.Should().Be(Dotfiles);
    }

    [Fact]
    public void Config_root_normalises_relative_segments_and_mixed_separators()
    {
        var environment = HostEnvironment();

        // This is the literal shape a real dotfiles setup produces.
        environment.Variables["XDG_CONFIG_HOME"] = OnWindows ? @"P:\dotfiles\configurations/../config/" : "/dotfiles/configurations/../config/";

        var paths = new RetracePaths(environment);

        paths.ConfigRoot.Should().Be(Dotfiles);
        paths.ConfigFilePath.Should().Be(Path.Combine(Dotfiles, "Zakira.Retrace", "retrace.json"));
    }

    [Fact]
    public void Config_falls_back_to_the_platform_location_without_XDG()
    {
        var paths = new RetracePaths(HostEnvironment());

        paths.ConfigFilePath.Should().Be(Path.Combine(PlatformConfigRoot, "Zakira.Retrace", "retrace.json"));
    }

    [Fact]
    public void RETRACE_CONFIG_PATH_overrides_everything()
    {
        var environment = HostEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = Dotfiles;
        environment.Variables["RETRACE_CONFIG_PATH"] = CustomConfig;

        var paths = new RetracePaths(environment);

        paths.ConfigFilePath.Should().Be(CustomConfig);
        paths.ConfigDirectory.Should().Be(Path.GetDirectoryName(CustomConfig));
    }

    [Fact]
    public void Data_directory_is_separate_from_config_and_lives_in_local_app_data()
    {
        var environment = HostEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = Dotfiles;

        var paths = new RetracePaths(environment);

        // The index and downloaded models are large, machine-specific, and rebuildable, so they
        // must never end up inside synchronised dotfiles.
        paths.DataDirectory.Should().Be(Path.Combine(PlatformDataRoot, "Zakira.Retrace"));
        paths.DataDirectory.Should().NotStartWith(paths.ConfigRoot);
        paths.DefaultIndexPath.Should().Be(Path.Combine(PlatformDataRoot, "Zakira.Retrace", "index.db"));
    }

    [Fact]
    public void Data_directory_honours_XDG_DATA_HOME_and_its_own_override()
    {
        var environment = HostEnvironment();
        environment.Variables["XDG_DATA_HOME"] = DataRoot;

        new RetracePaths(environment).DataDirectory.Should().Be(Path.Combine(DataRoot, "Zakira.Retrace"));

        environment.Variables["RETRACE_DATA_DIRECTORY"] = Cache;
        new RetracePaths(environment).DataDirectory.Should().Be(Cache);
    }

    [Theory]
    [InlineData("$XDG_CONFIG_HOME/skills", "skills")]
    [InlineData("${XDG_CONFIG_HOME}/skills", "skills")]
    [InlineData("~/notes", "notes")]
    public void Expand_path_resolves_the_variables_a_dotfiles_setup_uses(string input, string leaf)
    {
        var environment = HostEnvironment();
        environment.Variables["XDG_CONFIG_HOME"] = Dotfiles;

        var expected = input.StartsWith('~') ? Path.Combine(Profile, leaf) : Path.Combine(Dotfiles, leaf);

        // Values are stored verbatim so the same config works on every machine; expansion happens
        // at read time.
        new RetracePaths(environment).ExpandPath(input).Should().Be(expected);
    }

    [Fact]
    public async Task Generated_config_contains_every_option_including_nulls()
    {
        using var temp = new TempDirectory();
        var paths = new RetracePaths(HostEnvironment());
        var store = new ConfigStore(paths, Path.Combine(temp.Path, "retrace.json"));

        await store.EnsureExistsAsync(TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(store.ConfigPath, TestContext.Current.CancellationToken);

        // The generated file doubles as documentation, so options at their default value and
        // options whose default is null both have to be written out.
        json.Should().Contain("\"$schema\"");
        json.Should().Contain("\"dataDirectory\": null");
        json.Should().Contain("\"modelKind\": null");
        json.Should().Contain("\"autoRefresh\": true");
        json.Should().Contain("\"rrfK\": 60");
        json.Should().Contain("\"bge-small-en-v1.5\"");

        var flat = ConfigStore.Flatten(new RetraceConfig());
        foreach (var key in flat.Keys)
        {
            var leaf = key.Split('.')[^1];
            json.Should().Contain($"\"{leaf}\"", $"'{key}' must appear in the generated file");
        }
    }

    [Fact]
    public async Task Config_round_trips_through_save_and_load()
    {
        using var temp = new TempDirectory();
        var store = new ConfigStore(new RetracePaths(HostEnvironment()), Path.Combine(temp.Path, "retrace.json"));

        var original = new RetraceConfig();
        original.Search.RrfK = 42;
        original.Sources.CopilotCli.MinTurns = 5;
        original.Sources.CopilotVsCode.Editors = ["insiders"];
        original.Embeddings.Enabled = false;

        await store.SaveAsync(original, TestContext.Current.CancellationToken);
        var reloaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        reloaded.Search.RrfK.Should().Be(42);
        reloaded.Sources.CopilotCli.MinTurns.Should().Be(5);
        reloaded.Sources.CopilotVsCode.Editors.Should().ContainSingle().Which.Should().Be("insiders");
        reloaded.Embeddings.Enabled.Should().BeFalse();
    }

    [Fact]
    public void Set_coerces_a_string_argument_to_the_type_the_key_already_has()
    {
        var config = new RetraceConfig();

        // Command-line arguments are always strings; typing them from the existing value keeps
        // numbers as numbers and booleans as booleans rather than quietly storing "60".
        ConfigStore.Set(config, "search.rrfK", "80").Search.RrfK.Should().Be(80);
        ConfigStore.Set(config, "index.autoRefresh", "false").Index.AutoRefresh.Should().BeFalse();
        ConfigStore.Set(config, "search.recencyBoost", "0.5").Search.RecencyBoost.Should().Be(0.5);
        ConfigStore.Set(config, "embeddings.model", "multilingual-e5-small").Embeddings.Model.Should().Be("multilingual-e5-small");
        ConfigStore.Set(config, "embeddings.modelKind", "null").Embeddings.ModelKind.Should().BeNull();
        ConfigStore.Set(config, "sources.copilot-vscode.editors", "stable,insiders")
            .Sources.CopilotVsCode.Editors.Should().BeEquivalentTo("stable", "insiders");
    }

    [Fact]
    public void Set_rejects_an_unknown_key_instead_of_adding_dead_configuration()
    {
        var act = () => ConfigStore.Set(new RetraceConfig(), "search.notAThing", "1");

        act.Should().Throw<RetraceException>().WithMessage("*Unknown configuration key*");
    }

    [Fact]
    public void Set_rejects_a_value_of_the_wrong_type()
    {
        var config = new RetraceConfig();

        ((Action)(() => ConfigStore.Set(config, "search.rrfK", "banana")))
            .Should().Throw<RetraceException>().WithMessage("*expects an integer*");

        ((Action)(() => ConfigStore.Set(config, "index.autoRefresh", "maybe")))
            .Should().Throw<RetraceException>().WithMessage("*expects true or false*");
    }

    [Fact]
    public void Flatten_omits_schema_metadata_but_keeps_every_real_option()
    {
        var flat = ConfigStore.Flatten(new RetraceConfig());

        flat.Should().NotContainKey("$schema");
        flat.Should().ContainKey("sources.opencode.enabled");
        flat.Should().ContainKey("sources.copilot-cli.minTurns");
        flat.Should().ContainKey("search.semanticSessionShortlist");
        flat.Should().ContainKey("embeddings.batchSize");
        flat["embeddings.modelPath"].Should().BeNull();
    }

    [Fact]
    public async Task Load_reports_a_malformed_file_clearly_rather_than_crashing()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "retrace.json");
        await File.WriteAllTextAsync(path, "{ this is not json", TestContext.Current.CancellationToken);

        var store = new ConfigStore(new RetracePaths(HostEnvironment()), path);

        var act = async () => await store.LoadAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<RetraceException>().WithMessage("*Failed to parse*");
    }

    [Fact]
    public async Task Missing_config_yields_defaults_without_creating_a_file()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "absent.json");
        var store = new ConfigStore(new RetracePaths(HostEnvironment()), path);

        var config = await store.LoadAsync(TestContext.Current.CancellationToken);

        config.Search.RrfK.Should().Be(60);
        File.Exists(path).Should().BeFalse("Load must not have side effects; only EnsureExists writes");
    }
}

/// <summary>A scratch directory removed on dispose.</summary>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "Zakira.Retrace.CoreTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}