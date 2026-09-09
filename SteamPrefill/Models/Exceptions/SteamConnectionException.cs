namespace SteamPrefill.Models.Exceptions
{
    public enum SteamFailure
    {
        AuthLost,
        GameDetailsUnavailable
    }

    public sealed class SteamConnectionException : Exception
    {
        public SteamFailure? Failure { get; }
        public bool RequiresLogin => Failure == SteamFailure.AuthLost;
        internal string Stage { get; init; }
        internal IReadOnlyList<uint> AppIds { get; init; }
        internal uint? LoginId { get; init; }
        internal EResult? SteamResult { get; init; }

        internal string GetContext(string command = null, string operationId = null)
        {
            using var stream = new MemoryStream();
            using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                if (command != null) writer.WriteString("command", command);
                if (operationId != null) writer.WriteString("operationId", operationId);
                if (Stage != null) writer.WriteString("picsStage", Stage);
                if (LoginId.HasValue) writer.WriteNumber("loginId", LoginId.Value);
                if (SteamResult.HasValue) writer.WriteNumber("steamResult", (int)SteamResult.Value);
                if (AppIds != null)
                {
                    writer.WriteNumber("appCount", AppIds.Count);
                    writer.WriteStartArray("appIds");
                    foreach (var appId in AppIds.Take(100)) writer.WriteNumberValue(appId);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        public string ErrorCode => Failure switch
        {
            SteamFailure.AuthLost => "auth-lost",
            SteamFailure.GameDetailsUnavailable => "game-details-unavailable",
            _ => null
        };

        public SteamConnectionException(SteamFailure failure, Exception inner = null)
            : base(failure == SteamFailure.AuthLost
                ? "Steam is no longer signed in. Sign in again, then retry the prefill."
                : "Steam did not return game details. Try again.", inner)
        {
            Failure = failure;
        }
        public SteamConnectionException()
        {

        }

        public SteamConnectionException(string message) : base(message)
        {

        }

        public SteamConnectionException(string message, Exception inner) : base(message, inner)
        {

        }
    }
}
