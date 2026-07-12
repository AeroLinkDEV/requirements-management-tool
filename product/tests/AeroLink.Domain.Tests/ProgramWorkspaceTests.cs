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

    [Theory]
    [InlineData("", "NAV")]
    [InlineData("Navigation Systems", "")]
    public void Program_requires_name_and_code(string name, string code) =>
        Assert.Throws<ArgumentException>(() => new ProgramRecord(name, code));
}
