using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using PlaylistManager.Interfaces;
using PlaylistManager.Utilities;
using System;
using IPA.Loader;
using PlaylistManager.Downloaders;
using SiraUtil.Zenject;
using Tweening;
using UnityEngine;
using Zenject;

namespace PlaylistManager.UI
{
    internal class PlaylistViewButtonsController : IInitializable, IDisposable, ILevelCategoryUpdater, IParentManagerUpdater
    {
        private readonly PopupModalsController popupModalsController;
        private readonly TweeningManager uwuTweenyManager;
        private readonly PlaylistSequentialDownloader playlistDownloader;
        private readonly PlaylistDownloaderViewController playlistDownloaderViewController;
        private readonly PlaylistManagerFlowCoordinator playlistManagerFlowCoordinator;

        private readonly MainFlowCoordinator mainFlowCoordinator;
        private readonly AnnotatedBeatmapLevelCollectionsViewController annotatedBeatmapLevelCollectionsViewController;
        private readonly LevelFilteringNavigationController levelFilteringNavigationController;
        private readonly SelectLevelCategoryViewController selectLevelCategoryViewController;
        private readonly IconSegmentedControl levelCategorySegmentedControl;
        private readonly PluginMetadata pluginMetadata;
        private readonly BSMLParser bsmlParser;

        private BeatSaberPlaylistsLib.PlaylistManager parentManager;
        private CanvasGroup downloadButtonCanvasGroup;
        private bool disposed;

        [UIComponent("root")]
        private RectTransform rootTransform { get; set; }

        [UIComponent("create-button")]
        private RectTransform createButtonTransform { get; set; }

        [UIComponent("download-button")]
        private RectTransform downloadButtonTransform { get; set; }

        [UIComponent("download-button")]
        private ButtonIconImage downloadButton { get; set; }

        private Color downloadButtonIconColor;

        [UIComponent("flow-button")]
        private ButtonIconImage flowButton { get; set; }

        [UIComponent("queue-modal")]
        private ModalView queueModal { get; set; }

        [UIComponent("queue-modal")]
        private RectTransform queueModalTransform { get; set; }

        private Vector3 queueModalPosition;

        public PlaylistViewButtonsController(PopupModalsController popupModalsController, TimeTweeningManager uwuTweenyManager, PlaylistSequentialDownloader playlistDownloader, PlaylistDownloaderViewController playlistDownloaderViewController,
            MainFlowCoordinator mainFlowCoordinator, PlaylistManagerFlowCoordinator playlistManagerFlowCoordinator, AnnotatedBeatmapLevelCollectionsViewController annotatedBeatmapLevelCollectionsViewController,
            LevelFilteringNavigationController levelFilteringNavigationController, SelectLevelCategoryViewController selectLevelCategoryViewController, UBinder<Plugin, PluginMetadata> pluginMetadata, BSMLParser bsmlParser)
        {
            this.popupModalsController = popupModalsController;
            this.uwuTweenyManager = uwuTweenyManager;
            this.playlistDownloader = playlistDownloader;
            this.playlistDownloaderViewController = playlistDownloaderViewController;

            this.mainFlowCoordinator = mainFlowCoordinator;
            this.playlistManagerFlowCoordinator = playlistManagerFlowCoordinator;
            this.annotatedBeatmapLevelCollectionsViewController = annotatedBeatmapLevelCollectionsViewController;
            this.levelFilteringNavigationController = levelFilteringNavigationController;
            this.selectLevelCategoryViewController = selectLevelCategoryViewController;
            levelCategorySegmentedControl = selectLevelCategoryViewController._levelFilterCategoryIconSegmentedControl;
            this.pluginMetadata = pluginMetadata.Value;
            this.bsmlParser = bsmlParser;
        }

        public void Initialize()
        {
            bsmlParser.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(pluginMetadata.Assembly, "PlaylistManager.UI.Views.PlaylistViewButtons.bsml"), annotatedBeatmapLevelCollectionsViewController.gameObject, this);
            playlistDownloader.QueueUpdatedEvent += DownloadQueueUpdated;
            playlistDownloader.PopupEvent += TweenButton;
            UpdateDownloadButtonState();
            annotatedBeatmapLevelCollectionsViewController.didOpenBeatmapLevelCollectionsEvent += HidePlaylistViewButtons;
            annotatedBeatmapLevelCollectionsViewController.didCloseBeatmapLevelCollectionsEvent += ShowPlaylistViewButtons;
            annotatedBeatmapLevelCollectionsViewController.didDeactivateEvent += PlaylistViewDeactivated;
        }

        public void Dispose()
        {
            disposed = true;
            playlistDownloader.QueueUpdatedEvent -= DownloadQueueUpdated;
            playlistDownloader.PopupEvent -= TweenButton;
            annotatedBeatmapLevelCollectionsViewController.didOpenBeatmapLevelCollectionsEvent -= HidePlaylistViewButtons;
            annotatedBeatmapLevelCollectionsViewController.didCloseBeatmapLevelCollectionsEvent -= ShowPlaylistViewButtons;
            annotatedBeatmapLevelCollectionsViewController.didDeactivateEvent -= PlaylistViewDeactivated;
        }

        private void PlaylistViewDeactivated(bool removedFromHierarchy, bool screenSystemDisabling) => ShowPlaylistViewButtons();

        private void HidePlaylistViewButtons() => SetPlaylistViewButtonsVisible(false);

        private void ShowPlaylistViewButtons() => SetPlaylistViewButtonsVisible(true);

        private void DownloadQueueUpdated() => IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(UpdateDownloadButtonState);

        private void UpdateDownloadButtonState()
        {
            var hasDownloads = PlaylistSequentialDownloader.downloadQueue.Count != 0;
            downloadButtonCanvasGroup.alpha = hasDownloads ? 1f : 0.35f;
            downloadButtonCanvasGroup.blocksRaycasts = hasDownloads;
        }

        private void SetPlaylistViewButtonsVisible(bool visible)
        {
            createButtonTransform.gameObject.SetActive(visible);
            downloadButtonTransform.gameObject.SetActive(visible);
            flowButton.gameObject.SetActive(visible);
        }

        private void TweenButton()
        {
            uwuTweenyManager.KillAllTweens(downloadButton.Image);
            if (playlistDownloader.PendingPopup != null)
            {
                var tween = new FloatTween(0.35f, 0.6f, val =>
                {
                    var color = downloadButton.Image.color;
                    downloadButton.Image.color = new Color(val, val, val, color.a);
                }, 0.75f, EaseType.InOutBack);
                uwuTweenyManager.AddTween(tween, downloadButton.Image);
                tween.onCompleted = delegate () { TweenButton(); };
            }
            else
            {
                var color = downloadButton.Image.color;
                downloadButton.Image.color = new Color(downloadButtonIconColor.r, downloadButtonIconColor.g, downloadButtonIconColor.b, color.a);
            }
        }

        public void LevelCategoryUpdated(SelectLevelCategoryViewController.LevelCategory levelCategory, bool viewControllerActivated)
        {
            if (rootTransform != null)
            {
                if (levelCategory == SelectLevelCategoryViewController.LevelCategory.CustomSongs)
                {
                    rootTransform.gameObject.SetActive(true);
                }
                else
                {
                    rootTransform.gameObject.SetActive(false);
                    ShowPlaylistViewButtons();
                }
            }
        }

        public void ParentManagerUpdated(BeatSaberPlaylistsLib.PlaylistManager parentManager) => this.parentManager = parentManager;

        [UIAction("#post-parse")]
        private void PostParse()
        {
            queueModalPosition = queueModalTransform.localPosition;

            downloadButtonIconColor = downloadButton.Image.color;
            downloadButtonCanvasGroup = downloadButton.GetComponent<CanvasGroup>() ?? downloadButton.gameObject.AddComponent<CanvasGroup>();

            downloadButton.transform.localScale = new Vector3(0.36f, 0.36f, 1f);
            flowButton.transform.localScale = new Vector3(0.36f, 0.36f, 1f);
            ((ImageView)downloadButton.Image)._skew = 0.18f;
            ((ImageView)flowButton.Image)._skew = 0.18f;
        }

        #region Create Playlist

        [UIAction("create-click")]
        private void CreateClicked()
        {
            popupModalsController.ShowKeyboard(rootTransform, CreatePlaylist);
        }

        private async void CreatePlaylist(string playlistName)
        {
            if (string.IsNullOrWhiteSpace(playlistName))
            {
                return;
            }

            var manager = parentManager;
            var originalParent = parentManager;
            var catalog = PlaylistLibUtils.Catalog;
            try
            {
                manager ??= await PlaylistLibUtils.GetDefaultManagerAsync();
                if (disposed) return;
                var playlist = await PlaylistLibUtils.CreatePlaylistWithConfigAsync(playlistName, manager);
                if (!ReferenceEquals(catalog, PlaylistLibUtils.Catalog)) return;
                await catalog.ScanAsync();
                await IPA.Utilities.UnityGame.SwitchToMainThreadAsync();
                if (disposed || !ReferenceEquals(parentManager, originalParent)) return;
                popupModalsController.ShowYesNoModal(rootTransform, $"Successfully created {playlist.Title}", () =>
                {
                    if (disposed || !ReferenceEquals(catalog, PlaylistLibUtils.Catalog)
                        || catalog.Manager.GetManagerForPlaylist(playlist) == null) return;
                    levelCategorySegmentedControl.SelectCellWithNumber(1);
                    selectLevelCategoryViewController.LevelFilterCategoryIconSegmentedControlDidSelectCell(levelCategorySegmentedControl, 1);
                    levelFilteringNavigationController.SelectAnnotatedBeatmapLevelCollection(playlist.PlaylistLevelPack);
                }, "Go to playlist", "Dismiss");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log.Error(e); }
        }

        #endregion

        #region Download Queue

        [UIAction("queue-click")]
        private void ShowQueue()
        {
            if (PlaylistSequentialDownloader.downloadQueue.Count == 0)
            {
                return;
            }

            queueModalTransform.localPosition = queueModalPosition;
            queueModal.Show(true, moveToCenter: false, finishedCallback: () =>
            {
                playlistDownloaderViewController.SetParent(queueModalTransform, new Vector3(0.75f, 0.75f, 1f));
            });
        }

        #endregion

        #region Settings

        [UIAction("flow-click")]
        private void ShowSettings()
        {
            playlistManagerFlowCoordinator.PresentFlowCoordinator(mainFlowCoordinator.YoungestChildFlowCoordinatorOrSelf());
        }

        #endregion
    }
}
