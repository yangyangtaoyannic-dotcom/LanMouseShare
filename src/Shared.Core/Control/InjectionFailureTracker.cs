using System;

namespace LanMouseShare.Core.Control
{
    public sealed class InjectionFailureTracker
    {
        private readonly int _threshold;
        private readonly TimeSpan _window;
        private int _count;
        private DateTime _firstFailureUtc = DateTime.MinValue;

        public InjectionFailureTracker()
            : this(3, TimeSpan.FromSeconds(2))
        {
        }

        public InjectionFailureTracker(int threshold, TimeSpan window)
        {
            _threshold = threshold <= 0 ? 1 : threshold;
            _window = window <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : window;
        }

        public bool RegisterFailure(DateTime utcNow)
        {
            if (_firstFailureUtc == DateTime.MinValue || utcNow - _firstFailureUtc > _window)
            {
                _firstFailureUtc = utcNow;
                _count = 1;
            }
            else
            {
                _count++;
            }

            return _count >= _threshold;
        }

        public void Reset()
        {
            _count = 0;
            _firstFailureUtc = DateTime.MinValue;
        }

        public int Count
        {
            get { return _count; }
        }
    }
}
