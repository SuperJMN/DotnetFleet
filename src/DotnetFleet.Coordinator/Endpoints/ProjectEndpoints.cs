using DotnetFleet.Core.Domain;
using DotnetFleet.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;

namespace DotnetFleet.Coordinator.Endpoints;

public static class ProjectEndpoints
{
    public static void MapProjectEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects").RequireAuthorization();

        group.MapGet("/", GetAll);
        group.MapGet("/{id:guid}", GetById);
        group.MapPost("/", Create);
        group.MapPut("/{id:guid}", Update);
        group.MapDelete("/{id:guid}", Delete);
        group.MapPost("/{id:guid}/deploy", EnqueueDeploy);
        group.MapGet("/{id:guid}/jobs", GetJobsForProject);
    }

    private static async Task<IResult> GetAll(IFleetStorage storage)
    {
        var projects = await storage.GetProjectsAsync();
        return Results.Ok(projects);
    }

    private static async Task<IResult> GetById(Guid id, IFleetStorage storage)
    {
        var project = await storage.GetProjectAsync(id);
        return project is null ? Results.NotFound() : Results.Ok(project);
    }

    private static async Task<IResult> Create([FromBody] CreateProjectRequest req, IFleetStorage storage)
    {
        var project = new Project
        {
            Name = req.Name,
            GitUrl = req.GitUrl,
            Branch = req.Branch,
            PollingIntervalMinutes = req.PollingIntervalMinutes,
            GitToken = string.IsNullOrWhiteSpace(req.GitToken) ? null : req.GitToken
        };

        await storage.AddProjectAsync(project);
        return Results.Created($"/api/projects/{project.Id}", project);
    }

    private static async Task<IResult> Update(Guid id, [FromBody] UpdateProjectRequest req, IFleetStorage storage)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        project.Name = req.Name ?? project.Name;
        project.GitUrl = req.GitUrl ?? project.GitUrl;
        project.Branch = req.Branch ?? project.Branch;
        if (req.PollingIntervalMinutes.HasValue)
            project.PollingIntervalMinutes = req.PollingIntervalMinutes.Value;
        if (req.GitToken is not null)
            project.GitToken = string.IsNullOrWhiteSpace(req.GitToken) ? null : req.GitToken;

        await storage.UpdateProjectAsync(project);
        return Results.Ok(project);
    }

    private static async Task<IResult> Delete(Guid id, IFleetStorage storage)
    {
        await storage.DeleteProjectAsync(id);
        return Results.NoContent();
    }

    private static async Task<IResult> EnqueueDeploy(Guid id, IFleetStorage storage)
    {
        var project = await storage.GetProjectAsync(id);
        if (project is null)
            return Results.NotFound();

        var job = new DeploymentJob
        {
            ProjectId = id,
            IsAutoTriggered = false
        };

        await storage.AddJobAsync(job);
        return Results.Created($"/api/jobs/{job.Id}", job);
    }

    private static async Task<IResult> GetJobsForProject(Guid id, IFleetStorage storage)
    {
        var jobs = await storage.GetJobsByProjectAsync(id);
        return Results.Ok(jobs);
    }

    public record CreateProjectRequest(string Name, string GitUrl, string Branch = "main", int PollingIntervalMinutes = 0, string? GitToken = null);
    public record UpdateProjectRequest(string? Name, string? GitUrl, string? Branch, int? PollingIntervalMinutes, string? GitToken = null);
}
