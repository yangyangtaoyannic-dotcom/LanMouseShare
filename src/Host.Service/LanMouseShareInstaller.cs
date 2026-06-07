using System.ComponentModel;
using System.Configuration.Install;
using System.ServiceProcess;

namespace LanMouseShare.Service
{
    [RunInstaller(true)]
    public sealed class LanMouseShareInstaller : Installer
    {
        public LanMouseShareInstaller()
        {
            var processInstaller = new ServiceProcessInstaller();
            processInstaller.Account = ServiceAccount.LocalSystem;

            var serviceInstaller = new ServiceInstaller();
            serviceInstaller.ServiceName = "LanMouseShare";
            serviceInstaller.DisplayName = "LAN Mouse Share";
            serviceInstaller.Description = "Provides LAN discovery, pairing, encrypted transport, and file transfer for LanMouseShare.";
            serviceInstaller.StartType = ServiceStartMode.Manual;

            Installers.Add(processInstaller);
            Installers.Add(serviceInstaller);
        }
    }
}
