using System.ServiceProcess;
using LanMouseShare.Core.Diagnostics;

namespace LanMouseShare.Service
{
    public sealed class LanMouseShareService : ServiceBase
    {
        private static LanMouseShareService _current;
        private ServiceRuntime _runtime;

        public LanMouseShareService()
        {
            _current = this;
            ServiceName = "LanMouseShare";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        internal static void RequestStop()
        {
            var service = _current;
            if (service == null)
            {
                return;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    AppLog.Info("Service stop requested by Agent.");
                    service.Stop();
                }
                catch (System.Exception ex)
                {
                    AppLog.Error("Service stop request failed.", ex);
                }
            });
        }

        protected override void OnStart(string[] args)
        {
            AppLog.Info("Windows service starting.");
            _runtime = new ServiceRuntime();
            _runtime.Start();
            AppLog.Info("Windows service started.");
        }

        protected override void OnStop()
        {
            AppLog.Info("Windows service stopping.");
            if (_runtime != null)
            {
                _runtime.Stop();
                _runtime.Dispose();
                _runtime = null;
            }
            AppLog.Info("Windows service stopped.");
        }
    }

    internal sealed class ServiceRuntime : System.IDisposable
    {
        private readonly PeerCoordinator _coordinator;
        private readonly NamedPipeCommandServer _pipeServer;

        public ServiceRuntime()
        {
            _coordinator = new PeerCoordinator();
            _pipeServer = new NamedPipeCommandServer(_coordinator);
        }

        public void Start()
        {
            AppLog.Info("Runtime start.");
            _coordinator.Start();
            _pipeServer.Start();
        }

        public void Stop()
        {
            AppLog.Info("Runtime stop.");
            _pipeServer.Stop();
            _coordinator.Stop();
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
