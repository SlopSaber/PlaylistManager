using BeatSaberMarkupLanguage.MenuButtons;
using PlaylistManager.Utilities;
using SongCore;
using SongCore.UI;
using System;
using Zenject;

namespace PlaylistManager.UI
{
    public class RefreshButtonUI : IInitializable, IDisposable
    {
        private readonly Loader _loader;
        private readonly ProgressBar _progressBar;
        private readonly MenuButtons _menuButtons;

        private MenuButton refreshButton;
        private bool skipInitialSongLoad;

        private RefreshButtonUI(Loader loader, ProgressBar progressBar, MenuButtons menuButtons)
        {
            _loader = loader;
            _progressBar = progressBar;
            _menuButtons = menuButtons;
        }

        public void Initialize()
        {
            refreshButton = new MenuButton("Refresh Playlists", "Refresh Songs & Playlists", RefreshButtonPressed);
            _menuButtons.RegisterButton(refreshButton);
            // PlaylistUpdater has already loaded playlist files while setting up the menu.
            skipInitialSongLoad = !Loader.AreSongsLoaded;
            Loader.SongsLoadedEvent += SongsLoaded;
        }

        private void SongsLoaded(Loader _, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel> songs)
        {
            if (skipInitialSongLoad)
            {
                skipInitialSongLoad = false;
            }
            else
            {
                PlaylistLibUtils.playlistManager.RefreshPlaylists(true);
            }

            var numPlaylists = PlaylistLibUtils.playlistManager.GetPlaylistCount(true);
            _progressBar.AppendText($"\n{numPlaylists} playlists loaded");
        }

        public void Dispose()
        {
            _menuButtons.UnregisterButton(refreshButton);
            Loader.SongsLoadedEvent -= SongsLoaded;
        }

        private void RefreshButtonPressed()
        {
            if (!Loader.AreSongsLoading)
            {
                _loader.RefreshSongs(fullRefresh: false);
            }
        }
    }
}
