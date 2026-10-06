namespace TillPOS.Core.Security;

/// <summary>The action needs a supervisor's PIN (spec §13b.5); the caller shows the supervisor challenge and retries.</summary>
public sealed class ApprovalRequiredException(string reason) : Exception(reason);
