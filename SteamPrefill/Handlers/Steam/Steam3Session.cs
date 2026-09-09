using SteamKit2.Authentication;
using SteamPrefill.Api;

#nullable enable annotations

namespace SteamPrefill.Handlers.Steam
{
    public sealed class Steam3Session : IDisposable
    {
        private const int MaxLoginRetries = 5;

        /// <summary>
        /// CellId represents the region that the user is geographically located in, and determines which Connection Managers and CDNs
        /// will be used by SteamPrefill.
        ///
        /// Typically, Steam will automatically select the correct CellId using geolocation.
        /// However, the api endpoint used (ISteamDirectory/GetCMList) will unpredictably return non-local servers due to an issue with Valve's
        /// api not handling trailing slashes correctly.
        ///
        /// For example calling ISteamDirectory/GetCMList/v1?cellid=0 will always return the correct regional servers, however adding a trailing slash
        /// to the end of the url (ex. /v1/?) will cause Steam to return non-local servers.
        ///
        /// Upon login to the Steam network certain metadata about the session will be received, this includes the correct CellId which we will save
        /// and use for future logins.  Using the correct CellId will guarantee significantly faster login and app metadata retrieval times.
        ///
        /// See https://tpill90.github.io/steam-lancache-prefill/steam-docs/CDN-Regions/ for a list of known CDNs
        /// </summary>
        private uint CellId
        {
            get
            {
                if (AppConfig.CellIdOverride != null)
                {
                    return AppConfig.CellIdOverride.Value;
                }
                if (File.Exists(AppConfig.CachedCellIdPath))
                {
                    return uint.Parse(File.ReadAllText(AppConfig.CachedCellIdPath));
                }
                return 0;
            }
            set => File.WriteAllText(AppConfig.CachedCellIdPath, value.ToString());
        }

        #region Member fields

        // Steam services
        private readonly SteamClient _steamClient;
        public readonly SteamContent SteamContent;
        public readonly SteamApps SteamAppsApi;
        public readonly Client CdnClient;
        public Player unifiedPlayerService;
        private readonly CallbackManager _callbackManager;

        private SteamUser.LogOnDetails _logonDetails;
        private readonly IAnsiConsole _ansiConsole;

        private readonly UserAccountStore _userAccountStore;
        public readonly LicenseManager LicenseManager;
        private readonly ISteamAuthProvider? _authProvider;

        public SteamID _steamId;

        #endregion

        private readonly CancellationTokenSource _pumpCts = new();
        private readonly Task _pump;
        private readonly object _sessionLock = new();
        private TaskCompletionSource<bool> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<SteamUser.LoggedOnCallback> _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _licenses = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _authLost = new();
        private readonly CancellationToken _authLostToken;
        private bool _isAuthenticated;
        private bool _logonRequested;
        private bool _disposed;
        public bool IsAuthenticated { get { lock (_sessionLock) return _isAuthenticated; } }
        public CancellationToken AuthLostToken => _authLostToken;
        internal string Username => _userAccountStore.CurrentUsername;
        internal DateTime? AuthExpiryUtc => _userAccountStore.GetAccessTokenExpiryUtc();
        internal uint? LoginId => _userAccountStore.SessionId;
        internal EResult? LogoffResult { get; private set; }
        public event Action<EResult?> AuthenticationLost;

        internal void WhileAuthenticated(Action action, CancellationToken cancellationToken)
        {
            lock (_sessionLock)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!_isAuthenticated) throw new SteamConnectionException(SteamFailure.AuthLost);
                action();
            }
        }

        private void InvalidateAuthentication(EResult? result)
        {
            bool notify;
            lock (_sessionLock)
            {
                notify = _isAuthenticated;
                _isAuthenticated = false;
                if (notify) LogoffResult = result;
            }
            if (notify) AuthenticationLost?.Invoke(result);
            if (!_disposed && (notify || _logonRequested)) _authLost.Cancel();
        }

        public Steam3Session(IAnsiConsole? ansiConsole, ISteamAuthProvider? authProvider = null, Action<Action>? commitCredentials = null)
        {
            _authLostToken = _authLost.Token;
            _ansiConsole = ansiConsole ?? AnsiConsole.Console;
            _authProvider = authProvider;

            try
            {
                _userAccountStore = UserAccountStore.LoadFromFile(commitCredentials);
            }
            catch
            {
                _pumpCts.Dispose();
                _authLost.Dispose();
                throw;
            }

            _steamClient = new SteamClient(SteamConfiguration.Create(e => e.WithCellID(CellId)
                                                               .WithConnectionTimeout(TimeSpan.FromSeconds(10))));

            SteamAppsApi = _steamClient.GetHandler<SteamApps>();
            SteamContent = _steamClient.GetHandler<SteamContent>();
            SteamUnifiedMessages steamUnifiedMessages = _steamClient.GetHandler<SteamUnifiedMessages>();
            unifiedPlayerService = steamUnifiedMessages.CreateService<Player>();

            _callbackManager = new CallbackManager(_steamClient);

            // This callback is triggered when SteamKit2 makes a successful connection
            _callbackManager.Subscribe<SteamClient.ConnectedCallback>(e =>
            {
                _isConnecting = false;
                _disconnected = false;
                _connected.TrySetResult(true);
            });
            // If a connection attempt fails in any way, SteamKit2 notifies of the failure with a "disconnect"
            _callbackManager.Subscribe<SteamClient.DisconnectedCallback>(e =>
            {
                _isConnecting = false;
                _disconnected = true;
                _connected.TrySetResult(false);
                InvalidateAuthentication(null);
                _loggedOn.TrySetException(new SteamConnectionException(SteamFailure.AuthLost));
            });

            _callbackManager.Subscribe<SteamUser.LoggedOnCallback>(loggedOn =>
            {
                _loggedOnCallbackResult = loggedOn;
                lock (_sessionLock)
                {
                    if (!_disposed && !_authLost.IsCancellationRequested)
                        _isAuthenticated = loggedOn.Result == EResult.OK;
                }
                CellId = loggedOn.CellID;
                _loggedOn.TrySetResult(loggedOn);
            });
            _callbackManager.Subscribe<SteamUser.LoggedOffCallback>(loggedOff => InvalidateAuthentication(loggedOff.Result));
            _callbackManager.Subscribe<LicenseListCallback>(LicenseListCallback);

            CdnClient = new Client(_steamClient);
            // Configuring SteamKit's HttpClient to timeout in a more reasonable time frame.  This is only used when downloading manifests
            Client.RequestTimeout = TimeSpan.FromSeconds(60);

            _userAccountStore.AuthProvider = authProvider; // Set auth provider for API/daemon mode
            _userAccountStore.CommitCredentials = commitCredentials;
            LicenseManager = new LicenseManager(SteamAppsApi);
            _pump = Task.Factory.StartNew(() =>
            {
                while (!_pumpCts.IsCancellationRequested)
                    _callbackManager.RunWaitAllCallbacks(TimeSpan.FromMilliseconds(50));
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            // Setting up optional SteamKit2 debug output.  Not enabled by default because it writes out way too much output that isn't useful outside of debugging.
            if (AppConfig.DebugLogs)
            {
                DebugLog.Enabled = true;
                DebugLog.AddListener(new SteamKitDebugListener(_ansiConsole));
            }
        }

        public async Task LoginToSteamAsync(CancellationToken cancellationToken = default)
        {
            using var cancellation = cancellationToken.Register(() => InvalidateAuthentication(null));
            await ConfigureLoginDetailsAsync(cancellationToken);

            int retryCount = 0;
            bool logonSuccess = false;
            while (!logonSuccess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                SteamUser.LoggedOnCallback logonResult = null;
                await _ansiConsole.StatusSpinner().StartAsync("Connecting to Steam...", async ctx =>
                {
                    await ConnectToSteam(cancellationToken);

                    // Making sure that we have a valid access token before moving onto the login
                    ctx.Status = "Retrieving access token...";
                    await GetAccessTokenAsync(cancellationToken);

                    ctx.Status = "Logging in to Steam...";
                    logonResult = await AttemptSteamLogin(cancellationToken);
                });

                logonSuccess = HandleLogonResult(logonResult);

                retryCount++;
                if (retryCount >= MaxLoginRetries)
                {
                    throw new SteamLoginException("Unable to login to Steam!  Try again in a few moments...");
                }
            }

            _ansiConsole.LogMarkupVerbose($"Connected to CM {Cyan(_steamClient.CurrentEndPoint)}");
        }

        private async Task GetAccessTokenAsync(CancellationToken cancellationToken = default)
        {
            if (_userAccountStore.AccessTokenIsValid())
            {
                return;
            }

            _ansiConsole.LogMarkupLine("Requesting new access token...");

            // Use secure authenticator if auth provider is available, otherwise use console
            IAuthenticator authenticator = _authProvider != null
                ? new SecureSteamAuthenticator(_authProvider)
                : new UserConsoleAuthenticator();

            // Begin authenticating via credentials
            var authSession = await _steamClient.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = _logonDetails.Username,
                Password = _logonDetails.Password,
                IsPersistentSession = true,
                Authenticator = authenticator
            }).WaitAsync(cancellationToken);

            // Starting polling Steam for authentication response
            // Pass cancellation token so we can abort if cancel-login is called
            // Also add a 3-minute timeout as a safety measure for device confirmation
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

            try
            {
                var pollResponse = await authSession.PollingWaitForResultAsync(linkedCts.Token);
                _userAccountStore.AccessToken = pollResponse.RefreshToken;
                _userAccountStore.Save();
            }
            catch (AuthenticationException authEx) when (authEx.Message.Contains("Expired"))
            {
                // Steam's authentication session timed out (~2 minutes for mobile confirmation)
                throw new TimeoutException("Authentication session expired. Please try again and confirm on your Steam Mobile App within 2 minutes, or use a 2FA code instead.");
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("Authentication timed out waiting for device confirmation. Please try again.");
            }

            // Clearing password so it doesn't stay in memory
            _logonDetails.Password = null;
            GC.Collect();
        }

        private async Task ConfigureLoginDetailsAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var username = await _userAccountStore.GetUsernameAsync(_ansiConsole).WaitAsync(cancellationToken);

            _logonDetails = new SteamUser.LogOnDetails
            {
                Username = username,
                ShouldRememberPassword = true,
                Password = _userAccountStore.AccessTokenIsValid() ? null : await _userAccountStore.GetPasswordAsync(_ansiConsole).WaitAsync(cancellationToken),
                LoginID = _userAccountStore.SessionId
            };
            _ansiConsole.LogMarkupLine($"Session LoginID set to: {_userAccountStore.SessionId} (0x{_userAccountStore.SessionId:X8})");
        }

        #region  Connecting to Steam

        // Used to busy wait until the connection attempt finishes in either a success or failure
        private bool _isConnecting;

        /// <summary>
        /// Attempts to establish a connection to the Steam network.
        /// Retries if necessary until successful connection is established
        /// </summary>
        /// <exception cref="SteamConnectionException">Throws if unable to connect to Steam</exception>
        [SuppressMessage("Naming", "VSTHRD200", Justification = "Retains the established method name.")]
        private async Task ConnectToSteam(CancellationToken cancellationToken = default)
        {
            _ansiConsole.LogMarkupVerbose($"Connecting with CellId: {Magenta(CellId)}");
            var timeoutAfter = DateTime.Now.AddSeconds(30);

            // Busy waiting until the client has a successful connection established
            while (!_steamClient.IsConnected)
            {
                cancellationToken.ThrowIfCancellationRequested();

                _isConnecting = true;
                _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _steamClient.Connect();

                // Busy waiting until SteamKit2 either succeeds/fails the connection attempt
                while (_isConnecting)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    await _connected.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                    if (DateTime.Now > timeoutAfter)
                    {
                        throw new SteamConnectionException("Timeout connecting to Steam...  Try again in a few moments");
                    }
                }
            }
            _ansiConsole.LogMarkupLine("Connected to Steam!");
        }

        #endregion

        #region Logging into Steam

        private SteamUser.LoggedOnCallback _loggedOnCallbackResult;
        private int _failedLogonAttempts;

        [SuppressMessage("Maintainability", "CA1508:Avoid dead conditional code", Justification = "while() loop is not infinite.  _loggedOnCallbackResult is set after logging into Steam")]
        [SuppressMessage("Naming", "VSTHRD200", Justification = "Retains the established method name.")]
        private async Task<SteamUser.LoggedOnCallback> AttemptSteamLogin(CancellationToken cancellationToken = default)
        {
            var timeoutAfter = DateTime.Now.AddSeconds(30);
            // Need to reset this global result value, as it will be populated once the logon callback completes
            _loggedOnCallbackResult = null;
            _loggedOn = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _logonRequested = true;

            _logonDetails.AccessToken = _userAccountStore.AccessToken;
            _ansiConsole.LogMarkupLine($"Logging in with LoginID: {_logonDetails.LoginID} (0x{_logonDetails.LoginID:X8})");
            _steamClient.GetHandler<SteamUser>().LogOn(_logonDetails);

            // Busy waiting for the callback to complete, then we can return the callback value synchronously
            while (_loggedOnCallbackResult == null)
            {
                cancellationToken.ThrowIfCancellationRequested();

                await _loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
                if (DateTime.Now > timeoutAfter)
                {
                    throw new SteamLoginException("Timeout logging into Steam...  Try again in a few moments");
                }
            }
            return _loggedOnCallbackResult;
        }

        [SuppressMessage("", "VSTHRD002:Synchronously waiting on tasks may cause deadlocks.", Justification = "Its not possible for this callback method to be async, must block synchronously")]
        private bool HandleLogonResult(SteamUser.LoggedOnCallback logonResult)
        {
            _steamId = logonResult.ClientSteamID;

            var loggedOn = logonResult;

            // If the account has 2-Factor login enabled, then we will need to re-login with the supplied code
            if (loggedOn.Result == EResult.AccountLoginDeniedNeedTwoFactor)
            {
                _logonDetails.TwoFactorCode = _ansiConsole.Prompt(new TextPrompt<string>(LightYellow("2FA required for login.") +
                                                                                         $"  Please enter your {Cyan("Steam Guard code")} from your authenticator app : "));
                return false;
            }
            if (loggedOn.Result == EResult.TwoFactorCodeMismatch)
            {
                _logonDetails.TwoFactorCode = _ansiConsole.Prompt(new TextPrompt<string>(Red("Login failed. Incorrect Steam Guard code") +
                                                                                         "  Please try again : "));
                return false;
            }

            if (loggedOn.Result == EResult.InvalidPassword)
            {
                _failedLogonAttempts++;
                if (_failedLogonAttempts == 3)
                {
                    _ansiConsole.LogMarkupLine(Red("Invalid username/password combination!  Check your login credential validity, and try again.."));
                    throw new AuthenticationException("Invalid username/password");
                }

                _logonDetails.Password = _userAccountStore.GetPasswordAsync(_ansiConsole, $"{Red("Invalid password!  Please re-enter your password!")}").GetAwaiter().GetResult();
                return false;
            }
            // User previously authenticated, but changed their password such that the previous access token is no longer valid.
            if (loggedOn.Result == EResult.AccessDenied)
            {
                _ansiConsole.LogMarkupLine(Red("Steam password was changed!  Current login token is no longer valid.  Re-authentication is required..."));
                _logonDetails.Password = _userAccountStore.GetPasswordAsync(_ansiConsole, $"{Red("Please enter your password!")}").GetAwaiter().GetResult();
                _userAccountStore.AccessToken = null;
                return false;
            }
            // SteamGuard code required
            if (loggedOn.Result == EResult.AccountLogonDenied)
            {
                _logonDetails.AuthCode = _ansiConsole.Prompt(new TextPrompt<string>(LightYellow("This account is protected by Steam Guard.") +
                                                                                    "  Please enter the code sent to your email address:  "));
                return false;
            }
            if (loggedOn.Result == EResult.ServiceUnavailable)
            {
                throw new SteamLoginException("Unable to login to Steam : Service is unavailable");
            }
            if (loggedOn.Result != EResult.OK)
            {
                throw new SteamLoginException($"Unable to login to Steam.  An unknown error occurred : {loggedOn.Result}");
            }

            _ansiConsole.LogMarkupLine("Logged into Steam");

            // Forcing a garbage collect to remove stored password from memory
            if (_logonDetails.Password != null)
            {
                _logonDetails.Password = null;
                GC.Collect(3, GCCollectionMode.Forced);
            }

            return true;
        }

        private bool _disconnected = true;

        /// <summary>
        /// True once a logged in session has lost its connection to Steam.  <see cref="_disconnected"/> also reads
        /// true before the first connection is ever made, so the logon result is what separates a session that
        /// dropped from one that has not logged in yet.
        /// </summary>
        public bool IsDisconnected => _disconnected && _loggedOnCallbackResult != null;

        public void Disconnect()
        {
            if (_disposed) return;
            InvalidateAuthentication(null);
            _authLost.Cancel();
            _pumpCts.Cancel();
            _steamClient.Disconnect();
            if (Task.CurrentId != _pump.Id) _pump.GetAwaiter().GetResult();
            _disconnected = true;
            _isConnecting = false;
            _connected.TrySetCanceled();
            _loggedOn.TrySetCanceled();
        }

        #endregion

        #region LoadAccountLicenses

        /// <summary>
        /// Waits for the user's currently owned licenses(games) to be returned.
        /// The license query is triggered on application startup, and requires busy-waiting to receive the callback
        /// </summary>
        [SuppressMessage("Naming", "VSTHRD200", Justification = "Retains the established method name.")]
        public async Task WaitForLicenseCallback(CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _authLost.Token);
            try
            {
                await _licenses.Task.WaitAsync(TimeSpan.FromSeconds(45), linked.Token);
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                throw new SteamConnectionException(SteamFailure.AuthLost, ex);
            }
            if (!IsAuthenticated) throw new SteamConnectionException(SteamFailure.AuthLost);
        }

        private void LicenseListCallback(LicenseListCallback licenseList)
        {
            var timer = Stopwatch.StartNew();

            if (licenseList.Result != EResult.OK)
            {
                _ansiConsole.MarkupLine(Red($"Unexpected error while retrieving license list : {licenseList.Result}"));
                _licenses.TrySetException(new SteamLoginException("Unable to retrieve user licenses!"));
                return;
            }
            LicenseManager.LoadPackageInfo(licenseList.LicenseList);
            _licenses.TrySetResult(true);

            _ansiConsole.LogMarkupLine("Loaded account licenses", timer);
        }

        #endregion

        public void Dispose()
        {
            Disconnect();
            lock (_sessionLock)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _pumpCts.Cancel();
            if (Task.CurrentId != _pump.Id) _pump.GetAwaiter().GetResult();
            _pumpCts.Dispose();
            _authLost.Dispose();
            CdnClient.Dispose();
        }
    }
}
