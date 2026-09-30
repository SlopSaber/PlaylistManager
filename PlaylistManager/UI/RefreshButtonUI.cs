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
        private bool disposed;

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
            skipInitialSongLoad = !Loader.AreSongsLoaded;
            Loader.SongsLoadedEvent += SongsLoaded;
        }

        private async void SongsLoaded(Loader _, System.Collections.Concurrent.ConcurrentDictionary<string, BeatmapLevel> songs)
        {
            try
            {
                var manager = await PlaylistLibUtils.GetDefaultManagerAsync();
                if (disposed) return;
                if (skipInitialSongLoad) skipInitialSongLoad = false;
                else manager.RefreshPlaylists(true);
                await PlaylistLibUtils.Catalog.ScanAsync();
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                if (disposed) return;
                var numPlaylists = manager.GetPlaylistCount(true);
                _progressBar.AppendText($"\n{numPlaylists} playlists loaded");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log.Error(e); }
        }

        public void Dispose()
        {
            disposed = true;
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
