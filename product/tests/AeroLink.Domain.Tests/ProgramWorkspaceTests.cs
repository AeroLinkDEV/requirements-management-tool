using AeroLink.Domain.Programs;

namespace AeroLink.Domain.Tests;

public sealed class ProgramWorkspaceTests
{
    [Fact]
    public void Workspace_records_preserve_program_project_and_release_relationships()
    {
        var program = new ProgramRecord("Navigation Systems", "nav");
        var project = new ProjectRecord(program.Id, "Navigation Software", "Integrated Navigation Software");
        var release = new SoftwareRelease(project.Id, "1.0", true);
        Assert.Equal("NAV", program.Code);
        Assert.Equal(program.Id, project.ProgramId);
        Assert.Equal(project.Id, release.ProjectId);
        Assert.True(release.IsReleased);
    }

    [Fact]
    public void Successor_release_retains_explicit_product_lineage()
    {
        var projectId = Guid.NewGuid();
        var released = new SoftwareRelease(projectId, "1.5", true);
        var successor = new SoftwareRelease(projectId, "1.6", false, released.Id);

        Assert.Equal(released.Id, successor.PredecessorReleaseId);
        Assert.False(successor.IsReleased);
    }

    [Theory]
    [InlineData("", "NAV")]
    [InlineData("Navigation Systems", "")]
    public void Program_requires_name_and_code(string name, string code) =>
        Assert.Throws<ArgumentException>(() => new ProgramRecord(name, code));
}
