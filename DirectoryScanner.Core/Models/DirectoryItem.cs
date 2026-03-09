using System.Collections.Concurrent;

namespace DirectoryScanner.Core.Models;

public enum ItemType { Directory, File }

public class DirectoryItem
{
    public string Name { get; set; } = string.Empty;
    public long Size { get; set; }
    public ItemType Type { get; set; }
    public double Percentage { get; set; }

    public ConcurrentBag<DirectoryItem> Children { get; set; } = new();

    public IEnumerable<DirectoryItem> SortedChildren =>
        Children.OrderByDescending(x => x.Size);
}