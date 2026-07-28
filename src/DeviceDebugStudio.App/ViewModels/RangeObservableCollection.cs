using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DeviceDebugStudio.App.ViewModels;

public sealed class RangeObservableCollection<T> : ObservableCollection<T>
{
    public void AddRange(IEnumerable<T> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        int added = 0;
        foreach (T item in items)
        {
            Items.Add(item);
            added++;
        }

        if (added == 0)
        {
            return;
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void RemoveFirst(int count)
    {
        int removeCount = Math.Clamp(count, 0, Items.Count);
        if (removeCount == 0)
        {
            return;
        }

        if (Items is List<T> list)
        {
            list.RemoveRange(0, removeCount);
        }
        else
        {
            for (int index = 0; index < removeCount; index++)
            {
                Items.RemoveAt(0);
            }
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    public void ReplaceAll(IEnumerable<T> items)
    {
        Items.Clear();
        foreach (T item in items)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
