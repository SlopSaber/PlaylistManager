using System;
using System.Collections.Generic;
using Zenject;
using PlaylistManager.Interfaces;
using PlaylistManager.Configuration;
using PlaylistManager.Utilities;
using PlaylistManager.HarmonyPatches;
using System.Linq;
using System.Threading;
using PlaylistManager.Downloaders;
using PlaylistManager.UI;

namespace PlaylistManager.Managers
{
    internal class PlaylistUIManager : IInitializable, IDisposable, ILevelCollectionsTableUpdater
    {
        private readonly AnnotatedBeatmapLevelCollectionsViewController annotatedBeatmapLevelCollectionsViewController;
        private readonly LevelCollectionNavigationController levelCollectionNavigationController;
        private readonly SelectLevelCategoryViewController selectLevelCategoryViewController;
        private readonly SettingsViewController settingsViewController;
        private readonly PlaylistSequentialDownloader playlistDownloader;

        private int downloadingBeatmapCollectionIdx;
        private BeatmapLevelPack[] downloadingBeatmapLevelCollections;
        private BeatmapLevel downloadingBeatmap;
        private bool disposed;

        private readonly List<ILevelCategoryUpdater> levelCategoryUpdaters;
        private readonly IPMRefreshable refreshable;

        public event Action<IReadOnlyList<BeatmapLevelPack>, int> LevelCollectionTableViewUpdatedEvent;

        internal PlaylistUIManager(AnnotatedBeatmapLevelCollectionsViewController annotatedBeatmapLevelCollectionsViewController, LevelCollectionNavigationController levelCollectionNavigationController,
            SelectLevelCategoryViewController selectLevelCategoryViewController, SettingsViewController settingsViewController, PlaylistSequentialDownloader playlistDownloader,
            List<ILevelCategoryUpdater> levelCategoryUpdaters, IPMRefreshable refreshable)
        {
            this.annotatedBeatmapLevelCollectionsViewController = annotatedBeatmapLevelCollectionsViewController;
            this.levelCollectionNavigationController = levelCollectionNavigationController;
            this.selectLevelCategoryViewController = selectLevelCategoryViewController;
            this.settingsViewController = settingsViewController;
            this.playlistDownloader = playlistDownloader;

            this.levelCategoryUpdaters = levelCategoryUpdaters;
            this.refreshable = refreshable;
        }

        public void Initialize()
        {
            // Whenever a level category is selected
            selectLevelCategoryViewController.didSelectLevelCategoryEvent += SelectLevelCategoryViewController_didSelectLevelCategoryEvent;
            selectLevelCategoryViewController.didActivateEvent += SelectLevelCategoryViewController_didActivateEvent;
            selectLevelCategoryViewController.didDeactivateEvent += SelectLevelCategoryViewController_didDeactivateEvent;

            // When all playlists finish downloading
            playlistDownloader.QueueUpdatedEvent += PlaylistDownloader_QueueUpdatedEvent;

            // Whenever a refresh is requested
            PlaylistLibUtils.Catalog.Changed += CatalogChanged;
            if (PlaylistLibUtils.Catalog.IsReady) CatalogChanged();

            // For assigning playlist author
            settingsViewController.NameFetchRequestedEvent += AssignAuthor;
            AssignAuthor();
        }

        public void Dispose()
        {
            disposed = true;
            selectLevelCategoryViewController.didSelectLevelCategoryEvent -= SelectLevelCategoryViewController_didSelectLevelCategoryEvent;
            selectLevelCategoryViewController.didActivateEvent -= SelectLevelCategoryViewController_didActivateEvent;
            selectLevelCategoryViewController.didDeactivateEvent -= SelectLevelCategoryViewController_didDeactivateEvent;

            playlistDownloader.QueueUpdatedEvent -= PlaylistDownloader_QueueUpdatedEvent;
            SongCore_RefreshLevelPacks.PacksToBeRefreshedEvent -= OnPacksToBeRefreshed;
            LevelFilteringNavigationController_UpdateSecondChildControllerContent.SecondChildControllerUpdatedEvent -= LevelFilteringNavigationController_SecondChildControllerUpdatedEvent;

            if (PlaylistLibUtils.Catalog != null) PlaylistLibUtils.Catalog.Changed -= CatalogChanged;

            settingsViewController.NameFetchRequestedEvent -= AssignAuthor;
        }

        private void SelectLevelCategoryViewController_didSelectLevelCategoryEvent(SelectLevelCategoryViewController selectLevelCategoryViewController, SelectLevelCategoryViewController.LevelCategory levelCategory)
        {
            foreach (var levelCategoryUpdater in levelCategoryUpdaters)
            {
                levelCategoryUpdater.LevelCategoryUpdated(levelCategory, false);
            }
        }

        private void SelectLevelCategoryViewController_didActivateEvent(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
        {
            foreach (var levelCategoryUpdater in levelCategoryUpdaters)
            {
                levelCategoryUpdater.LevelCategoryUpdated(selectLevelCategoryViewController.selectedLevelCategory, true);
            }
        }

        private void SelectLevelCategoryViewController_didDeactivateEvent(bool removedFromHierarchy, bool screenSystemDisabling)
        {
            foreach (var levelCategoryUpdater in levelCategoryUpdaters)
            {
                levelCategoryUpdater.LevelCategoryUpdated(SelectLevelCategoryViewController.LevelCategory.None, false);
            }
        }

        private void PlaylistDownloader_QueueUpdatedEvent()
        {
            if (PlaylistSequentialDownloader.downloadQueue.Count == 0)
            {
                SongCore_RefreshLevelPacks.PacksToBeRefreshedEvent += OnPacksToBeRefreshed;
            }
        }

        private void OnPacksToBeRefreshed()
        {
            SongCore_RefreshLevelPacks.PacksToBeRefreshedEvent -= OnPacksToBeRefreshed;

            if (levelCollectionNavigationController.isActiveAndEnabled)
            {
                if (annotatedBeatmapLevelCollectionsViewController.isActiveAndEnabled)
                {
                    downloadingBeatmapLevelCollections = annotatedBeatmapLevelCollectionsViewController._annotatedBeatmapLevelCollections.ToArray();
                    downloadingBeatmapCollectionIdx = annotatedBeatmapLevelCollectionsViewController.selectedItemIndex;
                }
                downloadingBeatmap = levelCollectionNavigationController.beatmapLevel;
                LevelFilteringNavigationController_UpdateSecondChildControllerContent.SecondChildControllerUpdatedEvent += LevelFilteringNavigationController_SecondChildControllerUpdatedEvent;
            }
        }

        private void LevelFilteringNavigationController_SecondChildControllerUpdatedEvent()
        {
            LevelFilteringNavigationController_UpdateSecondChildControllerContent.SecondChildControllerUpdatedEvent -= LevelFilteringNavigationController_SecondChildControllerUpdatedEvent;
            if (annotatedBeatmapLevelCollectionsViewController.isActiveAndEnabled)
            {
                LevelCollectionTableViewUpdatedEvent?.Invoke(downloadingBeatmapLevelCollections, downloadingBeatmapCollectionIdx);
            }
            if (levelCollectionNavigationController.isActiveAndEnabled && downloadingBeatmap != null)
            {
                levelCollectionNavigationController.SelectLevel(downloadingBeatmap);
            }
        }

        private void CatalogChanged()
        {
            if (!disposed && selectLevelCategoryViewController.selectedLevelCategory == SelectLevelCategoryViewController.LevelCategory.CustomSongs
                && annotatedBeatmapLevelCollectionsViewController.isActiveAndEnabled) refreshable.Refresh();
        }

        private async void AssignAuthor()
        {
            if (PluginConfig.Instance.AutomaticAuthorName)
            {
                // var user = await platformUserModel.GetUserInfo(CancellationToken.None);
                PluginConfig.Instance.AuthorName = /* user?.userName ?? */ PluginConfig.Instance.AuthorName ?? nameof(PlaylistManager);
            } 
            else
            {
                PluginConfig.Instance.AuthorName = PluginConfig.Instance.AuthorName;
            }
        }
    }
}
