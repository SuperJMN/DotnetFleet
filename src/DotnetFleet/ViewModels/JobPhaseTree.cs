using System.Collections.ObjectModel;
using System.Reactive.Disposables;
using DynamicData;
using DynamicData.Binding;
using DotnetFleet.Core.Domain;

namespace DotnetFleet.ViewModels;

public sealed class JobPhaseTree : IDisposable
{
    private readonly SourceCache<JobPhaseModel, Guid> phases = new(model => model.Id);
    private readonly Dictionary<string, JobPhaseGroup> groups = new();
    private readonly IDisposable groupSubscription;
    private readonly JobPhaseGroup root;

    public JobPhaseTree(Func<string, string> formatName)
    {
        root = GetGroup(JobPhaseModel.RootGroupKey, formatName);
        groupSubscription = phases.Connect()
            .Group(model => model.ParentKey)
            .Subscribe(changes =>
            {
                foreach (var change in changes)
                {
                    if (change.Reason is ChangeReason.Add or ChangeReason.Update or ChangeReason.Refresh)
                    {
                        GetGroup(change.Current.Key, formatName).Attach(change.Current);
                    }
                    else if (change.Reason == ChangeReason.Remove &&
                             groups.TryGetValue(change.Key, out var removedGroup))
                    {
                        removedGroup.Detach();
                    }
                }
            });
    }

    public ReadOnlyObservableCollection<JobPhaseRowContainer> Phases => root.Rows;

    public void Refresh(IReadOnlyList<JobPhase> currentPhases)
    {
        var models = JobPhaseModel.Build(currentPhases);
        var modelKeys = models.Select(model => model.Id).ToHashSet();
        var removedKeys = phases.Keys.Where(key => !modelKeys.Contains(key)).ToArray();

        phases.Edit(updater =>
        {
            updater.AddOrUpdate(models);
            updater.RemoveKeys(removedKeys);
        });
    }

    public void Dispose()
    {
        groupSubscription.Dispose();
        phases.Dispose();

        foreach (var group in groups.Values)
            group.Dispose();
    }

    private JobPhaseGroup GetGroup(string key, Func<string, string> formatName)
    {
        if (groups.TryGetValue(key, out var group))
            return group;

        group = new JobPhaseGroup(id => GetGroup(JobPhaseModel.GroupKey(id), formatName).Rows, formatName);
        groups[key] = group;
        return group;
    }
}

internal sealed class JobPhaseGroup : IDisposable
{
    private readonly SourceCache<JobPhaseModel, Guid> models = new(model => model.Id);
    private readonly CompositeDisposable disposables = new();
    private IDisposable? attachedGroup;

    public JobPhaseGroup(
        Func<Guid, ReadOnlyObservableCollection<JobPhaseRowContainer>> childrenFor,
        Func<string, string> formatName)
    {
        var sorter = SortExpressionComparer<JobPhaseRowContainer>
            .Ascending(container => container.Content.StartedAt)
            .ThenByAscending(container => container.Content.EndedSort);

        var rowsSubscription = models.Connect()
            .TransformWithInlineUpdate<JobPhaseRowContainer, JobPhaseModel, Guid>(
                model => new JobPhaseRowContainer
                {
                    Content = new JobPhaseRow(model, childrenFor(model.Id), formatName)
                },
                (container, model) => container.Content.Update(model))
            .SortAndBind(out var rows, sorter)
            .Subscribe();
        disposables.Add(rowsSubscription);

        Rows = rows;
    }

    public ReadOnlyObservableCollection<JobPhaseRowContainer> Rows { get; }

    public void Attach(IGroup<JobPhaseModel, Guid, string> group)
    {
        attachedGroup?.Dispose();
        attachedGroup = group.Cache.Connect()
            .Subscribe(Apply);
        disposables.Add(attachedGroup);
    }

    public void Detach()
    {
        attachedGroup?.Dispose();
        attachedGroup = null;
        var keys = models.Keys.ToArray();
        models.Edit(updater => updater.RemoveKeys(keys));
    }

    public void Dispose()
    {
        disposables.Dispose();
        models.Dispose();
    }

    private void Apply(IChangeSet<JobPhaseModel, Guid> changes)
    {
        models.Edit(updater =>
        {
            foreach (var change in changes)
            {
                if (change.Reason is ChangeReason.Add or ChangeReason.Update or ChangeReason.Refresh)
                {
                    updater.AddOrUpdate(change.Current);
                }
                else if (change.Reason == ChangeReason.Remove)
                {
                    updater.RemoveKey(change.Key);
                }
            }
        });
    }
}

internal sealed record JobPhaseModel(JobPhase Phase, string ParentKey)
{
    public const string RootGroupKey = "";

    public Guid Id => Phase.Id;

    public static string GroupKey(Guid parentId) => parentId.ToString("N");

    public static IReadOnlyList<JobPhaseModel> Build(IReadOnlyList<JobPhase> phases)
    {
        var ordered = phases
            .OrderBy(phase => phase.StartedAt)
            .ThenBy(phase => phase.EndedAt ?? DateTimeOffset.MaxValue)
            .ToList();

        var models = new List<JobPhaseModel>(ordered.Count);
        var stack = new Stack<JobPhase>();

        foreach (var phase in ordered)
        {
            while (stack.Count > 0 && !Contains(stack.Peek(), phase))
                stack.Pop();

            var parentKey = stack.TryPeek(out var parent)
                ? GroupKey(parent.Id)
                : RootGroupKey;

            models.Add(new JobPhaseModel(phase, parentKey));
            stack.Push(phase);
        }

        return models;
    }

    private static bool Contains(JobPhase outer, JobPhase inner)
    {
        if (inner.StartedAt < outer.StartedAt) return false;
        if (outer.EndedAt is null) return true;
        var innerEnd = inner.EndedAt ?? inner.StartedAt;
        return innerEnd <= outer.EndedAt.Value;
    }
}
