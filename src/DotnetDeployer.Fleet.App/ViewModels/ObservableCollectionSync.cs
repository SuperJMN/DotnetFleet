using System.Collections.ObjectModel;

namespace DotnetDeployer.Fleet.App.ViewModels;

internal static class ObservableCollectionSync
{
    public static void Sync<TItem, TViewModel, TKey>(
        ObservableCollection<TViewModel> collection,
        IEnumerable<TItem> items,
        Func<TItem, TKey> itemKey,
        Func<TViewModel, TKey> viewModelKey,
        Func<TItem, TViewModel> create,
        Action<TViewModel, TItem> update)
        where TKey : notnull
    {
        var incoming = items.ToList();
        var incomingKeys = incoming.Select(itemKey).ToHashSet();

        for (var i = collection.Count - 1; i >= 0; i--)
        {
            if (!incomingKeys.Contains(viewModelKey(collection[i])))
                collection.RemoveAt(i);
        }

        var currentByKey = collection.ToDictionary(viewModelKey);

        for (var targetIndex = 0; targetIndex < incoming.Count; targetIndex++)
        {
            var item = incoming[targetIndex];
            var key = itemKey(item);

            if (currentByKey.TryGetValue(key, out var existing))
            {
                update(existing, item);
                var currentIndex = collection.IndexOf(existing);
                if (currentIndex != targetIndex)
                    collection.Move(currentIndex, targetIndex);
            }
            else
            {
                collection.Insert(targetIndex, create(item));
            }
        }
    }
}
