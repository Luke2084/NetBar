using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NetBar
{
    internal static class UpdateService
    {
        private const string LatestReleaseUrl = "https://api.github.com/repos/Luke2084/NetBar/releases/latest";
        private const string PreferredAssetName = "NetBar-win-x64.zip";

        private static readonly HttpClient Http = CreateHttpClient();

        internal sealed class UpdateInfo
        {
            public required Version Version { get; init; }
            public required string TagName { get; init; }
            public required string AssetName { get; init; }
            public required Uri DownloadUrl { get; init; }
            public required Uri ReleaseUrl { get; init; }
        }

        public static Version GetCurrentVersion()
        {
            return Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 1, 0);
        }

        public static string? GetExecutablePath()
        {
            try
            {
                var path = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    return path;
                }
            }
            catch
            {
                // MainModule may throw on certain systems
            }
            return AppContext.BaseDirectory.TrimEnd('\\', '/') + "\\NetBar.exe";
        }

        public static async Task<UpdateInfo?> CheckForUpdateAsync(Version currentVersion, CancellationToken cancellationToken = default)
        {
            using var response = await Http.GetAsync(LatestReleaseUrl, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = doc.RootElement;

            string tagName = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!TryParseVersion(tagName, out var latestVersion) || latestVersion <= currentVersion)
            {
                return null;
            }

            if (!root.TryGetProperty("assets", out var assets))
            {
                return null;
            }

            var asset = assets.EnumerateArray()
                .Select(item => new
                {
                    Name = item.GetProperty("name").GetString() ?? string.Empty,
                    Url = item.GetProperty("browser_download_url").GetString() ?? string.Empty
                })
                .FirstOrDefault(item =>
                    item.Name.Equals(PreferredAssetName, StringComparison.OrdinalIgnoreCase) ||
                    (item.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                     item.Name.Contains("win-x64", StringComparison.OrdinalIgnoreCase)));

            if (asset == null || string.IsNullOrWhiteSpace(asset.Url))
            {
                return null;
            }

            var releaseUrl = root.TryGetProperty("html_url", out var htmlUrlElement)
                ? htmlUrlElement.GetString()
                : "https://github.com/Luke2084/NetBar/releases/latest";

            return new UpdateInfo
            {
                Version = latestVersion,
                TagName = tagName,
                AssetName = asset.Name,
                DownloadUrl = new Uri(asset.Url),
                ReleaseUrl = new Uri(releaseUrl ?? "https://github.com/Luke2084/NetBar/releases/latest")
            };
        }

        public static async Task DownloadAndInstallAsync(UpdateInfo update, CancellationToken cancellationToken = default)
        {
            string? exePath = GetExecutablePath();
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                throw new InvalidOperationException("Unable to locate the running NetBar executable.");
            }

            string targetDir = AppContext.BaseDirectory;
            string updateRoot = Path.Combine(Path.GetTempPath(), "NetBar", "updates", update.TagName);
            string packagePath = Path.Combine(updateRoot, update.AssetName);
            string extractDir = Path.Combine(updateRoot, "package");
            Directory.CreateDirectory(updateRoot);

            if (Directory.Exists(extractDir))
            {
                Directory.Delete(extractDir, recursive: true);
            }

            await using (var remote = await Http.GetStreamAsync(update.DownloadUrl, cancellationToken))
            await using (var local = File.Create(packagePath))
            {
                await remote.CopyToAsync(local, cancellationToken);
            }

            ZipFile.ExtractToDirectory(packagePath, extractDir, overwriteFiles: true);

            string updaterScript = WriteUpdaterScript(updateRoot);
            StartUpdater(updaterScript, Environment.ProcessId, extractDir, targetDir, exePath);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("NetBar-Updater/0.0.1");
            client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            return client;
        }

        private static bool TryParseVersion(string tagName, out Version version)
        {
            string value = tagName.Trim().TrimStart('v', 'V');
            return Version.TryParse(value, out version!);
        }

        private static string WriteUpdaterScript(string updateRoot)
        {
            string script = Path.Combine(updateRoot, "apply-update.ps1");
            File.WriteAllText(script, """
param(
    [int]$ProcessId,
    [string]$SourceDir,
    [string]$TargetDir,
    [string]$ExePath
)

$ErrorActionPreference = 'Stop'
$Script:Success = $false

try {
    # Wait for the old process to exit
    $proc = $null
    try {
        $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
        if ($proc) {
            $proc.WaitForExit(15000)
        }
    } catch {
        # Process already exited or access denied — continue anyway
    }

    # Extra delay to let handle release
    Start-Sleep -Milliseconds 800

    # Clean target directory first to avoid stale files
    if (Test-Path $TargetDir) {
        Remove-Item -Path (Join-Path $TargetDir '*') -Recurse -Force -ErrorAction SilentlyContinue
    }

    # Create target directory
    New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

    # Copy new files
    Copy-Item -Path (Join-Path $SourceDir '*') -Destination $TargetDir -Recurse -Force
    $Script:Success = $true
} catch {
    Write-Error "Update failed: $($_.Exception.Message)"
    exit 1
}

# Exit code: 0 = success, non-zero = failure
exit [int](-not $Script:Success)
""");
            return script;
        }

        private static void StartUpdater(string updaterScript, int processId, string sourceDir, string targetDir, string exePath)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(updaterScript);
            startInfo.ArgumentList.Add("-ProcessId");
            startInfo.ArgumentList.Add(processId.ToString());
            startInfo.ArgumentList.Add("-SourceDir");
            startInfo.ArgumentList.Add(sourceDir);
            startInfo.ArgumentList.Add("-TargetDir");
            startInfo.ArgumentList.Add(targetDir);
            startInfo.ArgumentList.Add("-ExePath");
            startInfo.ArgumentList.Add(exePath);

            try
            {
                Process.Start(startInfo);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to start updater: {ex.Message}");
            }
        }
    }
}
