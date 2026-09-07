using AeroLink.Domain.ChangeControl;

namespace AeroLink.Infrastructure.Persistence;

public sealed partial class FmsShowcaseSeeder
{
    // Exact retained HOME observations qualified for #913. These are deliberately unresolved historical
    // answers and authoring/assessment examples, not newly invented provenance. A different revision,
    // warning class or record is not silently adopted by the upgrade.
    private static readonly IReadOnlyDictionary<Guid, (string Number, string Upstream, string Downstream)> HomeChangeGaps =
        new Dictionary<Guid, (string, string, string)>
        {
            [Guid.Parse("1f7311d8-5bbb-488a-b7ca-95239f65a192")] = ("HLRCR-00076.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("2a92117b-87fd-4fb1-ae7e-f4488b10807e")] = ("HLRCR-00077.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("cafea69f-cf72-48ae-a44d-e2a4799ee8cc")] = ("HLRCR-00120.00", "IncompleteAuthoring", "NoDownstreamWork"),
            [Guid.Parse("230d3248-6d11-45eb-8443-f4f2b02b4bc5")] = ("HLRCR-00123.00", "UpstreamGap", "Pending"),
            [Guid.Parse("c01e82a4-0892-4182-976d-ececd9a3c623")] = ("HLRCR-00124.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("c99c14cc-b8f4-409b-9036-53640986479e")] = ("HLRCR-00125.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("0bf5d440-93a5-4dc3-8c46-1f1be25e6639")] = ("HLRCR-00126.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("c8887ece-07c0-4946-81bc-c91b5472ba24")] = ("HLRCR-00127.00", "UpstreamGap", "Pending"),
            [Guid.Parse("047fe918-01ff-48b1-9cdb-935e12658d64")] = ("HLRCR-00128.00", "UpstreamGap", "Pending"),
            [Guid.Parse("bcf761a6-8a24-4ab6-85a5-1ddc78a002e9")] = ("HLRCR-00134.00", "UpstreamGap", "Pending"),
            [Guid.Parse("2f63ece4-0274-4672-89b5-a4d3b2a75ccc")] = ("LLRCR-00078.00", "IncompleteAuthoring", "NoDownstreamWork"),
            [Guid.Parse("5cb41df3-1b0b-4c14-828b-501946fedd5b")] = ("LLRCR-00079.00", "IncompleteAuthoring", "NoDownstreamWork"),
            [Guid.Parse("9f1102f9-d39a-499b-879e-b149f2ba6cb0")] = ("LLRCR-00080.00", "IncompleteAuthoring", "NoDownstreamWork"),
            [Guid.Parse("8461c90a-8bc0-45a0-b1b0-7b21a3263662")] = ("LLRCR-00081.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("e7aaf6ad-f7c7-47bc-984e-cd765c3b06a8")] = ("LLRCR-00129.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("9b288af6-c8d0-46c0-8a12-27967be0b38f")] = ("LLRCR-00130.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("dec80486-0ce6-4648-8917-d21edfabceb3")] = ("LLRCR-00131.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("f3f7294d-784f-49eb-9735-7be1885ee628")] = ("LLRCR-00132.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("bec83160-d63f-4002-9417-91f12b7dc3d9")] = ("LLRCR-00133.00", "UpstreamGap", "NoDownstreamWork"),
            [Guid.Parse("38653a7b-f6ae-408f-9fff-5bc5d6f6b11d")] = ("SRCR-00031.00", "Root", "ActionGap"),
            [Guid.Parse("5813c911-9371-437d-bddc-8c5bf38b47e5")] = ("SRCR-00034.00", "Root", "ActionGap"),
            [Guid.Parse("63ced617-b3c2-4b1d-a7ec-b1e918eee3c1")] = ("SRCR-00110.00", "Root", "Pending"),
        };

    private static bool IsNamedChangeGap(SystemChangeRequest request, ChangeRequestTraceState state)
    {
        if (HomeChangeGaps.TryGetValue(request.Id, out var home))
            return (request.DisplayNumber, state.Upstream, state.Downstream) == home;
        var package = request.BaseNumber switch
        {
            "SRCR-00031" => 1, "SRCR-00032" => 2, "HLRCR-00076" => 3, "HLRCR-00077" => 4,
            "LLRCR-00078" => 5, "LLRCR-00079" => 6, "LLRCR-00080" => 7, "LLRCR-00081" => 8, _ => 0
        };
        if (package == 0 || request.Revision != 0
            || request.Title != (package == 1 ? "Introduce oceanic round-robin waypoint sequencing" : $"FMS 1.6 change package {package}")
            || request.AuthorId != (package <= 2 ? "systems.author" : "software.author")) return false;
        return (state.Upstream, state.Downstream) == (package <= 2 ? "Root" : package is >= 5 and <= 7 ? "IncompleteAuthoring" : "UpstreamGap",
            package <= 3 ? "Pending" : "NoDownstreamWork");
    }

    private static readonly IReadOnlyDictionary<Guid, (string Number, string Warning)> HomeRequirementGaps =
        new Dictionary<Guid, (string, string)>
        {
            [Guid.Parse("0953f119-7e51-4418-8e7d-dc47c5be56ce")] = ("SYSR-000040.01", "Uncovered"),
            [Guid.Parse("4c8ff1f9-22c3-495c-872f-1003a92fecdb")] = ("SYSR-000076.00", "Uncovered"),
            [Guid.Parse("9ceb87dc-5896-4bbb-ac87-62c228c29766")] = ("SYSR-000115.01", "Uncovered"),
            [Guid.Parse("e23f34fa-6b9c-49e2-baab-5b6336c2a971")] = ("SYSR-000151.00", "Uncovered"),
            [Guid.Parse("7947a245-5fff-45c3-8e7c-419d5b0c2519")] = ("HLR-000075.02", "Suspect"),
            [Guid.Parse("3b3df2ee-357e-4e9a-bffd-80ce3df96ce1")] = ("LLR-000475.01", "UpstreamGap"),
            [Guid.Parse("3df6fac0-d3f8-4c0c-9086-32dbbbd516a6")] = ("LLR-000075.01", "UpstreamGap"),
        };
}
