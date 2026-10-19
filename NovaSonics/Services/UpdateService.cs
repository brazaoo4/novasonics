using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using NovaSonics.Models;

namespace NovaSonics.Services
{
    public class UpdateService : IUpdateService
    {
        private const string GitHubApiLatestReleaseUrl = "https://api.github.com/repos/brazaoo4/novasonics/releases/latest";
        private const string UserAgent = "NovaSonics-Updater";
        private const int BufferSize = 81920;

        public Version CurrentVersion { get; } = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

        public bool IsInstalledVersion => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

        public string CurrentVersionDisplay => $"v{CurrentVersion.ToString(3)} ({(IsInstalledVersion ? "Installed" : "Portable")})";

        public async Task<UpdateInfoModel?> CheckForUpdateAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(10));

                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

                using var response = await client.GetAsync(GitHubApiLatestReleaseUrl, cts.Token);
                if (!response.IsSuccessStatusCode)
                {
                    return null;
                }

                var json = await response.Content.ReadAsStringAsync(cts.Token);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string tagName = root.TryGetProperty("tag_name", out var tagElem) ? tagElem.GetString() ?? string.Empty : string.Empty;
                string htmlUrl = root.TryGetProperty("html_url", out var htmlElem) ? htmlElem.GetString() ?? string.Empty : string.Empty;
                string body = root.TryGetProperty("body", out var bodyElem) ? bodyElem.GetString() ?? string.Empty : string.Empty;

                string versionStr = tagName.TrimStart('v', 'V').Trim();
                if (!Version.TryParse(versionStr, out var remoteVersion))
                {
                    return null;
                }

                bool isNewer = remoteVersion > CurrentVersion;

                string? selectedDownloadUrl = null;
                string? selectedAssetName = null;

                if (root.TryGetProperty("assets", out var assetsElem) && assetsElem.ValueKind == JsonValueKind.Array)
                {
                    var assets = new List<(string Name, string Url)>();
                    foreach (var asset in assetsElem.EnumerateArray())
                    {
                        string name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? string.Empty : string.Empty;
                        string url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() ?? string.Empty : string.Empty;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url))
                        {
                            assets.Add((name, url));
                        }
                    }

                    if (IsInstalledVersion)
                    {
                        var match = assets.FirstOrDefault(a => a.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                        if (string.IsNullOrEmpty(match.Name))
                        {
                            match = assets.FirstOrDefault(a => a.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
                        }
                        selectedDownloadUrl = match.Url;
                        selectedAssetName = match.Name;
                    }
                    else
                    {
                        var match = assets.FirstOrDefault(a => a.Name.Contains("Portable", StringComparison.OrdinalIgnoreCase) && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                        if (string.IsNullOrEmpty(match.Name))
                        {
                            match = assets.FirstOrDefault(a => a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
                        }
                        selectedDownloadUrl = match.Url;
                        selectedAssetName = match.Name;
                    }
                }

                return new UpdateInfoModel
                {
                    IsUpdateAvailable = isNewer && !string.IsNullOrEmpty(selectedDownloadUrl),
                    LatestVersion = remoteVersion.ToString(3),
                    ReleaseUrl = htmlUrl,
                    DownloadUrl = selectedDownloadUrl ?? string.Empty,
                    AssetName = selectedAssetName ?? string.Empty,
                    ReleaseNotes = body
                };
            }
            catch (Exception ex)
            {
                App.LogCrash("CheckForUpdateAsync", ex);
                return null;
            }
        }

        public async Task DownloadAndApplyUpdateAsync(UpdateInfoModel updateInfo, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(updateInfo.DownloadUrl))
            {
                throw new InvalidOperationException("Download URL is not specified.");
            }

            string assetName = !string.IsNullOrWhiteSpace(updateInfo.AssetName)
                ? updateInfo.AssetName
                : (IsInstalledVersion ? "NovaSonics-Setup.exe" : "NovaSonics-Portable.zip");

            string tempDir = Path.GetTempPath();
            string downloadPath = Path.Combine(tempDir, assetName);

            if (File.Exists(downloadPath))
            {
                try { File.Delete(downloadPath); } catch { }
            }

            // 1. Stream asset with progress reporting
            using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
            {
                client.DefaultRequestHeaders.Add("User-Agent", UserAgent);
                using var response = await client.GetAsync(updateInfo.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                response.EnsureSuccessStatusCode();

                long? totalBytes = response.Content.Headers.ContentLength;
                await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var fileStream = new FileStream(downloadPath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, true);

                var buffer = new byte[BufferSize];
                long totalRead = 0;
                int bytesRead;

                while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    totalRead += bytesRead;
                    if (totalBytes.HasValue && totalBytes.Value > 0)
                    {
                        double percent = Math.Min(100.0, (double)totalRead / totalBytes.Value * 100.0);
                        progress?.Report(percent);
                    }
                }
            }

            progress?.Report(100.0);

            // 2. Remove Zone.Identifier alternate data stream to avoid Windows SmartScreen block
            try
            {
                File.Delete(downloadPath + ":Zone.Identifier");
            }
            catch { }

            int currentPid = Environment.ProcessId;
            string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');

            // 3. Dual-Mode Update Dispatch
            if (IsInstalledVersion)
            {
                ApplyInstalledUpdate(downloadPath, appDir, currentPid);
            }
            else
            {
                ApplyPortableUpdate(downloadPath, appDir, currentPid, tempDir);
            }
        }

        private static void ApplyInstalledUpdate(string installerPath, string appDir, int currentPid)
        {
            string tempDir = Path.GetTempPath();
            string scriptPath = Path.Combine(tempDir, "novasonics_update_installed.ps1");

            string escapedInstallerPath = installerPath.Replace("'", "''");
            string escapedAppDir = appDir.Replace("'", "''");

            string script = $@"
$pidToWait = {currentPid}
$installerPath = '{escapedInstallerPath}'
$appDir = '{escapedAppDir}'
$exePath = Join-Path $appDir 'NovaSonics.exe'

# Wait up to 10 seconds for current process to exit
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ((Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) -and ($sw.Elapsed.TotalSeconds -lt 10)) {{
    Start-Sleep -Milliseconds 200
}}

# Check if admin elevation is required
$isProgramFiles = $false
$pf = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFiles)
$pfX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
if (![string]::IsNullOrEmpty($pf) -and $appDir.StartsWith($pf, [StringComparison]::OrdinalIgnoreCase)) {{
    $isProgramFiles = $true
}}
if (![string]::IsNullOrEmpty($pfX86) -and $appDir.StartsWith($pfX86, [StringComparison]::OrdinalIgnoreCase)) {{
    $isProgramFiles = $true
}}

$setupArgs = '/SILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS'
try {{
    if ($isProgramFiles) {{
        Start-Process -FilePath $installerPath -ArgumentList $setupArgs -Verb RunAs -Wait
    }} else {{
        Start-Process -FilePath $installerPath -ArgumentList $setupArgs -Wait
    }}
}} catch {{}}

# Clean up installer artifact
try {{ Remove-Item -Path $installerPath -Force -ErrorAction SilentlyContinue }} catch {{}}

# Relaunch NovaSonics.exe
if (Test-Path $exePath) {{
    Start-Process -FilePath $exePath -WorkingDirectory $appDir
}}

# Self-delete script
try {{ Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue }} catch {{}}
";

            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);

            App.ExitApplication();
        }

        private static void ApplyPortableUpdate(string zipPath, string appDir, int currentPid, string tempDir)
        {
            string stagingDir = Path.Combine(tempDir, "NovaSonics_Portable_Staging");
            if (Directory.Exists(stagingDir))
            {
                try { Directory.Delete(stagingDir, true); } catch { }
            }
            Directory.CreateDirectory(stagingDir);

            ZipFile.ExtractToDirectory(zipPath, stagingDir);

            // Dynamically locate the directory inside staging that contains NovaSonics.exe
            var matches = Directory.GetFiles(stagingDir, "NovaSonics.exe", SearchOption.AllDirectories);
            if (matches.Length == 0)
            {
                throw new FileNotFoundException("NovaSonics.exe was not found inside the downloaded portable package.");
            }
            string sourcePayloadDir = Path.GetDirectoryName(matches[0])!;

            string scriptPath = Path.Combine(tempDir, "novasonics_update_portable.ps1");
            string escapedSourcePayloadDir = sourcePayloadDir.Replace("'", "''");
            string escapedAppDir = appDir.Replace("'", "''");
            string escapedStagingDir = stagingDir.Replace("'", "''");
            string escapedZipPath = zipPath.Replace("'", "''");

            string script = $@"
$pidToWait = {currentPid}
$sourcePayloadDir = '{escapedSourcePayloadDir}'
$targetAppDir = '{escapedAppDir}'
$stagingDir = '{escapedStagingDir}'
$zipPath = '{escapedZipPath}'
$exePath = Join-Path $targetAppDir 'NovaSonics.exe'

# Wait up to 10 seconds for current process to exit
$sw = [System.Diagnostics.Stopwatch]::StartNew()
while ((Get-Process -Id $pidToWait -ErrorAction SilentlyContinue) -and ($sw.Elapsed.TotalSeconds -lt 10)) {{
    Start-Sleep -Milliseconds 200
}}

# Copy all files and subdirectories into targetAppDir
Copy-Item -Path ""$sourcePayloadDir\*"" -Destination ""$targetAppDir"" -Recurse -Force

# Clean up staging and downloaded zip
try {{ Remove-Item -Path $stagingDir -Recurse -Force -ErrorAction SilentlyContinue }} catch {{}}
try {{ Remove-Item -Path $zipPath -Force -ErrorAction SilentlyContinue }} catch {{}}

# Relaunch NovaSonics.exe
if (Test-Path $exePath) {{
    Start-Process -FilePath $exePath -WorkingDirectory $targetAppDir
}}

# Self-delete script
try {{ Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue }} catch {{}}
";

            File.WriteAllText(scriptPath, script);

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            Process.Start(psi);

            App.ExitApplication();
        }
    }
}
