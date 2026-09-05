namespace AeroLink.Domain.Common;

/// <summary>
/// A controlled export refused to proceed because evidence inside its scope does not carry an exact
/// revision identity. Legacy attachments that predate exact binding stay readable, but presenting them as
/// evidence of a specific exported revision would invent provenance, so the export fails closed with the
/// stable <see cref="DiagnosticCode"/> diagnostic instead of silently dropping or guessing the binding.
/// </summary>
public sealed class ControlledEvidenceBindingException(string message) : InvalidOperationException(message)
{
    /// <summary>The stable, actionable diagnostic surfaced to operators and clients.</summary>
    public const string DiagnosticCode = "attachment_revision_binding_required";
}
