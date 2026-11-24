using System.Diagnostics;

namespace Yamux.Interop.Tests;

public sealed class GoInteropFixture : IAsyncLifetime
{
    public string? BinaryPath { get; private set; }

    public string? SkipReason { get; private set; }

    public ValueTask InitializeAsync()
    {
        try
        {
            var repoRoot = FindRepositoryRoot();
            var projectDirectory = Path.Combine(repoRoot, "benchmarks", "go-interop");
            var outputDirectory = Path.Combine(Path.GetTempPath(), "yamux-go-interop");
            Directory.CreateDirectory(outputDirectory);

            var binary = Path.Combine(
                outputDirectory,
                OperatingSystem.IsWindows() ? "go-interop.exe" : "go-interop");

            var startInfo = new ProcessStartInfo("go", $"build -o \"{binary}\" .")
            {
                WorkingDirectory = projectDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(startInfo);
            if (process is null)
            {
                SkipReason = "Unable to start the Go toolchain.";
                return ValueTask.CompletedTask;
            }

            var stdout = process.StandardOutput.ReadToEnd();
            var stderr = process.StandardError.ReadToEnd();

            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                SkipReason = "Timed out building the Go interoperability helper.";
                return ValueTask.CompletedTask;
            }

            if (process.ExitCode != 0)
            {
                SkipReason = $"go build failed ({process.ExitCode}): {stderr}{stdout}";
                return ValueTask.CompletedTask;
            }

            BinaryPath = binary;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            SkipReason = "The Go toolchain is not available on this machine.";
        }
        catch (Exception ex)
        {
            SkipReason = $"Go interoperability setup failed: {ex.Message}";
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Yamux.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Unable to locate the repository root (Yamux.slnx).");
    }
}

[CollectionDefinition("GoInterop")]
public sealed class GoInteropCollection : ICollectionFixture<GoInteropFixture>
{
}
