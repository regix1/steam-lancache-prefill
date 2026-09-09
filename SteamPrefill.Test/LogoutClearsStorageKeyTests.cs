using System.Reflection;
using SteamPrefill.Api;
using SteamPrefill.Settings;
using Xunit;

namespace SteamPrefill.Test
{
    /// <summary>
    /// HandleLogoutAsync previously deleted only
    /// the account file and left storage.key on the persistent volume, so a subsequent login could
    /// re-save a token the SAME key decrypts, making the volume login sticky across erase-on-stop.
    /// Logout must now delete storage.key alongside the account file.
    ///
    /// Drives the private HandleCommandAsync via reflection (SocketCommandInterface owns a live
    /// SocketServer, never StartAsync'd here) - same seam as LogoutPreLoginGateTests - and backs up /
    /// restores both files around the test like TokenStorageEncryptionTests. Joined to the
    /// SteamAccountFile collection because it writes and deletes the shared on-disk store files.
    /// </summary>
    [Collection("SteamAccountFile")]
    public sealed class LogoutClearsStorageKeyTests : IDisposable
    {
        private readonly string _accountPath = AppConfig.AccountSettingsStorePath;
        private readonly string _keyPath = TokenStorageEncryption.KeyFilePath;
        private readonly bool _accountFileExisted;
        private readonly string? _originalAccountContent;
        private readonly bool _keyFileExisted;
        private readonly string? _originalKeyContent;

        public LogoutClearsStorageKeyTests()
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
        public async Task Shutdown_PreservesAccountFileAndStorageKey()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_accountPath)!);
            File.WriteAllText(_accountPath, "saved-account");
            File.WriteAllText(_keyPath, "saved-key");
            using var commands = new SocketCommandInterface(Path.Combine(Path.GetTempPath(), $"steam-shutdown-{Guid.NewGuid():N}.sock"));
            var method = typeof(SocketCommandInterface).GetMethod("HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
            var response = await (Task<CommandResponse>)method.Invoke(commands,
                new object[] { new CommandRequest { Id = "shutdown-1", Type = "shutdown" }, CancellationToken.None })!;
            Assert.True(response.Success);
            Assert.Equal("saved-account", File.ReadAllText(_accountPath));
            Assert.Equal("saved-key", File.ReadAllText(_keyPath));
        }

        [Fact]
        public async Task Logout_DeletesStorageKeyAlongsideAccountFile()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_accountPath)!);
            File.WriteAllText(_accountPath, "dummy-account-store");
            File.WriteAllText(_keyPath, "dummy-storage-key");

            using var socketInterface = new SocketCommandInterface(
                Path.Combine(Path.GetTempPath(), $"steam-logout-storagekey-{Guid.NewGuid():N}.sock"));

            var handleCommandAsync = typeof(SocketCommandInterface).GetMethod(
                "HandleCommandAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

            var request = new CommandRequest { Id = "1", Type = "logout" };
            var response = await (Task<CommandResponse>)handleCommandAsync.Invoke(
                socketInterface, new object[] { request, CancellationToken.None })!;

            Assert.True(response.Success);
            Assert.False(File.Exists(_accountPath));
            // Before the fix storage.key survived logout, resurrecting the volume login on next start.
            Assert.False(File.Exists(_keyPath));
        }
    }
}
