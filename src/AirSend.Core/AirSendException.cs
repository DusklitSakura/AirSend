using AirSend.Core.Localization;

namespace AirSend.Core;

/// <summary>
/// Base class for the failures AirSend explains to the user. The text lives in the
/// catalog and is resolved when it is displayed, not when it is thrown, so the same
/// exception reads in whatever language the interface is using at that moment.
/// </summary>
public abstract class AirSendException : Exception
{
    protected AirSendException(string messageKey, object? parameters = null, Exception? innerException = null)
        : base(AppText.Get(messageKey, parameters), innerException)
    {
        MessageKey = messageKey;
        Parameters = parameters;
    }

    /// <summary>Catalog key of the message, for tests and for logging.</summary>
    public string MessageKey { get; }

    public object? Parameters { get; }

    /// <summary>The message in the language selected right now.</summary>
    public string LocalizedMessage => AppText.Get(MessageKey, Parameters);
}

/// <summary>
/// Message shown for a failure, in the language the interface is using now. Framework
/// exceptions (sockets, files) keep their own technical text.
/// </summary>
public static class AirSendError
{
    public static string Describe(Exception exception) =>
        exception is AirSendException localized ? localized.LocalizedMessage : exception.Message;
}
