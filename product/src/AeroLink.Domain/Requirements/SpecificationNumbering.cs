using System.Text.RegularExpressions;

namespace AeroLink.Domain.Requirements;

/// <summary>A section, the number the document gives it, and how deeply it sits.</summary>
/// <param name="Id">The section node.</param>
/// <param name="ParentId">The section it sits under, or null at the top of the document.</param>
/// <param name="Number">Its number, read off the structure: "4", "4.1", "4.1.1".</param>
/// <param name="Depth">How many sections it sits under, so a reader can be shown the shape.</param>
/// <param name="Heading">The heading, without a number in it.</param>
public sealed record NumberedSection(Guid Id, Guid? ParentId, string Number, int Depth, string Heading);

/// <summary>
/// Section numbers, derived from the document's structure rather than stored in its headings.
///
/// Headings used to be written with their own number in them — "1. Functional Behavior". That is correct
/// exactly until somebody inserts a section, at which point every heading below it is wrong and the only
/// repair is to retype them all. It also cannot express a sub-section: there is nowhere for 4.1.1 to come
/// from when 4 is a string somebody typed.
///
/// A number is a fact about where a section sits, so it is read from where the section sits. Insert a new
/// section between 4 and 5 and everything after it renumbers itself, which is what a reader expects a
/// document to do and what no amount of careful typing reliably achieves.
/// </summary>
public static class SpecificationNumbering
{
    // Matches a leading "4.", "4.1", "4.1.1 " and the separator after it, and nothing else. Anchored, so a
    // heading that legitimately begins with a figure — "3D Terrain Rendering" — keeps it.
    private static readonly Regex LeadingNumber = new(@"^\s*\d+(\.\d+)*\.?[\s ]+", RegexOptions.Compiled);

    /// <summary>A heading with any number it was written with removed.</summary>
    public static string WithoutLeadingNumber(string heading) => LeadingNumber.Replace(heading ?? "", "").Trim();

    /// <summary>
    /// Numbers every section of one specification, depth first, in the order a reader meets them.
    ///
    /// Ordering within a parent is by recorded position and then by id, which is the same order the controlled
    /// editing snapshot uses. Two sections cannot share a position under one parent — check-in refuses it — so
    /// the id only decides between rows that a half-written draft has left tied.
    /// </summary>
    public static IReadOnlyList<NumberedSection> Number(IEnumerable<(Guid Id, Guid? ParentId, int Position, string Heading)> sections)
    {
        var all = sections.ToList();
        // A lookup rather than a dictionary: the top of the document is the group whose parent is null, and a
        // dictionary refuses a null key outright.
        var byParent = all.ToLookup(x => x.ParentId);
        var ordered = new List<NumberedSection>();

        void Walk(Guid? parentId, string prefix, int depth)
        {
            var children = byParent[parentId].OrderBy(x => x.Position).ThenBy(x => x.Id).ToList();
            for (var index = 0; index < children.Count; index++)
            {
                var child = children[index];
                var number = prefix.Length == 0 ? $"{index + 1}" : $"{prefix}.{index + 1}";
                ordered.Add(new NumberedSection(child.Id, child.ParentId, number, depth, WithoutLeadingNumber(child.Heading)));
                Walk(child.Id, number, depth + 1);
            }
        }

        Walk(null, "", 0);
        // A section whose parent is missing from the set would otherwise vanish silently. It is listed at the
        // end, unnumbered, so a caller can see it rather than wonder where it went.
        foreach (var orphan in all.Where(x => ordered.All(row => row.Id != x.Id)).OrderBy(x => x.Position).ThenBy(x => x.Id))
            ordered.Add(new NumberedSection(orphan.Id, orphan.ParentId, "", 0, WithoutLeadingNumber(orphan.Heading)));
        return ordered;
    }
}
