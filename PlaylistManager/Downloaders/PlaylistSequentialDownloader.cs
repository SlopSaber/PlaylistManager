using BeatSaberPlaylistsLib.Types;
using BeatSaverSharp;
using BeatSaverSharp.Models;
using IPA.Loader;
using IPA.Utilities;
using PlaylistManager.Configuration;
using PlaylistManager.Types;
using SiraUtil.Web;
using SiraUtil.Zenject;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PlaylistManager.Utilities;
using SongCore;
using Zenject;

namespace PlaylistManager.Downloaders
{
    internal class PlaylistSequentialDownloader : IInitializable, IDisposable
    {
        private readonly IHttpService siraHttpService;
        private readonly BeatSaverSharp.Http.UnityWebRequestService beatSaverHttpService;
        private readonly Dictionary<string, Beatmap> beatmapsByKey = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> beatmapKeysByHash = new(StringComparer.OrdinalIgnoreCase);
        private readonly SemaphoreSlim downloadSemaphore;
        private static readonly HashSet<string> ownedHashes = new(StringComparer.OrdinalIgnoreCase);
        private DownloadQueueEntry currentDownload;

        private readonly SemaphoreSlim pauseSemaphore;
        private readonly SemaphoreSlim popupSemaphore;
        private readonly CancellationTokenSource lifetimeCancellation = new();
        private bool preferCustomArchiveURL;
        private bool ignoredDiskWarning;
        private bool disposed;

        internal event Action PopupEvent;
        internal event Action QueueUpdatedEvent;

        internal static readonly List<object> downloadQueue = new();
        private static readonly LinkedList<BeatSaberPlaylistsLib.Types.Playlist> coversToRefresh = new();

        private PopupContents _pendingPopup;
        internal PopupContents PendingPopup
        {
            get => _pendingPopup;
            private set
            {
                _pendingPopup = value;
                PopupEvent?.Invoke();
            }
        }

        public PlaylistSequentialDownloader(UBinder<Plugin, PluginMetadata> metadata, IHttpService siraHttpService)
        {
            this.siraHttpService = siraHttpService;
            var options = new BeatSaverOptions(metadata.Value.Name, metadata.Value.HVersion.ToString());
            beatSaverHttpService = new BeatSaverSharp.Http.UnityWebRequestService
            {
                BaseURL = options.BeatSaverAPI.ToString(),
                Timeout = options.Timeout,
                UserAgent = $"{options.ApplicationName}/{options.Version}"
            };
            downloadSemaphore = new SemaphoreSlim(1, 1);
            pauseSemaphore = new SemaphoreSlim(0, 1);
            popupSemaphore = new SemaphoreSlim(0, 1);
            PendingPopup = null;
        }

        public void Initialize()
        {
            foreach (var downloadQueueEntry in downloadQueue.OfType<DownloadQueueEntry>())
            {
                downloadQueueEntry.DownloadAbortedEvent += OnDownloadAborted;
            }

            foreach (DownloadQueueEntry _ in downloadQueue)
            {
                IterateQueue();
            }

            PlaylistDownloader.PlaylistQueuedEvent += OnPlaylistQueued;
        }

        public void Dispose()
        {
            disposed = true;
            lifetimeCancellation.Cancel();
            beatmapsByKey.Clear();
            beatmapKeysByHash.Clear();
            if (currentDownload != null && !currentDownload.cancellationTokenSource.IsCancellationRequested)
            {
                currentDownload.cancellationTokenSource.Cancel();
            }

            foreach (var downloadQueueEntry in downloadQueue.OfType<DownloadQueueEntry>())
            {
                downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
            }

            PlaylistDownloader.PlaylistQueuedEvent -= OnPlaylistQueued;
            Loader.SongsLoadedEvent -= OnSongsLoaded;
        }

        public void QueuePlaylist(DownloadQueueEntry downloadQueueEntry)
        {
            if (!UnityGame.OnMainThread)
            {
                _ = IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                {
                    try { QueuePlaylist(downloadQueueEntry); }
                    catch (Exception e) { Plugin.Log.Error(e); }
                });
                return;
            }
            downloadQueue.Add(downloadQueueEntry);
            OnPlaylistQueued(downloadQueueEntry);
        }

        private void OnPlaylistQueued(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueueEntry.DownloadAbortedEvent += OnDownloadAborted;
            QueueUpdatedEvent?.Invoke();
            IterateQueue();
        }

        private void OnDownloadAborted(DownloadQueueEntry downloadQueueEntry)
        {
            downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
            downloadQueue.Remove(downloadQueueEntry);
            QueueUpdatedEvent?.Invoke();
        }

        private async void IterateQueue()
        {
            await downloadSemaphore.WaitAsync();
            try
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (downloadQueue.Count > 0 && !disposed)
                {
                    var toDownload = downloadQueue.OfType<DownloadQueueEntry>().FirstOrDefault();
                    if (toDownload == null) return;
                    await DownloadPlaylist(toDownload);
                    await UnityGame.SwitchToMainThreadAsync();
                    if (!disposed) downloadQueue.Remove(toDownload);
                    QueueUpdatedEvent?.Invoke();
                }
            }
            catch (OperationCanceledException) when (disposed) { }
            catch (Exception e) { Plugin.Log.Error(e); }
            finally { downloadSemaphore.Release(); }
        }

        internal void OnQueueClear()
        {
            if (downloadQueue.Count == 0)
            {
                Loader.SongsLoadedEvent -= OnSongsLoaded;
                Loader.SongsLoadedEvent += OnSongsLoaded;
                Loader.Instance.RefreshSongs(false);
                ownedHashes.Clear();
            }
        }

        private void OnSongsLoaded(Loader loader, ConcurrentDictionary<string, BeatmapLevel> beatmapLevels)
        {
            Loader.SongsLoadedEvent -= OnSongsLoaded;
            foreach (var playlist in coversToRefresh)
            {
                playlist.RaiseCoverImageChangedForDefaultCover();
            }
            coversToRefresh.Clear();
        }

        internal void PauseDownload()
        {
            if (currentDownload != null && !currentDownload.cancellationTokenSource.IsCancellationRequested)
            {
                currentDownload.cancellationTokenSource.Cancel();
            }
        }

        internal void ResumeDownload()
        {
            if (currentDownload != null && pauseSemaphore.CurrentCount == 0)
            {
                pauseSemaphore.Release();
            }
        }

        private async Task DownloadPlaylist(DownloadQueueEntry downloadQueueEntry)
        {
            await UnityGame.SwitchToMainThreadAsync();
            currentDownload = downloadQueueEntry;
            try
            {
                var missingSongs = await PlaylistLibUtils.GetMissingSongsAsync(downloadQueueEntry.playlist, ownedHashes, lifetimeCancellation.Token);
                await UnityGame.SwitchToMainThreadAsync();
                if (disposed || downloadQueueEntry.Aborted || !downloadQueue.Contains(downloadQueueEntry)) return;
                await downloadQueueEntry.parentManager.WaitForPlaylistFilePublicationAsync(downloadQueueEntry.playlist);
                if (disposed || downloadQueueEntry.Aborted || !downloadQueue.Contains(downloadQueueEntry)) return;
                downloadQueueEntry.SetMissingLevels(missingSongs.Count);
                downloadQueueEntry.SetTotalProgress(0);

                preferCustomArchiveURL = true;
                var shownCustomArchiveWarning = false;

                for (var i = 0; i < missingSongs.Count; i++)
                {
                    bool downloadSkipped = false;
                    if (preferCustomArchiveURL && missingSongs[i].TryGetCustomData("customArchiveURL", out var outCustomArchiveURL))
                    {
                        var customArchiveURL = (string)outCustomArchiveURL;
                        var identifier = PlaylistLibUtils.GetIdentifierForPlaylistSong(missingSongs[i]);
                        if (identifier == "")
                        {
                            continue;
                        }

                        if (!shownCustomArchiveWarning)
                        {
                            shownCustomArchiveWarning = true;
                            if (!await RequestCustomArchivePreference(downloadQueueEntry.cancellationTokenSource.Token))
                            {
                                shownCustomArchiveWarning = false;
                                downloadSkipped = true;
                            }
                            else if (!preferCustomArchiveURL)
                            {
                                i--;
                                continue;
                            }
                        }
                        if (!downloadSkipped)
                            await BeatmapDownloadByCustomURL(customArchiveURL, identifier, downloadQueueEntry.cancellationTokenSource.Token, downloadQueueEntry);
                        await UnityGame.SwitchToMainThreadAsync();
                    }
                    else if (!string.IsNullOrEmpty(missingSongs[i].Hash))
                    {
                        await BeatmapDownloadByHash(missingSongs[i].Hash, downloadQueueEntry.cancellationTokenSource.Token, downloadQueueEntry);
                        await UnityGame.SwitchToMainThreadAsync();
                    }
                    else if (!string.IsNullOrEmpty(missingSongs[i].Key))
                    {
                        var hash = await BeatmapDownloadByKey(missingSongs[i].Key.ToLowerInvariant(), downloadQueueEntry.cancellationTokenSource.Token, downloadQueueEntry);
                        await UnityGame.SwitchToMainThreadAsync();
                        if (!string.IsNullOrEmpty(hash))
                        {
                            missingSongs[i].Hash = hash;
                        }
                    }

                    if (!downloadSkipped) downloadQueueEntry.SetTotalProgress(i + 1);

                    if (downloadQueueEntry.Aborted)
                    {
                        break;
                    }

                    if (disposed)
                    {
                        return;
                    }

                    if (downloadQueueEntry.cancellationTokenSource.IsCancellationRequested)
                    {
                        // If we directly cancel, it is a pause. So we wait at this semaphore till it is released.
                        await pauseSemaphore.WaitAsync(lifetimeCancellation.Token);
                        await UnityGame.SwitchToMainThreadAsync();
                        i--;
                        downloadQueueEntry.cancellationTokenSource = new CancellationTokenSource();
                    }
                }

                downloadQueueEntry.playlist.RaisePlaylistChanged();
                await PlaylistLibUtils.StorePlaylistAsync(downloadQueueEntry.playlist, downloadQueueEntry.parentManager);
                await UnityGame.SwitchToMainThreadAsync();

                if (downloadQueueEntry.playlist is BeatSaberPlaylistsLib.Types.Playlist playlist)
                {
                    coversToRefresh.AddLast(playlist);
                }
            }
            finally
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (disposed && !downloadQueueEntry.Aborted)
                    downloadQueueEntry.cancellationTokenSource = new CancellationTokenSource();
                downloadQueueEntry.DownloadAbortedEvent -= OnDownloadAborted;
                currentDownload = null;
            }
        }

        private async Task<bool> RequestCustomArchivePreference(CancellationToken token)
        {
            PopupContents popup = null;
            bool answered = false;
            void Choose(bool useMirror)
            {
                if (disposed || answered || token.IsCancellationRequested || !ReferenceEquals(PendingPopup, popup)) return;
                answered = true;
                preferCustomArchiveURL = useMirror;
                popupSemaphore.Release();
            }
            popup = new YesNoPopupContents("This playlist uses mirror download links. Would you like to use them?", () => Choose(true),
                noButtonPressedCallback: () => Choose(false), animateParentCanvas: false);
            PendingPopup = popup;
            try
            {
                await popupSemaphore.WaitAsync(token);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            finally
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (ReferenceEquals(PendingPopup, popup)) PendingPopup = null;
            }
        }

        #region Map Download

        private async Task<Beatmap> GetBeatmapAsync(string identifier, bool byHash, CancellationToken token)
        {
            await UnityGame.SwitchToMainThreadAsync();
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, lifetimeCancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            string key = byHash ? null : identifier.ToLowerInvariant();
            if (byHash)
            {
                if (string.IsNullOrWhiteSpace(identifier)) return null;
                if (beatmapKeysByHash.TryGetValue(identifier, out var cachedKey) && beatmapsByKey.TryGetValue(cachedKey, out var cachedByHash)) return cachedByHash;
            }
            else if (beatmapsByKey.TryGetValue(key, out var cachedByKey)) return cachedByKey;

            string path = byHash ? "maps/hash/" + identifier : "maps/id/" + key;
            var response = await beatSaverHttpService.GetAsync(path, cancellation.Token);
            await UnityGame.SwitchToMainThreadAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (!response.Successful) return null;
            // This service captures owned bytes before disposing its native request.
            var beatmap = await Task.Run(() => response.ReadAsObjectAsync<Beatmap>(), cancellation.Token);
            await UnityGame.SwitchToMainThreadAsync();
            cancellation.Token.ThrowIfCancellationRequested();
            if (beatmap == null) return null;
            beatmapsByKey[beatmap.ID] = beatmap;
            foreach (var version in beatmap.Versions) beatmapKeysByHash[version.Hash] = beatmap.ID;
            if (byHash) beatmapKeysByHash[identifier] = beatmap.ID;
            return beatmap;
        }

        private async Task BeatSaverBeatmapDownload(Beatmap song, BeatmapVersion songversion, CancellationToken token, IProgress<double> progress = null)
        {
            await UnityGame.SwitchToMainThreadAsync();
            token.ThrowIfCancellationRequested();
            var customSongsPath = CustomLevelPathHelper.customLevelsDirectoryPath;
            string hash = songversion.Hash;
            if (!ownedHashes.Contains(hash))
            {
                // Decoded metadata stays private; its versions have no attached BeatSaver client.
                var response = await beatSaverHttpService.GetAsync(songversion.DownloadURL, token, progress);
                await UnityGame.SwitchToMainThreadAsync();
                token.ThrowIfCancellationRequested();
                if (!response.Successful) throw new IOException("BeatSaver map archive download failed.");
                var zip = await response.ReadAsByteArrayAsync();
                bool extracted = await ExtractZipAsync(zip, customSongsPath, FolderNameForBeatsaverMap(song), token).ConfigureAwait(false);
                await UnityGame.SwitchToMainThreadAsync();
                if (extracted) ownedHashes.Add(hash);
            }
        }

        private async Task<string> BeatmapDownloadByKey(string key, CancellationToken token, IProgress<double> progress = null)
        {
            await UnityGame.SwitchToMainThreadAsync();
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var song = await GetBeatmapAsync(key, false, token);
                    await UnityGame.SwitchToMainThreadAsync();
                    if (song == null)
                    {
                        Plugin.Log.Error($"Failed to download Song {key}. Unable to find a beatmap for that hash.");
                        return "";
                    }
                    // A key is not enough to identify a specific version. So just get the latest one.
                    if (Loader.GetLevelByHash(song.LatestVersion.Hash) == null)
                    {
                        await BeatSaverBeatmapDownload(song, song.LatestVersion, token, progress);
                    }
                    return song.LatestVersion.Hash;
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Plugin.Log.Error($"Failed to download Song {key}. Exception: {e}");
                    }
                }
            }
            return "";
        }

        private async Task BeatmapDownloadByHash(string hash, CancellationToken token, IProgress<double> progress = null)
        {
            await UnityGame.SwitchToMainThreadAsync();
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var song = await GetBeatmapAsync(hash, true, token);
                    await UnityGame.SwitchToMainThreadAsync();
                    if (song == null)
                    {
                        Plugin.Log.Error($"Failed to download Song {hash}. Unable to find a beatmap for that hash.");
                        return;
                    }

                    BeatmapVersion matchingVersion = null;
                    foreach (var version in song.Versions)
                    {
                        if (string.Equals(hash, version.Hash, StringComparison.OrdinalIgnoreCase))
                        {
                            matchingVersion = version;
                        }
                    }

                    if (matchingVersion != null)
                    {
                        await BeatSaverBeatmapDownload(song, matchingVersion, token, progress);
                    }
                    else
                    {
                        await BeatmapDownloadByCustomURL($"https://cdn.beatsaver.com/{hash.ToLowerInvariant()}.zip", FolderNameForBeatsaverMap(song), token, progress as IProgress<float>);
                    }
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Plugin.Log.Error($"Failed to download Song {hash}. Exception: {e}");
                    }
                }
            }
        }

        private async Task BeatmapDownloadByCustomURL(string url, string songName, CancellationToken token, IProgress<float> progress = null)
        {
            await UnityGame.SwitchToMainThreadAsync();
            if (!token.IsCancellationRequested)
            {
                try
                {
                    var customSongsPath = CustomLevelPathHelper.customLevelsDirectoryPath;
                    var httpResponse = await siraHttpService.GetAsync(url, progress, token);
                    await UnityGame.SwitchToMainThreadAsync();
                    if (httpResponse.Successful)
                    {
                        var zip = await httpResponse.ReadAsByteArrayAsync();
                        await ExtractZipAsync(zip, customSongsPath, songName, token).ConfigureAwait(false);
                    }
                    else
                    {
                        Plugin.Log.Error($"Failed to download Song {url}");
                    }
                }
                catch (Exception e)
                {
                    if (e is not OperationCanceledException)
                    {
                        Plugin.Log.Error($"Failed to download Song {url}");
                    }
                }
            }
        }

        private string FolderNameForBeatsaverMap(Beatmap song)
        {
            // A workaround for the max path issue and long folder names
            var longFolderName = song.ID + " (" + song.Metadata.SongName + " - " + song.Metadata.LevelAuthorName;
            return longFolderName.Truncate(49, true) + ")";
        }

        private sealed class PreparedArchive : IDisposable
        {
            internal readonly ZipArchive Archive;
            internal readonly string Path;
            internal readonly bool NeedsDiskWarning;

            internal PreparedArchive(ZipArchive archive, string path, bool needsDiskWarning)
            {
                Archive = archive;
                Path = path;
                NeedsDiskWarning = needsDiskWarning;
            }

            public void Dispose() => Archive.Dispose();
        }

        private static PreparedArchive PrepareArchive(byte[] zip, string customSongsPath, string songName, bool checkDisk, bool overwrite)
        {
            var stream = new MemoryStream(zip);
            ZipArchive archive = null;
            try
            {
                archive = new ZipArchive(stream, ZipArchiveMode.Read);
                var basePath = string.Join("", songName.Split(Path.GetInvalidFileNameChars().Concat(Path.GetInvalidPathChars()).ToArray()));
                var path = Path.Combine(customSongsPath, basePath);
                if (!overwrite && Directory.Exists(path))
                {
                    var pathNum = 1;
                    while (Directory.Exists(path + $" ({pathNum})")) ++pathNum;
                    path += $" ({pathNum})";
                }

                bool needsDiskWarning = false;
                if (checkDisk)
                {
                    var driveInfo = new DriveInfo(Path.GetPathRoot(path));
                    long totalSize = 0;
                    foreach (var entry in archive.Entries)
                        totalSize += entry.Length;
                    needsDiskWarning = driveInfo.AvailableFreeSpace - totalSize < 104857600;
                }
                return new PreparedArchive(archive, path, needsDiskWarning);
            }
            catch
            {
                archive?.Dispose();
                stream.Dispose();
                throw;
            }
        }

        private async Task<bool> ExtractZipAsync(byte[] zip, string customSongsPath, string songName, CancellationToken token, bool overwrite = false)
        {
            await UnityGame.SwitchToMainThreadAsync();
            bool checkDisk = PluginConfig.Instance.DriveFullProtection && !ignoredDiskWarning;
            PreparedArchive prepared = null;
            PopupContents diskPopup = null;
            try
            {
                prepared = await Task.Run(() => PrepareArchive(zip, customSongsPath, songName, checkDisk, overwrite), token);
                await UnityGame.SwitchToMainThreadAsync();
                token.ThrowIfCancellationRequested();
                if (disposed) return false;
                if (prepared.NeedsDiskWarning)
                {
                    CreateDrivePopup(token);
                    diskPopup = PendingPopup;
                    await popupSemaphore.WaitAsync(token);
                    await UnityGame.SwitchToMainThreadAsync();
                    PendingPopup = null;
                    if (!ignoredDiskWarning)
                    {
                        currentDownload.AbortDownload();
                        downloadQueue.Clear();
                        downloadQueue.Add(currentDownload);
                        return false;
                    }
                }
                return await Task.Run(() =>
                {
                    Directory.CreateDirectory(prepared.Path);
                    foreach (var entry in prepared.Archive.Entries)
                    {
                        if (!string.IsNullOrWhiteSpace(entry.Name) && entry.Name == entry.FullName)
                        {
                            var entryPath = Path.Combine(prepared.Path, entry.Name); // Name instead of FullName for better security and because song zips don't have nested directories anyway
                            if (overwrite || !File.Exists(entryPath)) // Either we're overwriting or there's no existing file
                                entry.ExtractToFile(entryPath, overwrite);
                        }
                    }
                    return true;
                }, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e)
            {
                Plugin.Log.Error($"Unable to extract ZIP! Exception: {e}");
                return false;
            }
            finally
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (diskPopup != null && ReferenceEquals(PendingPopup, diskPopup)) PendingPopup = null;
                if (prepared != null) await Task.Run(prepared.Dispose).ConfigureAwait(false);
            }
        }

        private void CreateDrivePopup(CancellationToken token)
        {
            var popupText = "You are running out of disk space (less than 100MB), continuing the download can cause issues such as corrupt game configs" +
                            " (as there may not be enough space to save them).";

            if (PluginConfig.Instance.EasterEggs && PluginConfig.Instance.AuthorName.IndexOf("SKALX", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                popupText = "Remember the October 26th, 2021 \"JoeSaber\" incident? Wanna do it again?";
            }

            PopupContents popup = null;
            bool answered = false;
            popup = new YesNoPopupContents(popupText, yesButtonText: "Continue", noButtonText: "Abort", yesButtonPressedCallback: () =>
            {
                if (disposed || answered || token.IsCancellationRequested || !ReferenceEquals(PendingPopup, popup)) return;
                answered = true;
                ignoredDiskWarning = true;
                popupSemaphore.Release();
            },
            noButtonPressedCallback: () =>
            {
                if (disposed || answered || token.IsCancellationRequested || !ReferenceEquals(PendingPopup, popup)) return;
                answered = true;
                ignoredDiskWarning = false;
                popupSemaphore.Release();
            }, animateParentCanvas: false);
            PendingPopup = popup;
        }

        #endregion
    }
}
