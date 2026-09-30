using BeatSaberMarkupLanguage;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaberMarkupLanguage.Components;
using HMUI;
using System;
using IPA.Loader;
using PlaylistManager.Downloaders;
using PlaylistManager.Types;
using SiraUtil.Zenject;
using UnityEngine;
using UnityEngine.SceneManagement;
using Zenject;

namespace PlaylistManager.UI
{
    public class PlaylistDownloaderViewController : MonoBehaviour, IInitializable, IDisposable
    {
        private PlaylistSequentialDownloader playlistDownloader;
        private PopupModalsController popupModalsController;
        private PluginMetadata pluginMetadata;
        private BSMLParser bsmlParser;
        private bool parsed;
        private bool refreshRequested;
        private bool disposed;
        private PopupContents shownPopup;

        [UIComponent("download-list")]
        private CustomCellListTableData customListTableData { get; set; }

        [UIComponent("root")]
        private RectTransform rootTransform { get; set; }

        [Inject]
        internal void Construct(PlaylistSequentialDownloader playlistDownloader, PopupModalsController popupModalsController, UBinder<Plugin, PluginMetadata> pluginMetadata, BSMLParser bsmlParser)
        {
            this.playlistDownloader = playlistDownloader;
            this.popupModalsController = popupModalsController;
            this.pluginMetadata = pluginMetadata.Value;
            this.bsmlParser = bsmlParser;
        }

        public void SetParent(Transform parent, Vector3? scale = null)
        {
            if (!parsed)
            {
                bsmlParser.Parse(BeatSaberMarkupLanguage.Utilities.GetResourceContent(pluginMetadata.Assembly, "PlaylistManager.UI.Views.PlaylistDownloaderView.bsml"), parent.gameObject, this);
            }
            rootTransform.SetParent(parent, false);
            rootTransform.localScale = scale ?? Vector3.one;
            OnPopupRequested();
        }

        public void OnDisable()
        {
            if (playlistDownloader.PendingPopup != null)
            {
                popupModalsController.HideYesNoModal(shownPopup);
                shownPopup = null;
            }
        }

        public void Initialize()
        {
            playlistDownloader.PopupEvent += OnPopupRequested;
            playlistDownloader.QueueUpdatedEvent += UpdateQueue;
            SceneManager.activeSceneChanged += OnMenuLoaded;
        }

        public void Dispose()
        {
            disposed = true;
            playlistDownloader.PopupEvent -= OnPopupRequested;
            playlistDownloader.QueueUpdatedEvent -= UpdateQueue;
            SceneManager.activeSceneChanged -= OnMenuLoaded;
        }

        [UIAction("#post-parse")]
        private void PostParse()
        {
            parsed = true;
            transform.SetParent(rootTransform);
            customListTableData.Data = PlaylistSequentialDownloader.downloadQueue;
            customListTableData.TableView.ReloadDataKeepingPosition();
        }

        private void OnPopupRequested()
        {
            var popup = playlistDownloader.PendingPopup;
            if (!disposed && parsed)
            {
                _ = IPA.Utilities.Async.UnityMainThreadTaskScheduler.Factory.StartNew(() =>
                {
                    try { ShowPendingPopup(popup); }
                    catch (Exception e) { Plugin.Log.Error(e); }
                });
            }
        }

        private void ShowPendingPopup(PopupContents popup)
        {
            if (disposed || !rootTransform || !ReferenceEquals(playlistDownloader.PendingPopup, popup)) return;
            if (popup == null)
            {
                if (shownPopup != null) popupModalsController.HideYesNoModal(shownPopup);
                shownPopup = null;
                return;
            }
            popup.parent = rootTransform;
            popup.animateParentCanvas = !rootTransform.GetComponentInParent<ModalView>();
            if (rootTransform.gameObject.activeInHierarchy)
            {
                popupModalsController.ShowModal(popup);
                shownPopup = popup;
            }
        }

        private void UpdateQueue()
        {
            if (customListTableData != null)
            {
                customListTableData.TableView.ReloadDataKeepingPosition();
            }

            if (PlaylistSequentialDownloader.downloadQueue.Count == 0)
            {
                if (SceneManager.GetActiveScene().name == "GameCore")
                {
                    refreshRequested = true;
                }
                else
                {
                    playlistDownloader.OnQueueClear();
                }
            }
        }

        private void OnMenuLoaded(Scene previousScene, Scene newScene)
        {
            if (refreshRequested && newScene.name == "MainMenu")
            {
                refreshRequested = false;
                playlistDownloader.OnQueueClear();
            }
        }
    }
}
