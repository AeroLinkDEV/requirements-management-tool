using AeroLink.Domain.Verification;
using AeroLink.Domain.ChangeControl;
using System.Linq.Expressions;

namespace AeroLink.Domain.Hierarchy;

/// <summary>
/// One authoritative answer to "which artifact kind does this level execute?".
///
/// #720/#726 settle the rule: System executes its Procedure; software Case-only executes the Case; software
/// Case+Procedure executes the Procedure. The resolved project ladder already derives the executable from
/// the persisted enabled kinds, so this helper exists so execution, test-set, readiness, workspace and
/// materialization consumers stop re-implementing "System || Case" guesses that cannot see the Procedure
/// tier. Consumers that intentionally speak about Case coverage or Case documents keep their Case filters;
/// this helper is only for the effective executable.
/// </summary>
public static class EffectiveExecutableArtifact
{
    /// <summary>The executable artifact kind for one verification-capable level under the given policy.</summary>
    public static VerificationArtifactKind KindFor(ILadderPolicy policy, RequirementLevel level) =>
        policy.ExecutableArtifactKey(level).Kind;

    /// <summary>
    /// Executable bindings for every verification-capable level, for EF-translatable filtering
    /// (<c>bindings.Any(b => b.Level == procedure.Level &amp;&amp; b.Kind == procedure.ArtifactKind)</c>).
    /// </summary>
    public static IReadOnlyList<ArtifactKindBinding> Bindings(ILadderPolicy policy) =>
        policy.OrderedLevels
            .Where(level => policy.Definition(level).Verification is not null)
            .Select(level => new ArtifactKindBinding(policy.ProcedureLevel(level), KindFor(policy, level)))
            .ToArray();

    /// <summary>
    /// Every enabled (level, kind) binding for the effective profile. Used by navigation, search and
    /// workspace surfaces that must show BOTH Cases and Procedures when the Procedure tier is enabled;
    /// execution and test-set consumers use <see cref="Bindings"/> (executable only).
    /// </summary>
    public static IReadOnlyList<ArtifactKindBinding> EnabledBindings(ILadderPolicy policy) =>
        policy.OrderedLevels
            .Where(level => policy.Definition(level).Verification is not null)
            .SelectMany(level => policy.VerificationProfile(level).Definitions
                .Select(definition => new ArtifactKindBinding(policy.ProcedureLevel(level), definition.Kind)))
            .ToArray();

    /// <summary>True when the given procedure artifact is the executable for its level under the policy.</summary>
    public static bool IsExecutable(ILadderPolicy policy, TestProcedureLevel level, VerificationArtifactKind kind) =>
        Bindings(policy).Any(binding => binding.Level == level && binding.Kind == kind);

    /// <summary>
    /// EF-translatable executable-kind predicate with the binding values embedded as constants, so queries
    /// do not rely on translating a parameter collection of records.
    /// </summary>
    public static Expression<Func<TestProcedure, bool>> ExecutablePredicate(ILadderPolicy policy) =>
        Predicate(policy, Bindings(policy));

    /// <summary>EF-translatable enabled-kind predicate (all profile definitions, not only the executable).</summary>
    public static Expression<Func<TestProcedure, bool>> EnabledPredicate(ILadderPolicy policy) =>
        Predicate(policy, EnabledBindings(policy));

    private static Expression<Func<TestProcedure, bool>> Predicate(ILadderPolicy policy,
        IReadOnlyList<ArtifactKindBinding> bindings)
    {
        var procedure = Expression.Parameter(typeof(TestProcedure), "procedure");
        var level = Expression.Property(procedure, nameof(TestProcedure.Level));
        var kind = Expression.Property(procedure, nameof(TestProcedure.ArtifactKind));
        Expression? body = null;
        foreach (var binding in bindings)
        {
            var condition = Expression.AndAlso(
                Expression.Equal(level, Expression.Constant(binding.Level)),
                Expression.Equal(kind, Expression.Constant(binding.Kind)));
            body = body is null ? condition : Expression.OrElse(body, condition);
        }
        return Expression.Lambda<Func<TestProcedure, bool>>(body ?? Expression.Constant(false), procedure);
    }
}

/// <summary>Stable (level, kind) artifact binding used for EF translation.</summary>
public sealed record ArtifactKindBinding(TestProcedureLevel Level, VerificationArtifactKind Kind);
