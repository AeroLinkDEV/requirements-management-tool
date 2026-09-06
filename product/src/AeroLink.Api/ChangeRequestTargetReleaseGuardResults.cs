using AeroLink.Infrastructure.Persistence;

/// <summary>
/// HTTP presentation of the shared target-release guard verdict. The decision lives in the guard; each
/// flow family keeps its own stable lifecycle presentation where clients already depend on one, while the
/// not-found posture (foreign and nonexistent are one answer) is identical everywhere.
/// </summary>
public static class ChangeRequestTargetReleaseGuardResults
{
    public const string ReleasedFallbackError =
        "Build {version} has been released and takes no new change requests. Switch to the in-work build and raise it there.";

    public static IResult? ToFailureResult(this ChangeRequestTargetReleaseVerdict verdict,
        string releasedCode = ChangeRequestTargetReleaseGuard.ReleasedCode, string? releasedErrorTemplate = null) =>
        verdict.Eligible ? null
        : verdict.Rejection == ChangeRequestTargetReleaseRejection.Released
            ? Results.BadRequest(new
            {
                error = (releasedErrorTemplate ?? ReleasedFallbackError).Replace("{version}", verdict.ReleasedVersion),
                code = releasedCode,
            })
            : Results.BadRequest(new { error = ChangeRequestTargetReleaseGuard.NotFoundError, code = ChangeRequestTargetReleaseGuard.NotFoundCode });
}
