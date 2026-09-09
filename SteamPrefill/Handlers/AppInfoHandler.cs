namespace SteamPrefill.Handlers
{
    /// <summary>
    /// Responsible for retrieving application metadata from Steam
    /// </summary>
    public class AppInfoHandler
    {
        private readonly IAnsiConsole _ansiConsole;
        private readonly Steam3Session _steam3Session;
        private readonly LicenseManager _licenseManager;
        private readonly Func<List<uint>, Task<PICSTokensCallback>> _requestAccessTokensAsync;
        private readonly Func<List<PICSRequest>, Task<AsyncJobMultiple<PICSProductInfoCallback>.ResultSet>> _requestProductsAsync;

        private static readonly TimeSpan MetadataRequestTimeout = TimeSpan.FromSeconds(45);

        private List<CPlayer_GetOwnedGames_Response.Game> _recentlyPlayed;

        /// <summary>
        /// A dictionary of all app metadata currently retrieved from Steam
        /// </summary>
        internal ConcurrentDictionary<uint, AppInfo> LoadedAppInfos { get; } = new ConcurrentDictionary<uint, AppInfo>();

        public AppInfoHandler(IAnsiConsole ansiConsole, Steam3Session steam3Session, LicenseManager licenseManager)
            : this(ansiConsole,
                   steam3Session,
                   licenseManager,
                   appIds => steam3Session.SteamAppsApi.PICSGetAccessTokens(appIds, new List<uint>()).ToTask())
        {
        }

        internal AppInfoHandler(
            IAnsiConsole ansiConsole,
            Steam3Session steam3Session,
            LicenseManager licenseManager,
            Func<List<uint>, Task<PICSTokensCallback>> requestAccessTokensAsync,
            Func<List<PICSRequest>, Task<AsyncJobMultiple<PICSProductInfoCallback>.ResultSet>> requestProductsAsync = null)
        {
            _ansiConsole = ansiConsole;
            _steam3Session = steam3Session;
            _licenseManager = licenseManager;
            _requestAccessTokensAsync = requestAccessTokensAsync;
            _requestProductsAsync = requestProductsAsync ?? (requests => steam3Session.SteamAppsApi.PICSGetProductInfo(requests, new List<PICSRequest>()).ToTask());
        }

        /// <summary>
        /// Clears all cached app metadata, forcing it to be re-fetched from Steam on next request
        /// </summary>
        public void ClearLoadedAppInfos()
        {
            LoadedAppInfos.Clear();
            _recentlyPlayed = null;
            _ansiConsole.LogMarkupVerbose("Cleared app metadata cache");
        }

        /// <summary>
        /// Removes specific app IDs from the cache, forcing them to be re-fetched from Steam.
        /// Use this to ensure fresh metadata for specific apps without clearing the entire cache.
        /// </summary>
        public void InvalidateApps(List<uint> appIds)
        {
            foreach (var appId in appIds)
            {
                LoadedAppInfos.TryRemove(appId, out _);
            }
            _ansiConsole.LogMarkupVerbose($"Invalidated cache for {Magenta(appIds.Count)} apps");
        }

        #region Loading Metadata

        /// <summary>
        /// Gets the latest app metadata from steam, for the specified apps, as well as their related DLC apps.
        /// </summary>
        public virtual async Task RetrieveAppMetadataAsync(
            List<uint> appIds,
            bool getRecentlyPlayedMetadata = false,
            CancellationToken cancellationToken = default)
        {
            await _ansiConsole.StatusSpinner().StartAsync("Retrieving latest App metadata...", async _ =>
            {
                await BulkLoadAppInfoAsync(appIds, cancellationToken);
                // Once we have loaded all the apps we'll know what DLC is owned, so we can then load DLC metadata
                await FetchDlcAppInfoAsync(cancellationToken);

                // Populating play time if needed in the case of select-apps or using prefill's --recent flag.
                // Otherwise we'll skip it to slightly speed things up
                if (getRecentlyPlayedMetadata)
                {
                    foreach (var app in await GetRecentlyPlayedGamesAsync(cancellationToken))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var appInfo = await GetAppInfoAsync((uint)app.appid, cancellationToken);
                        appInfo.MinutesPlayed2Weeks = app.playtime_2weeks;
                    }
                }
            });
        }

        private async Task BulkLoadAppInfoAsync(List<uint> appIds, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var initialAppIdLoadTimer = Stopwatch.StartNew();

            var filteredAppIds = appIds.Where(e => !LoadedAppInfos.ContainsKey(e))
                                                .Where(e => _licenseManager.AccountHasAppAccess(e))
                                                .Distinct()
                                                .ToList();

            // Breaking into at most 10 concurrent batches
            int batchSize = (filteredAppIds.Count / 10) + 1;
            var batches = filteredAppIds.Chunk(batchSize).ToList();

            // Breaking the request into smaller batches that complete faster
            var batchJobs = new List<Task>();
            foreach (var batch in batches)
            {
                batchJobs.Add(AppInfoRequestAsync(batch.ToList(), cancellationToken));
            }

            await Task.WhenAll(batchJobs);
            _ansiConsole.LogMarkupVerbose($"Loaded metadata for {Magenta(filteredAppIds.Count)} apps", initialAppIdLoadTimer);
        }

        /// <summary>
        /// Retrieves the latest AppInfo for multiple apps at the same time.  One large request containing multiple apps is significantly faster
        /// than multiple individual requests, as it seems that there is a minimum threshold for how quickly steam will return results.
        /// </summary>
        /// <param name="appIdsToLoad">The list of App Ids to retrieve info for</param>
        private async Task AppInfoRequestAsync(
            List<uint> appIdsToLoad,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (appIdsToLoad.Empty())
            {
                return;
            }

            // A dropped Steam connection leaves these PICS jobs pending forever, so both are bounded the
            // same way the manifest request code is.  One catch covers both because nothing between them
            // waits on Steam, and the framework's own timeout text does not say what failed. [30]
            AsyncJobMultiple<PICSProductInfoCallback>.ResultSet resultSet;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _steam3Session.AuthLostToken);
            var stage = "access-tokens";
            try
            {
                // Some apps will require an additional "access token" in order to retrieve their app metadata
                var accessTokensResponse = await _requestAccessTokensAsync(appIdsToLoad)
                    .WaitAsync(MetadataRequestTimeout, linked.Token);
                var appTokens = accessTokensResponse.AppTokens;

                // Build out the requests
                var requests = new List<PICSRequest>();
                foreach (var appId in appIdsToLoad)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var request = new PICSRequest(appId);
                    if (appTokens.ContainsKey(appId))
                    {
                        request.AccessToken = appTokens[appId];
                    }
                    requests.Add(request);
                }

                // Finally request the metadata from steam
                stage = "product-details";
                resultSet = await _requestProductsAsync(requests)
                    .WaitAsync(MetadataRequestTimeout, linked.Token);
            }
            catch (Exception e) when (e is AsyncJobFailedException or TimeoutException ||
                                      e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var failure = new SteamConnectionException(_steam3Session.IsAuthenticated
                    ? SteamFailure.GameDetailsUnavailable : SteamFailure.AuthLost, e)
                {
                    Stage = stage,
                    AppIds = appIdsToLoad.ToArray(),
                    LoginId = _steam3Session.LoginId,
                    SteamResult = _steam3Session.LogoffResult
                };
                FileLogger.LogException(failure.GetContext(), failure);
                throw failure;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!_steam3Session.IsAuthenticated) throw new SteamConnectionException(SteamFailure.AuthLost);

            List<PicsProductInfo> appInfos = resultSet.Results.SelectMany(e => e.Apps).Select(e => e.Value).ToList();
            foreach (var app in appInfos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _steam3Session.WhileAuthenticated(
                    () => LoadedAppInfos.TryAdd(app.ID, new AppInfo(_steam3Session, app.ID, app.KeyValues)),
                    cancellationToken);

                app.KeyValues.WriteSteamMetadataToDisk($@"{AppConfig.DebugOutputDir}\AppInfo\AppInfo_{app.ID}.txt");
            }
        }

        /// <summary>
        /// Steam stores all DLCs for a game as separate "apps", so they must be loaded after the game's AppInfo has been retrieved,
        /// and the list of DLC AppIds are known.
        ///
        /// Once the DLC apps are loaded, the final combined depot list (both the app + dlc apps) will be built.
        /// </summary>
        internal async Task FetchDlcAppInfoAsync(CancellationToken cancellationToken)
        {
            var dlcAppIds = LoadedAppInfos.Values.SelectMany(e => e.DlcAppIds).ToList();
            var manifestAppIds = LoadedAppInfos.Values.Where(e => e.Type == AppType.Game)
                                               .SelectMany(e => e.Depots)
                                               .Select(e => e.ManifestRequestAppId)
                                               .ToList();

            var idsToLoad = manifestAppIds.Union(dlcAppIds).ToList();
            await BulkLoadAppInfoAsync(idsToLoad, cancellationToken);

            // Builds out the list of all depots for each game, including depots from all related DLCs
            // DLCs are stored as separate "apps", so their info comes back separately.
            foreach (var app in LoadedAppInfos.Values.Where(e => e.Type == AppType.Game))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var dlcAppId in app.DlcAppIds)
                {
                    var dlcApp = await GetAppInfoAsync(dlcAppId, cancellationToken);
                    foreach (var depot in dlcApp.Depots)
                    {
                        // The game already lists this depot itself, so there is no parent app to re-point it at.  Leaving
                        // DlcAppId unset keeps the license check on the base game, which is the package that actually grants
                        // a depot the game's own record contains.  Ownership of the depot itself is still checked first, in
                        // DepotHandler.FilterDepotsToDownloadAsync, so an unowned depot never reaches a download.
                        if (app.Depots.Any(e => e.DepotId == depot.DepotId))
                        {
                            continue;
                        }

                        depot.AttachToParentApp(app.AppId, dlcAppId);
                        app.Depots.Add(depot);
                    }
                }

                var distinctDepots = app.Depots.DistinctBy(e => e.DepotId).ToList();
                app.Depots.Clear();
                app.Depots.AddRange(distinctDepots);
            }
        }

        #endregion

        /// <summary>
        /// Will return an AppInfo for the specified AppId, that contains various metadata about the app.
        /// If the information for the specified app hasn't already been retrieved, then a request to the Steam network will be made.
        /// </summary>
        public virtual async Task<AppInfo> GetAppInfoAsync(
            uint appId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LoadedAppInfos.ContainsKey(appId))
            {
                return LoadedAppInfos[appId];
            }

            await AppInfoRequestAsync(new List<uint> { appId }, cancellationToken);
            return LoadedAppInfos[appId];
        }

        /// <summary>
        /// Gets a list of games owned by the user, and filters them down to just the ones that have recent playtime in the last 2 weeks.
        /// </summary>
        public async Task<List<CPlayer_GetOwnedGames_Response.Game>> GetRecentlyPlayedGamesAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_recentlyPlayed != null)
            {
                return _recentlyPlayed;
            }

            var request = new CPlayer_GetOwnedGames_Request
            {
                steamid = _steam3Session._steamId,
                include_appinfo = true,
                include_free_sub = true,
                include_played_free_games = true,
                skip_unvetted_apps = false
            };
            SteamUnifiedMessages.ServiceMethodResponse<CPlayer_GetOwnedGames_Response> response;
            try
            {
                response = await _steam3Session.unifiedPlayerService
                    .GetOwnedGames(request)
                    .ToTask()
                    .WaitAsync(MetadataRequestTimeout, cancellationToken);
            }
            catch (TimeoutException e)
            {
                throw new SteamConnectionException(
                    $"Steam did not answer a request for your game library within {MetadataRequestTimeout.TotalSeconds:0} seconds. " +
                    "The Steam connection may be down; try again, and log in to Steam again if it keeps failing.", e);
            }

            if (response.Result != EResult.OK)
            {
                throw new Exception("Unexpected error while requesting owned games!");
            }

            _recentlyPlayed = response.Body.games.Where(e => e.playtime_2weeks > 0).ToList();
            return _recentlyPlayed;
        }


        //TODO is this necessary because --all includes things that shouldn't be downloaded?
        /// <summary>
        /// Gets a list of available games, filtering out any unavailable, non-Windows games.
        /// </summary>
        public virtual async Task<List<AppInfo>> GetAvailableGamesByIdAsync(
            List<uint> appIds,
            CancellationToken cancellationToken = default)
        {
            //TODO maybe a call to retreive app metadata async here so that you don't need to remember to do it manually
            var appInfos = new List<AppInfo>();
            foreach (var appId in appIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                appInfos.Add(await GetAppInfoAsync(appId, cancellationToken));
            }

            // We'll filter out some specific non-game appids which would otherwise be included.
            var excludedAppIds = Enum.GetValues(typeof(ExcludedAppId)).Cast<uint>().ToList();
            var filteredGames = appInfos.Where(e => (e.Type == AppType.Game || e.Type == AppType.Beta)
                                                    && (e.ReleaseState != ReleaseState.Unavailable && e.ReleaseState != ReleaseState.Disabled)
                                                    && e.SupportsWindows
                                                    && _steam3Session.LicenseManager.AccountHasAppAccess(e.AppId))
                                                    .Where(e => !excludedAppIds.Contains(e.AppId))
                                                    .Where(e => !e.Categories.Contains(Category.Mods) && !e.Categories.Contains(Category.ModsHL2))
                                                    .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                                                    .ToList();

            return filteredGames;
        }
    }
}
