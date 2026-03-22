using DirectoryScanner.Core.Services;
using DirectoryScanner.Core.Models;
using Xunit;

namespace DirectoryScanner.Tests;

public class ScannerTests
{

    [Fact]
    public async Task ScanAsync_ValidDirectoryWithSubdirectories_CalculatesTotalSizeAndPercentagesCorrectly()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_Valid_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);
        var subDir = Path.Combine(tempPath, "Sub");
        Directory.CreateDirectory(subDir);

        await File.WriteAllBytesAsync(Path.Combine(tempPath, "f1.bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(subDir, "f2.bin"), new byte[200]);

        var scanner = new ScannerService(maxThreads: 2);

        try
        {
            var result = await scanner.ScanAsync(tempPath, CancellationToken.None);

            Assert.Equal(300, result.Size);
            var subNode = result.Children.First(c => c.Name == "Sub");
            Assert.Equal(200, subNode.Size);
            Assert.Equal(66.7, subNode.Percentage, 1);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task ScanAsync_EmptyDirectory_ReturnsZeroSizeAndEmptyChildren()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_Empty_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        var scanner = new ScannerService();

        try
        {
            var result = await scanner.ScanAsync(tempPath, CancellationToken.None);

            Assert.Equal(0, result.Size);
            Assert.Empty(result.Children);
            Assert.Equal(100.0, result.Percentage);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task ScanAsync_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var fakePath = Path.Combine(Path.GetTempPath(), "ScannerTest_NonExistent_" + Guid.NewGuid());
        var scanner = new ScannerService();

        await Assert.ThrowsAsync<DirectoryNotFoundException>(() =>
            scanner.ScanAsync(fakePath, CancellationToken.None));
    }


    [Fact]
    public async Task ScanAsync_DirectoryWithSymbolicLinkFile_IgnoresLinkInSizeCalculation()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_SymLinkFile_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        var targetFile = Path.Combine(tempPath, "target.bin");
        await File.WriteAllBytesAsync(targetFile, new byte[1000]);

        var linkFile = Path.Combine(tempPath, "link.bin");
        try
        {
            File.CreateSymbolicLink(linkFile, targetFile);
        }
        catch
        {
            return;
        }

        var scanner = new ScannerService();

        try
        {
            var result = await scanner.ScanAsync(tempPath, CancellationToken.None);
            Assert.Equal(1000, result.Size);
            Assert.Single(result.Children);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task ScanAsync_DirectoryWithSymbolicLinkFolder_IgnoresLinkedFolder()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_SymLinkFolder_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        var targetFolder = Path.Combine(tempPath, "TargetFolder");
        Directory.CreateDirectory(targetFolder);
        await File.WriteAllBytesAsync(Path.Combine(targetFolder, "target.bin"), new byte[500]);

        var linkFolder = Path.Combine(tempPath, "LinkFolder");
        try
        {
            Directory.CreateSymbolicLink(linkFolder, targetFolder);
        }
        catch
        {
            return;
        }

        var scanner = new ScannerService();

        try
        {
            var result = await scanner.ScanAsync(tempPath, CancellationToken.None);

            Assert.Equal(500, result.Size);
            Assert.Single(result.Children, c => c.Type == ItemType.Directory && c.Name == "TargetFolder");
            Assert.DoesNotContain(result.Children, c => c.Name == "LinkFolder");
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task ScanAsync_CancellationTokenCanceled_ReturnsPartialResultsWithoutThrowing()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_Cancel_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        for (int i = 0; i < 50; i++)
            await File.WriteAllBytesAsync(Path.Combine(tempPath, $"f{i}.bin"), new byte[10]);

        var scanner = new ScannerService(maxThreads: 2);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); 

        try
        {
            var result = await scanner.ScanAsync(tempPath, cts.Token);

            Assert.NotNull(result);
            Assert.Equal(ItemType.Directory, result.Type);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task ScanAsync_CancellationAfterStart_ReturnsSizeConsistentWithCollectedChildren()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_CancelConsistency_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        for (int i = 0; i < 5; i++)
        {
            var sub = Path.Combine(tempPath, $"sub{i}");
            Directory.CreateDirectory(sub);
            await File.WriteAllBytesAsync(Path.Combine(sub, "file.bin"), new byte[100]);
        }

        var scanner = new ScannerService(maxThreads: 2);

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(millisecondsDelay: 5);

        var result = await scanner.ScanAsync(tempPath, cts.Token);

        long expectedSize = CalculateExpectedSize(result);
        Assert.Equal(expectedSize, result.Size);

        if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
    }

    private static long CalculateExpectedSize(DirectoryItem node)
    {
        long sum = 0;
        foreach (var child in node.Children)
            sum += child.Type == ItemType.File ? child.Size : CalculateExpectedSize(child);
        return sum;
    }

    [Fact]
    public async Task ScanAsync_DeeplyNestedDirectoriesWithFewThreads_DoesNotDeadlock()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_Deadlock_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        int depth = 15;
        string currentPath = tempPath;
        for (int i = 0; i < depth; i++)
        {
            currentPath = Path.Combine(currentPath, $"Level{i}");
            Directory.CreateDirectory(currentPath);
            await File.WriteAllBytesAsync(Path.Combine(currentPath, "file.bin"), new byte[10]);
        }

        var scanner = new ScannerService(maxThreads: 2);

        try
        {
            var scanTask = scanner.ScanAsync(tempPath, CancellationToken.None);
            var finished = await Task.WhenAny(scanTask, Task.Delay(5000));

            Assert.True(finished == scanTask, "Scan deadlocked: did not complete within 5 seconds.");

            var result = await scanTask;
            Assert.Equal(depth * 10, result.Size);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    public static IEnumerable<object[]> WorkerCounts =>
    [
        [1],
    [100],
    [2 * Environment.ProcessorCount] 
    ];

    [Theory]
    [MemberData(nameof(WorkerCounts))]
    public async Task ScanAsync_WithWorkerCount_CompletesAndCalculatesCorrectly(int maxThreads)
    {
        var tempPath = Path.Combine(Path.GetTempPath(),
            $"ScannerTest_Workers{maxThreads}_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        var subDir = Path.Combine(tempPath, "Sub");
        Directory.CreateDirectory(subDir);
        await File.WriteAllBytesAsync(Path.Combine(tempPath, "f1.bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(subDir, "f2.bin"), new byte[200]);

        var scanner = new ScannerService(maxThreads: maxThreads);

        try
        {
            var scanTask = scanner.ScanAsync(tempPath, CancellationToken.None);
            var finished = await Task.WhenAny(scanTask, Task.Delay(10_000));

            Assert.True(finished == scanTask,
                $"Scan deadlocked with maxThreads={maxThreads}.");

            var result = await scanTask;
            Assert.Equal(300, result.Size);

            var subNode = result.Children.First(c => c.Name == "Sub");
            Assert.Equal(200, subNode.Size);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }

    [Fact]
    public async Task ScanAsync_SingleWorker_DeeplyNested_DoesNotDeadlock()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_1Thread_Deep_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        int depth = 10;
        string currentPath = tempPath;
        for (int i = 0; i < depth; i++)
        {
            currentPath = Path.Combine(currentPath, $"Level{i}");
            Directory.CreateDirectory(currentPath);
            await File.WriteAllBytesAsync(Path.Combine(currentPath, "file.bin"), new byte[10]);
        }

        var scanner = new ScannerService(maxThreads: 1);

        try
        {
            var scanTask = scanner.ScanAsync(tempPath, CancellationToken.None);
            var finished = await Task.WhenAny(scanTask, Task.Delay(10_000));

            Assert.True(finished == scanTask,
                "Scan deadlocked with maxThreads=1 on deep nesting.");

            var result = await scanTask;
            Assert.Equal(depth * 10, result.Size);
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }


    [Fact]
    public async Task ScanAsync_DirectoryWithMultipleLevels_CalculatesPercentagesCorrectlyDownTheTree()
    {
        var tempPath = Path.Combine(Path.GetTempPath(), "ScannerTest_MultiLevel_" + Guid.NewGuid());
        Directory.CreateDirectory(tempPath);

        var level1A = Path.Combine(tempPath, "Level1A");
        var level1B = Path.Combine(tempPath, "Level1B");
        Directory.CreateDirectory(level1A);
        Directory.CreateDirectory(level1B);

        var level2A = Path.Combine(level1A, "Level2A");
        Directory.CreateDirectory(level2A);

        await File.WriteAllBytesAsync(Path.Combine(tempPath, "root.bin"), new byte[100]);
        await File.WriteAllBytesAsync(Path.Combine(level1A, "a.bin"), new byte[200]);
        await File.WriteAllBytesAsync(Path.Combine(level1B, "b.bin"), new byte[400]);
        await File.WriteAllBytesAsync(Path.Combine(level2A, "c.bin"), new byte[300]);

        var scanner = new ScannerService(maxThreads: 4);

        try
        {
            var result = await scanner.ScanAsync(tempPath, CancellationToken.None);

            Assert.Equal(1000, result.Size);

            var node1A = result.Children.First(c => c.Name == "Level1A");
            var node1B = result.Children.First(c => c.Name == "Level1B");
            var node2A = node1A.Children.First(c => c.Name == "Level2A");

            Assert.Equal(50.0, node1A.Percentage, 1);  
            Assert.Equal(40.0, node1B.Percentage, 1);  
            Assert.Equal(60.0, node2A.Percentage, 1);  
        }
        finally
        {
            if (Directory.Exists(tempPath)) Directory.Delete(tempPath, true);
        }
    }
}