using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using BeatSaberMarkupLanguage.Parser;
using HMUI;
using PlaylistManager.Types;
using PlaylistManager.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using IPA.Loader;
using IPA.Utilities;
using SiraUtil.Zenject;
using UnityEngine;
using static BeatSaberMarkupLanguage.Components.CustomListTableData;

namespace PlaylistManager.UI
{
    public class ImageSelectionModalController : NotifiableBase, IDisposable
    {
        private readonly LevelPackDetailViewController levelPackDetailViewController;
        private readonly PopupModalsController popupModalsController;
        private readonly PluginMetadata pluginMetadata;
        private readonly BSMLParser bsmlParser;

        private readonly Task<Sprite> playlistManagerIcon;
        private readonly Task<string> imageDirectoryReady;
        private readonly Dictionary<string, CoverImage> coverImages;
        private bool parsed;
        private bool disposed;
        private int selectedIndex;
        private int showRevision;
        private int imageChangeRevision;
        private Sprite generatedPlaylistIcon;
        private BeatSaberPlaylistsLib.Types.IPlaylist shownPlaylist;

        public event Action<byte[]> ImageSelectedEvent;

        [UIComponent("list")]
        public CustomListTableData customListTableData;

        [UIComponent("modal")]
        private RectTransform modalTransform { get; set; }

        [UIComponent("modal")]
        private ModalView modalView { get; set; }

        private Vector3 modalPosition;

        [UIParams]
        private BSMLParserParams parserParams { get; set; }

        public ImageSelectionModalController(LevelPackDetailViewController levelPackDetailViewController, PopupModalsController popupModalsController, UBinder<Plugin, PluginMetadata> pluginMetadata, BSMLParser bsmlParser)
        {
            this.levelPackDetailViewController = levelPackDetailViewController;
            this.popupModalsController = popupModalsController;
            this.pluginMetadata = pluginMetadata.Value;
            this.bsmlParser = bsmlParser;

            imageDirectoryReady = PrepareImageDirectoryAsync();

            coverImages = new Dictionary<string, CoverImage>();
            playlistManagerIcon = BeatSaberMarkupLanguage.Utilities.LoadSpriteFromAssemblyAsync("PlaylistManager.Icons.DefaultIcon.png");
            parsed = false;
        }

        private async Task<string> PrepareImageDirectoryAsync()
        {
            try
            {
                var manager = await PlaylistLibUtils.GetDefaultManagerAsync();
                if (disposed) return null;
                var catalog = PlaylistLibUtils.Catalog;
                string directory = Path.Combine(manager.PlaylistPath, "CoverImages");
                bool createdIgnore = await Task.Run(() =>
                {
                    Directory.CreateDirectory(directory);
                    var ignorePath = Path.Combine(directory, ".plignore");
                    if (File.Exists(ignorePath)) return false;
                    using (File.Create(ignorePath)) { }
                    return true;
                });
                await UnityGame.SwitchToMainThreadAsync();
                if (disposed || !ReferenceEquals(catalog, PlaylistLibUtils.Catalog)) return null;
                if (createdIgnore) await catalog.ScanAsync();
                return directory;
            }
            catch (OperationCanceledException) { return null; }
            catch (Exception e)
            {
                Plugin.Log.Error($"Could not make images path.\nExcepton:{e.Message}");
                return null;
            }
        }

        private void Parse()
        {
            if (!parsed)
            {
                bsmlParser.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(pluginMetadata.Assembly, "PlaylistManager.UI.Views.ImageSelectionModal.bsml"), levelPackDetailViewController._detailWrapper.gameObject, this);
                modalPosition = modalTransform.position;
            }
            modalTransform.position = modalPosition;
        }

        public void Dispose()
        {
            disposed = true;
            showRevision++;
            imageChangeRevision++;
            foreach (var cover in coverImages.Values) cover.Release();
            coverImages.Clear();
            DestroyGeneratedIcon(generatedPlaylistIcon);
            generatedPlaylistIcon = null;
        }

        [UIAction("#post-parse")]
        private void PostParse()
        {
            parsed = true;
            modalView._animateParentCanvas = false;
        }

        internal void ShowModal(BeatSaberPlaylistsLib.Types.IPlaylist playlist)
        {
            if (disposed) return;
            shownPlaylist = playlist;
            Parse();
            parserParams.EmitEvent("close-modal");
            parserParams.EmitEvent("open-modal");
            ShowImages(playlist);
        }

        private async Task<bool> LoadImages(int revision)
        {
            string[] knownPaths = coverImages.Keys.ToArray();
            string directory = await imageDirectoryReady;
            if (directory == null) return false;
            var files = await Task.Run(() =>
            {
                string[] ext = { "jpg", "png" };
                var imageFiles = Directory.EnumerateFiles(directory, "*.*", SearchOption.AllDirectories)
                    .Where(s => ext.Contains(Path.GetExtension(s).TrimStart('.').ToLowerInvariant())).ToArray();
                return (Images: imageFiles, Removed: knownPaths.Where(path => !File.Exists(path)).ToArray());
            });
            await UnityGame.SwitchToMainThreadAsync();
            if (revision != showRevision || !customListTableData) return false;
            foreach (var path in files.Removed)
            {
                if (coverImages.TryGetValue(path, out var removed)) removed.Release();
                coverImages.Remove(path);
            }

            foreach (var file in files.Images)
            {
                if (!coverImages.ContainsKey(file))
                {
                    coverImages.Add(file, new CoverImage(file));
                }
            }
            return true;
        }

        private async void ShowImages(BeatSaberPlaylistsLib.Types.IPlaylist playlist)
        {
            int revision = ++showRevision;
            foreach (var coverImage in coverImages.Values) coverImage.SpriteLoaded -= CoverImage_SpriteLoaded;
            customListTableData.Data.Clear();
            IsLoading = true;
            DestroyGeneratedIcon(generatedPlaylistIcon);
            generatedPlaylistIcon = null;
            Sprite generatedIcon = null;
            try
            {
                generatedIcon = await PlaylistLibUtils.GeneratePlaylistIcon(playlist);
                var defaultIcon = await playlistManagerIcon;
                if (!await LoadImages(revision)) return;
                await UnityGame.SwitchToMainThreadAsync();
                if (revision != showRevision || !customListTableData) return;
                customListTableData.Data.Add(new CustomCellInfo("Clear Icon", "Clear", generatedIcon));
                generatedPlaylistIcon = generatedIcon;
                generatedIcon = null;
                customListTableData.Data.Add(new CustomCellInfo("PlaylistManager Icon", "Default", defaultIcon));
                foreach (var coverImage in coverImages)
                {
                    if (!coverImage.Value.SpriteWasLoaded && !coverImage.Value.Blacklist)
                    {
                        coverImage.Value.SpriteLoaded -= CoverImage_SpriteLoaded;
                        coverImage.Value.SpriteLoaded += CoverImage_SpriteLoaded;
                        _ = coverImage.Value.Sprite;
                    }
                    else if (coverImage.Value.SpriteWasLoaded)
                    {
                        customListTableData.Data.Add(new CustomCellInfo(Path.GetFileName(coverImage.Key), coverImage.Key, coverImage.Value.Sprite));
                    }
                }
                customListTableData.TableView.ReloadData();
                customListTableData.TableView.ScrollToCellWithIdx(0, TableView.ScrollPositionType.Beginning, false);
                _ = ViewControllerMonkeyCleanup();
            }
            catch (Exception e)
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (revision == showRevision && customListTableData) IsLoading = false;
                Plugin.Log.Error(e);
            }
            finally
            {
                await UnityGame.SwitchToMainThreadAsync();
                DestroyGeneratedIcon(generatedIcon);
            }
        }

        private static void DestroyGeneratedIcon(Sprite sprite)
        {
            if (!sprite || ReferenceEquals(sprite, BeatSaberPlaylistsLib.Utilities.DefaultSprite)) return;
            var texture = sprite.texture;
            UnityEngine.Object.Destroy(sprite);
            if (texture) UnityEngine.Object.Destroy(texture);
        }

        private void CoverImage_SpriteLoaded(object sender, EventArgs e)
        {
            if (sender is CoverImage coverImage)
            {
                if (customListTableData && customListTableData.TableView && coverImage.SpriteWasLoaded
                    && coverImages.TryGetValue(coverImage.Path, out var current) && ReferenceEquals(current, coverImage))
                {
                    customListTableData.Data.Add(new CustomCellInfo(Path.GetFileName(coverImage.Path), coverImage.Path, coverImage.Sprite));
                    customListTableData.TableView.ReloadDataKeepingPosition();

                    if (customListTableData.Data.Count == 4)
                    {
                        customListTableData.TableView.AddCellToReusableCells(customListTableData.TableView.dataSource.CellForIdx(customListTableData.TableView, 3));
                    }
                    _ = ViewControllerMonkeyCleanup();
                }
                coverImage.SpriteLoaded -= CoverImage_SpriteLoaded;
            }
        }

        [UIAction("select-cell")]
        private void OnCellSelect(TableView tableView, int index)
        {
            customListTableData.TableView.ClearSelection();
            selectedIndex = index;
            popupModalsController.ShowYesNoModal(modalTransform, "Are you sure you want to change the image of the playlist? This cannot be reverted.", ChangeImage, animateParentCanvas: false);
        }

        private async void ChangeImage()
        {
            int revision = showRevision;
            int change = ++imageChangeRevision;
            var playlist = shownPlaylist;
            var assembly = pluginMetadata.Assembly;
            if (selectedIndex == 0)
            {
                ImageSelectedEvent?.Invoke(null);
                parserParams.EmitEvent("close-modal");
            }
            else
            {
                var selectedImagePath = selectedIndex == 1 ? null : customListTableData.Data[selectedIndex].Subtext;
                try
                {
                    var imageBytes = await Task.Run(() => selectedImagePath == null
                        ? BeatSaberPlaylistsLib.Utilities.GetResource(assembly, "PlaylistManager.Icons.DefaultIcon.png")
                        : File.ReadAllBytes(selectedImagePath));
                    await UnityGame.SwitchToMainThreadAsync();
                    if (revision != showRevision || change != imageChangeRevision
                        || levelPackDetailViewController._pack is not BeatSaberPlaylistsLib.Types.PlaylistLevelPack pack
                        || !ReferenceEquals(pack.playlist, playlist)) return;
                    ImageSelectedEvent?.Invoke(imageBytes);
                    parserParams.EmitEvent("close-modal");
                }
                catch (Exception e)
                {
                    await UnityGame.SwitchToMainThreadAsync();
                    if (revision == showRevision && change == imageChangeRevision && customListTableData)
                    {
                        popupModalsController.ShowOkModal(modalTransform, "There was an error loading this image. Check logs for more details.", null, animateParentCanvas: false);
                    }
                    Plugin.Log.Critical("Could not load " + selectedImagePath + "\nException message: " + e.Message);
                }
            }
        }

        private async Task ViewControllerMonkeyCleanup()
        {
            int revision = showRevision;
            await SiraUtil.Extras.Utilities.PauseChamp;
            await UnityGame.SwitchToMainThreadAsync();
            if (revision != showRevision || !customListTableData || !customListTableData.TableView) return;
            var imageViews = customListTableData.TableView.GetComponentsInChildren<ImageView>(true);
            for (var i = 0; i < imageViews.Length; i++)
            {
                imageViews[i]._skew = 0f;
            }
            IsLoading = false;
        }

        private bool _isLoading;

        [UIValue("is-loading")]
        private bool IsLoading
        {
            get => _isLoading;
            set
            {
                _isLoading = value;
                NotifyPropertyChanged();
                NotifyPropertyChanged(nameof(IsNotLoading));
            }
        }

        [UIValue("is-not-loading")]
        private bool IsNotLoading => !IsLoading;
    }
}
