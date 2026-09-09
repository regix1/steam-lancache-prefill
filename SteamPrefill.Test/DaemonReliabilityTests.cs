using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using LancachePrefill.Common;
using Moq;
using Spectre.Console;
using Spectre.Console.Testing;
using SteamKit2;
using SteamKit2.CDN;
using SteamPrefill.Api;
using SteamPrefill.Handlers;
using SteamPrefill.Handlers.Steam;
using SteamPrefill.Models;
using SteamPrefill.Models.Exceptions;
using Xunit;

namespace SteamPrefill.Test;

[Collection("SteamAccountFile")]
public sealed class DaemonReliabilityTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task PicsFailure_UsesLiveAuthenticationAndPreservesCause(bool productStage, bool lost)
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, !lost);
        var cause = new AsyncJobFailedException();
        var tokens = (SteamApps.PICSTokensCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamApps.PICSTokensCallback));
        typeof(SteamApps.PICSTokensCallback).GetProperty(nameof(SteamApps.PICSTokensCallback.AppTokens))!.SetValue(tokens, new Dictionary<uint, ulong>());
        var handler = new AppInfoHandler(new TestConsole(), session, session.LicenseManager,
            _ => productStage ? Task.FromResult(tokens) : Task.FromException<SteamApps.PICSTokensCallback>(cause),
            _ => Task.FromException<AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet>(cause));

        var error = await Assert.ThrowsAsync<SteamConnectionException>(() => handler.GetAppInfoAsync(222));
        Assert.Same(cause, error.InnerException);
        Assert.Equal(lost ? SteamFailure.AuthLost : SteamFailure.GameDetailsUnavailable, error.Failure);
        Assert.Empty(handler.LoadedAppInfos);
        Assert.DoesNotContain(nameof(AsyncJobFailedException), error.Message, StringComparison.Ordinal);
        using var context = JsonDocument.Parse(error.GetContext("get-owned-games", "request-1"));
        Assert.Equal(productStage ? "product-details" : "access-tokens", context.RootElement.GetProperty("picsStage").GetString());
        Assert.Equal("request-1", context.RootElement.GetProperty("operationId").GetString());
        Assert.Equal(222u, context.RootElement.GetProperty("appIds")[0].GetUInt32());
        Assert.Equal(!lost, session.IsAuthenticated);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IdleSteamLoss_IsConsumedByPumpAndInvalidatesApi(bool disconnect)
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var losses = 0;
        session.AuthenticationLost += _ => { Interlocked.Increment(ref losses); lost.TrySetResult(); };
        var client = (SteamClient)typeof(Steam3Session).GetField("_steamClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        CallbackMsg callback = disconnect
            ? (SteamClient.DisconnectedCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamClient.DisconnectedCallback))
            : (SteamUser.LoggedOffCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
        typeof(CallbackMsg).GetProperty(nameof(CallbackMsg.JobID))!.SetValue(callback, new JobID(0));
        client.PostCallback(callback);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(session.IsAuthenticated);
        client.PostCallback(callback);
        session.Disconnect();
        Assert.Equal(1, losses);
        var pump = (Task)typeof(Steam3Session).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        Assert.True(pump.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SessionLoss_UnblocksLicenseWait()
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        var waiting = session.WaitForLicenseCallback();
        session.Disconnect();
        var error = await Assert.ThrowsAsync<SteamConnectionException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(SteamFailure.AuthLost, error.Failure);
    }

    [Fact]
    public async Task LoggedOffBeforeInitializationContinuation_CannotRestoreReadiness()
    {
        using var session = new Steam3Session(new TestConsole());
        using var api = new SteamPrefillApi(new StaticAuthProvider("test", "test"));
        typeof(SteamPrefillApi).GetField("_steamManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api,
            new SteamManager(new TestConsole(), new DownloadArguments(), session));
        var client = (SteamClient)typeof(Steam3Session).GetField("_steamClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        var loggedOn = (SteamUser.LoggedOnCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOnCallback));
        typeof(CallbackMsg).GetProperty(nameof(CallbackMsg.JobID))!.SetValue(loggedOn, new JobID(0));
        typeof(SteamUser.LoggedOnCallback).GetProperty(nameof(SteamUser.LoggedOnCallback.Result))!.SetValue(loggedOn, EResult.OK);
        var loggedOff = (SteamUser.LoggedOffCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
        typeof(CallbackMsg).GetProperty(nameof(CallbackMsg.JobID))!.SetValue(loggedOff, new JobID(0));
        var lost = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AuthenticationLost += _ => lost.TrySetResult();
        client.PostCallback(loggedOn);
        client.PostCallback(loggedOff);
        await lost.Task.WaitAsync(TimeSpan.FromSeconds(2));
        typeof(SteamPrefillApi).GetField("_isInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, true);
        Assert.False(api.IsInitialized);
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public async Task CallbackShutdown_DoesNotWaitOnItsOwnPump()
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        session.AuthenticationLost += _ => { session.Disconnect(); stopped.TrySetResult(); };
        var callback = (SteamUser.LoggedOffCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamUser.LoggedOffCallback));
        typeof(CallbackMsg).GetProperty(nameof(CallbackMsg.JobID))!.SetValue(callback, new JobID(0));
        var client = (SteamClient)typeof(Steam3Session).GetField("_steamClient", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(session)!;
        client.PostCallback(callback);
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(session.IsAuthenticated);
    }

    [Fact]
    public async Task TerminalPublication_KeepsSuccessorAdmissionClosed()
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        using var api = new SteamPrefillApi(new StaticAuthProvider("test", "test"));
        typeof(SteamPrefillApi).GetField("_steamManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api,
            new SteamManager(new TestConsole(), new DownloadArguments(), session));
        typeof(SteamPrefillApi).GetField("_isInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, true);
        using var commands = new SocketCommandInterface(GetFreeTcpPort());
        typeof(SocketCommandInterface).GetField("_api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(commands, api);
        using var release = new ManualResetEventSlim();
        var publishing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var progress = new SocketCommandInterface.SocketProgress(_ => { publishing.TrySetResult(); release.Wait(); }, operationId: "publishing-run");
        var slot = typeof(SocketCommandInterface).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic)!;
        slot.SetValue(commands, progress);
        var completion = (Task)typeof(SocketCommandInterface).GetMethod("CompletePrefillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(commands, new object[] { progress, Task.FromResult(new OwnedOperationResult(OwnedOperationStatus.Completed)) })!;
        try
        {
            await publishing.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var response = await (Task<CommandResponse>)typeof(SocketCommandInterface).GetMethod("HandlePrefillAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(commands, new object[] { new CommandRequest { Id = "next-run", Type = "prefill" }, CancellationToken.None })!;
            Assert.False(response.Success);
            Assert.Same(progress, slot.GetValue(commands));
            Assert.False(progress.Publication.Task.IsCompleted);
        }
        finally { release.Set(); await completion; }
        Assert.Null(slot.GetValue(commands));
        Assert.True(progress.Publication.Task.IsCompletedSuccessfully);
    }

    [Theory]
    [InlineData(OwnedOperationStatus.Completed)]
    [InlineData(OwnedOperationStatus.Cancelled)]
    [InlineData(OwnedOperationStatus.Failed)]
    public async Task TerminalSelection_IsStableAndProgressPrecedesTerminal(OwnedOperationStatus first)
    {
        var updates = new List<PrefillProgressUpdate>();
        var progress = new SocketCommandInterface.SocketProgress(updates.Add, operationId: "run-1");
        progress.OnAppStarted(new AppDownloadInfo { AppId = 222, Name = "Game" });
        var failure = new SteamConnectionException(SteamFailure.AuthLost, new AsyncJobFailedException());
        progress.SelectTerminal(first, first == OwnedOperationStatus.Failed ? failure : null);
        progress.SelectTerminal(OwnedOperationStatus.Failed, failure);
        progress.OnAppStarted(new AppDownloadInfo { AppId = 333, Name = "Late" });
        progress.OnPrefillCompleted(new PrefillSummary { TotalApps = 999 });
        await progress.PublishTerminalAsync(new OwnedOperationResult(OwnedOperationStatus.Completed));
        await progress.PublishTerminalAsync(new OwnedOperationResult(OwnedOperationStatus.Failed));
        Assert.Equal(2, updates.Count);
        Assert.Equal("downloading", updates[0].State);
        Assert.Equal(first == OwnedOperationStatus.Completed ? "completed" : first == OwnedOperationStatus.Cancelled ? "cancelled" : "error", updates[1].State);
        Assert.Equal(0, updates[1].TotalApps);
        Assert.All(updates, update => Assert.Equal("run-1", update.OperationId));
        if (first == OwnedOperationStatus.Failed)
        {
            Assert.True(updates[1].RequiresLogin);
            Assert.Equal("auth-lost", updates[1].ErrorCode);
        }
    }

    [Fact]
    public async Task FailedResult_ProducesFailedOwnerAndPreservesOriginalException()
    {
        await using var owner = new OwnedOperationCoordinator();
        var updates = new List<PrefillProgressUpdate>();
        var progress = new SocketCommandInterface.SocketProgress(updates.Add, operationId: "run-failed");
        var error = new SteamConnectionException(SteamFailure.GameDetailsUnavailable, new AsyncJobFailedException());
        await owner.StartAsync(token => SocketCommandInterface.RunPrefillOperationAsync(
            (_, _) => Task.FromResult(new PrefillResult { Success = false, Exception = error }),
            new PrefillOptions(), progress, token));
        var result = await owner.WaitAsync();
        Assert.Equal(OwnedOperationStatus.Failed, result.Status);
        Assert.Same(error, result.Exception);
        Assert.Empty(updates);
        await progress.PublishTerminalAsync(result);
        Assert.Single(updates);
        Assert.Equal("game-details-unavailable", updates[0].ErrorCode);
        Assert.False(updates[0].RequiresLogin);
    }

    [Fact]
    public void WireFields_AreOptionalAndNeverExposeException()
    {
        var result = new PrefillResult
        {
            Success = false,
            ErrorCode = "auth-lost",
            RequiresLogin = true,
            Exception = new SteamConnectionException(SteamFailure.AuthLost, new AsyncJobFailedException())
        };
        var json = JsonSerializer.Serialize(result, DaemonSerializationContext.Default.PrefillResult);
        Assert.DoesNotContain("exception", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SteamKit", json, StringComparison.Ordinal);
        var old = JsonSerializer.Deserialize("{\"success\":true}", DaemonSerializationContext.Default.PrefillResult)!;
        Assert.Null(old.ErrorCode);
        Assert.Null(old.RequiresLogin);
        var progress = JsonSerializer.Deserialize("{\"state\":\"completed\"}", DaemonSerializationContext.Default.PrefillProgressUpdate)!;
        Assert.Null(progress.OperationId);
        Assert.Null(progress.ErrorCode);
        Assert.Null(progress.RequiresLogin);
    }

    [Fact]
    public async Task UnknownCommand_ReturnsCorrelatedSafeFailure()
    {
        using var commands = new SocketCommandInterface(GetFreeTcpPort());
        var method = typeof(SocketCommandInterface).GetMethod("HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var response = await (Task<CommandResponse>)method.Invoke(commands, new object[]
            { new CommandRequest { Id = "unknown-1", Type = "not-a-command" }, CancellationToken.None })!;
        Assert.False(response.Success);
        Assert.Equal("unknown-1", response.Id);
        Assert.DoesNotContain("not-a-command", response.Error!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("get-owned-games", false)]
    [InlineData("get-owned-games", true)]
    [InlineData("get-selected-apps-status", false)]
    [InlineData("get-selected-apps-status", true)]
    [InlineData("check-cache-status", false)]
    [InlineData("check-cache-status", true)]
    public async Task QueryFailure_ReturnsSafeFailureWithoutPrefillTerminal(string command, bool productStage)
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        session.LicenseManager._userLicenses.OwnedAppIds.Add(222);
        var cause = new AsyncJobFailedException();
        var tokens = (SteamApps.PICSTokensCallback)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SteamApps.PICSTokensCallback));
        typeof(SteamApps.PICSTokensCallback).GetProperty(nameof(SteamApps.PICSTokensCallback.AppTokens))!.SetValue(tokens, new Dictionary<uint, ulong>());
        var handler = new AppInfoHandler(new TestConsole(), session, session.LicenseManager,
            _ => productStage ? Task.FromResult(tokens) : Task.FromException<SteamApps.PICSTokensCallback>(cause),
            _ => Task.FromException<AsyncJobMultiple<SteamApps.PICSProductInfoCallback>.ResultSet>(cause));
        using var api = new SteamPrefillApi(new StaticAuthProvider("test", "test"));
        var manager = new SteamManager(new TestConsole(), new DownloadArguments(), session, appInfoHandler: handler);
        typeof(SteamPrefillApi).GetField("_steamManager", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, manager);
        typeof(SteamPrefillApi).GetField("_isInitialized", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, true);
        typeof(SteamPrefillApi).GetField("_selectedAppsCache", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(api, new List<uint> { 222 });
        using var commands = new SocketCommandInterface(GetFreeTcpPort());
        typeof(SocketCommandInterface).GetField("_api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(commands, api);
        var updates = new List<PrefillProgressUpdate>();
        var active = new SocketCommandInterface.SocketProgress(updates.Add, operationId: "other-run");
        var slot = typeof(SocketCommandInterface).GetField("_run", BindingFlags.Instance | BindingFlags.NonPublic)!;
        slot.SetValue(commands, active);
        try
        {
            var method = typeof(SocketCommandInterface).GetMethod("HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var response = await (Task<CommandResponse>)method.Invoke(commands, new object[]
            {
                new CommandRequest { Id = "query-1", Type = command, Parameters = new Dictionary<string, string>
                    { ["cachedDepots"] = "[{\"appId\":222,\"depotId\":223,\"manifestId\":\"1\"}]" } },
                CancellationToken.None
            })!;
            Assert.False(response.Success);
            Assert.Null(response.Data);
            Assert.Equal("query-1", response.Id);
            Assert.Equal("game-details-unavailable", response.ErrorCode);
            Assert.False(response.RequiresLogin);
            Assert.Equal("Steam did not return game details. Try again.", response.Error);
            Assert.Null(active.Terminal);
            Assert.Empty(updates);
        }
        finally { slot.SetValue(commands, null); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingPicsRequest_StopsOnLossOrUserCancellation(bool cancel)
    {
        using var session = new Steam3Session(new TestConsole());
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(session, true);
        var response = new TaskCompletionSource<SteamApps.PICSTokensCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new AppInfoHandler(new TestConsole(), session, session.LicenseManager,
            _ => { started.TrySetResult(); return response.Task; });
        using var cancellation = new CancellationTokenSource();
        var query = handler.GetAppInfoAsync(222, cancellation.Token);
        await started.Task;
        if (cancel) cancellation.Cancel();
        else session.Disconnect();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => query.WaitAsync(TimeSpan.FromSeconds(2)));
        else Assert.Equal(SteamFailure.AuthLost, (await Assert.ThrowsAsync<SteamConnectionException>(() => query.WaitAsync(TimeSpan.FromSeconds(2)))).Failure);
        response.TrySetResult(null!);
        Assert.Empty(handler.LoadedAppInfos);
    }

    [Fact]
    public async Task AuthLossDuringPrefill_WaitsForCleanupAndPublishesFailureOnce()
    {
        await using var owner = new OwnedOperationCoordinator();
        var updates = new List<PrefillProgressUpdate>();
        var progress = new SocketCommandInterface.SocketProgress(updates.Add, operationId: "lost-run");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await owner.StartAsync(token => SocketCommandInterface.RunPrefillOperationAsync(async (_, token) =>
        {
            started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { cleanup.TrySetResult(); await release.Task; }
            return new PrefillResult { Success = true };
        }, new PrefillOptions(), progress, token));
        await started.Task;
        var error = new SteamConnectionException(SteamFailure.AuthLost);
        progress.SelectTerminal(OwnedOperationStatus.Failed, error);
        var completion = owner.CancelAndWaitAsync();
        await cleanup.Task;
        Assert.False(completion.IsCompleted);
        Assert.Empty(updates);
        release.TrySetResult();
        var result = await completion;
        Assert.Equal(OwnedOperationStatus.Failed, result.Status);
        Assert.Same(error, result.Exception);
        await progress.PublishTerminalAsync(result);
        Assert.Single(updates);
        Assert.Equal("error", updates[0].State);
        Assert.True(updates[0].RequiresLogin);
    }

    [Fact]
    public async Task ControlCommand_RespondsWhileSerializedCommandIsRunning()
    {
        var longCommandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLongCommand = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = GetFreeTcpPort();

        await using var server = new SocketServer(port, bindAddress: IPAddress.Loopback)
        {
            CommandLaneSelector = request => request.Type == "status"
                ? DaemonCommandLane.Control
                : DaemonCommandLane.Serialized,
            OnCommand = async (request, cancellationToken) =>
            {
                if (request.Type == "prefill")
                {
                    longCommandStarted.TrySetResult();
                    await releaseLongCommand.Task.WaitAsync(cancellationToken);
                }

                return new CommandResponse
                {
                    Id = request.Id,
                    Success = true,
                    Message = request.Type
                };
            }
        };

        await server.StartAsync();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        var stream = client.GetStream();
        await AuthenticateIfRequiredAsync(stream);

        await WriteRequestAsync(stream, new CommandRequest { Id = "long-1", Type = "prefill" });
        await longCommandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await WriteRequestAsync(stream, new CommandRequest { Id = "control-1", Type = "status" });

        var controlResponse = await ReadResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("control-1", controlResponse.Id);
        Assert.Equal("status", controlResponse.Message);

        releaseLongCommand.TrySetResult();
        var longResponse = await ReadResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal("long-1", longResponse.Id);
    }

    [Fact]
    public async Task ClientDisconnect_CancelsItsRunningCommand()
    {
        var commandStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commandCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var port = GetFreeTcpPort();

        await using var server = new SocketServer(port, bindAddress: IPAddress.Loopback)
        {
            CommandLaneSelector = _ => DaemonCommandLane.Concurrent,
            OnCommand = async (request, cancellationToken) =>
            {
                commandStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                finally
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        commandCancelled.TrySetResult();
                    }
                }

                return new CommandResponse { Id = request.Id, Success = true };
            }
        };

        await server.StartAsync();
        using (var client = new TcpClient())
        {
            await client.ConnectAsync(IPAddress.Loopback, port);
            var stream = client.GetStream();
            await AuthenticateIfRequiredAsync(stream);
            await WriteRequestAsync(stream, new CommandRequest { Id = "disconnect-1", Type = "prefill" });
            await commandStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }

        await commandCancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PrefillCancellation_WaitsForCleanup_EmitsOneTerminalState_AndAllowsRestart()
    {
        var updates = new List<PrefillProgressUpdate>();
        var progress = new SocketCommandInterface.SocketProgress(updates.Add);
        await using var coordinator = new OwnedOperationCoordinator();
        var operationStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await coordinator.StartAsync(
            cancellationToken => SocketCommandInterface.RunPrefillOperationAsync(
                async (_, token) =>
                {
                    operationStarted.TrySetResult();
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    }
                    finally
                    {
                        cleanupStarted.TrySetResult();
                        await allowCleanup.Task;
                    }

                    return new PrefillResult { Success = true };
                },
                new PrefillOptions(),
                progress,
                cancellationToken),
            CancellationToken.None);

        await operationStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var cancelTask = coordinator.CancelAndWaitAsync();
        await cleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(cancelTask.IsCompleted);

        allowCleanup.TrySetResult();
        var result = await cancelTask.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(OwnedOperationStatus.Cancelled, result.Status);
        Assert.Empty(updates);
        await progress.PublishTerminalAsync(result);
        Assert.Single(updates, update => update.State == "cancelled");
        Assert.DoesNotContain(updates, update => update.State is "completed" or "error");

        progress = new SocketCommandInterface.SocketProgress(updates.Add);
        await coordinator.StartAsync(
            cancellationToken => SocketCommandInterface.RunPrefillOperationAsync(
                (_, _) => Task.FromResult(new PrefillResult { Success = true }),
                new PrefillOptions(),
                progress,
                cancellationToken),
            CancellationToken.None);

        var restartedResult = await coordinator.WaitAsync();
        Assert.Equal(OwnedOperationStatus.Completed, restartedResult.Status);
    }

    [Fact]
    public void SocketLogging_SuppressesDebugByDefault_AndPreservesWarnings()
    {
        var defaultLogs = new List<string>();
        var defaultProgress = new SocketCommandInterface.SocketProgress(logWriter: defaultLogs.Add);

        defaultProgress.OnLog(LogLevel.Debug, "hidden-debug");
        defaultProgress.OnLog(LogLevel.Warning, "visible-warning");

        Assert.DoesNotContain(defaultLogs, line => line.Contains("hidden-debug", StringComparison.Ordinal));
        Assert.Contains(defaultLogs, line => line.Contains("visible-warning", StringComparison.Ordinal));

        var debugLogs = new List<string>();
        var debugProgress = new SocketCommandInterface.SocketProgress(enableDebugLogs: true, logWriter: debugLogs.Add);
        debugProgress.OnLog(LogLevel.Debug, "visible-debug");

        Assert.Contains(debugLogs, line => line.Contains("visible-debug", StringComparison.Ordinal));
    }

    [Fact]
    public void DaemonStartupMessages_AreConcise()
    {
        var messages = new[]
        {
            DaemonMode.GetUnixStartupMessage("/tmp/daemon.sock"),
            DaemonMode.GetTcpStartupMessage(12345)
        };

        Assert.All(messages, message =>
        {
            Assert.DoesNotContain("┌", message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECURITY", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("[DEBUG]", message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void SteamKitListenerConstruction_DoesNotEnableGlobalDebugTracing()
    {
        var wasEnabled = DebugLog.Enabled;
        try
        {
            DebugLog.Enabled = false;
            _ = new SteamKitDebugListener(new TestConsole());
            Assert.False(DebugLog.Enabled);
        }
        finally
        {
            DebugLog.Enabled = wasEnabled;
        }
    }

    [Fact]
    public async Task SteamChartsRequest_PropagatesCallerCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var httpClient = new HttpClient(new BlockingHttpHandler(requestStarted));
        using var cancellation = new CancellationTokenSource();

        var requestTask = SteamChartsService.MostPlayedByDailyPlayersAsync(
            new TestConsole(),
            httpClient,
            cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
    }

    [Fact]
    public async Task CdnServerRequest_PropagatesCallerCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<Server[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pool = new CdnPool(
            new TestConsole(),
            () =>
            {
                requestStarted.TrySetResult();
                return response.Task;
            });
        using var cancellation = new CancellationTokenSource();

        var requestTask = pool.PopulateAvailableServersAsync(cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        response.TrySetCanceled();
    }

    [Fact]
    public async Task ManifestRequestCode_PropagatesCallerCancellation()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ulong>(TaskCreationOptions.RunContinuationsAsynchronously);
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            _ =>
            {
                requestStarted.TrySetResult();
                return response.Task;
            },
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start before the request code completes."));
        using var cancellation = new CancellationTokenSource();

        var requestTask = handler.GetAllManifestsAsync(
            new List<DepotInfo> { CreateUncachedDepot() },
            cancellation.Token);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        response.TrySetCanceled();
    }

    // A prefill's first Steam call is app metadata. When the connection is gone the PICS job never
    // completes, so the run used to sit silent with no download and no error until the caller's stall
    // timeout. The wait is bounded now, and the run fails with a reason naming Steam.
    [Fact(Timeout = 120_000)]
    public async Task AppMetadataRequest_TimesOutWhenSteamNeverAnswers()
    {
        var requestStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<SteamApps.PICSTokensCallback>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var steam3 = new Steam3Session(null);
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(steam3, true);
        var appInfoHandler = new AppInfoHandler(
            new TestConsole(),
            steam3,
            steam3.LicenseManager,
            _ =>
            {
                requestStarted.TrySetResult();
                return response.Task;
            });

        var requestTask = appInfoHandler.GetAppInfoAsync(222, CancellationToken.None);
        await requestStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var exception = await Assert.ThrowsAsync<SteamConnectionException>(() => requestTask);
        Assert.Equal(SteamFailure.GameDetailsUnavailable, exception.Failure);
        Assert.IsType<TimeoutException>(exception.InnerException);
        response.TrySetCanceled();
    }

    // The pool is a stack, so a server returned after a failure is the very next one taken, and Steam keeps
    // listing a server after it has been dropped, so a refill would hand it straight back. One dead CDN edge
    // therefore answered every retry, and every download, until the process restarted. A server that fails with
    // a server-side error is now dropped instead of returned, left out of the refill, and the retry reaches a
    // different one.
    [Fact]
    public async Task ManifestDownloadOnFailingCdn_RetriesOnADifferentServer()
    {
        var console = new TestConsole();
        var serversUsed = new List<Server>();
        var steamHosts = Enumerable.Range(0, 8).Select(i => $"cdn-{i}").ToList();
        var pool = new CdnPool(console, () => Task.FromResult(steamHosts.Select(CreateCdnServer).ToArray()));
        // Holding only the minimum, so the first drop makes the pool ask Steam again, and Steam still lists the
        // server that just failed.
        pool.AvailableServerEndpoints = new ConcurrentStack<Server>(steamHosts.Take(5).Select(CreateCdnServer));
        var handler = new ManifestHandler(
            console,
            pool,
            _ => Task.FromResult(1UL),
            (_, _, server) =>
            {
                serversUsed.Add(server);
                // A faulted task, not a synchronous throw: the real client is an async method, and the
                // connection handling being tested only runs once the download task is awaited.
                return Task.FromException<DepotManifest>(new SteamKitWebRequestException(
                    "504 Gateway Timeout",
                    new HttpResponseMessage(HttpStatusCode.GatewayTimeout)));
            });
        var depot = CreateUncachedDepot();

        var (manifests, skippedDepots) = await handler.GetAllManifestsAsync(new List<DepotInfo> { depot });

        var hostsUsed = serversUsed.Select(e => e.Host).ToList();
        Assert.Equal(3, hostsUsed.Count);
        Assert.Equal(3, hostsUsed.Distinct().Count());
        Assert.DoesNotContain(pool.AvailableServerEndpoints, e => hostsUsed.Contains(e.Host));
        Assert.Equal(5, pool.AvailableServerEndpoints.Count);
        Assert.Empty(manifests);
        Assert.Same(depot, Assert.Single(skippedDepots));
    }

    // Dropped servers are left out of every refill, so once every server Steam lists has been dropped the
    // refill would come back empty and the daemon would have no CDN until it was restarted.
    [Fact]
    public async Task CdnPoolWithItsOnlyServerDropped_OffersItAgain()
    {
        var pool = new CdnPool(new TestConsole(), () => Task.FromResult(new[] { CreateCdnServer("cdn-0") }));
        await pool.PopulateAvailableServersAsync();
        Assert.True(pool.AvailableServerEndpoints.TryPop(out var server));

        await pool.DiscardConnectionAsync(server);

        Assert.Equal("cdn-0", Assert.Single(pool.AvailableServerEndpoints).Host);
    }

    // The download delegate used to be called outside the try, so a client that threw before returning its task
    // skipped the finally and the connection was never returned.
    [Fact]
    public async Task ManifestClientThrowingBeforeReturningATask_StillReturnsTheConnection()
    {
        var console = new TestConsole();
        var pool = new CdnPool(console, new ConcurrentStack<Server>(Enumerable.Range(0, 5).Select(_ => new Server())));
        var handler = new ManifestHandler(
            console,
            pool,
            _ => Task.FromResult(1UL),
            (_, _, _) => throw new InvalidOperationException("The CDN client is not ready"));
        var depot = CreateUncachedDepot();

        var (manifests, skippedDepots) = await handler.GetAllManifestsAsync(new List<DepotInfo> { depot });

        Assert.Equal(5, pool.AvailableServerEndpoints.Count);
        Assert.Empty(manifests);
        Assert.Same(depot, Assert.Single(skippedDepots));
    }

    // Chunk requests go through the cache with the CDN as the Host header, so a 5xx or a timeout is that CDN not
    // answering, while a 404 is a chunk no server has. Dropping the server on any failure stripped a healthy pool
    // down over a long download, and nothing ever refilled it.
    [Theory]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    public async Task ChunkDownloadFailure_DropsTheServerOnlyWhenItIsTheServersFault(HttpStatusCode status, bool serverDropped)
    {
        var console = new TestConsole();
        var steamHosts = Enumerable.Range(0, 6).Select(i => $"cdn-{i}").ToList();
        var pool = new CdnPool(console, () => Task.FromResult(steamHosts.Select(CreateCdnServer).ToArray()));
        // Holding only the minimum, so a drop has to ask Steam for more.
        pool.AvailableServerEndpoints = new ConcurrentStack<Server>(steamHosts.Take(5).Select(CreateCdnServer));
        var server = pool.AvailableServerEndpoints.First();
        using var handler = new DownloadHandler(console, pool);
        var cache = new TcpListener(IPAddress.Loopback, 0);
        cache.Start();
        // The cache address is resolved from DNS in InitializeAsync, which this test does not run.
        typeof(DownloadHandler).GetField("_lancacheAddress", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(handler, $"127.0.0.1:{((IPEndPoint)cache.LocalEndpoint).Port}");
        var answer = Task.Run(async () =>
        {
            using var client = await cache.AcceptTcpClientAsync();
            using var stream = client.GetStream();
            await stream.ReadAsync(new byte[4096]);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {(int)status} {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
        });
        var failedRequests = new ConcurrentBag<QueuedRequest>();

        await console.Progress().StartAsync(async ctx =>
        {
            failedRequests = await handler.AttemptDownloadAsync(
                ctx,
                "Downloading..",
                new List<QueuedRequest> { default },
                new DownloadArguments { MaxConcurrentRequests = 1 });
        });
        await answer;
        cache.Stop();

        Assert.Single(failedRequests);
        Assert.Equal(!serverDropped, pool.AvailableServerEndpoints.Any(e => e.Host == server.Host));
        Assert.Equal(5, pool.AvailableServerEndpoints.Count);
    }

    // A cache that answers nothing still had every queued chunk walked, one per-request timeout at a time, and a
    // queue holds tens of thousands of them, so a dead cache meant hours of silence. The walk now stops once two
    // full waves have failed without a single byte arriving.
    [Fact(Timeout = 60_000)]
    public async Task ChunkDownload_AbandonsTheQueueOnceTwoWavesFailWithNothingTransferred()
    {
        var console = new TestConsole();
        var pool = new CdnPool(console, () => Task.FromResult(new[] { CreateCdnServer("cdn-0") }));
        pool.AvailableServerEndpoints = new ConcurrentStack<Server>(new[] { CreateCdnServer("cdn-0") });
        using var handler = new DownloadHandler(console, pool);
        using var cache = new CacheListener(_ => HttpStatusCode.ServiceUnavailable);
        // The cache address is resolved from DNS in InitializeAsync, which this test does not run.
        typeof(DownloadHandler).GetField("_lancacheAddress", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(handler, cache.Address);

        await console.Progress().StartAsync(async ctx =>
        {
            var exception = await Assert.ThrowsAsync<TimeoutException>(() => handler.AttemptDownloadAsync(
                ctx,
                "Downloading..",
                Enumerable.Range(0, 30).Select(_ => default(QueuedRequest)).ToList(),
                new DownloadArguments { MaxConcurrentRequests = 2 }));

            Assert.Contains("not one byte arrived", exception.Message, StringComparison.Ordinal);
            Assert.Contains("cdn-0", exception.Message, StringComparison.Ordinal);
            Assert.Contains(cache.Address, exception.Message, StringComparison.Ordinal);
        });

        // The point of the rule: the length of the queue is no longer a multiplier on the time to fail.
        Assert.True(cache.Served < 30, $"the queue should have been abandoned, but {cache.Served} of 30 requests were sent");
    }

    // The guard against firing on a download that works: one failed wave followed by a recovery must still finish.
    // This passes with and without the abandon rule, because code that never abandons cannot fail an assertion
    // that it did not abandon. It is here to prove the rule cannot break a working download.
    [Fact(Timeout = 60_000)]
    public async Task ChunkDownload_DoesNotAbandonWhenAFailedWaveRecovers()
    {
        var console = new TestConsole();
        var pool = new CdnPool(console, () => Task.FromResult(new[] { CreateCdnServer("cdn-0") }));
        pool.AvailableServerEndpoints = new ConcurrentStack<Server>(new[] { CreateCdnServer("cdn-0") });
        using var handler = new DownloadHandler(console, pool);
        // Exactly one wave fails, so the failure count can never reach the two wave threshold of 8.
        using var cache = new CacheListener(served => served < 4 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        typeof(DownloadHandler).GetField("_lancacheAddress", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(handler, cache.Address);

        var failedRequests = new ConcurrentBag<QueuedRequest>();
        await console.Progress().StartAsync(async ctx =>
        {
            failedRequests = await handler.AttemptDownloadAsync(
                ctx,
                "Downloading..",
                Enumerable.Range(0, 20).Select(_ => default(QueuedRequest)).ToList(),
                new DownloadArguments { MaxConcurrentRequests = 4 });
        });

        Assert.Equal(4, failedRequests.Count);
        Assert.Equal(20, cache.Served);
    }

    [Fact]
    public async Task ManifestRequestCodeFailure_RemovesOnlyBrokenDepot()
    {
        var attempts = 0;
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            _ =>
            {
                attempts++;
                return Task.FromResult(0UL);
            },
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var healthyDepot = CreateCachedDepot();
        var brokenDepot = CreateUncachedDepot();
        var depots = new List<DepotInfo> { healthyDepot, brokenDepot };

        var (manifests, skippedDepots) = await handler.GetAllManifestsAsync(depots);

        Assert.Single(manifests);
        Assert.Same(healthyDepot, Assert.Single(depots));
        Assert.Same(brokenDepot, Assert.Single(skippedDepots));
        Assert.Equal(3, attempts);

        File.Delete(healthyDepot.ManifestFileName);
    }

    [Fact]
    public async Task EveryManifestFailing_LeavesNoDepotsAndReportsEverySkip()
    {
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var depots = new List<DepotInfo> { CreateUncachedDepot(), CreateUncachedDepot() };

        var (manifests, skippedDepots) = await handler.GetAllManifestsAsync(depots);

        // This is the state the status path guards against, an app whose depot list emptied out while
        // fetching manifests has nothing left to compare and would otherwise read as up to date
        Assert.Empty(manifests);
        Assert.Empty(depots);
        Assert.Equal(2, skippedDepots.Count);
    }

    [Fact]
    public async Task ManifestFailureForOneDepot_KeepsTheAppFromReportingUpToDate()
    {
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var cachedDepot = CreateCachedDepot();
        var secondCachedDepot = CreateCachedDepot();
        var updatedDepot = CreateUncachedDepot();
        var depots = new List<DepotInfo> { cachedDepot, secondCachedDepot, updatedDepot };
        var depotHandler = new DepotHandler(console, new Steam3Session(null), null, null);
        depotHandler.SetCachedManifests(new[]
        {
            (cachedDepot.DepotId, cachedDepot.ManifestId!.Value),
            (secondCachedDepot.DepotId, secondCachedDepot.ManifestId!.Value)
        });

        var (_, skippedDepots) = await handler.GetAllManifestsAsync(depots);

        // Everything left in the list is cached, so the list on its own says the app is up to date.
        // The skipped depot is the only thing that keeps the app from being counted that way.
        Assert.True(depotHandler.AppIsUpToDate(depots));
        Assert.Same(updatedDepot, Assert.Single(skippedDepots));

        File.Delete(cachedDepot.ManifestFileName);
        File.Delete(secondCachedDepot.ManifestFileName);
    }

    [Fact]
    public async Task ManifestTransportFailure_SkipsOnlyTheBrokenDepot()
    {
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            _ => throw new SteamKitWebRequestException("404 Not Found", new HttpResponseMessage(HttpStatusCode.NotFound)),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var healthyDepot = CreateCachedDepot();
        var brokenDepot = CreateUncachedDepot();
        var depots = new List<DepotInfo> { healthyDepot, brokenDepot };

        var (manifests, skippedDepots) = await handler.GetAllManifestsAsync(depots);

        Assert.Single(manifests);
        Assert.Same(healthyDepot, Assert.Single(depots));
        Assert.Same(brokenDepot, Assert.Single(skippedDepots));

        File.Delete(healthyDepot.ManifestFileName);
    }

    [Fact]
    public async Task UnreadableCachedManifest_IsDeletedAndRequestedAgain()
    {
        var attempts = 0;
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            _ =>
            {
                attempts++;
                return Task.FromResult(0UL);
            },
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var depot = CreateUncachedDepot();
        File.WriteAllBytes(depot.ManifestFileName, new byte[] { 0xFF, 0xFF, 0xFF, 0xFF });
        var depots = new List<DepotInfo> { depot };

        var (manifests, skippedDepots) = await handler.GetAllManifestsAsync(depots);

        Assert.False(File.Exists(depot.ManifestFileName));
        Assert.Equal(3, attempts);
        Assert.Empty(manifests);
        Assert.Same(depot, Assert.Single(skippedDepots));
    }

    [Fact]
    public async Task VtolDlcDepot_RequestsManifestWithDlcAppId()
    {
        uint requestedAppId = 0;
        uint requestedDepotId = 0;
        var console = new TestConsole();
        var handler = new ManifestHandler(
            console,
            new CdnPool(console, new ConcurrentStack<Server>()),
            depot =>
            {
                requestedAppId = depot.ManifestRequestAppId;
                requestedDepotId = depot.DepotId;
                return Task.FromResult(0UL);
            },
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var depotKey = new KeyValue("1770480")
        {
            Children =
            {
                new KeyValue("dlcappid", "1770480"),
                new KeyValue("manifests")
                {
                    Children = { new KeyValue("public", "2836902461265788005") }
                }
            }
        };
        var depot = new DepotInfo(depotKey, 1770480);
        depot.AttachToParentApp(667970, 1770480);
        var depots = new List<DepotInfo> { depot };

        await handler.GetAllManifestsAsync(depots);

        Assert.Equal(1770480U, requestedAppId);
        Assert.Equal(1770480U, requestedDepotId);
        Assert.Equal(1770480U, depot.LicenseAppId);
    }

    [Fact]
    public async Task ManifestDownload_PropagatesCallerCancellation_AndDefersConnectionReuse()
    {
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<DepotManifest>(TaskCreationOptions.RunContinuationsAsynchronously);
        var console = new TestConsole();
        var server = new Server();
        var pool = new CdnPool(console, new ConcurrentStack<Server>(new[] { server }));
        var handler = new ManifestHandler(
            console,
            pool,
            _ => Task.FromResult(1UL),
            (_, _, _) =>
            {
                downloadStarted.TrySetResult();
                return response.Task;
            });
        using var cancellation = new CancellationTokenSource();

        var requestTask = handler.GetAllManifestsAsync(
            new List<DepotInfo> { CreateUncachedDepot() },
            cancellation.Token);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => requestTask);
        Assert.Empty(pool.AvailableServerEndpoints);

        response.TrySetCanceled();

        // The connection comes back from a continuation on the download task, so it is not returned on this thread
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (pool.AvailableServerEndpoints.IsEmpty && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }
        Assert.Single(pool.AvailableServerEndpoints);
    }

    [Fact]
    public async Task AppStatus_WhenEveryManifestFails_IsNotReportedUpToDate()
    {
        var console = new TestConsole();
        using var steam3 = new Steam3Session(null);
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(steam3, true);
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(222);
        steam3.LicenseManager._userLicenses.OwnedDepotIds.Add(123);

        var appKeyValues = new KeyValue
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children = { new KeyValue("type", "game") }
                }
            }
        };
        var app = new AppInfo(steam3, 222, appKeyValues);
        app.Depots.Add(new DepotInfo(new KeyValue("0"), 222)
        {
            DepotId = 123,
            ManifestId = unchecked((ulong)Random.Shared.NextInt64(1, long.MaxValue))
        });

        var appInfoHandler = new Mock<AppInfoHandler>(console, steam3, steam3.LicenseManager);
        appInfoHandler.Setup(e => e.RetrieveAppMetadataAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<bool>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);
        appInfoHandler.Setup(e => e.GetAvailableGamesByIdAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(new List<AppInfo> { app }));
        appInfoHandler.Setup(e => e.GetAppInfoAsync(
                          It.IsAny<uint>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(app));

        // A pool holding the minimum server count keeps the status path from requesting CDNs from Steam
        var cdnPool = new CdnPool(console, new ConcurrentStack<Server>(Enumerable.Range(0, 5).Select(_ => new Server())));
        var manifestHandler = new ManifestHandler(
            console,
            cdnPool,
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var steamManager = new SteamManager(
            console,
            new DownloadArguments(),
            steam3,
            cdnPool: cdnPool,
            appInfoHandler: appInfoHandler.Object,
            depotHandler: new DepotHandler(steam3, appInfoHandler.Object, manifestHandler));

        var appStatuses = await steamManager.GetSelectedAppsStatusAsync(new List<uint> { 222 });

        var status = Assert.Single(appStatuses);
        Assert.False(status.IsUpToDate);
        Assert.Equal("No downloadable depots", status.UnavailableReason);
    }

    [Fact]
    public async Task AppStatus_WhenOneManifestFails_IsNotReportedUpToDate()
    {
        var console = new TestConsole();
        using var steam3 = new Steam3Session(null);
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(steam3, true);
        var cachedDepot = CreateCachedDepot();
        var brokenDepot = CreateUncachedDepot();
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(222);
        foreach (var depot in new[] { cachedDepot, brokenDepot })
        {
            steam3.LicenseManager._userLicenses.OwnedAppIds.Add(depot.LicenseAppId);
            steam3.LicenseManager._userLicenses.OwnedDepotIds.Add(depot.DepotId);
        }

        var appKeyValues = new KeyValue
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children = { new KeyValue("type", "game") }
                }
            }
        };
        var app = new AppInfo(steam3, 222, appKeyValues);
        app.Depots.Add(cachedDepot);
        app.Depots.Add(brokenDepot);

        var appInfoHandler = new Mock<AppInfoHandler>(console, steam3, steam3.LicenseManager);
        appInfoHandler.Setup(e => e.RetrieveAppMetadataAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<bool>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);
        appInfoHandler.Setup(e => e.GetAvailableGamesByIdAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(new List<AppInfo> { app }));
        appInfoHandler.Setup(e => e.GetAppInfoAsync(
                          It.IsAny<uint>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(app));

        var cdnPool = new CdnPool(console, new ConcurrentStack<Server>(Enumerable.Range(0, 5).Select(_ => new Server())));
        var manifestHandler = new ManifestHandler(
            console,
            cdnPool,
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var depotHandler = new DepotHandler(steam3, appInfoHandler.Object, manifestHandler);
        // The depot that keeps its manifest is already cached, so the depots left in the list say the app is up to date
        depotHandler.SetCachedManifests(new[] { (cachedDepot.DepotId, cachedDepot.ManifestId!.Value) });
        var steamManager = new SteamManager(
            console,
            new DownloadArguments(),
            steam3,
            cdnPool: cdnPool,
            appInfoHandler: appInfoHandler.Object,
            depotHandler: depotHandler);

        var appStatuses = await steamManager.GetSelectedAppsStatusAsync(new List<uint> { 222 });

        // One depot could not be fetched, so the app is incomplete and a prefill would have to run again
        var status = Assert.Single(appStatuses);
        Assert.False(status.IsUpToDate);

        File.Delete(cachedDepot.ManifestFileName);
    }

    [Fact]
    public async Task PrefillTwice_CountsOnlyTheSecondRun_AndNamesTheFailedApp()
    {
        // In daemon mode one manager serves every prefill command, and the summary used to be created
        // once with it. A second run therefore reported the first run's counts as well: a one-game
        // prefill could answer "4 updated, 13 failed", and the failure count climbed forever, so a
        // caller could not tell what THIS run did. The reason was invisible too, because the per-app
        // handler wrote it only to the console and the log file, never to the progress channel.
        var console = new TestConsole();
        using var steam3 = new Steam3Session(null);
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(steam3, true);
        var brokenDepot = CreateUncachedDepot();
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(222);
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(brokenDepot.LicenseAppId);
        steam3.LicenseManager._userLicenses.OwnedDepotIds.Add(brokenDepot.DepotId);

        var appKeyValues = new KeyValue
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children = { new KeyValue("type", "game") }
                }
            }
        };
        var app = new AppInfo(steam3, 222, appKeyValues);
        app.Depots.Add(brokenDepot);

        var appInfoHandler = new Mock<AppInfoHandler>(console, steam3, steam3.LicenseManager);
        appInfoHandler.Setup(e => e.RetrieveAppMetadataAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<bool>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);
        appInfoHandler.Setup(e => e.GetAvailableGamesByIdAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(new List<AppInfo> { app }));
        appInfoHandler.Setup(e => e.GetAppInfoAsync(
                          It.IsAny<uint>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(app));

        var cdnPool = new CdnPool(console, new ConcurrentStack<Server>(Enumerable.Range(0, 5).Select(_ => new Server())));
        var manifestHandler = new ManifestHandler(
            console,
            cdnPool,
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));

        var progress = new CallbackProgress();
        PrefillSummary? summary = null;
        progress.PrefillCompleted += completed => summary = completed;

        var steamManager = new SteamManager(
            console,
            new DownloadArguments(),
            steam3,
            progress,
            cdnPool: cdnPool,
            appInfoHandler: appInfoHandler.Object,
            depotHandler: new DepotHandler(steam3, appInfoHandler.Object, manifestHandler));

        await steamManager.DownloadMultipleAppsAsync(false, false, null, true);
        Assert.Equal(1, summary!.FailedApps);

        // The same manager, a second command. One app failed on this run, so the summary reports one,
        // not the two it has now seen in total.
        await steamManager.DownloadMultipleAppsAsync(false, false, null, true);
        Assert.Equal(1, summary!.FailedApps);
        Assert.Equal(1, summary.TotalApps);
    }

    [Fact]
    public async Task PrefillWhenEveryManifestFails_CountsTheAppAsFailed()
    {
        var console = new TestConsole();
        using var steam3 = new Steam3Session(null);
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(steam3, true);
        var brokenDepot = CreateUncachedDepot();
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(222);
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(brokenDepot.LicenseAppId);
        steam3.LicenseManager._userLicenses.OwnedDepotIds.Add(brokenDepot.DepotId);

        var appKeyValues = new KeyValue
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children = { new KeyValue("type", "game") }
                }
            }
        };
        var app = new AppInfo(steam3, 222, appKeyValues);
        app.Depots.Add(brokenDepot);

        var appInfoHandler = new Mock<AppInfoHandler>(console, steam3, steam3.LicenseManager);
        appInfoHandler.Setup(e => e.RetrieveAppMetadataAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<bool>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);
        appInfoHandler.Setup(e => e.GetAvailableGamesByIdAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(new List<AppInfo> { app }));
        appInfoHandler.Setup(e => e.GetAppInfoAsync(
                          It.IsAny<uint>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(app));

        var cdnPool = new CdnPool(console, new ConcurrentStack<Server>(Enumerable.Range(0, 5).Select(_ => new Server())));
        var manifestHandler = new ManifestHandler(
            console,
            cdnPool,
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));

        var progress = new CallbackProgress();
        AppDownloadResult? appResult = null;
        PrefillSummary? summary = null;
        progress.AppCompleted += (_, result) => appResult = result;
        progress.PrefillCompleted += completed => summary = completed;

        var steamManager = new SteamManager(
            console,
            new DownloadArguments(),
            steam3,
            progress,
            cdnPool: cdnPool,
            appInfoHandler: appInfoHandler.Object,
            depotHandler: new DepotHandler(steam3, appInfoHandler.Object, manifestHandler));

        await steamManager.DownloadMultipleAppsAsync(false, false, null, true);

        // The only depot was dropped while fetching manifests, which empties the list.  That empty list is a
        // manifest failure and not a filter that excluded everything, so the app has to be counted as failed
        Assert.Equal(AppDownloadResult.Failed, appResult);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.FailedApps);
    }

    [Fact]
    public async Task PrefillWhenOneManifestFails_CountsTheAppAsFailed()
    {
        var console = new TestConsole();
        using var steam3 = new Steam3Session(null);
        typeof(Steam3Session).GetField("_isAuthenticated", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(steam3, true);
        var cachedDepot = CreateCachedDepot();
        var brokenDepot = CreateUncachedDepot();
        steam3.LicenseManager._userLicenses.OwnedAppIds.Add(222);
        foreach (var depot in new[] { cachedDepot, brokenDepot })
        {
            steam3.LicenseManager._userLicenses.OwnedAppIds.Add(depot.LicenseAppId);
            steam3.LicenseManager._userLicenses.OwnedDepotIds.Add(depot.DepotId);
        }

        var appKeyValues = new KeyValue
        {
            Children =
            {
                new KeyValue("common")
                {
                    Children = { new KeyValue("type", "game") }
                }
            }
        };
        var app = new AppInfo(steam3, 222, appKeyValues);
        app.Depots.Add(cachedDepot);
        app.Depots.Add(brokenDepot);

        var appInfoHandler = new Mock<AppInfoHandler>(console, steam3, steam3.LicenseManager);
        appInfoHandler.Setup(e => e.RetrieveAppMetadataAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<bool>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.CompletedTask);
        appInfoHandler.Setup(e => e.GetAvailableGamesByIdAsync(
                          It.IsAny<List<uint>>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(new List<AppInfo> { app }));
        appInfoHandler.Setup(e => e.GetAppInfoAsync(
                          It.IsAny<uint>(),
                          It.IsAny<CancellationToken>()))
                      .Returns(Task.FromResult(app));

        var cdnPool = new CdnPool(console, new ConcurrentStack<Server>(Enumerable.Range(0, 5).Select(_ => new Server())));
        var manifestHandler = new ManifestHandler(
            console,
            cdnPool,
            _ => Task.FromResult(0UL),
            (_, _, _) => throw new InvalidOperationException("Manifest download should not start without a request code."));
        var depotHandler = new DepotHandler(steam3, appInfoHandler.Object, manifestHandler);
        // The surviving depot is already cached, so the up to date check would claim the whole app is done
        depotHandler.SetCachedManifests(new[] { (cachedDepot.DepotId, cachedDepot.ManifestId!.Value) });

        var progress = new CallbackProgress();
        AppDownloadResult? appResult = null;
        PrefillSummary? summary = null;
        bool appStarted = false;
        progress.AppCompleted += (_, result) => appResult = result;
        progress.PrefillCompleted += completed => summary = completed;
        progress.AppStarted += _ => appStarted = true;

        var steamManager = new SteamManager(
            console,
            new DownloadArguments(),
            steam3,
            progress,
            cdnPool: cdnPool,
            appInfoHandler: appInfoHandler.Object,
            depotHandler: depotHandler);

        await steamManager.DownloadMultipleAppsAsync(false, false, null, true);

        // One depot of two could not be fetched, so the app is incomplete no matter how cached its siblings are
        Assert.True(appStarted);
        Assert.Equal(AppDownloadResult.Failed, appResult);
        Assert.NotNull(summary);
        Assert.Equal(1, summary!.FailedApps);
        Assert.Equal(0, summary.AlreadyUpToDate);
        Assert.Equal(0, summary.UpdatedApps);

        File.Delete(cachedDepot.ManifestFileName);
    }

    [Fact]
    public async Task PerAppDownload_CallerCancellationIsNotCountedAsFailure()
    {
        var downloadStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var failureCount = 0;

        var downloadTask = SteamManager.DownloadAppsAsync(
            new[] { 1 },
            async (_, token) =>
            {
                downloadStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            },
            (_, _) => failureCount++,
            cancellation.Token);
        await downloadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloadTask);
        Assert.Equal(0, failureCount);
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static DepotInfo CreateUncachedDepot()
    {
        DepotInfo depot;
        do
        {
            var depotId = unchecked((uint)Random.Shared.NextInt64(1, uint.MaxValue));
            var manifestId = unchecked((ulong)Random.Shared.NextInt64(1, long.MaxValue));
            depot = new DepotInfo(new KeyValue("0"), depotId)
            {
                DepotId = depotId,
                ManifestId = manifestId
            };
        }
        while (File.Exists(depot.ManifestFileName));

        return depot;
    }

    private static DepotInfo CreateCachedDepot()
    {
        var depot = CreateUncachedDepot();
        // An empty manifest is all the cached path needs, these tests only care that the load succeeds
        File.WriteAllBytes(depot.ManifestFileName, Array.Empty<byte>());
        return depot;
    }

    // A cacheable server as Steam would list it.  SteamKit only sets these from Steam's own reply, so the
    // setters are internal.
    // A stand-in cache on loopback that answers every chunk request with whatever status the test picks for it,
    // so a test can make a source fail everything, or fail one wave and then recover. Requests are counted as
    // they are accepted, which is serialized, so "the first four" is exact no matter how the client interleaves.
    private sealed class CacheListener : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stopped = new CancellationTokenSource();
        private int _served;

        public CacheListener(Func<int, HttpStatusCode> statusForRequest)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Address = $"127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = Task.Run(() => AcceptAsync(statusForRequest));
        }

        public string Address { get; }

        public int Served => Volatile.Read(ref _served);

        private async Task AcceptAsync(Func<int, HttpStatusCode> statusForRequest)
        {
            while (!_stopped.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopped.Token);
                }
                catch (Exception)
                {
                    return;
                }

                var status = statusForRequest(Interlocked.Increment(ref _served) - 1);
                _ = Task.Run(async () =>
                {
                    using (client)
                    {
                        using var stream = client.GetStream();
                        await stream.ReadAsync(new byte[4096]);
                        var body = status == HttpStatusCode.OK ? "chunk" : "";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(
                            $"HTTP/1.1 {(int)status} {status}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
                    }
                });
            }
        }

        public void Dispose()
        {
            _stopped.Cancel();
            _listener.Stop();
            _stopped.Dispose();
        }
    }

    private static Server CreateCdnServer(string host)
    {
        var server = new Server();
        typeof(Server).GetProperty(nameof(Server.Host))!.SetValue(server, host);
        typeof(Server).GetProperty(nameof(Server.Type))!.SetValue(server, "CDN");
        typeof(Server).GetProperty(nameof(Server.AllowedAppIds))!.SetValue(server, Array.Empty<uint>());
        return server;
    }

    private static async Task WriteRequestAsync(NetworkStream stream, CommandRequest request)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(request, DaemonSerializationContext.Default.CommandRequest);
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        await stream.WriteAsync(prefix);
        await stream.WriteAsync(payload);
    }

    private static async Task AuthenticateIfRequiredAsync(NetworkStream stream)
    {
        var secret = Environment.GetEnvironmentVariable("PREFILL_SOCKET_SECRET");
        if (string.IsNullOrEmpty(secret))
        {
            return;
        }

        await WriteRequestAsync(stream, new CommandRequest
        {
            Id = "auth-1",
            Type = "auth",
            Parameters = new Dictionary<string, string> { ["secret"] = secret }
        });
        var response = await ReadResponseAsync(stream).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(response.Success);
        Assert.Equal("auth-1", response.Id);
    }

    private static async Task<CommandResponse> ReadResponseAsync(NetworkStream stream)
    {
        var prefix = new byte[sizeof(int)];
        await stream.ReadExactlyAsync(prefix);
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload);
        return JsonSerializer.Deserialize(payload, DaemonSerializationContext.Default.CommandResponse)
            ?? throw new InvalidOperationException("The daemon returned an empty response.");
    }

    private sealed class BlockingHttpHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestStarted;

        public BlockingHttpHandler(TaskCompletionSource requestStarted)
        {
            _requestStarted = requestStarted;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            _requestStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
