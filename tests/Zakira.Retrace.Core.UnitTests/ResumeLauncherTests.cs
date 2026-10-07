using Zakira.Retrace.Abstractions;
using Zakira.Retrace.Core.Services;

namespace Zakira.Retrace.Core.UnitTests;

/// <summary>How a resume command becomes a process, including the npm shim case on Windows.</summary>
public sealed class ResumeLauncherTests
{
    private static ResumeCommand Command(string executable, string? workingDirectory = null) => new()
    {
        Executable = executable,
        Arguments = ["--session", "ses_123"],
        WorkingDirectory = workingDirectory,
        DisplayCommand = $"{executable} --session ses_123"
    };

    [Fact]
    public void ResolveExecutable_finds_a_file_on_PATH()
    {
        using var temp = new TempDirectory();
        var name = OperatingSystem.IsWindows() ? "fake-harness.exe" : "fake-harness";
        File.WriteAllText(Path.Combine(temp.Path, name), string.Empty);

        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", temp.Path + Path.PathSeparator + original);

            ResumeLauncher.ResolveExecutable("fake-harness").Should().BeEquivalentTo(Path.Combine(temp.Path, name), "PATHEXT decides the extension's casing on a case-insensitive file system");
            ResumeLauncher.ResolveExecutable("definitely-not-installed-anywhere").Should().BeNull();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public void Build_wraps_a_cmd_shim_in_cmd_exe_on_Windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("npm .cmd shims only exist on Windows.");
        }

        using var temp = new TempDirectory();
        var shim = Path.Combine(temp.Path, "opencode.cmd");
        File.WriteAllText(shim, "@echo off");

        var original = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", temp.Path + Path.PathSeparator + original);

            var startInfo = ResumeLauncher.Build(Command("opencode", temp.Path));

            // CreateProcess cannot start a batch file directly; the shell has to, and /s makes its
            // quoting rules predictable.
            startInfo.FileName.Should().Be("cmd.exe");
            startInfo.Arguments.Should().BeEquivalentTo($"/d /s /c \"\"{shim}\" --session ses_123\"");
            startInfo.WorkingDirectory.Should().Be(temp.Path);
            startInfo.UseShellExecute.Should().BeFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", original);
        }
    }

    [Fact]
    public void Build_passes_arguments_through_unchanged_for_a_real_executable()
    {
        var startInfo = ResumeLauncher.Build(Command("some-harness-not-on-path"));

        startInfo.FileName.Should().Be("some-harness-not-on-path");
        startInfo.ArgumentList.Should().Equal("--session", "ses_123");
        startInfo.WorkingDirectory.Should().BeEmpty("a missing working directory must not be set, or Process.Start fails");
    }

    [Fact]
    public void Build_ignores_a_working_directory_that_no_longer_exists()
    {
        var startInfo = ResumeLauncher.Build(Command("harness", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        startInfo.WorkingDirectory.Should().BeEmpty();
    }
}
