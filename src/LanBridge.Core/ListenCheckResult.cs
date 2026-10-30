namespace LanBridge.Core;

/// <summary>Conflict includes saved-rule overlap and a bind refused as occupied or prohibited.</summary>
public enum ListenCheckStatus { Unknown, Available, Conflict }

/// <summary>
/// Advisory local bind preflight; Available is not a reservation or a save guarantee.
/// Unknown means invalid input or inability to check, not proof that the port is free.
/// </summary>
public sealed record ListenCheckResult(ListenCheckStatus Status, string Message);
