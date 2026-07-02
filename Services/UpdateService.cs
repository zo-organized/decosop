using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;

namespace DecoSOP.Services;

/// <summary>
/// Singleton service that periodically checks GitHub Releases for a newer version
/// and can download + apply updates in-place.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private readonly ILogger<UpdateService> _logger;
    private readonly HttpClient _http;
    private readonly Timer _timer;

    // Configurable via update-config.json next to the exe
    private bool _enabled = true;
    private string _repoOwner = "zo-organized";
    private string _repoName = "DecoSOP";
    private TimeSpan _checkInterval = TimeSpan.FromHours(24);
    private string? _skippedVersion;
    private bool _autoInstall;
    private string _autoInstallTime = "02:00";
    private DateTime _lastAutoInstallAttempt = DateTime.MinValue;

    public string CurrentVersion { get; }
    public string? NewVersion { get; private set; }
    public string? ReleaseUrl { get; private set; }
    public string? DownloadUrl { get; private set; }
    public string? ZipUrl { get; private set; }
    public string? ReleaseNotes { get; private set; }
    public bool UpdateAvailable => NewVersion is not null;

    // Download / install state
    public bool IsDownloading { get; private set; }
    public double DownloadProgress { get; private set; }
    public bool IsInstalling { get; private set; }
    public string? UpdateError { get; private set; }

    // Config exposed for Settings UI
    public bool Enabled => _enabled;
    public bool AutoInstall => _autoInstall;
    public string AutoInstallTime => _autoInstallTime;

    public event Action? OnUpdateChecked;

    public UpdateService(ILogger<UpdateService> logger)
    {
        _logger = logger;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DecoSOP-UpdateChecker/1.0");

        CurrentVersion = Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString(3) ?? "0.0.0";

        LoadConfig();

        // Initial check after 30 seconds, then every _checkInterval
        _timer = new Timer(async _ => await PeriodicCheckAsync(),
            null,
            _enabled ? TimeSpan.FromSeconds(30) : Timeout.InfiniteTimeSpan,
            _enabled ? _checkInterval : Timeout.InfiniteTimeSpan);
    }

    private async Task PeriodicCheckAsync()
    {
        await CheckForUpdateAsync();

        // Auto-install if configured
        if (_autoInstall && UpdateAvailable && ZipUrl is not null && !IsDownloading && !IsInstalling)
        {
            if (TimeSpan.TryParse(_autoInstallTime, out var scheduledTime))
            {
                var now = DateTime.Now;
                var diff = (now.TimeOfDay - scheduledTime).Duration();
                // Within 15-minute window of scheduled time, and haven't tried today
                if (diff <= TimeSpan.FromMinutes(15) && _lastAutoInstallAttempt.Date != now.Date)
                {
                    _lastAutoInstallAttempt = now;
                    _logger.LogInformation("Auto-install triggered at scheduled time {Time}", _autoInstallTime);
                    await DownloadAndInstallAsync();
                }
            }
        }
    }

    public async Task CheckForUpdateAsync()
    {
        if (!_enabled) return;

        try
        {
            var url = $"https://api.github.com/repos/{_repoOwner}/{_repoName}/releases/latest";
            var response = await _http.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Update check returned {Status}", response.StatusCode);
                return;
            }

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();

            var tagName = json.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            var htmlUrl = json.GetProperty("html_url").GetString() ?? "";
            var body = json.TryGetProperty("body", out var bodyProp) ? bodyProp.GetString() ?? "" : "";

            // Find installer and zip assets separately
            string? installerUrl = null;
            string? zipUrl = null;
            if (json.TryGetProperty("assets", out var assets))
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.GetProperty("name").GetString() ?? "";
                    var downloadUrl = asset.GetProperty("browser_download_url").GetString();

                    if (installerUrl is null &&
                        name.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = downloadUrl;
                    }
                    else if (zipUrl is null && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        zipUrl = downloadUrl;
                    }
                }
            }

            if (!Version.TryParse(tagName, out var remoteVer) ||
                !Version.TryParse(CurrentVersion, out var localVer))
            {
                _logger.LogDebug("Could not parse versions: remote={Remote}, local={Local}", tagName, CurrentVersion);
                return;
            }

            if (remoteVer > localVer && tagName != _skippedVersion)
            {
                NewVersion = tagName;
                ReleaseUrl = htmlUrl;
                DownloadUrl = installerUrl ?? zipUrl ?? htmlUrl;
                ZipUrl = zipUrl;
                ReleaseNotes = body.Length > 500 ? body[..500] + "..." : body;
                _logger.LogInformation("Update available: {Version}", tagName);
            }
            else
            {
                NewVersion = null;
                ReleaseUrl = null;
                DownloadUrl = null;
                ZipUrl = null;
                ReleaseNotes = null;
            }

            OnUpdateChecked?.Invoke();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Update check failed");
        }
    }

    public async Task DownloadAndInstallAsync()
    {
        var downloadUrl = ZipUrl;
        if (downloadUrl is null)
        {
            UpdateError = "No ZIP download available for this release.";
            OnUpdateChecked?.Invoke();
            return;
        }

        if (IsDownloading || IsInstalling) return;

        IsDownloading = true;
        DownloadProgress = 0;
        UpdateError = null;
        OnUpdateChecked?.Invoke();

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var stagingDir = Path.Combine(baseDir, "update-staging");
            var zipPath = Path.Combine(stagingDir, "update.zip");
            var extractDir = Path.Combine(stagingDir, "files");

            // Clean up any previous staging
            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, true);
            Directory.CreateDirectory(stagingDir);

            // Download ZIP with progress
            _logger.LogInformation("Downloading update from {Url}", downloadUrl);
            using var response = await _http.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1;
            await using var contentStream = await response.Content.ReadAsStreamAsync();
            await using var fileStream = new FileStream(zipPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true);

            var buffer = new byte[81920];
            long bytesRead = 0;
            int read;
            while ((read = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, read));
                bytesRead += read;
                if (totalBytes > 0)
                {
                    DownloadProgress = (double)bytesRead / totalBytes;
                    OnUpdateChecked?.Invoke();
                }
            }

            DownloadProgress = 1.0;
            IsDownloading = false;
            OnUpdateChecked?.Invoke();

            // Extract ZIP
            _logger.LogInformation("Extracting update to {Dir}", extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, true);

            // Write the PowerShell update script
            var scriptPath = Path.Combine(stagingDir, "apply-update.ps1");
            var script = GenerateUpdateScript(baseDir, extractDir);
            await File.WriteAllTextAsync(scriptPath, script);

            // Launch the script as a detached process
            _logger.LogInformation("Launching update script: {Script}", scriptPath);
            IsInstalling = true;
            OnUpdateChecked?.Invoke();

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update download/install failed");
            IsDownloading = false;
            IsInstalling = false;
            UpdateError = $"Update failed: {ex.Message}";
            OnUpdateChecked?.Invoke();
        }
    }

    private static string GenerateUpdateScript(string installDir, string stagingFilesDir)
    {
        // Escape backslashes for PowerShell string literals
        var install = installDir.TrimEnd('\\');
        var staging = stagingFilesDir.TrimEnd('\\');

        return $@"# apply-update.ps1 — generated by DecoSOP UpdateService
$ErrorActionPreference = 'Stop'
$serviceName = 'DecoSOP'
$installDir = '{install}'
$stagingDir = '{staging}'
$logFile = Join-Path (Split-Path $stagingDir) 'update.log'

Start-Transcript -Path $logFile -Force

Write-Host 'Stopping service...'
Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 3

# Wait for exe to release (up to 30 seconds)
$exe = Join-Path $installDir 'DecoSOP.exe'
for ($i = 0; $i -lt 10; $i++) {{
    try {{
        $fs = [IO.File]::OpenWrite($exe)
        $fs.Close()
        break
    }} catch {{
        Write-Host ""Waiting for exe to release ($i)...""
        Start-Sleep -Seconds 3
    }}
}}

Write-Host 'Copying new files...'
$skipPatterns = @('decosop.db', 'doc-uploads', 'sop-uploads', 'port.config', 'update-config.json', 'update-staging')

Get-ChildItem $stagingDir -Recurse | ForEach-Object {{
    $rel = $_.FullName.Substring($stagingDir.Length + 1)
    $skip = $false
    foreach ($pattern in $skipPatterns) {{
        if ($rel -eq $pattern -or $rel.StartsWith($pattern + '\')) {{
            $skip = $true
            break
        }}
    }}
    if ($skip) {{ return }}

    $dest = Join-Path $installDir $rel
    if ($_.PSIsContainer) {{
        New-Item -ItemType Directory -Path $dest -Force | Out-Null
    }} else {{
        $destDir = Split-Path $dest
        if (-not (Test-Path $destDir)) {{
            New-Item -ItemType Directory -Path $destDir -Force | Out-Null
        }}
        Copy-Item $_.FullName $dest -Force
    }}
}}

Write-Host 'Starting service...'
Start-Service -Name $serviceName
Start-Sleep -Seconds 2

Write-Host 'Cleaning up staging...'
$stagingRoot = Split-Path $stagingDir
Remove-Item $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host 'Update complete.'
Stop-Transcript
";
    }

    public void SkipVersion(string version)
    {
        _skippedVersion = version;
        NewVersion = null;
        ReleaseUrl = null;
        DownloadUrl = null;
        ZipUrl = null;
        ReleaseNotes = null;
        SaveConfig();
        OnUpdateChecked?.Invoke();
    }

    public void Dismiss()
    {
        NewVersion = null;
        ReleaseUrl = null;
        DownloadUrl = null;
        ZipUrl = null;
        ReleaseNotes = null;
        OnUpdateChecked?.Invoke();
    }

    public void EnableChecks(bool enabled)
    {
        _enabled = enabled;
        if (!enabled)
            _autoInstall = false;
        SaveConfig();

        // Restart or stop the timer
        if (_enabled)
            _timer.Change(TimeSpan.FromSeconds(5), _checkInterval);
        else
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        OnUpdateChecked?.Invoke();
    }

    public void SetAutoInstall(bool enabled, string? time)
    {
        _autoInstall = enabled;
        if (time is not null)
            _autoInstallTime = time;
        SaveConfig();
        OnUpdateChecked?.Invoke();
    }

    private void LoadConfig()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "update-config.json");
            if (!File.Exists(configPath)) return;

            var json = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(configPath));

            if (json.TryGetProperty("enabled", out var enabled))
                _enabled = enabled.GetBoolean();
            if (json.TryGetProperty("repoOwner", out var owner))
                _repoOwner = owner.GetString() ?? _repoOwner;
            if (json.TryGetProperty("repoName", out var name))
                _repoName = name.GetString() ?? _repoName;
            if (json.TryGetProperty("checkIntervalHours", out var hours))
                _checkInterval = TimeSpan.FromHours(hours.GetInt32());
            if (json.TryGetProperty("skippedVersion", out var skipped))
                _skippedVersion = skipped.GetString();
            if (json.TryGetProperty("autoInstall", out var autoInstall))
                _autoInstall = autoInstall.GetBoolean();
            if (json.TryGetProperty("autoInstallTime", out var autoTime))
                _autoInstallTime = autoTime.GetString() ?? _autoInstallTime;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load update config");
        }
    }

    private void SaveConfig()
    {
        try
        {
            var configPath = Path.Combine(AppContext.BaseDirectory, "update-config.json");
            var config = new
            {
                enabled = _enabled,
                repoOwner = _repoOwner,
                repoName = _repoName,
                checkIntervalHours = (int)_checkInterval.TotalHours,
                skippedVersion = _skippedVersion,
                autoInstall = _autoInstall,
                autoInstallTime = _autoInstallTime
            };
            File.WriteAllText(configPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to save update config");
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }
}
