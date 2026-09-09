using System.Text.Json.Serialization;

#nullable enable annotations

namespace SteamPrefill.Api;

/// <summary>
/// JSON serialization context for daemon API types.
/// Uses source generation for AOT compatibility.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CommandRequest))]
[JsonSerializable(typeof(CommandResponse))]
[JsonSerializable(typeof(CredentialChallenge))]
[JsonSerializable(typeof(EncryptedCredentialResponse))]
[JsonSerializable(typeof(List<OwnedGame>))]
[JsonSerializable(typeof(List<uint>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(PrefillResult))]
[JsonSerializable(typeof(StatusData))]
[JsonSerializable(typeof(PrefillProgressUpdate))]
[JsonSerializable(typeof(ClearCacheResult))]
[JsonSerializable(typeof(AppStatus))]
[JsonSerializable(typeof(SelectedAppsStatus))]
[JsonSerializable(typeof(DepotManifestUpdateInfo))]
[JsonSerializable(typeof(List<DepotManifestUpdateInfo>))]
[JsonSerializable(typeof(CacheStatusResult))]
[JsonSerializable(typeof(CachedDepotInput))]
[JsonSerializable(typeof(List<CachedDepotInput>))]
// Socket event types
[JsonSerializable(typeof(SocketEvent<CredentialChallenge>))]
[JsonSerializable(typeof(SocketEvent<PrefillProgressUpdate>))]
[JsonSerializable(typeof(SocketEvent<AuthStateData>))]
[JsonSerializable(typeof(AuthStateData))]
[JsonSerializable(typeof(object))] // Required for polymorphic Data property in CommandResponse
[JsonSerializable(typeof(AutoLoginCredentials))]
internal sealed partial class DaemonSerializationContext : JsonSerializerContext
{
}

/// <summary>
/// Auto-login credentials format for secure token exchange
/// </summary>
public sealed class AutoLoginCredentials
{
    public string Username { get; init; } = string.Empty;
    public string RefreshToken { get; init; } = string.Empty;
}

/// <summary>
/// Status data returned by the status command
/// </summary>
public class StatusData
{
    public bool IsLoggedIn { get; init; }
    public bool IsInitialized { get; init; }

    /// <summary>
    /// UTC expiry of the stored refresh-token JWT (ISO-8601), or null when not logged in / no token.
    /// Lets a manager show a login-expiry countdown.
    /// </summary>
    public DateTime? AuthExpiryUtc { get; init; }

    /// <summary>
    /// The logged-in Steam account username, or null when not available.
    /// </summary>
    public string? Username { get; init; }
}

/// <summary>
/// Command request sent from client to daemon via socket
/// </summary>
public class CommandRequest
{
    /// <summary>
    /// Unique request ID for tracking
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Command type (login, logout, prefill, status, etc.)
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// Command parameters (varies by command type)
    /// </summary>
    public Dictionary<string, string>? Parameters { get; set; }

    /// <summary>
    /// Timestamp when command was created
    /// </summary>
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Command response sent from daemon to client via socket
/// </summary>
public class CommandResponse
{
    public string? ErrorCode { get; set; }
    /// <summary>
    /// Request ID this response corresponds to
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Whether the command succeeded
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Human-readable message
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    /// Error message if failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Response data (varies by command type)
    /// </summary>
    public object? Data { get; set; }

    /// <summary>
    /// Whether login is required
    /// </summary>
    public bool RequiresLogin { get; set; }

    /// <summary>
    /// Timestamp when response was created
    /// </summary>
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}
