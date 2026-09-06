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
    [Theory]
    [InlineData(false, "-hide_banner -i \"movie.mkv\" -af ebur128=framelog=verbose -f null -")]
    [InlineData(true, "-hide_banner -i \"movie.mkv\" -vn -af ebur128=framelog=verbose -f null -")]
    public void BuildFfmpegArguments_SkipVideo_InsertsVnForVideoFiles(bool skipVideo, string expected)
    {
        var args = AudioNormalizationTask.BuildFfmpegArguments("-i \"movie.mkv\"", skipVideo);

        Assert.Equal(expected, args);
    }

    [Fact]
    public async Task ExecuteAsync_MovieLibrary_QueriesAndPersistsVideoItems()
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

        IReadOnlyList<BaseItem>? saved = null;
        var persistence = new Mock<IItemPersistenceService>();
        persistence
            .Setup(x => x.SaveItems(It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()))
            .Callback<IReadOnlyList<BaseItem>, CancellationToken>((items, _) => saved = items);

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
        Assert.NotNull(saved);
        Assert.Same(movie, Assert.Single(saved));
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
