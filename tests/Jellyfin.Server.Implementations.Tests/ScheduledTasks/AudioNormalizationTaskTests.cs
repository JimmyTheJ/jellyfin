using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Server.Implementations.ScheduledTasks.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.ScheduledTasks;

public class AudioNormalizationTaskTests
{
    [Fact]
    public void BuildFfmpegArguments_MapsFirstAudioStreamAndSkipsVideo()
    {
        var args = AudioNormalizationTask.BuildFfmpegArguments("-i \"movie.mkv\"");

        Assert.Equal("-hide_banner -nostdin -i \"movie.mkv\" -vn -sn -dn -map 0:a:0 -af ebur128 -f null -", args);
    }

    [Theory]
    [InlineData("    I:         -23.7 LUFS", -23.7f)]
    [InlineData("  I: -14.0 LUFS", -14f)]
    public void TryParseSummaryLufs_SummaryLine_ParsesIntegratedLoudness(string line, float expected)
    {
        Assert.True(AudioNormalizationTask.TryParseSummaryLufs(line, out var lufs));
        Assert.Equal(expected, lufs);
    }

    [Theory]
    [InlineData("t: 0.5  TARGET:-23 LUFS    M: -70.0 S: -70.0     I: -70.0 LUFS       LRA:   0.0 LU")]
    [InlineData("/media/movie.mkv: No such file or directory")]
    [InlineData("    Threshold: -33.1 LUFS")]
    public void TryParseSummaryLufs_NonSummaryLine_ReturnsFalse(string line)
    {
        Assert.False(AudioNormalizationTask.TryParseSummaryLufs(line, out _));
    }

    [Fact]
    public async Task ExecuteAsync_MovieLibrary_QueriesVideoItems()
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Test Movie",
            Path = Path.Combine(Path.GetTempPath(), "movie.mkv")
        };

        var library = new Folder { Name = "Movies" };
        var root = new AggregateFolder { Children = [library] };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSourceManager.Object;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.SetupGet(x => x.RootFolder).Returns(root);
        libraryManager.Setup(x => x.GetLibraryOptions(library)).Returns(new LibraryOptions { EnableLUFSScan = true });
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
                q.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [movie] : Array.Empty<BaseItem>());

        var persistence = new Mock<IItemPersistenceService>();

        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.SetupGet(x => x.EncoderPath).Returns(Path.Combine(Path.GetTempPath(), "missing-ffmpeg"));

        var task = new AudioNormalizationTask(
            persistence.Object,
            libraryManager.Object,
            mediaEncoder.Object,
            Mock.Of<IApplicationPaths>(),
            Mock.Of<ILocalizationManager>(),
            NullLogger<AudioNormalizationTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        libraryManager.Verify(
            x => x.GetItemList(It.Is<InternalItemsQuery>(q =>
                q.Recursive
                && q.MediaTypes.Contains(MediaType.Video)
                && q.IncludeItemTypes.Contains(BaseItemKind.Movie)
                && q.IncludeItemTypes.Contains(BaseItemKind.Episode)
                && q.IncludeItemTypes.Contains(BaseItemKind.MusicVideo))),
            Times.Once);
        persistence.Verify(
            x => x.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_VideoAlreadyHasLufs_DoesNotRescan()
    {
        var movie = new Movie
        {
            Id = Guid.NewGuid(),
            Name = "Already Scanned",
            Path = Path.Combine(Path.GetTempPath(), "movie.mkv"),
            LUFS = -23f
        };

        var library = new Folder { Name = "Movies" };
        var root = new AggregateFolder { Children = [library] };

        var mediaSourceManager = new Mock<IMediaSourceManager>();
        mediaSourceManager.Setup(x => x.GetPathProtocol(It.IsAny<string>())).Returns(MediaProtocol.File);
        BaseItem.MediaSourceManager = mediaSourceManager.Object;

        var libraryManager = new Mock<ILibraryManager>();
        libraryManager.SetupGet(x => x.RootFolder).Returns(root);
        libraryManager.Setup(x => x.GetLibraryOptions(library)).Returns(new LibraryOptions { EnableLUFSScan = true });
        libraryManager
            .Setup(x => x.GetItemList(It.IsAny<InternalItemsQuery>()))
            .Returns((InternalItemsQuery q) =>
                q.IncludeItemTypes.Contains(BaseItemKind.Movie) ? [movie] : Array.Empty<BaseItem>());

        var persistence = new Mock<IItemPersistenceService>();
        var mediaEncoder = new Mock<IMediaEncoder>();
        mediaEncoder.SetupGet(x => x.EncoderPath).Returns(Path.Combine(Path.GetTempPath(), "missing-ffmpeg"));

        var task = new AudioNormalizationTask(
            persistence.Object,
            libraryManager.Object,
            mediaEncoder.Object,
            Mock.Of<IApplicationPaths>(),
            Mock.Of<ILocalizationManager>(),
            NullLogger<AudioNormalizationTask>.Instance);

        await task.ExecuteAsync(new Progress<double>(), CancellationToken.None);

        persistence.Verify(
            x => x.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()),
            Times.Never);
        mediaEncoder.VerifyGet(x => x.EncoderPath, Times.Never);
    }
}
