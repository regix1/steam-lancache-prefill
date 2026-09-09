using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using SteamPrefill.Api;

#nullable enable annotations

namespace SteamPrefill.Settings
{
    /// <summary>
    /// Keeps track of the auth tokens (JWT) returned by Steam, that allow for subsequent logins without passwords.
    /// </summary>
    [ProtoContract(SkipConstructor = true)]
    public sealed class UserAccountStore
    {
        [ProtoMember(3)]
        public string CurrentUsername { get; set; }

        /// <summary>
        /// Used to identify separate instances of Steam/SteamPrefill on the Steam network.
        /// As long as these don't collide, multiple instances can be logged in without logging each other out.
        /// </summary>
        [ProtoMember(4)]
        public uint? SessionId { get; private set; }

        /// <summary>
        /// Steam has switched over to using JWT tokens for authorization.
        /// </summary>
        [ProtoMember(5)]
        public string AccessToken { get; set; }

        /// <summary>
        /// Optional auth provider for API/daemon mode (bypasses console prompts)
        /// </summary>
        [ProtoIgnore]
        public ISteamAuthProvider? AuthProvider { get; set; }

        [ProtoIgnore]
        public Action<Action>? CommitCredentials { get; set; }

        [SuppressMessage("Security", "CA5394:Random is an insecure RNG", Justification = "Security doesn't matter here, as all that is needed is a unique id.")]
        private UserAccountStore()
        {
            var random = new Random();
            SessionId = (uint)random.Next(0, 16384);
        }

        /// <summary>
        /// Gets the current user's username, if they have already entered it before.
        /// If they have not yet entered it, they will be prompted to do so.
        ///
        /// Will timeout after 30 seconds of no user activity.
        /// </summary>
        public async Task<string> GetUsernameAsync(IAnsiConsole ansiConsole)
        {
            if (!string.IsNullOrEmpty(CurrentUsername))
            {
                return CurrentUsername;
            }

            // Use auth provider if available (API/daemon mode)
            if (AuthProvider != null)
            {
                CurrentUsername = await AuthProvider.GetUsernameAsync();
                return CurrentUsername;
            }

            CurrentUsername = await PromptForUsernameAsync(ansiConsole).WaitAsync(TimeSpan.FromSeconds(30));
            return CurrentUsername;
        }

        /// <summary>
        /// Gets password using auth provider if available, otherwise uses console prompt
        /// </summary>
        public async Task<string?> GetPasswordAsync(IAnsiConsole ansiConsole, string? promptText = null)
        {
            // Use auth provider if available (API/daemon mode)
            if (AuthProvider != null)
            {
                if (AccessTokenIsValid())
                {
                    return await AuthProvider.GetCachedPasswordAsync();
                }
                return await AuthProvider.GetPasswordAsync();
            }

            // Fall back to console prompt
            return await ansiConsole.ReadPasswordAsync(promptText);
        }

        public bool AccessTokenIsValid()
        {
            if (string.IsNullOrEmpty(AccessToken))
            {
                return false;
            }

            var parsedToken = new JwtSecurityToken(AccessToken);

            // Tokens seem to be valid for ~6 months.  We're going to add a bit of "buffer" (1 day) to make sure that new tokens are request prior to expiration
            var tokenHasExpired = DateTimeOffset.Now.DateTime.AddDays(1) < parsedToken.ValidTo;
            return tokenHasExpired;
        }

        /// <summary>
        /// Gets the UTC expiry (<c>ValidTo</c>) of the current refresh-token JWT, or null if no token is stored
        /// or it cannot be parsed.  Reuses the same JWT read as <see cref="AccessTokenIsValid"/>; no new crypto.
        /// </summary>
        public DateTime? GetAccessTokenExpiryUtc()
        {
            if (string.IsNullOrEmpty(AccessToken))
            {
                return null;
            }

            try
            {
                var parsedToken = new JwtSecurityToken(AccessToken);
                return DateTime.SpecifyKind(parsedToken.ValidTo, DateTimeKind.Utc);
            }
            catch
            {
                return null;
            }
        }

        private async Task<string> PromptForUsernameAsync(IAnsiConsole ansiConsole)
        {
            return await Task.Run(() =>
            {
                ansiConsole.MarkupLine($"A {Cyan("Steam")} account is required in order to prefill apps!");

                var prompt = new TextPrompt<string>($"Please enter your {Cyan("Steam account name")} : ")
                {
                    PromptStyle = new Style(SpectreColors.MediumPurple1)
                };
                return ansiConsole.Prompt(prompt);
            });
        }

        /// <summary>
        /// Sets credentials from a refresh token (for auto-login)
        /// </summary>
        public void SetCredentialsFromToken(string username, string refreshToken)
        {
            CurrentUsername = username;
            AccessToken = refreshToken;
            Save();
        }

        #region Serialization

        [SuppressMessage("Security", "CA5394:Random is an insecure RNG", Justification = "Security doesn't matter here, as all that is needed is a unique id.")]
        public static UserAccountStore LoadFromFile(Action<Action>? commitCredentials = null)
        {
            if (commitCredentials == null) return ReadAccount(null);
            UserAccountStore account = null;
            commitCredentials(() => account = ReadAccount(commitCredentials));
            return account;
        }

        [SuppressMessage("Security", "CA5394", Justification = "Session identifiers need uniqueness, not secrecy.")]
        private static UserAccountStore ReadAccount(Action<Action>? commitCredentials)
        {
            if (!File.Exists(AppConfig.AccountSettingsStorePath))
            {
                return new UserAccountStore { CommitCredentials = commitCredentials };
            }

            // Read file content to determine if it's encrypted or legacy plaintext protobuf
            var fileContent = File.ReadAllText(AppConfig.AccountSettingsStorePath);

            UserAccountStore userAccountStore;
            bool needsMigration = false;

            if (TokenStorageEncryption.IsEncrypted(fileContent))
            {
                // Encrypted format: decrypt -> Base64 decode -> protobuf deserialize
                string decryptedBase64;
                try
                {
                    decryptedBase64 = TokenStorageEncryption.Decrypt(fileContent);
                }
                catch (CryptographicException)
                {
                    // Store was encrypted under a key this container cannot reproduce
                    // (pre-key-file build, or lost key file). Discard and start fresh -
                    // the user must log in again once; afterwards the key travels with the volume.
                    AnsiConsole.MarkupLine("Stored credentials could not be decrypted, discarded stale token store, please log in again");
                    try
                    {
                        if (commitCredentials == null) File.Delete(AppConfig.AccountSettingsStorePath);
                    }
                    catch (Exception ex)
                    {
                        // Deletion is best-effort; a fresh Save() overwrites it anyway.
                        AnsiConsole.MarkupLine($"Warning - failed to delete stale token store : {ex.Message}");
                    }
                    return new UserAccountStore { CommitCredentials = commitCredentials };
                }
                var protobufBytes = System.Convert.FromBase64String(decryptedBase64);
                using var memStream = new MemoryStream(protobufBytes);
                userAccountStore = ProtoBuf.Serializer.Deserialize<UserAccountStore>(memStream);
            }
            else
            {
                // Legacy unencrypted protobuf binary — deserialize directly
                var fileBytes = File.ReadAllBytes(AppConfig.AccountSettingsStorePath);
                using var memStream = new MemoryStream(fileBytes);
                userAccountStore = ProtoBuf.Serializer.Deserialize<UserAccountStore>(memStream);
                needsMigration = true;
            }

            // Ensure SessionId is set - protobuf skips constructor so old files may not have it
            // This prevents LoginID collisions with lancache-manager depot mapping (which uses 16384-65535)
            if (userAccountStore.SessionId == null)
            {
                var random = new Random();
                userAccountStore.SessionId = (uint)random.Next(0, 16384);
                needsMigration = true;
            }

            userAccountStore.CommitCredentials = commitCredentials;
            if (needsMigration)
            {
                AnsiConsole.MarkupLine("Migrating account credentials to encrypted storage...");
                userAccountStore.Save();
            }

            return userAccountStore;
        }

        public void Save()
        {
            if (CommitCredentials != null)
            {
                CommitCredentials(SaveAccount);
                return;
            }
            SaveAccount();
        }

        private void SaveAccount()
        {
            // Protobuf serialize -> byte[] -> Base64 string -> encrypt -> write to file
            using var memStream = new MemoryStream();
            ProtoBuf.Serializer.Serialize(memStream, this);
            var base64 = System.Convert.ToBase64String(memStream.ToArray());
            var encrypted = TokenStorageEncryption.Encrypt(base64);

            File.WriteAllText(AppConfig.AccountSettingsStorePath, encrypted);
            TokenStorageEncryption.SetRestrictivePermissions(AppConfig.AccountSettingsStorePath);
        }

        #endregion
    }
}
