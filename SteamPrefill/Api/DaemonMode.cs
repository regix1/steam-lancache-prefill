#nullable enable

namespace SteamPrefill.Api;

/// <summary>
/// Runs SteamPrefill in daemon mode using Unix Domain Socket for IPC.
/// This is the recommended way to run SteamPrefill in a Docker container for web integration.
///
/// Features:
/// - Reliable bidirectional communication
/// - Low latency (&lt;1ms)
/// - Works in both host and bridge Docker network modes
/// - Real-time progress streaming
///
/// Security features:
/// - Login required before any commands
/// - Encrypted credential exchange (ECDH + AES-GCM)
/// - Plain text passwords never written to disk
/// - Secure memory handling for credentials
/// </summary>
public static class DaemonMode
{
    /// <summary>
    /// Run in socket-based daemon mode.
    /// Uses Unix Domain Socket for reliable, low-latency bidirectional communication.
    /// </summary>
    /// <param name="socketPath">Path to the Unix socket file (e.g., /responses/daemon.sock)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    public static async Task RunAsync(
        string socketPath = "/responses/daemon.sock",
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(GetUnixStartupMessage(socketPath));

        using var socketInterface = new SocketCommandInterface(socketPath);

        await socketInterface.StartAsync(cancellationToken);

        Console.WriteLine("Daemon started. Waiting for connections...");

        // Keep running until cancelled
        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        Console.WriteLine("Daemon stopped.");
    }

    /// <summary>
    /// Run in TCP-based daemon mode.
    /// Uses TCP for bidirectional communication (useful for Windows Docker Desktop).
    /// </summary>
    public static async Task RunTcpAsync(
        int port,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine(GetTcpStartupMessage(port));

        using var socketInterface = new SocketCommandInterface(port);

        await socketInterface.StartAsync(cancellationToken);

        Console.WriteLine("Daemon started. Waiting for connections...");

        try
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine("Daemon shutdown requested...");
        }

        await socketInterface.StopAsync();
        Console.WriteLine("Daemon stopped.");
    }

    internal static string GetUnixStartupMessage(string socketPath)
        => $"Starting SteamPrefill daemon on socket {socketPath}";

    internal static string GetTcpStartupMessage(int port)
        => $"Starting SteamPrefill daemon on TCP port {port}";
}

/// <summary>
/// Progress update sent via socket for external monitoring
/// </summary>
public class PrefillProgressUpdate
{
    public string? OperationId { get; set; }
    public string? ErrorCode { get; set; }
    public bool? RequiresLogin { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("state")]
    public string State { get; set; } = "idle";

    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string? Message { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppId")]
    public uint CurrentAppId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("currentAppName")]
    public string? CurrentAppName { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesDownloaded")]
    public long BytesDownloaded { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("percentComplete")]
    public double PercentComplete { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("bytesPerSecond")]
    public double BytesPerSecond { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsed")]
    public TimeSpan Elapsed { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("elapsedSeconds")]
    public double ElapsedSeconds => Elapsed.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("result")]
    public string? Result { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("errorMessage")]
    public string? ErrorMessage { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalApps")]
    public int TotalApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("updatedApps")]
    public int UpdatedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("alreadyUpToDate")]
    public int AlreadyUpToDate { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("failedApps")]
    public int FailedApps { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytesTransferred")]
    public long TotalBytesTransferred { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTime")]
    public TimeSpan TotalTime { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalTimeSeconds")]
    public double TotalTimeSeconds => TotalTime.TotalSeconds;

    [System.Text.Json.Serialization.JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("depots")]
    public List<DepotManifestUpdateInfo>? Depots { get; set; }
}

/// <summary>
/// Depot manifest info for cache tracking
/// </summary>
public class DepotManifestUpdateInfo
{
    [System.Text.Json.Serialization.JsonPropertyName("depotId")]
    public uint DepotId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("manifestId")]
    public ulong ManifestId { get; set; }

    [System.Text.Json.Serialization.JsonPropertyName("totalBytes")]
    public long TotalBytes { get; set; }
}
