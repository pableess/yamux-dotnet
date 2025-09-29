# Exceptions

Yamux defines several exception types for error handling:

## YamuxException
Base exception for Yamux errors.

## SessionException
Thrown for session-level errors (e.g., protocol errors, session shutdown).

## SessionChannelException
Thrown for channel-specific errors (e.g., channel rejected, session closed).

---

All exceptions provide meaningful messages and, where appropriate, error codes. See the XML documentation in the code for details on when exceptions are thrown by public APIs.