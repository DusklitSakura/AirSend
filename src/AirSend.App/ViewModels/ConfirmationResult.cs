namespace AirSend.ViewModels;

/// <summary>
/// Outcome of a confirmation dialog: whether the action was accepted, and whether
/// the user ticked "don't remind me again" (which is scoped to this app run).
/// </summary>
public readonly record struct ConfirmationResult(bool Confirmed, bool DontAskAgain);
