using PlaylistManager.Downloaders;
using PlaylistManager.Utilities;
using Zenject;

namespace PlaylistManager.Installers
{
    internal class PlaylistManagerAppInstaller : Installer
    {
        public override void InstallBindings()
        {
            Container.BindInterfacesAndSelfTo<global::PlaylistManager.Managers.PlaylistCatalog>().AsSingle();
            Container.BindInterfacesAndSelfTo<PlaylistDownloader>().AsSingle();
            Container.BindInterfacesAndSelfTo<PlaylistSequentialDownloader>().AsSingle();
        }
    }
}
