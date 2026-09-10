namespace AeroLink.Infrastructure.Persistence;

public sealed record ChangeRequestUpstreamDraft(Guid UpstreamChangeRequestId, string? Rationale = null);
