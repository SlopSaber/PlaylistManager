using BeatSaberPlaylistsLib.Types;
using IPA.Utilities;
using PlaylistManager.Utilities;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Zenject;

namespace PlaylistManager.Managers
{
    internal sealed class PlaylistCatalog : IInitializable, IDisposable
    {
        private readonly CancellationTokenSource lifetime = new();
        private readonly TaskCompletionSource<object> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly HashSet<BeatSaberPlaylistsLib.PlaylistManager> subscriptions = new();
        private BeatSaberPlaylistsLib.PlaylistManager.ScanResult current;
        private Task scanTask = Task.CompletedTask;
        private bool requested;
        private bool scanning;
        private bool disposed;

        internal BeatSaberPlaylistsLib.PlaylistManager Manager { get; private set; }
        internal Task Ready => ready.Task;
        internal bool IsReady => current != null;
        internal event Action Changed;

        public PlaylistCatalog()
        {
            PlaylistLibUtils.Catalog = this;
        }

        public void Initialize() => _ = InitializeAsync();

        private async Task InitializeAsync()
        {
            try
            {
                var manager = await Task.Run(() => BeatSaberPlaylistsLib.PlaylistManager.DefaultManager);
                await UnityGame.SwitchToMainThreadAsync();
                if (disposed) return;
                Manager = manager;
                Subscribe(manager);
                await ScanAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log.Error(e); }
            finally { ready.TrySetResult(null); }
        }

        internal IPlaylist[] GetPlaylists(BeatSaberPlaylistsLib.PlaylistManager manager = null)
        {
            if (current == null) return Array.Empty<IPlaylist>();
            if (manager == null) return current.Playlists;
            return current.PlaylistsByManager.TryGetValue(manager, out var playlists) ? playlists : Array.Empty<IPlaylist>();
        }

        internal BeatSaberPlaylistsLib.PlaylistManager GetAvailableManager(BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            while (manager != null && (current == null || !current.PlaylistsByManager.ContainsKey(manager)))
                manager = manager.Parent;
            return manager ?? Manager;
        }

        internal Task ScanAsync()
        {
            if (disposed) return Task.CompletedTask;
            requested = true;
            if (Manager == null) return Ready;
            if (!scanning) scanTask = RunScansAsync();
            return scanTask;
        }

        private async Task RunScansAsync()
        {
            scanning = true;
            try
            {
                while (requested && !disposed)
                {
                    requested = false;
                    var result = await Manager.GetAllPlaylistsAsync(true, lifetime.Token);
                    await UnityGame.SwitchToMainThreadAsync();
                    if (disposed) return;
                    current = result;
                    if (result.Exception != null)
                        foreach (var error in result.Exception.InnerExceptions) Plugin.Log.Error(error);
                    foreach (var manager in new List<BeatSaberPlaylistsLib.PlaylistManager>(subscriptions))
                    {
                        if (!result.PlaylistsByManager.ContainsKey(manager))
                        {
                            manager.PlaylistsRefreshRequested -= RefreshRequested;
                            subscriptions.Remove(manager);
                        }
                    }
                    foreach (var manager in result.PlaylistsByManager.Keys) Subscribe(manager);
                    if (!ContainsCurrentManagers(Manager))
                    {
                        requested = true;
                        continue;
                    }
                    ready.TrySetResult(null);
                    if (Changed != null)
                        foreach (Action callback in Changed.GetInvocationList())
                        {
                            try { callback(); }
                            catch (Exception e) { Plugin.Log.Error(e); }
                        }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Plugin.Log.Error(e); }
            finally
            {
                scanning = false;
                if (requested && !disposed) scanTask = RunScansAsync();
            }
        }

        private bool ContainsCurrentManagers(BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            if (!current.PlaylistsByManager.ContainsKey(manager)) return false;
            foreach (var child in manager.GetChildManagers())
                if (!ContainsCurrentManagers(child)) return false;
            return true;
        }

        private void Subscribe(BeatSaberPlaylistsLib.PlaylistManager manager)
        {
            if (subscriptions.Add(manager)) manager.PlaylistsRefreshRequested += RefreshRequested;
        }

        private async void RefreshRequested(object sender, string requester)
        {
            try
            {
                await UnityGame.SwitchToMainThreadAsync();
                if (disposed) return;
                Plugin.Log.Info("Playlist Refresh requested by: " + requester);
                _ = ScanAsync();
            }
            catch (Exception e) { Plugin.Log.Error(e); }
        }

        public void Dispose()
        {
            disposed = true;
            lifetime.Cancel();
            foreach (var manager in subscriptions) manager.PlaylistsRefreshRequested -= RefreshRequested;
            subscriptions.Clear();
            Changed = null;
            ready.TrySetResult(null);
            if (ReferenceEquals(PlaylistLibUtils.Catalog, this)) PlaylistLibUtils.Catalog = null;
        }
    }
}
