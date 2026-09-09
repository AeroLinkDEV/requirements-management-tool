using System.Linq.Expressions;

namespace AeroLink.Domain.ChangeControl;

/// <summary>Approval belongs to the exact upstream revision, never to its base number or successor.</summary>
public static class ChangeRequestUpstreamEligibility
{
    public static bool IsApproved(ChangeRequestState state) =>
        state is ChangeRequestState.Approved or ChangeRequestState.SelectedForBaseline;

    public static Expression<Func<SystemChangeRequest, bool>> VisibleSources =>
        source => source.State == ChangeRequestState.Approved
            || source.State == ChangeRequestState.SelectedForBaseline
            || source.State == ChangeRequestState.Deferred;

    public static string RefusalFor(SystemChangeRequest source) =>
        source.State == ChangeRequestState.Deferred
            ? $"{source.DisplayNumber} is deferred and cannot be linked. Reassign this CR to the current build before linking it. The exact revision must also be approved."
            : Refusal;

    public const string Refusal = "An upstream dependency requires an exact Approved or Allocated change-request revision.";
}
