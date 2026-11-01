namespace UpsHub.Core.Nut;

/// <summary>Error codes defined by the NUT network protocol (docs/net-protocol.txt).</summary>
public enum NutError
{
    Unknown,
    AccessDenied,
    UnknownUps,
    VarNotSupported,
    CmdNotSupported,
    InvalidArgument,
    InstcmdFailed,
    SetFailed,
    Readonly,
    TooLong,
    FeatureNotSupported,
    FeatureNotConfigured,
    AlreadySslMode,
    DriverNotConnected,
    DataStale,
    AlreadyLoggedIn,
    InvalidPassword,
    AlreadySetPassword,
    InvalidUsername,
    AlreadySetUsername,
    UsernameRequired,
    PasswordRequired,
    UnknownCommand,
    InvalidValue,
}

public static class NutErrorParser
{
    public static NutError Parse(string code) => code.Trim().ToUpperInvariant() switch
    {
        "ACCESS-DENIED" => NutError.AccessDenied,
        "UNKNOWN-UPS" => NutError.UnknownUps,
        "VAR-NOT-SUPPORTED" => NutError.VarNotSupported,
        "CMD-NOT-SUPPORTED" => NutError.CmdNotSupported,
        "INVALID-ARGUMENT" => NutError.InvalidArgument,
        "INSTCMD-FAILED" => NutError.InstcmdFailed,
        "SET-FAILED" => NutError.SetFailed,
        "READONLY" => NutError.Readonly,
        "TOO-LONG" => NutError.TooLong,
        "FEATURE-NOT-SUPPORTED" => NutError.FeatureNotSupported,
        "FEATURE-NOT-CONFIGURED" => NutError.FeatureNotConfigured,
        "ALREADY-SSL-MODE" => NutError.AlreadySslMode,
        "DRIVER-NOT-CONNECTED" => NutError.DriverNotConnected,
        "DATA-STALE" => NutError.DataStale,
        "ALREADY-LOGGED-IN" => NutError.AlreadyLoggedIn,
        "INVALID-PASSWORD" => NutError.InvalidPassword,
        "ALREADY-SET-PASSWORD" => NutError.AlreadySetPassword,
        "INVALID-USERNAME" => NutError.InvalidUsername,
        "ALREADY-SET-USERNAME" => NutError.AlreadySetUsername,
        "USERNAME-REQUIRED" => NutError.UsernameRequired,
        "PASSWORD-REQUIRED" => NutError.PasswordRequired,
        "UNKNOWN-COMMAND" => NutError.UnknownCommand,
        "INVALID-VALUE" => NutError.InvalidValue,
        _ => NutError.Unknown,
    };

    /// <summary>A short, human explanation suitable for the UI.</summary>
    public static string Describe(NutError e) => e switch
    {
        NutError.AccessDenied => "Access denied. Check the user in upsd.users, or the server's client allow-list (Synology DSM keeps one).",
        NutError.UnknownUps => "The server doesn't know that UPS name. Pick one from the server's UPS list.",
        NutError.DriverNotConnected => "upsd is running but its UPS driver isn't connected to the UPS.",
        NutError.DataStale => "The UPS driver has stopped sending fresh data (DATA-STALE).",
        NutError.InvalidPassword or NutError.InvalidUsername => "Wrong username or password.",
        NutError.UsernameRequired or NutError.PasswordRequired => "This server needs a username and password.",
        NutError.FeatureNotConfigured => "TLS isn't configured on this server.",
        NutError.FeatureNotSupported => "The server doesn't support this feature.",
        NutError.AlreadyLoggedIn => "Already logged in to this UPS.",
        _ => "The server returned an error.",
    };
}
