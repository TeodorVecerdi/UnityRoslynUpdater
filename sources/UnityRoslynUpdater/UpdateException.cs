namespace UnityRoslynUpdater;

/// <summary>An error that is reported to the user without a stack trace.</summary>
internal sealed class UpdateException(string message) : Exception(message);
