namespace UpsHub.Core.Nut;

/// <summary>An <c>ERR ...</c> reply from upsd.</summary>
public sealed class NutException : Exception
{
    public NutError Error { get; }
    public string RawCode { get; }
    public string Command { get; }

    public NutException(string rawCode, string command)
        : base($"{rawCode} (after '{RedactCommand(command)}')")
    {
        RawCode = rawCode;
        Error = NutErrorParser.Parse(rawCode.Split(' ', 2)[0]);
        Command = RedactCommand(command);
    }

    internal static string RedactCommand(string command) =>
        command.StartsWith("PASSWORD", StringComparison.OrdinalIgnoreCase) ? "PASSWORD ***" : command;
}

/// <summary>The connection broke, timed out or the server replied with something unparseable.</summary>
public sealed class NutConnectionException : Exception
{
    public NutConnectionException(string message, Exception? inner = null) : base(message, inner) { }
}
