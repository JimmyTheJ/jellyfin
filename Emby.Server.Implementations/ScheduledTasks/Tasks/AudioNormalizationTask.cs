using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Emby.Server.Implementations.ScheduledTasks.Tasks;

/// <summary>
/// The audio normalization task.
/// </summary>
public partial class AudioNormalizationTask : IScheduledTask
{
    private readonly IItemPersistenceService _persistenceService;
    private readonly ILibraryManager _libraryManager;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly IApplicationPaths _applicationPaths;
    private readonly ILocalizationManager _localization;
    private readonly ILogger<AudioNormalizationTask> _logger;

    private static readonly TimeSpan _dbSaveInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Initializes a new instance of the <see cref="AudioNormalizationTask"/> class.
    /// </summary>
    /// <param name="persistenceService">Instance of the <see cref="IItemPersistenceService"/> interface.</param>
    /// <param name="libraryManager">Instance of the <see cref="ILibraryManager"/> interface.</param>
    /// <param name="mediaEncoder">Instance of the <see cref="IMediaEncoder"/> interface.</param>
    /// <param name="applicationPaths">Instance of the <see cref="IApplicationPaths"/> interface.</param>
    /// <param name="localizationManager">Instance of the <see cref="ILocalizationManager"/> interface.</param>
    /// <param name="logger">Instance of the <see cref="ILogger{AudioNormalizationTask}"/> interface.</param>
    public AudioNormalizationTask(
        IItemPersistenceService persistenceService,
        ILibraryManager libraryManager,
        IMediaEncoder mediaEncoder,
        IApplicationPaths applicationPaths,
        ILocalizationManager localizationManager,
        ILogger<AudioNormalizationTask> logger)
    {
        _persistenceService = persistenceService;
        _libraryManager = libraryManager;
        _mediaEncoder = mediaEncoder;
        _applicationPaths = applicationPaths;
        _localization = localizationManager;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => _localization.GetLocalizedString("TaskAudioNormalization");

    /// <inheritdoc />
    public string Description => _localization.GetLocalizedString("TaskAudioNormalizationDescription");

    /// <inheritdoc />
    public string Category => _localization.GetLocalizedString("TasksLibraryCategory");

    /// <inheritdoc />
    public string Key => "AudioNormalization";

    [GeneratedRegex(@"^\s+I:\s+(-?\d+(?:\.\d+)?)\s+LUFS")]
    private static partial Regex LUFSSummaryRegex();

    private const int MaxLoggedStderrLines = 40;

    /// <inheritdoc />
    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var numComplete = 0;
        var libraries = _libraryManager.RootFolder.Children.Where(library => _libraryManager.GetLibraryOptions(library).EnableLUFSScan).ToArray();
        double percent = 0;

        foreach (var library in libraries)
        {
            var startDbSaveInterval = Stopwatch.GetTimestamp();
            var albums = _libraryManager.GetItemList(new InternalItemsQuery { IncludeItemTypes = [BaseItemKind.MusicAlbum], Parent = library, Recursive = true });
            var toSaveDbItems = new List<BaseItem>();

            double nextPercent = numComplete + 1;
            nextPercent /= libraries.Length;
            nextPercent -= percent;
            // Split progress for this library into thirds: album gain, audio track gain, video item gain.
            nextPercent /= 3;
            var albumComplete = 0;

            foreach (var a in albums)
            {
                if (!a.NormalizationGain.HasValue && !a.LUFS.HasValue)
                {
                    // Album gain
                    var albumTracks = ((MusicAlbum)a).Tracks.Where(x => x.IsFileProtocol).ToList();

                    // Skip albums that don't have multiple tracks, album gain is useless here
                    if (albumTracks.Count > 1)
                    {
                        _logger.LogInformation("Calculating LUFS for album: {Album} with id: {Id}", a.Name, a.Id);
                        var tempDir = _applicationPaths.TempDirectory;
                        Directory.CreateDirectory(tempDir);
                        var tempFile = Path.Join(tempDir, a.Id + ".concat");
                        var inputLines = albumTracks.Select(x => string.Format(CultureInfo.InvariantCulture, "file '{0}'", x.Path.Replace("'", @"'\''", StringComparison.Ordinal)));
                        await File.WriteAllLinesAsync(tempFile, inputLines, cancellationToken).ConfigureAwait(false);
                        try
                        {
                            a.LUFS = await CalculateLUFSAsync(
                                string.Format(CultureInfo.InvariantCulture, "-f concat -safe 0 -i \"{0}\"", tempFile),
                                a.Name,
                                cancellationToken).ConfigureAwait(false);
                            if (a.LUFS.HasValue)
                            {
                                toSaveDbItems.Add(a);
                            }
                        }
                        finally
                        {
                            try
                            {
                                File.Delete(tempFile);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError(ex, "Failed to delete concat file: {FileName}.", tempFile);
                            }
                        }
                    }
                }

                if (Stopwatch.GetElapsedTime(startDbSaveInterval) > _dbSaveInterval)
                {
                    if (toSaveDbItems.Count > 1)
                    {
                        _persistenceService.SaveItems(toSaveDbItems, cancellationToken);
                        toSaveDbItems.Clear();
                    }

                    startDbSaveInterval = Stopwatch.GetTimestamp();
                }

                // Update sub-progress for album gain
                albumComplete++;
                ReportSubProgress(progress, percent, nextPercent, albumComplete, albums.Count);
            }

            // Update progress to start at the track gain percent calculation
            percent += nextPercent;

            if (toSaveDbItems.Count > 1)
            {
                _persistenceService.SaveItems(toSaveDbItems, cancellationToken);
                toSaveDbItems.Clear();
            }

            startDbSaveInterval = Stopwatch.GetTimestamp();

            // Track gain
            var tracks = _libraryManager.GetItemList(new InternalItemsQuery { MediaTypes = [MediaType.Audio], IncludeItemTypes = [BaseItemKind.Audio], Parent = library, Recursive = true });

            var tracksComplete = 0;
            foreach (var t in tracks)
            {
                if (!t.NormalizationGain.HasValue && !t.LUFS.HasValue && t.IsFileProtocol)
                {
                    t.LUFS = await CalculateLUFSAsync(
                        string.Format(CultureInfo.InvariantCulture, "-i \"{0}\"", t.Path.EscapeProcessArgument()),
                        t.Name,
                        cancellationToken).ConfigureAwait(false);
                    if (t.LUFS.HasValue)
                    {
                        toSaveDbItems.Add(t);
                    }
                }

                if (Stopwatch.GetElapsedTime(startDbSaveInterval) > _dbSaveInterval)
                {
                    if (toSaveDbItems.Count > 1)
                    {
                        _persistenceService.SaveItems(toSaveDbItems, cancellationToken);
                        toSaveDbItems.Clear();
                    }

                    startDbSaveInterval = Stopwatch.GetTimestamp();
                }

                // Update sub-progress for track gain
                tracksComplete++;
                ReportSubProgress(progress, percent, nextPercent, tracksComplete, tracks.Count);
            }

            if (toSaveDbItems.Count > 0)
            {
                _persistenceService.SaveItems(toSaveDbItems, cancellationToken);
                toSaveDbItems.Clear();
            }

            startDbSaveInterval = Stopwatch.GetTimestamp();

            // Video item gain (Movies, Episodes, MusicVideos)
            percent += nextPercent;
            var videoItems = _libraryManager.GetItemList(new InternalItemsQuery
            {
                MediaTypes = [MediaType.Video],
                IncludeItemTypes = [BaseItemKind.Movie, BaseItemKind.Episode, BaseItemKind.MusicVideo],
                Parent = library,
                Recursive = true
            });

            var videoComplete = 0;
            foreach (var v in videoItems)
            {
                if (!v.NormalizationGain.HasValue && !v.LUFS.HasValue && v.IsFileProtocol)
                {
                    _logger.LogInformation("Calculating LUFS for video item: {Name} with id: {Id}", v.Name, v.Id);
                    v.LUFS = await CalculateLUFSAsync(
                        string.Format(CultureInfo.InvariantCulture, "-i \"{0}\"", v.Path.EscapeProcessArgument()),
                        v.Name,
                        cancellationToken).ConfigureAwait(false);
                    if (v.LUFS.HasValue)
                    {
                        toSaveDbItems.Add(v);
                    }
                }

                if (Stopwatch.GetElapsedTime(startDbSaveInterval) > _dbSaveInterval)
                {
                    if (toSaveDbItems.Count > 1)
                    {
                        _persistenceService.SaveItems(toSaveDbItems, cancellationToken);
                        toSaveDbItems.Clear();
                    }

                    startDbSaveInterval = Stopwatch.GetTimestamp();
                }

                videoComplete++;
                ReportSubProgress(progress, percent, nextPercent, videoComplete, videoItems.Count);
            }

            if (toSaveDbItems.Count > 0)
            {
                _persistenceService.SaveItems(toSaveDbItems, cancellationToken);
            }

            numComplete++;
            percent = numComplete;
            percent /= libraries.Length;

            progress.Report(100 * percent);
        }

        progress.Report(100.0);
    }

    /// <inheritdoc />
    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(24).Ticks
        };
    }

    /// <summary>
    /// Builds the ffmpeg arguments used to measure EBU R128 integrated loudness.
    /// </summary>
    /// <param name="inputArgs">Input arguments, including <c>-i</c>.</param>
    /// <returns>The complete ffmpeg argument string.</returns>
    internal static string BuildFfmpegArguments(string inputArgs)
    {
        return $"-hide_banner -nostdin {inputArgs} -vn -sn -dn -map 0:a:0 -af ebur128 -f null -";
    }

    /// <summary>
    /// Parses an ffmpeg ebur128 summary line for integrated loudness.
    /// </summary>
    /// <param name="line">A single stderr line.</param>
    /// <param name="lufs">The parsed LUFS value.</param>
    /// <returns>Whether the line was a summary integrated-loudness value.</returns>
    internal static bool TryParseSummaryLufs(string line, out float lufs)
    {
        lufs = 0;
        var match = LUFSSummaryRegex().Match(line);
        return match.Success
            && float.TryParse(match.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out lufs);
    }

    private static void ReportSubProgress(IProgress<double> progress, double percent, double nextPercent, int complete, int total)
    {
        var portion = total == 0 ? 1d : (double)complete / total;
        progress.Report(100 * (percent + (portion * nextPercent)));
    }

    private async Task<float?> CalculateLUFSAsync(string inputArgs, string itemName, CancellationToken cancellationToken)
    {
        var args = BuildFfmpegArguments(inputArgs);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true
            }
        };

        _logger.LogDebug("Starting ffmpeg for {Name} with arguments: {Arguments}", itemName, args);
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error starting ffmpeg for {Name} with arguments: {Arguments}", itemName, args);
            return null;
        }

        try
        {
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error setting ffmpeg process priority");
        }

        var stdoutDrain = process.StandardOutput.ReadToEndAsync(cancellationToken);
        float? lufs = null;
        var stderrTail = new Queue<string>();

        await foreach (var line in process.StandardError.ReadAllLinesAsync(cancellationToken).ConfigureAwait(false))
        {
            if (stderrTail.Count >= MaxLoggedStderrLines)
            {
                stderrTail.Dequeue();
            }

            stderrTail.Enqueue(line);

            if (lufs is null && TryParseSummaryLufs(line, out var parsed))
            {
                lufs = parsed;
            }
        }

        try
        {
            await stdoutDrain.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error draining ffmpeg stdout for {Name}", itemName);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (lufs is null)
        {
            _logger.LogError(
                "Failed to find LUFS value for {Name}. ExitCode: {ExitCode}. Arguments: {Arguments}. Output: {Output}",
                itemName,
                process.ExitCode,
                args,
                string.Join('\n', stderrTail));
        }

        return lufs;
    }
}
