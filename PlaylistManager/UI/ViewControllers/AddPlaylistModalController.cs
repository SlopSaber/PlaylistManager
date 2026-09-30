using System;
using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using static BeatSaberMarkupLanguage.Components.CustomListTableData;
using System.Linq;
using HMUI;
using PlaylistManager.Utilities;
using UnityEngine;
using PlaylistManager.Configuration;
using BeatSaberPlaylistsLib.Types;
using BeatSaberMarkupLanguage.Parser;
using System.IO;
using System.ComponentModel;
using System.Collections.Generic;
using IPA.Loader;
using SiraUtil.Zenject;

namespace PlaylistManager.UI
{
    public class AddPlaylistModalController : INotifyPropertyChanged, IDisposable
    {
        private readonly StandardLevelDetailViewController standardLevelDetailViewController;
        private readonly PopupModalsController popupModalsController;
        private readonly PluginMetadata pluginMetadata;
        private readonly BSMLParser bsmlParser;

        private BeatSaberPlaylistsLib.PlaylistManager parentManager;
        private List<BeatSaberPlaylistsLib.PlaylistManager> childManagers;
        private List<IPlaylist> childPlaylists;
        private readonly HashSet<IPlaylist> coverSubscriptions = new();

        private Sprite folderIcon;
        private bool parsed;
        private bool disposed;
        public event PropertyChangedEventHandler PropertyChanged;

        [UIComponent("list")]
        public CustomListTableData playlistTableData;

        [UIComponent("dropdown-options")]
        public CustomListTableData dropdownTableData;

        [UIComponent("highlight-checkbox")]
        private RectTransform highlightCheckboxTransform { get; set; }

        [UIComponent("modal")]
        private RectTransform modalTransform { get; set; }

        private Vector3 modalPosition;

        [UIComponent("create-dropdown")]
        private ModalView createModal { get; set; }

        [UIComponent("create-dropdown")]
        private RectTransform createModalTransform { get; set; }

        private Vector3 createModalPosition;

        [UIParams]
        private BSMLParserParams parserParams { get; set; }

        public AddPlaylistModalController(StandardLevelDetailViewController standardLevelDetailViewController, PopupModalsController popupModalsController, UBinder<Plugin, PluginMetadata> pluginMetadata, BSMLParser bsmlParser)
        {
            this.standardLevelDetailViewController = standardLevelDetailViewController;
            this.popupModalsController = popupModalsController;
            this.pluginMetadata = pluginMetadata.Value;
            this.bsmlParser = bsmlParser;
            _ = LoadFolderIconAsync();
            parsed = false;
        }

        public void Dispose()
        {
            disposed = true;
            foreach (var playlist in coverSubscriptions) playlist.SpriteLoaded -= StagedSpriteLoadPlaylist_SpriteLoaded;
            coverSubscriptions.Clear();
        }

        private async System.Threading.Tasks.Task LoadFolderIconAsync()
        {
            try
            {
                var sprite = await BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("PlaylistManager.Icons.FolderIcon.png");
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                if (!disposed) folderIcon = sprite;
            }
            catch (Exception e) { Plugin.Log.Error(e); }
        }

        private void Parse()
        {
            if (!parsed)
            {
                bsmlParser.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(pluginMetadata.Assembly, "PlaylistManager.UI.Views.AddPlaylistModal.bsml"), standardLevelDetailViewController._standardLevelDetailView.gameObject, this);
                modalPosition = modalTransform.localPosition;
                createModalPosition = createModalTransform.localPosition;
            }
            modalTransform.localPosition = modalPosition; // Reset position
            createModalTransform.localPosition = createModalPosition;
        }

        [UIAction("#post-parse")]
        private void PostParse()
        {
            parsed = true;
            highlightCheckboxTransform.transform.localScale *= 0.5f;

            createModal._animateParentCanvas = false;
            dropdownTableData.Data.Add(new CustomCellInfo("Playlist"));
            dropdownTableData.Data.Add(new CustomCellInfo("Folder"));
            dropdownTableData.TableView.ReloadData();
        }

        #region Show Playlists

        internal void ShowModal()
        {
            Parse();
            parserParams.EmitEvent("close-modal");
            parserParams.EmitEvent("open-modal");
            ShowPlaylistsForManager(PlaylistLibUtils.playlistManager);
        }

        internal void ShowPlaylistsForManager(BeatSaberPlaylistsLib.PlaylistManager parentManager)
        {
            playlistTableData.Data.Clear();

            this.parentManager = parentManager;
            childManagers = parentManager.GetChildManagers().ToList();
            var childPlaylists = parentManager.GetAllPlaylists(false).Where(playlist => !playlist.ReadOnly);
            this.childPlaylists = childPlaylists.ToList();

            foreach (var playlistManager in childManagers)
            {
                playlistTableData.Data.Add(new CustomCellInfo(Path.GetFileName(playlistManager.PlaylistPath), "Folder", folderIcon));
            }
            foreach (var playlist in childPlaylists)
            {
                if (!playlist.SmallSpriteWasLoaded)
                {
                    playlist.SpriteLoaded -= StagedSpriteLoadPlaylist_SpriteLoaded;
                    playlist.SpriteLoaded += StagedSpriteLoadPlaylist_SpriteLoaded;
                    coverSubscriptions.Add(playlist);
                    _ = playlist.SmallSprite;
                }
                else
                {
                    ShowPlaylist(playlist);
                }
            }
            playlistTableData.TableView.ReloadData();

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BackActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FolderText)));
        }

        private void StagedSpriteLoadPlaylist_SpriteLoaded(object sender, EventArgs e)
        {
            if (sender is IStagedSpriteLoad stagedSpriteLoadPlaylist)
            {
                stagedSpriteLoadPlaylist.SpriteLoaded -= StagedSpriteLoadPlaylist_SpriteLoaded;
                coverSubscriptions.Remove((IPlaylist)stagedSpriteLoadPlaylist);
                if (disposed) return;
                if (childPlaylists != null && childPlaylists.Contains((IPlaylist)stagedSpriteLoadPlaylist))
                {
                    ShowPlaylist((IPlaylist)stagedSpriteLoadPlaylist);
                }
                playlistTableData.TableView.ReloadDataKeepingPosition();
            }
        }

        private void ShowPlaylist(IPlaylist playlist)
        {
            var beatmapLevels = playlist.BeatmapLevels;
            var subName = string.Format("{0} songs", beatmapLevels.Length);
            if (beatmapLevels.Any(level => level.levelID == standardLevelDetailViewController.beatmapKey.levelId))
            {
                if (!playlist.AllowDuplicates)
                {
                    childPlaylists.Remove(playlist);
                    return;
                }
                subName += " (contains song)";
            }
            playlistTableData.Data.Add(new CustomCellInfo(playlist.Title, subName, playlist.SmallSprite));
        }

        [UIAction("select-cell")]
        private async void OnCellSelect(TableView tableView, int index)
        {
            playlistTableData.TableView.ClearSelection();
            // Folder Selected
            if (index < childManagers.Count)
            {
                ShowPlaylistsForManager(childManagers[index]);
            }
            else
            {
                index -= childManagers.Count;
                var selectedPlaylist = childPlaylists[index];
                var manager = parentManager;
                IPlaylistSong playlistSong;
                if (HighlightDifficulty)
                {
                    playlistSong = selectedPlaylist.Add(standardLevelDetailViewController.beatmapLevel, standardLevelDetailViewController.beatmapKey);
                }
                else
                {
                    playlistSong = selectedPlaylist.Add(standardLevelDetailViewController.beatmapLevel);
                }
                try
                {
                    selectedPlaylist.RaisePlaylistChanged();
                    await PlaylistLibUtils.StorePlaylistAsync(selectedPlaylist, manager);
                    if (!disposed && ReferenceEquals(parentManager, manager))
                        popupModalsController.ShowOkModal(modalTransform, string.Format("Song successfully added to {0}", selectedPlaylist.Title), null, animateParentCanvas: false);
                    // TODO: Doesn't refresh the sprite.
                    Events.RaisePlaylistSongAdded(playlistSong, selectedPlaylist);
                }
                catch (Exception e)
                {
                    if (!disposed && ReferenceEquals(parentManager, manager))
                        popupModalsController.ShowOkModal(modalTransform, "An error occured while adding song to playlist.", null, animateParentCanvas: false);
                    Plugin.Log.Critical(string.Format("An exception was thrown while adding a song to a playlist.\nException Message: {0}", e.Message));
                }
                finally
                {
                    if (!disposed && ReferenceEquals(parentManager, manager)) ShowPlaylistsForManager(manager);
                }
            }
        }

        [UIAction("back-button-pressed")]
        private void BackButtonPressed()
        {
            ShowPlaylistsForManager(parentManager.Parent);
        }

        [UIValue("folder-text")]
        private string FolderText
        {
            get => parentManager == null ? "" : Path.GetFileName(parentManager.PlaylistPath);
        }

        [UIValue("highlight-difficulty")]
        private bool HighlightDifficulty
        {
            get => PluginConfig.Instance.HighlightDifficulty;
            set
            {
                PluginConfig.Instance.HighlightDifficulty = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HighlightDifficulty)));
            }
        }

        [UIValue("back-active")]
        private bool BackActive
        {
            get => parentManager != null && parentManager.Parent != null;
        }

        #endregion

        #region Create Playlist

        [UIAction("select-option")]
        private void OnOptionSelect(TableView tableView, int index)
        {
            popupModalsController.ShowKeyboard(modalTransform, index == 0 ? CreatePlaylist : CreateFolder, animateParentCanvas: false);
            tableView.ClearSelection();
            parserParams.EmitEvent("close-dropdown");
        }

        private void CreatePlaylist(string playlistName)
        {
            if (string.IsNullOrWhiteSpace(playlistName))
            {
                return;
            }

            var playlist = PlaylistLibUtils.CreatePlaylistWithConfig(playlistName, parentManager);

            if (playlist is IDeferredSpriteLoad deferredSpriteLoadPlaylist && !deferredSpriteLoadPlaylist.SpriteWasLoaded)
            {
                deferredSpriteLoadPlaylist.SpriteLoaded -= StagedSpriteLoadPlaylist_SpriteLoaded;
                deferredSpriteLoadPlaylist.SpriteLoaded += StagedSpriteLoadPlaylist_SpriteLoaded;
                coverSubscriptions.Add(playlist);
                _ = playlist.Sprite;
            }

            childPlaylists.Add(playlist);
            playlistTableData.TableView.ReloadDataKeepingPosition();
        }

        private void CreateFolder(string folderName)
        {
            folderName = folderName.Replace("/", "").Replace("\\", "").Replace(".", "");
            if (!string.IsNullOrEmpty(folderName))
            {
                var childManager = parentManager.CreateChildManager(folderName);

                if (childManagers.Contains(childManager))
                {
                    popupModalsController.ShowOkModal(modalTransform, "\"" + folderName + "\" already exists! Please use a different name.", null, animateParentCanvas: false);
                }
                else
                {
                    playlistTableData.Data.Insert(childManagers.Count, new CustomCellInfo(Path.GetFileName(childManager.PlaylistPath), "Folder", folderIcon));
                    playlistTableData.TableView.ReloadDataKeepingPosition();
                    childManagers.Add(childManager);
                    PlaylistLibUtils.playlistManager.RequestRefresh("PlaylistManager (plugin)");
                }
            }
        }

        #endregion
    }
}
