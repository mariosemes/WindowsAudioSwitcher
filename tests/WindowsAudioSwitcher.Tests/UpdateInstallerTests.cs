using System.IO;
using System.Security.Cryptography;
using WindowsAudioSwitcher.Updates;
using Xunit;

namespace WindowsAudioSwitcher.Tests;

public class UpdateInstallerTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();
    private static readonly string PayloadSha = Convert.ToHexString(SHA256.HashData(Payload));

    private sealed class RecordingProgress : IProgress<DownloadProgress>
    {
        public List<DownloadProgress> Reports { get; } = new();
        public void Report(DownloadProgress value) => Reports.Add(value);
    }

    [Fact]
    public async Task SaveVerified_MatchingHash_WritesFileAndClosesIt()
    {
        using var dir = new TempDir();
        var dest = dir.File("setup.exe");
        var progress = new RecordingProgress();

        await UpdateInstaller.SaveVerifiedAsync(new MemoryStream(Payload), dest, PayloadSha, Payload.Length, progress, CancellationToken.None);

        Assert.Equal(Payload, File.ReadAllBytes(dest));
        // The regression in 0.5.7 to 0.7.0: the download handle stayed open.
        // An exclusive open only succeeds once every other handle is closed.
        using (new FileStream(dest, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(Payload.Length, progress.Reports[^1].BytesReceived);
        Assert.Equal(100.0, progress.Reports[^1].Percent);
    }

    [Fact]
    public async Task SaveVerified_HashComparisonIsCaseInsensitive()
    {
        using var dir = new TempDir();
        var dest = dir.File("setup.exe");
        await UpdateInstaller.SaveVerifiedAsync(new MemoryStream(Payload), dest, PayloadSha.ToLowerInvariant(), null, null, CancellationToken.None);
        Assert.True(File.Exists(dest));
    }

    [Fact]
    public async Task SaveVerified_HashMismatch_ThrowsAndDeletesFile()
    {
        using var dir = new TempDir();
        var dest = dir.File("setup.exe");
        var wrong = new string('0', 64);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateInstaller.SaveVerifiedAsync(new MemoryStream(Payload), dest, wrong, Payload.Length, null, CancellationToken.None));

        Assert.Contains("SHA-256 mismatch", ex.Message);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task SaveVerified_TruncatedDownload_ThrowsAndDeletesFile()
    {
        using var dir = new TempDir();
        var dest = dir.File("setup.exe");
        var truncated = Payload.AsSpan(0, 1000).ToArray();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            UpdateInstaller.SaveVerifiedAsync(new MemoryStream(truncated), dest, PayloadSha, Payload.Length, null, CancellationToken.None));

        Assert.Contains("incomplete", ex.Message);
        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task SaveVerified_Cancelled_LeavesNoPartialFile()
    {
        using var dir = new TempDir();
        var dest = dir.File("setup.exe");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            UpdateInstaller.SaveVerifiedAsync(new MemoryStream(Payload), dest, PayloadSha, Payload.Length, null, cts.Token));

        Assert.False(File.Exists(dest));
    }

    [Fact]
    public async Task SaveVerified_OverwritesStaleFileFromEarlierAttempt()
    {
        using var dir = new TempDir();
        var dest = dir.File("setup.exe");
        File.WriteAllBytes(dest, new byte[] { 1, 2, 3 });

        await UpdateInstaller.SaveVerifiedAsync(new MemoryStream(Payload), dest, PayloadSha, Payload.Length, null, CancellationToken.None);

        Assert.Equal(Payload.Length, new FileInfo(dest).Length);
    }
}
