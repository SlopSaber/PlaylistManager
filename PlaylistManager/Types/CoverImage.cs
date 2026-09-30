using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using IPA.Utilities;
using BeatSaberPlaylistsLib;
using BeatSaberPlaylistsLib.Types;
using UnityEngine;

/*
 * Yoinked from Playlists Lib with some changes
 * Original Author: Zingabopp
 */

namespace PlaylistManager.Types
{
    public class CoverImage : IDeferredSpriteLoad
    {
        public string Path { get; private set; }
        private Sprite _sprite;
        private bool SpriteLoadQueued;
        private bool disposed;

        public bool SpriteWasLoaded { get; private set; }
        public bool Blacklist { get; private set; }
        public event EventHandler SpriteLoaded;

        private static readonly object _loaderLock = new();
        private static bool CoroutineRunning = false;
        private static readonly Queue<Action> SpriteQueue = new();
        private static readonly SemaphoreSlim preparationSlots = new(2, 2);

        public CoverImage(string path)
        {
            Path = path;
            SpriteWasLoaded = false;
            Blacklist = false;
            SpriteLoadQueued = false;
        }

        public Sprite Sprite
        {
            get
            {
                if (_sprite == null)
                {
                    if (!disposed && !SpriteLoadQueued && !Blacklist)
                    {
                        SpriteLoadQueued = true;
                        QueueLoadSprite(this);
                    }
                    return BeatSaberMarkupLanguage.Utilities.ImageResources.WhitePixel;
                }
                return _sprite;
            }
        }

        public static YieldInstruction LoadWait = new WaitForEndOfFrame();

        internal void Release()
        {
            disposed = true;
            SpriteLoaded = null;
            if (_sprite && !ReferenceEquals(_sprite, BeatSaberPlaylistsLib.Utilities.DefaultSprite))
            {
                var texture = _sprite.texture;
                UnityEngine.Object.Destroy(_sprite);
                if (texture) UnityEngine.Object.Destroy(texture);
            }
            _sprite = null;
        }

        private static async void QueueLoadSprite(CoverImage coverImage)
        {
            await UnityGame.SwitchToMainThreadAsync();
            string path = coverImage.Path;
            await preparationSlots.WaitAsync();
            try
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (coverImage.disposed) return;
                byte[] bytes = await Task.Run(() => File.ReadAllBytes(path));
                await UnityGame.SwitchToMainThreadAsync();
                if (coverImage.disposed) return;
                var starter = SharedCoroutineStarter.instance;
                if (starter == null) return;
                var published = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                SpriteQueue.Enqueue(() =>
                {
                    try
                    {
                        if (coverImage.disposed) return;
                        coverImage._sprite = BeatSaberPlaylistsLib.Utilities.GetSpriteFromBytes(bytes);
                        coverImage.SpriteWasLoaded = coverImage._sprite != null;
                        coverImage.Blacklist = !coverImage.SpriteWasLoaded;
                        if (coverImage.Blacklist) Plugin.Log.Critical("Could not load " + path);
                        coverImage.SpriteLoaded?.Invoke(coverImage, null);
                    }
                    finally { published.TrySetResult(true); }
                });
                if (!CoroutineRunning) starter.StartCoroutine(SpriteLoadCoroutine());
                await published.Task;
            }
            catch (Exception e)
            {
                await UnityGame.SwitchToMainThreadAsync();
                Plugin.Log.Critical("Could not load " + path + "\nException message: " + e.Message);
                if (coverImage.disposed) return;
                coverImage.SpriteWasLoaded = false;
                coverImage.Blacklist = true;
                try { coverImage.SpriteLoaded?.Invoke(coverImage, null); }
                catch (Exception callbackError) { Plugin.Log.Error(callbackError); }
            }
            finally
            {
                preparationSlots.Release();
                await UnityGame.SwitchToMainThreadAsync();
                coverImage.SpriteLoadQueued = false;
            }
        }

        private static IEnumerator<YieldInstruction> SpriteLoadCoroutine()
        {
            lock (_loaderLock)
            {
                if (CoroutineRunning)
                    yield break;
                CoroutineRunning = true;
            }
            while (SpriteQueue.Count > 0)
            {
                yield return LoadWait;
                var loader = SpriteQueue.Dequeue();
                try { loader?.Invoke(); }
                catch (Exception e) { Plugin.Log.Error(e); }
            }
            CoroutineRunning = false;
            if (SpriteQueue.Count > 0)
                SharedCoroutineStarter.instance.StartCoroutine(SpriteLoadCoroutine());
        }
    }
}
