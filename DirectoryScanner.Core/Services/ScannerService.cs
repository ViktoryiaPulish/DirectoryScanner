using DirectoryScanner.Core.Models;
using System.Collections.Concurrent;
using System.IO;

namespace DirectoryScanner.Core.Services;

public class ScannerService
{
    private readonly int _maxThreads;

    private SemaphoreSlim _semaphore = null!;

    public ScannerService(int maxThreads = 10)
    {
        _maxThreads = maxThreads;
    }

    public async Task<DirectoryItem> ScanAsync(string path, CancellationToken token)
    {
        _semaphore = new SemaphoreSlim(_maxThreads);
        var rootDir = new DirectoryInfo(path);

        if (!rootDir.Exists)
            throw new DirectoryNotFoundException(path);

        var rootNode = new DirectoryItem
        {
            Name = rootDir.Name,
            Type = ItemType.Directory
        };

        int pendingOperations = 1;
        var tcs = new TaskCompletionSource<bool>();

        void OperationCompleted()
        {
            if (Interlocked.Decrement(ref pendingOperations) == 0)
            {
                tcs.TrySetResult(true);
            }
        }

        ThreadPool.QueueUserWorkItem(_ =>
            ProcessDirectory(rootDir, rootNode, token,
                             incrementOp: () => Interlocked.Increment(ref pendingOperations),
                             completeOp: OperationCompleted));

        await tcs.Task;

        CalculateFolderSizes(rootNode);
        CalculatePercentages(rootNode, null);

        return rootNode;
    }

    private void ProcessDirectory(DirectoryInfo dirInfo, DirectoryItem node, CancellationToken token, Action incrementOp, Action completeOp)
    {
        _semaphore.Wait();

        try
        {
            if (token.IsCancellationRequested) return;

            try
            {
                foreach (var file in dirInfo.EnumerateFiles())
                {
                    if (token.IsCancellationRequested) break;

                    if (file.LinkTarget != null) continue;

                    node.Children.Add(new DirectoryItem
                    {
                        Name = file.Name,
                        Size = file.Length,
                        Type = ItemType.File
                    });

                    node.Size += file.Length;
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception) { }

            try
            {
                foreach (var subDir in dirInfo.EnumerateDirectories())
                {
                    if (token.IsCancellationRequested) break;

                    if (subDir.LinkTarget != null) continue;

                    var subNode = new DirectoryItem
                    {
                        Name = subDir.Name,
                        Type = ItemType.Directory
                    };
                    node.Children.Add(subNode);

                    incrementOp();

                    ThreadPool.QueueUserWorkItem(_ =>
                        ProcessDirectory(subDir, subNode, token, incrementOp, completeOp));
                }
            }
            catch (UnauthorizedAccessException) { }
            catch (Exception) { }
        }
        finally
        {
            _semaphore.Release();
            completeOp(); 
        }
    }

    private long CalculateFolderSizes(DirectoryItem node)
    {
        long totalSize = node.Size; 

        foreach (var child in node.Children)
        {
            if (child.Type == ItemType.Directory)
            {
                totalSize += CalculateFolderSizes(child);
            }
        }

        node.Size = totalSize;
        return totalSize;
    }

    private void CalculatePercentages(DirectoryItem node, long? parentSize)
    {
        if (parentSize.HasValue && parentSize.Value > 0)
        {
            node.Percentage = (double)node.Size / parentSize.Value * 100.0;
        }
        else
        {
            node.Percentage = 100.0;
        }

        foreach (var child in node.Children)
        {
            CalculatePercentages(child, node.Size);
        }
    }
}