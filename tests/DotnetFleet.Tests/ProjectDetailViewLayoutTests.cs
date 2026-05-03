using System.Xml.Linq;

public class ProjectDetailViewLayoutTests
{
    [Fact]
    public void ProjectDetailView_ShouldSeparateDeployAndBuildIntoTabs()
    {
        var document = XDocument.Load(ProjectDetailViewPath());
        XNamespace axaml = "https://github.com/avaloniaui";

        var tabControl = document.Descendants(axaml + "TabControl").SingleOrDefault();

        tabControl.Should().NotBeNull();
        var tabs = tabControl!.Elements(axaml + "TabItem").ToList();
        tabs.Select(tab => tab.Attribute("Header")?.Value).Should().Equal("Deploy", "Build");
        tabs[0].Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .Contain("Project Secrets");
        tabs[1].Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .Contain("Package Targets");
    }

    private static string ProjectDetailViewPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Combine(directory.FullName, "src", "DotnetFleet", "Views", "ProjectDetailView.axaml");
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not find ProjectDetailView.axaml from the test output directory.");
    }
}
