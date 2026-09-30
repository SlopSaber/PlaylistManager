using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BeatSaberPlaylistsLib;
using BeatSaberPlaylistsLib.Blist;
using BeatSaberPlaylistsLib.Legacy;
using BeatSaberPlaylistsLib.Types;
using JetBrains.Annotations;
using PlaylistManager.Configuration;
using UnityEngine;

namespace PlaylistManager.Utilities
{
    public static class PlaylistLibUtils
    {
        private const string ICON_PATH = "PlaylistManager.Icons.DefaultIcon.png";
        private const string EASTER_EGG_URL = "https://raw.githubusercontent.com/rithik-b/PlaylistManager/master/img/easteregg.bplist";
        private static readonly HashSet<BeatSaberPlaylistsLib.PlaylistManager> savingManagers = new();
        internal static global::PlaylistManager.Managers.PlaylistCatalog Catalog { get; set; }

        internal static async Task<BeatSaberPlaylistsLib.PlaylistManager> GetDefaultManagerAsync()
        {
            var catalog = Catalog ?? throw new InvalidOperationException("Playlist catalog is unavailable.");
            await catalog.Ready;
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            if (!ReferenceEquals(Catalog, catalog)) throw new OperationCanceledException();
            if (!catalog.IsReady) throw new InvalidOperationException("Playlist catalog initialization failed.");
            return catalog.Manager ?? throw new InvalidOperationException("Playlist manager initialization failed.");
        }

        internal static IPlaylist[] GetCachedPlaylists(BeatSaberPlaylistsLib.PlaylistManager manager = null) =>
            Catalog?.GetPlaylists(manager) ?? Array.Empty<IPlaylist>();

        internal static BeatmapLevelPack[] GetCachedPlaylistLevelPacks()
        {
            var playlists = GetCachedPlaylists();
            var packs = new BeatmapLevelPack[playlists.Length];
            for (int i = 0; i < packs.Length; ++i) packs[i] = playlists[i].PlaylistLevelPack;
            return packs;
        }

        internal static Task StorePlaylistAsync(IPlaylist playlist, BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            savingManagers.Add(manager);
            return manager.StorePlaylistAsync(playlist);
        }

        internal static async void StorePlaylist(IPlaylist playlist, BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            try { await StorePlaylistAsync(playlist, manager); }
            catch (Exception e) { Plugin.Log.Error(e); }
        }

        internal static async Task WaitForPendingSavesAsync(BeatSaberPlaylistsLib.PlaylistManager manager, bool includeChildren = false)
        {
            try { await manager.WaitForPendingSavesAsync(includeChildren); }
            catch (Exception e) { Plugin.Log.Error(e); }
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            await manager.WaitForFilePublicationAsync();
        }

        internal static void FlushPendingSaves()
        {
            foreach (var manager in savingManagers)
            {
                try { manager.WaitForPendingSavesAsync().GetAwaiter().GetResult(); }
                catch (Exception e) { Plugin.Log.Error(e); }
            }
            savingManagers.Clear();
        }

        public static BeatSaberPlaylistsLib.PlaylistManager playlistManager
        {
            get
            {
                return BeatSaberPlaylistsLib.PlaylistManager.DefaultManager;
            }
        }

        public static IPlaylist CreatePlaylistWithConfig(string playlistName, BeatSaberPlaylistsLib.PlaylistManager playlistManager)
        {
            var playlistAuthorName = PluginConfig.Instance.AuthorName;
            var easterEgg = playlistAuthorName.IndexOf("BINTER", StringComparison.OrdinalIgnoreCase) >= 0 && playlistName.IndexOf("TECH", StringComparison.OrdinalIgnoreCase) >= 0 && PluginConfig.Instance.EasterEggs;
            return CreatePlaylist(playlistName, playlistAuthorName, playlistManager, !PluginConfig.Instance.DefaultImageDisabled, PluginConfig.Instance.DefaultAllowDuplicates, easterEgg);
        }

        internal static Task<IPlaylist> CreatePlaylistWithConfigAsync(string playlistName, BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            string author = PluginConfig.Instance.AuthorName;
            bool defaultCover = !PluginConfig.Instance.DefaultImageDisabled;
            var data = new Dictionary<string, object>();
            if (!PluginConfig.Instance.DefaultAllowDuplicates) data.Add("AllowDuplicates", false);
            if (PluginConfig.Instance.EasterEggs && author.IndexOf("BINTER", StringComparison.OrdinalIgnoreCase) >= 0
                && playlistName.IndexOf("TECH", StringComparison.OrdinalIgnoreCase) >= 0) data.Add("syncURL", EASTER_EGG_URL);
            var assembly = Assembly.GetExecutingAssembly();
            Func<Stream> cover = defaultCover ? () => assembly.GetManifestResourceStream(ICON_PATH) : null;
            savingManagers.Add(manager);
            return manager.CreatePlaylistAsync(playlistName, author, cover, data);
        }

        internal static Task<IPlaylist> ClonePlaylistAsync(IPlaylist playlist, BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            savingManagers.Add(manager);
            return manager.ClonePlaylistAsync(playlist);
        }

        internal static Task<BeatSaberPlaylistsLib.PlaylistManager> CreateChildManagerAsync(string folderName, BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            savingManagers.Add(manager);
            return manager.CreateChildManagerAsync(folderName);
        }

        internal static Task RenameManagerAsync(BeatSaberPlaylistsLib.PlaylistManager manager, string folderName)
        {
            savingManagers.Add(manager);
            return manager.RenameManagerAsync(folderName);
        }

        internal static Task DeletePlaylistAsync(IPlaylist playlist, BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            savingManagers.Add(manager);
            return manager.DeletePlaylistAsync(playlist, true);
        }

        public static IPlaylist CreatePlaylist(string playlistName, string playlistAuthorName, BeatSaberPlaylistsLib.PlaylistManager playlistManager, bool defaultCover = true,
            bool allowDups = true, bool easterEgg = false)
        {
            var playlist = playlistManager.CreatePlaylist("", playlistName, playlistAuthorName, "");

            if (defaultCover)
            {
                using (var imageStream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ICON_PATH))
                {
                    playlist.SetCover(imageStream);
                }
            }


            if (!allowDups)
            {
                playlist.AllowDuplicates = false;
            }

            if (easterEgg)
            {
                playlist.SetCustomData("syncURL", EASTER_EGG_URL);
            }

            playlist.RaisePlaylistChanged();
            playlistManager.StorePlaylist(playlist);
            PlaylistLibUtils.playlistManager.RequestRefresh("PlaylistManager (plugin)");
            return playlist;
        }

        public static string GetIdentifierForPlaylistSong(IPlaylistSong playlistSong)
        {
            if (playlistSong.Identifiers.HasFlag(Identifier.Hash))
            {
                return playlistSong.Hash;
            }
            if (playlistSong.Identifiers.HasFlag(Identifier.Key))
            {
                return playlistSong.Key;
            }
            if (playlistSong.Identifiers.HasFlag(Identifier.LevelId))
            {
                return playlistSong.LevelId;
            }
            return "";
        }

        public static List<IPlaylistSong> GetMissingSongs(IPlaylist playlist, HashSet<string> ownedHashes = null)
        {
            if (playlist != null)
            {
                return playlist.Where(s => s.BeatmapLevel == null && !(ownedHashes?.Contains(s.Hash) ?? false)).Distinct(IPlaylistSongComparer<IPlaylistSong>.Default).ToList();
            }
            return new List<IPlaylistSong>();
        }

        private readonly struct SongIdentity
        {
            internal readonly IPlaylistSong Song;
            internal readonly string LevelId;
            internal readonly string Key;

            internal SongIdentity(IPlaylistSong song)
            {
                Song = song;
                LevelId = song?.LevelId;
                Key = song?.Key;
            }
        }

        private sealed class SongIdentityComparer : IEqualityComparer<SongIdentity>
        {
            internal static readonly SongIdentityComparer Instance = new();

            public bool Equals(SongIdentity x, SongIdentity y)
            {
                if (x.Song == null) return y.Song == null;
                if (y.Song == null || GetHashCode(x) != GetHashCode(y)) return false;
                // Preserve the library comparer's level ID precedence and identifier-less entries.
                if (x.LevelId != null) return x.LevelId == y.LevelId;
                return x.Key != null && x.Key == y.Key;
            }

            public int GetHashCode(SongIdentity song) =>
                238947239 ^ (song.LevelId?.GetHashCode() ?? song.Key?.GetHashCode() ?? 0);
        }

        internal static bool HasMissingSongs(IPlaylist playlist) =>
            playlist != null && playlist.Any(song => song.BeatmapLevel == null);

        internal static async Task<List<IPlaylistSong>> GetMissingSongsAsync(IPlaylist playlist,
            HashSet<string> ownedHashes = null, CancellationToken cancellationToken = default)
        {
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            if (playlist == null) return new List<IPlaylistSong>();
            bool changed = false;
            void OnChanged(object sender, EventArgs args) => changed = true;
            playlist.PlaylistChanged += OnChanged;
            try
            {
                for (;;)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    changed = false;
                    var songs = playlist.ToArray();
                    var candidates = new List<SongIdentity>();
                    foreach (var song in songs)
                        if (song.BeatmapLevel == null && !(ownedHashes?.Contains(song.Hash) ?? false))
                            candidates.Add(new SongIdentity(song));
                    var missing = await Task.Run(() =>
                    {
                        var seen = new HashSet<SongIdentity>(SongIdentityComparer.Instance);
                        var result = new List<IPlaylistSong>();
                        foreach (var candidate in candidates)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            if (seen.Add(candidate)) result.Add(candidate.Song);
                        }
                        return result;
                    }, cancellationToken);
                    await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!changed && MatchesSongReferences(playlist, songs)
                        && candidates.All(candidate => candidate.Song.LevelId == candidate.LevelId && candidate.Song.Key == candidate.Key
                            && !(ownedHashes?.Contains(candidate.Song.Hash) ?? false))) return missing;
                }
            }
            finally
            {
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                playlist.PlaylistChanged -= OnChanged;
            }
        }

        internal static async Task DisableDuplicatesAsync(IPlaylist playlist,
            BeatSaberPlaylistsLib.PlaylistManager manager, CancellationToken cancellationToken)
        {
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            bool changed = false;
            void OnChanged(object sender, EventArgs args) => changed = true;
            playlist.PlaylistChanged += OnChanged;
            var builtIn = playlist as BeatSaberPlaylistsLib.Types.Playlist;
            if (builtIn != null) builtIn.CoverImageChanged += OnChanged;
            try
            {
                for (;;)
                {
                    await manager.WaitForPlaylistFilePublicationAsync(playlist);
                    cancellationToken.ThrowIfCancellationRequested();
                    changed = false;
                    var original = playlist.Select(song => new SongIdentity(song)).ToArray();
                    Func<Action> prepare = null;
                    if (playlist.GetType() == typeof(LegacyPlaylist)
                        && original.All(song => song.Song?.GetType() == typeof(LegacyPlaylistSong)))
                    {
                        prepare = ((LegacyPlaylist)playlist).CaptureDuplicateRemoval();
                    }
                    else if (playlist.GetType() == typeof(BlistPlaylist)
                        && original.All(song => song.Song?.GetType() == typeof(BlistPlaylistSong)))
                    {
                        prepare = ((BlistPlaylist)playlist).CaptureDuplicateRemoval();
                    }
                    if (prepare == null)
                    {
                        playlist.AllowDuplicates = false;
                        if (playlist is BlistPlaylist blist) blist.RemoveDuplicates();
                        else if (playlist is LegacyPlaylist legacy) legacy.RemoveDuplicates();
                    }
                    else
                    {
                        var publish = await Task.Run(prepare, cancellationToken);
                        await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                        await manager.WaitForPlaylistFilePublicationAsync(playlist);
                        cancellationToken.ThrowIfCancellationRequested();
                        if (changed || !MatchesSongSequence(playlist, original)) continue;
                        playlist.AllowDuplicates = false;
                        publish?.Invoke();
                    }
                    playlist.RaisePlaylistChanged();
                    await StorePlaylistAsync(playlist, manager);
                    return;
                }
            }
            finally
            {
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                playlist.PlaylistChanged -= OnChanged;
                if (builtIn != null) builtIn.CoverImageChanged -= OnChanged;
            }
        }

        private static bool MatchesSongSequence(IPlaylist playlist, SongIdentity[] original)
        {
            if (playlist.Count != original.Length) return false;
            for (int i = 0; i < original.Length; ++i)
            {
                var current = playlist[i];
                if (!ReferenceEquals(current, original[i].Song)
                    || current?.LevelId != original[i].LevelId || current?.Key != original[i].Key) return false;
            }
            return true;
        }

        private static bool MatchesSongReferences(IPlaylist playlist, IPlaylistSong[] original)
        {
            if (playlist.Count != original.Length) return false;
            for (int i = 0; i < original.Length; ++i)
                if (!ReferenceEquals(playlist[i], original[i])) return false;
            return true;
        }

        public static IPlaylist[] TryGetAllPlaylists()
        {
            var playlists = playlistManager.GetAllPlaylists(true, out AggregateException ex);
            if (ex is not null)
            {
                Plugin.Log.Error(ex.Message);
                foreach (var e in ex.InnerExceptions)
                {
                    Plugin.Log.Error(e.ToString());
                }
            }

            return playlists;
        }

        public static BeatmapLevelPack[] TryGetAllPlaylistsAsLevelPacks()
        {
            IPlaylist[] playlists = TryGetAllPlaylists();
            BeatmapLevelPack[] levelPacks = new BeatmapLevelPack[playlists.Length];
            for (int i = 0; i < playlists.Length; ++i)
            {
                levelPacks[i] = playlists[i].PlaylistLevelPack;
            }
            return levelPacks;
        }

        #region Image


        private static Stream GetFolderImageStream() =>
            Assembly.GetExecutingAssembly().GetManifestResourceStream("PlaylistManager.Icons.FolderIcon.png");

        internal static async Task<Sprite> GeneratePlaylistIcon(IPlaylist playlist)
        {
            using var coverStream = await playlist.GetDefaultCoverStream();
            await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
            if (coverStream != null)
            {
                var bytes = await Task.Run(() => coverStream.ToArray());
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                Sprite sprite = await BeatSaberMarkupLanguage.Utilities.LoadSpriteAsync(bytes);
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                return sprite ? sprite : BeatSaberPlaylistsLib.Utilities.DefaultSprite;
            }
            return BeatSaberPlaylistsLib.Utilities.DefaultSprite;
        }

        #endregion
    }
}
