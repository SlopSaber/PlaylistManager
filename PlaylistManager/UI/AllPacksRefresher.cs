using PlaylistManager.Interfaces;
using PlaylistManager.Utilities;
using System;
using System.Linq;

namespace PlaylistManager.UI
{
    public class AllPacksRefresher : IPMRefreshable, ILevelCollectionsTableUpdater
    {
        public event Action<System.Collections.Generic.IReadOnlyList<BeatmapLevelPack>, int> LevelCollectionTableViewUpdatedEvent;
        private readonly AnnotatedBeatmapLevelCollectionsViewController annotatedBeatmapLevelCollectionsViewController;
        private readonly BeatmapLevelsModel beatmapLevelsModel;
        private readonly PlaylistUpdater playlistUpdater;

        private AllPacksRefresher(AnnotatedBeatmapLevelCollectionsViewController annotatedBeatmapLevelCollectionsViewController, BeatmapLevelsModel beatmapLevelsModel, PlaylistUpdater playlistUpdater)
        {
            this.annotatedBeatmapLevelCollectionsViewController = annotatedBeatmapLevelCollectionsViewController;
            this.beatmapLevelsModel = beatmapLevelsModel;
            this.playlistUpdater = playlistUpdater;
        }

        public void Refresh()
        {
            var playlistLevelPacks = PlaylistLibUtils.GetCachedPlaylistLevelPacks();
            playlistUpdater.RefreshPlaylistChangedListeners(playlistLevelPacks);
            var annotatedBeatmapLevelCollections = beatmapLevelsModel._customLevelsRepository.beatmapLevelPacks.Concat(playlistLevelPacks).ToArray();
            var selectedId = annotatedBeatmapLevelCollectionsViewController.selectedAnnotatedBeatmapLevelPack?.packID;
            var indexToSelect = Array.FindIndex(annotatedBeatmapLevelCollections, pack => pack.packID == selectedId);
            if (indexToSelect != -1)
            {
                annotatedBeatmapLevelCollectionsViewController.SetData(annotatedBeatmapLevelCollections, indexToSelect, false);
            }
            else LevelCollectionTableViewUpdatedEvent?.Invoke(annotatedBeatmapLevelCollections, 0);
        }
    }
}
