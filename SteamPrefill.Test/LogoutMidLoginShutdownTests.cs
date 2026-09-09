using SteamPrefill.Api;
using Xunit;

namespace SteamPrefill.Test
{
    /// <summary>
    /// SocketCommandInterface itself has no test seam (it owns a live SocketServer and only reaches
    /// _isLoggedIn=true via a real Steam login), so this covers the underlying invariant its
    /// complete-forget contract depends on: <see cref="SteamPrefillApi.Shutdown"/> must tear down the
    /// live client even when a login never got far enough for IsInitialized to become true - exactly
    /// the shape a logout leaves behind when it races and cancels an in-flight login. Before the fix,
    /// Shutdown() was gated on _isInitialized and silently no-op'd in this state (diagnostic:
    /// SteamPrefillApi.cs Shutdown L353-361).
    /// </summary>
    [Collection("SteamAccountFile")]
    public sealed class LogoutMidLoginShutdownTests
    {
        [Fact]
        public async Task Shutdown_AfterCancelledMidInitialize_DoesNotThrow_AndStaysUninitialized()
        {
            using var api = new SteamPrefillApi(new StaticAuthProvider("testuser", "testpass"));
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            // InitializeAsync constructs SteamManager/Steam3Session synchronously before the first
            // await, then throws immediately on the pre-cancelled token (ConfigureLoginDetailsAsync's
            // first line is ThrowIfCancellationRequested). This reproduces the exact
            // "_steamManager constructed, _isInitialized still false" shape left behind by a logout
            // that cancels an in-flight login task before it finishes connecting.
            await Assert.ThrowsAsync<OperationCanceledException>(() => api.InitializeAsync(cts.Token));
            Assert.False(api.IsInitialized);

            var ex = Record.Exception(() => api.Shutdown());

            Assert.Null(ex);
            Assert.False(api.IsInitialized);

            // Idempotent: logout's CleanupApiInstance calls Shutdown() then Dispose() in sequence -
            // both must tolerate being invoked again on an already-torn-down instance.
            var ex2 = Record.Exception(() => api.Shutdown());
            Assert.Null(ex2);
        }
    }
}
