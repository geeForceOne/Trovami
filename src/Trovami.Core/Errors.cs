namespace Trovami.Core;

// Thrown when something doesn't exist or belongs to someone else. The message is user-facing.
public sealed class NotFoundException(string message = "That no longer exists.") : Exception(message);

// A problem with what the person entered, in user-facing words.
public sealed class InputException(string message) : Exception(message);
