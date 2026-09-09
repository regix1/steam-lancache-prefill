#nullable enable

using SteamPrefill.Models;
using SteamPrefill.Models.Enums;

namespace SteamPrefill.Api;

/// <summary>
/// High-level programmatic API for Steam Prefill operations.
/// Use this instead of SteamManager for non-CLI applications.
/// </summary>
public sealed class SteamPrefillApi : IDisposable
{
    private readonly ISteamAuthProvider _authProvider;
    private readonly IPrefillProgress _progress;
    private readonly Action<Action>? _commitCredentials;
    public event Action<EResult?>? AuthenticationLost;

    private SteamManager? _steamManager;

    // In-memory cache for selected apps in daemon mode - avoids file I/O issues in containers
    private List<uint>? _selectedAppsCache;
    private volatile bool _isInitialized;
    private volatile bool _isDisposed;
    private readonly object _sync = new();

    /// <summary>
    /// Creates a new Steam Prefill API instance
    /// </summary>
    /// <param name="authProvider">Provider for Steam authentication credentials</param>
    /// <param name="progress">Optional progress reporter for status updates</param>
    public SteamPrefillApi(
        ISteamAuthProvider authProvider,
        IPrefillProgress? progress = null,
        Action<Action>? commitCredentials = null)
    {
        _authProvider = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _progress = progress ?? NullProgress.Instance;
        _commitCredentials = commitCredentials;
    }

    /// <summary>
    /// Whether the API is initialized and logged into Steam
    /// </summary>
    public bool IsInitialized
    {
        get
        {
            lock (_sync) return _isInitialized && !_isDisposed && _steamManager?.IsAuthenticated == true;
        }
    }
    internal string? Username => _steamManager?.Username;
    internal DateTime? AuthExpiryUtc => _steamManager?.AuthExpiryUtc;

    /// <summary>
    /// Initializes the API and logs into Steam.
    /// Must be called before any other operations.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (_isInitialized)
            return;

        _progress.OnOperationStarted("Initializing Steam connection");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Create console adapter that routes through our auth provider
            var consoleAdapter = new ApiConsoleAdapter(_authProvider, _progress);

            // Default to ALL operating systems since Lancache serves clients on any platform
            var downloadArgs = new DownloadArguments
            {
                Force = false,
                TransferSpeedUnit = TransferSpeedUnit.Bits,
                OperatingSystems = new List<OperatingSystem> { OperatingSystem.Windows, OperatingSystem.Linux, OperatingSystem.MacOS }
            };

            var manager = new SteamManager(consoleAdapter, downloadArgs, _authProvider, _progress, _commitCredentials);
            lock (_sync)
            {
                if (_isDisposed)
                {
                    manager.Dispose();
                    throw new ObjectDisposedException(nameof(SteamPrefillApi));
                }
                _steamManager = manager;
                manager.AuthenticationLost += result => AuthenticationLost?.Invoke(result);
            }

            await _steamManager.InitializeAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_steamManager.IsAuthenticated) throw new SteamConnectionException(SteamFailure.AuthLost);
            _isInitialized = true;

            _progress.OnOperationCompleted("Initializing Steam connection", timer.Elapsed);
            _progress.OnLog(LogLevel.Info, "Successfully logged into Steam");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Steam initialization cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to initialize Steam connection", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets all games owned by the logged-in user
    /// </summary>
    public async Task<List<OwnedGame>> GetOwnedGamesAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        _progress.OnOperationStarted("Fetching owned games");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var apps = await _steamManager!.GetAllAvailableAppsAsync(cancellationToken);
            var result = apps.Select(a => new OwnedGame
            {
                AppId = a.AppId,
                Name = a.Name,
                MinutesPlayedLast2Weeks = a.MinutesPlayed2Weeks ?? 0,
                ReleaseDate = a.ReleaseDate.HasValue ? DateOnly.FromDateTime(a.ReleaseDate.Value) : null
            }).ToList();

            _progress.OnOperationCompleted("Fetching owned games", timer.Elapsed);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Fetching owned games cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to fetch owned games", ex);
            throw;
        }
    }

    /// <summary>
    /// Gets the list of previously selected app IDs
    /// </summary>
    public List<uint> GetSelectedApps()
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        // Prefer in-memory cache for daemon mode reliability
        if (_selectedAppsCache != null && _selectedAppsCache.Count > 0)
        {
            _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Returning {_selectedAppsCache.Count} cached apps");
            return _selectedAppsCache;
        }

        // Fall back to file-based storage for CLI mode
        var fileApps = _steamManager!.LoadPreviouslySelectedApps();
        _progress.OnLog(LogLevel.Info, $"GetSelectedApps: Loaded {fileApps.Count} apps from file");
        return fileApps;
    }


    /// <summary>
    /// Gets detailed status information for selected apps including download sizes.
    /// Requires login to be completed.
    /// </summary>
    public async Task<SelectedAppsStatus> GetSelectedAppsStatusAsync(List<CachedDepotInput>? cachedDepots = null, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var appIds = GetSelectedApps();

        if (appIds.Count == 0)
        {
            return new SelectedAppsStatus
            {
                Apps = new List<AppStatus>(),
                TotalDownloadSize = 0,
                Message = "No apps selected"
            };
        }

        try
        {
            var appStatuses = await _steamManager!.GetSelectedAppsStatusAsync(
                appIds,
                cachedDepots,
                cancellationToken);
            var totalSize = appStatuses.Sum(a => a.DownloadSize);
            var totalSizeFormatted = ByteSize.FromBytes(totalSize);

            return new SelectedAppsStatus
            {
                Apps = appStatuses,
                TotalDownloadSize = totalSize,
                Message = $"{appStatuses.Count} apps selected, {totalSizeFormatted.ToDecimalString()} total"
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Selected apps status request cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to get selected apps status", ex);
            throw;
        }
    }


    /// <summary>
    /// Checks cache status by comparing cached depot manifests against Steam's current manifests.
    /// This allows accurate detection of which apps are truly up-to-date even when daemon restarts.
    /// </summary>
    public async Task<CacheStatusResult> CheckCacheStatusAsync(List<CachedDepotInput> cachedDepots, CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        if (cachedDepots.Count == 0)
        {
            return new CacheStatusResult
            {
                Apps = new List<AppCacheStatus>(),
                Message = "No cached depots provided"
            };
        }

        try
        {
            return await _steamManager!.CheckCacheStatusAsync(cachedDepots, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Cache status request cancelled");
            throw;
        }
        catch (Exception ex)
        {
            _progress.OnError("Failed to check cache status", ex);
            throw;
        }
    }

    /// <summary>
    /// Sets the list of app IDs to prefill
    /// </summary>
    public void SetSelectedApps(IEnumerable<uint> appIds)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var appIdList = appIds.ToList();

        // Cache in memory for daemon mode reliability
        _selectedAppsCache = appIdList;

        var tuiApps = appIdList.Select(id => new TuiAppInfo(id.ToString(), "")
        {
            IsSelected = true
        }).ToList();

        _steamManager!.SetAppsAsSelected(tuiApps);
        _progress.OnLog(LogLevel.Info, $"Set {tuiApps.Count} apps for prefill (cached in memory)");
    }

    /// <summary>
    /// Populates the internal cache with externally provided cached depot manifest data.
    /// This allows the daemon to know which games are already cached without having downloaded them in this session.
    /// Used by lancache-manager to restore cache state after daemon restart.
    /// Call this BEFORE starting a prefill to ensure cached games are recognized.
    /// </summary>
    /// <param name="cachedDepots">List of cached depot info with depot ID and manifest ID</param>
    public void SetCachedManifests(IEnumerable<(uint DepotId, ulong ManifestId)> cachedDepots)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var depotList = cachedDepots.ToList();
        _steamManager!.SetCachedManifests(depotList);
        _progress.OnLog(LogLevel.Info, $"Set {depotList.Count} cached depot manifests from lancache-manager");
    }

    /// <summary>
    /// Clears all cached manifests from the internal cache.
    /// This forces all games to be re-evaluated on the next prefill.
    /// Used by lancache-manager when clearing the prefill cache database.
    /// </summary>
    /// <returns>The number of depots that were cleared</returns>
    public int ClearCachedManifests()
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        var count = _steamManager!.ClearCachedManifests();
        _progress.OnLog(LogLevel.Info, $"Cleared {count} cached depot manifests");
        return count;
    }

    /// <summary>
    /// Runs the prefill operation with the specified options
    /// </summary>
    [SuppressMessage("Design", "CA1068", Justification = "Preserves existing positional cancellation callers.")]
    public async Task<PrefillResult> PrefillAsync(
        PrefillOptions? options = null,
        CancellationToken cancellationToken = default,
        IPrefillProgress? progress = null)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        options ??= new PrefillOptions();

        // Update download options before starting
        _steamManager!.UpdateDownloadOptions(
            force: options.Force,
            operatingSystems: options.OperatingSystems);

        _progress.OnOperationStarted("Prefill operation");
        var timer = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            await _steamManager!.DownloadMultipleAppsAsync(
                downloadAllOwnedGames: options.DownloadAllOwnedGames,
                prefillRecentGames: options.PrefillRecentGames,
                prefillPopularGames: options.PrefillTopGames,
                prefillRecentlyPurchasedGames: options.PrefillRecentlyPurchased,
                cancellationToken: cancellationToken,
                progress: progress);

            _progress.OnOperationCompleted("Prefill operation", timer.Elapsed);

            // Return result summary
            return new PrefillResult
            {
                Success = true,
                TotalTime = timer.Elapsed
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _progress.OnLog(LogLevel.Info, "Prefill operation cancelled");
            throw;
        }
        catch (Exception ex)
        {
            // Only the message is broadcast to the caller; the exception argument goes to the local log.
            // Without the reason in the message the caller is told the prefill failed and nothing more. [29]
            _progress.OnError("Prefill operation failed", ex);
            return new PrefillResult
            {
                Success = false,
                ErrorMessage = ex is SteamConnectionException { Failure: not null } ? ex.Message : "The prefill daemon could not complete the request. Try again.",
                ErrorCode = (ex as SteamConnectionException)?.ErrorCode,
                RequiresLogin = (ex as SteamConnectionException)?.RequiresLogin == true,
                Exception = ex,
                TotalTime = timer.Elapsed
            };
        }
    }

    /// <summary>
    /// Prefills specific apps by their IDs
    /// </summary>
    public async Task<PrefillResult> PrefillAppsAsync(
        IEnumerable<uint> appIds,
        bool force = false,
        CancellationToken cancellationToken = default)
    {
        ThrowIfNotInitialized();
        ThrowIfDisposed();

        // Set the apps and run prefill
        SetSelectedApps(appIds);

        return await PrefillAsync(new PrefillOptions { Force = force }, cancellationToken);
    }

    /// <summary>
    /// Disconnects from Steam. Unconditional on _isInitialized: _steamManager (and its
    /// Steam3Session) is constructed synchronously before InitializeAsync's login handshake
    /// completes, so a logout racing a mid-login task must still be able to tear down the live
    /// client even though _isInitialized never flipped true.
    /// </summary>
    public void Shutdown()
    {
        if (_steamManager != null)
        {
            _steamManager.Shutdown();
            _isInitialized = false;
            _progress.OnLog(LogLevel.Info, "Disconnected from Steam");
        }
    }

    /// <summary>
    /// Clears the in-memory app info cache.
    /// Should be called when clearing disk cache to ensure data consistency.
    /// </summary>
    public void ClearAppInfoCache()
    {
        if (_steamManager != null && _isInitialized)
        {
            _steamManager.ClearAppInfoCache();
            _progress.OnLog(LogLevel.Info, "Cleared in-memory app info cache");
        }
    }

    /// <summary>
    /// Updates download options that affect filtering (e.g., operating systems).
    /// </summary>
    public void UpdateDownloadOptions(bool? force = null, List<OperatingSystem>? operatingSystems = null)
    {
        if (_steamManager != null && _isInitialized)
        {
            _steamManager.UpdateDownloadOptions(force, operatingSystems);
        }
    }

    /// <summary>
    /// Clears the temporary cache directory to free up disk space.
    /// This is a static method that doesn't require initialization.
    /// </summary>
    /// <returns>Cache clear result with file count and total size cleared</returns>
    public static ClearCacheResult ClearCache()
    {
        var tempDir = new DirectoryInfo(AppConfig.TempDir);

        if (!tempDir.Exists)
        {
            return new ClearCacheResult
            {
                Success = true,
                FileCount = 0,
                BytesCleared = 0,
                Message = "Cache directory is already empty"
            };
        }

        var tempFiles = tempDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        var totalBytes = tempFiles.Sum(e => e.Length);
        var fileCount = tempFiles.Count;

        if (fileCount == 0)
        {
            return new ClearCacheResult
            {
                Success = true,
                FileCount = 0,
                BytesCleared = 0,
                Message = "Cache directory is already empty"
            };
        }

        try
        {
            Directory.Delete(tempDir.FullName, true);
            // Recreate the temp directory so future operations can use it
            Directory.CreateDirectory(AppConfig.TempDir);
            var clearedSize = ByteSize.FromBytes(totalBytes);
            return new ClearCacheResult
            {
                Success = true,
                FileCount = fileCount,
                BytesCleared = totalBytes,
                Message = $"Cleared {fileCount} files ({clearedSize.ToDecimalString()})"
            };
        }
        catch (Exception ex)
        {
            return new ClearCacheResult
            {
                Success = false,
                FileCount = 0,
                BytesCleared = 0,
                Message = $"Failed to clear cache: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Gets information about the current cache without clearing it.
    /// </summary>
    public static ClearCacheResult GetCacheInfo()
    {
        var tempDir = new DirectoryInfo(AppConfig.TempDir);

        if (!tempDir.Exists)
        {
            return new ClearCacheResult
            {
                Success = true,
                FileCount = 0,
                BytesCleared = 0,
                Message = "Cache directory is empty"
            };
        }

        var tempFiles = tempDir.EnumerateFiles("*.*", SearchOption.AllDirectories).ToList();
        var totalBytes = tempFiles.Sum(e => e.Length);
        var cacheSize = ByteSize.FromBytes(totalBytes);

        return new ClearCacheResult
        {
            Success = true,
            FileCount = tempFiles.Count,
            BytesCleared = totalBytes,
            Message = $"Cache contains {tempFiles.Count} files ({cacheSize.ToDecimalString()})"
        };
    }

    public void Dispose()
    {
        SteamManager? manager;
        lock (_sync)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _isInitialized = false;
            manager = _steamManager;
        }
        manager?.Shutdown();
        manager?.Dispose();
    }

    private void ThrowIfNotInitialized()
    {
        if (!IsInitialized)
            throw new SteamConnectionException(SteamFailure.AuthLost);
    }

    private void ThrowIfDisposed()
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(SteamPrefillApi));
    }
}

/// <summary>
/// Options for prefill operations
/// </summary>
public class PrefillOptions
{
    /// <summary>
    /// Download all owned games
    /// </summary>
    public bool DownloadAllOwnedGames { get; set; }

    /// <summary>
    /// Include games played in the last 2 weeks
    /// </summary>
    public bool PrefillRecentGames { get; set; }

    /// <summary>
    /// Include recently purchased games (last 2 weeks)
    /// </summary>
    public bool PrefillRecentlyPurchased { get; set; }

    /// <summary>
    /// Number of top games by player count to prefill (null = disabled)
    /// </summary>
    public int? PrefillTopGames { get; set; }

    /// <summary>
    /// Force re-download even if already up to date
    /// </summary>
    public bool Force { get; set; }

    /// <summary>
    /// Target operating systems for downloads. Defaults to the current OS.
    /// </summary>
    public List<OperatingSystem> OperatingSystems { get; set; } = new() { GetCurrentOperatingSystem() };

    public static OperatingSystem GetCurrentOperatingSystem()
    {
        if (System.OperatingSystem.IsLinux())
            return OperatingSystem.Linux;
        if (System.OperatingSystem.IsMacOS())
            return OperatingSystem.MacOS;
        return OperatingSystem.Windows;
    }
}

/// <summary>
/// Result of a prefill operation
/// </summary>
public class PrefillResult
{
    public string? ErrorCode { get; init; }
    public bool? RequiresLogin { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    internal Exception? Exception { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan TotalTime { get; init; }
}


/// <summary>
/// Result of a cache clear operation
/// </summary>
public class ClearCacheResult
{
    public bool Success { get; init; }
    public int FileCount { get; init; }
    public long BytesCleared { get; init; }
    public string? Message { get; init; }
}


/// <summary>
/// Status information for a single app
/// </summary>
public class AppStatus
{
    public uint AppId { get; init; }
    public string Name { get; init; } = "";
    public long DownloadSize { get; init; }
    public bool IsUpToDate { get; init; }
    /// <summary>
    /// If true, this game has no depots for the selected operating systems
    /// </summary>
    public bool IsUnsupportedOs { get; init; }
    /// <summary>
    /// Human-readable reason why the game is unavailable (e.g., "Not available for Linux")
    /// </summary>
    public string? UnavailableReason { get; init; }
}

/// <summary>
/// Status information for all selected apps
/// </summary>
public class SelectedAppsStatus
{
    public List<AppStatus> Apps { get; init; } = new();
    public long TotalDownloadSize { get; init; }
    public string? Message { get; init; }
}


/// <summary>
/// Input for cached depot manifest info from lancache-manager.
/// Uses camelCase JSON from lancache-manager with manifestId as string for large numbers.
/// </summary>
public class CachedDepotInput
{
    public uint AppId { get; init; }
    public uint DepotId { get; init; }

    [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)]
    public ulong ManifestId { get; init; }
}

/// <summary>
/// Result of cache status check for all apps.
/// </summary>
public class CacheStatusResult
{
    public List<AppCacheStatus> Apps { get; init; } = new();
    public string? Message { get; init; }
}

/// <summary>
/// Cache status for a single app.
/// </summary>
public class AppCacheStatus
{
    public uint AppId { get; init; }
    public string Name { get; init; } = "";
    public bool IsUpToDate { get; init; }
    public long DownloadSize { get; init; }
    public List<OutdatedDepot> OutdatedDepots { get; init; } = new();
}

/// <summary>
/// Details about an outdated depot that needs updating.
/// </summary>
public class OutdatedDepot
{
    public uint DepotId { get; init; }
    public ulong CachedManifest { get; init; }
    public ulong CurrentManifest { get; init; }
}

/// <summary>
/// Represents an owned game
/// </summary>
public class OwnedGame
{
    public uint AppId { get; init; }
    public string Name { get; init; } = string.Empty;
    public int MinutesPlayedLast2Weeks { get; init; }
    public DateOnly? ReleaseDate { get; init; }
}
