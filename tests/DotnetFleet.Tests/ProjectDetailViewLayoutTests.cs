using System.Xml.Linq;

public class ProjectDetailViewLayoutTests
{
    [Fact]
    public void ProjectDetailView_ShouldUseTheFullScreenForJobHistory()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectDetailView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "TabControl").Should().BeEmpty();
        document.Descendants(axaml + "ItemsControl")
            .Select(control => control.Attribute("ItemsSource")?.Value)
            .Should()
            .Contain("{Binding Jobs}");

        document.Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .NotContain(["Project Secrets", "Package Targets"]);
    }

    [Fact]
    public void PackageBuildOptionsDialog_ShouldExposeBuildTargetOptions()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "PackageBuildOptionsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .Contain("Package Targets");

        document.Descendants(axaml + "DataTemplate")
            .Select(template => template.Attribute("DataType")?.Value)
            .Should()
            .Contain(["vm:PackagePlatformViewModel", "vm:PackageFormatViewModel", "vm:PackageTargetOptionViewModel"]);

        document.Descendants(axaml + "CheckBox")
            .Select(checkBox => checkBox.Attribute("IsChecked")?.Value)
            .Should()
            .Contain("{Binding IsSelected}");
    }

    [Fact]
    public void ProjectSecretsView_ShouldBeAvailableAsFlyoutContent()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "ProjectSecretsView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Root!.Attribute(XName.Get("DataType", "http://schemas.microsoft.com/winfx/2006/xaml"))!
            .Value.Should().Be("vm:ProjectSecretsViewModel");

        document.Descendants(axaml + "TextBlock")
            .Select(x => x.Attribute("Text")?.Value)
            .Should()
            .Contain("Project Secrets");
    }

    [Theory]
    [InlineData("AddProjectView.axaml")]
    [InlineData("EditProjectView.axaml")]
    public void ProjectFormViews_ShouldFitNarrowScreens(string viewFile)
    {
        var document = XDocument.Load(ProjectFilePath("Views", viewFile));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "ScrollViewer")
            .Select(scrollViewer => scrollViewer.Attribute("HorizontalScrollBarVisibility")?.Value)
            .Should()
            .Contain("Disabled");

        document.Descendants()
            .Where(element => element.Attribute("Width")?.Value == "480")
            .Should()
            .BeEmpty();

        document.Descendants(axaml + "StackPanel")
            .Select(stackPanel => stackPanel.Attribute("Orientation")?.Value)
            .Should()
            .NotContain("Horizontal");
    }

    [Fact]
    public void AddProjectView_ShouldLeaveDialogChromeToDialogService()
    {
        var document = XDocument.Load(ProjectFilePath("Views", "AddProjectView.axaml"));
        XNamespace axaml = "https://github.com/avaloniaui";

        document.Descendants(axaml + "EnhancedButton").Should().BeEmpty();
        document.Descendants(axaml + "TextBlock")
            .Select(textBlock => textBlock.Attribute("Text")?.Value)
            .Should()
            .NotContain("Add Project");
    }

    [Fact]
    public void ProjectsViewModel_ShouldOpenAddProjectInDialog()
    {
        var source = File.ReadAllText(ProjectFilePath("ViewModels", "ProjectsViewModel.cs"));

        source.Should().Contain("_dialog.Show(vm, \"Add Project\"");
        source.Should().NotContain("Navigator.Go(() => new AddProjectViewModel");
    }

    private static string ProjectFilePath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var pathParts = new[] { directory.FullName, "src", "DotnetFleet" }.Concat(parts).ToArray();
            var path = Path.Combine(pathParts);
            if (File.Exists(path))
                return path;

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not find {Path.Combine(parts)} from the test output directory.");
    }
}
