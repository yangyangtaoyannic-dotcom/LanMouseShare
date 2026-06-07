namespace LanMouseShare.Input
{
    public sealed class ControlTokenState
    {
        private readonly object _sync = new object();
        private string _ownerDeviceId;

        public bool TryAcquire(string deviceId)
        {
            lock (_sync)
            {
                if (string.IsNullOrEmpty(_ownerDeviceId) || _ownerDeviceId == deviceId)
                {
                    _ownerDeviceId = deviceId;
                    return true;
                }

                return false;
            }
        }

        public void Release(string deviceId)
        {
            lock (_sync)
            {
                if (_ownerDeviceId == deviceId)
                {
                    _ownerDeviceId = null;
                }
            }
        }

        public string OwnerDeviceId
        {
            get
            {
                lock (_sync)
                {
                    return _ownerDeviceId;
                }
            }
        }
    }
}
