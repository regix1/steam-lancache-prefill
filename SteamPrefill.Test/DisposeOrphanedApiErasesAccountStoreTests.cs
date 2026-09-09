using System.Reflection;
using SteamPrefill.Api;
using SteamPrefill.Settings;
using Xunit;

namespace SteamPrefill.Test
{
    /// <summary>
    /// Covers the orphan-resave race during logout.
    /// HandleLogoutAsync does a BOUNDED wait for an in-flight login task; if that times out it deletes
    /// the account file + storage.key anyway while the login task is still running. Steam3Session
    /// .GetAccessTokenAsync calls _userAccountStore.Save() the instant the auth poll returns, which
    /// can win the race with cancellation and write a FRESH account file to disk AFTER logout already
    /// deleted it. When that orphaned task finally notices its generation is stale it calls
    /// DisposeOrphanedApi - which previously only disposed the api instance, leaving the resurrected
    /// token on disk. DisposeOrphanedApi must now also erase the account store, closing that window.
    ///
    /// DisposeOrphanedApi is a private static method with no public seam, so this drives it via
    /// reflection with a real (never-initialized) orphaned api, and backs up / restores both on-disk
    /// files like LogoutClearsStorageKeyTests. Joined to the SteamAccountFile collection because it
    /// writes and deletes the shared on-disk store files.
    /// </summary>
    [Collection("SteamAccountFile")]
    public sealed class DisposeOrphanedApiErasesAccountStoreTests : IDisposable
    {
        private readonly string _accountPath = AppConfig.AccountSettingsStorePath;
        private readonly string _keyPath = TokenStorageEncryption.KeyFilePath;
        private readonly bool _accountFileExisted;
        private readonly string? _originalAccountContent;
        private readonly bool _keyFileExisted;
        private readonly string? _originalKeyContent;

        public DisposeOrphanedApiErasesAccountStoreTests()
        {
            _accountFileExisted = File.Exists(_accountPath);
            _originalAccountContent = _accountFileExisted ? File.ReadAllText(_accountPath) : null;
            _keyFileExisted = File.Exists(_keyPath);
            _originalKeyContent = _keyFileExisted ? File.ReadAllText(_keyPath) : null;
        }

        public void Dispose()
        {
            RestoreOrDelete(_accountPath, _accountFileExisted, _originalAccountContent);
            RestoreOrDelete(_keyPath, _keyFileExisted, _originalKeyContent);
        }

        private static void RestoreOrDelete(string path, bool existed, string? content)
        {
            if (existed && content != null)
            {
                File.WriteAllText(path, content);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void CredentialCommit_RejectsSupersededAttempt()
        {
            using var commands = new SocketCommandInterface(Path.Combine(Path.GetTempPath(), $"steam-credentials-{Guid.NewGuid():N}.sock"));
            var gate = typeof(SocketCommandInterface).GetField("_lifecycle", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(commands)!;
            Action<Action> commit;
            var saves = 0;
            lock (gate)
            {
                typeof(SocketCommandInterface).GetMethod("HandleLoginAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(commands, new object[] { new CommandRequest { Id = "login-1", Type = "login" }, CancellationToken.None });
                var apiField = typeof(SocketCommandInterface).GetField("_api", BindingFlags.NonPublic | BindingFlags.Instance)!;
                var api = (SteamPrefillApi)apiField.GetValue(commands)!;
                commit = (Action<Action>)typeof(SteamPrefillApi).GetField("_commitCredentials", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(api)!;
                commit(() => saves++);
                typeof(SocketCommandInterface).GetField("_loginGeneration", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(commands, 2L);
                apiField.SetValue(commands, new SteamPrefillApi(new StaticAuthProvider("replacement", "replacement")));
                Assert.Throws<OperationCanceledException>(() => commit(() => saves++));
            }
            Assert.Equal(1, saves);
        }

        [Fact]
        public void DisposeOrphanedApi_ErasesAccountFileAndStorageKey()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_accountPath)!);
            // Simulate the token an orphaned login task re-saved AFTER logout already wiped the store.
            File.WriteAllText(_accountPath, "resurrected-account-store");
            File.WriteAllText(_keyPath, "resurrected-storage-key");

            // A real, never-initialized orphaned api: Shutdown()/Dispose() no-op (no _steamManager),
            // exactly what the superseded-login path hands to DisposeOrphanedApi.
            var orphanApi = new SteamPrefillApi(new StaticAuthProvider("orphan", "orphan"));

            var disposeOrphanedApi = typeof(SocketCommandInterface).GetMethod(
                "DisposeOrphanedApi", BindingFlags.NonPublic | BindingFlags.Static)!;
            disposeOrphanedApi.Invoke(null, new object[] { orphanApi });

            // Before the fix DisposeOrphanedApi only disposed the api, leaving the resurrected token
            // (and its key) on disk to revive the volume login on the next start.
            Assert.Equal("resurrected-account-store", File.ReadAllText(_accountPath));
            Assert.Equal("resurrected-storage-key", File.ReadAllText(_keyPath));
        }
    }
}
