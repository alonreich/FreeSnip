using log4net;
using System;
using System.Threading;

namespace freesnip.helpers
{
    public class ResourceMutex : IDisposable
    {
        public const string SingleInstanceArbitratorName = @"Global\FreeSnip_SingleInstance_Arbitrator";
        public const string LegacySingleInstanceArbitratorName = @"Global\SnapVox_SingleInstance_Arbitrator";

        private static readonly ILog Log = freesnip.foundation.core.LogHelper.GetLogger(typeof(ResourceMutex));
        private readonly string _mutexId;
        private readonly string _resourceName;
        private Semaphore _semaphore;
        private int _isLocked;
        private int _disposedValue;

        private ResourceMutex(string mutexId, string resourceName = null)
        {
            _mutexId = mutexId;
            _resourceName = resourceName ?? "some resource";
        }

        public bool IsLocked
        {
            get => Volatile.Read(ref _isLocked) == 1;
            private set => Volatile.Write(ref _isLocked, value ? 1 : 0);
        }

        public static ResourceMutex Create(string mutexId, string resourceName = null, bool global = false)
        {
            string formattedId;
            if (mutexId.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase) ||
                mutexId.StartsWith(@"Local\", StringComparison.OrdinalIgnoreCase))
            {
                formattedId = mutexId;
            }
            else
            {
                formattedId = (global ? @"Global\" : @"Local\") + mutexId;
            }

            var applicationMutex = new ResourceMutex(formattedId, resourceName);
            applicationMutex.Lock();
            return applicationMutex;
        }

        public bool Lock()
        {
            Log.DebugFormat("{0} is trying to get Semaphore {1}", _resourceName, _mutexId);

            try
            {
                _semaphore = new Semaphore(0, 1, _mutexId, out bool createdNew);
                if (createdNew)
                {
                    IsLocked = true;
                    Log.InfoFormat("{0} has created & claimed the semaphore {1}", _resourceName, _mutexId);
                }
                else if (_semaphore.WaitOne(100, false))
                {
                    IsLocked = true;
                    Log.InfoFormat("{0} has claimed the semaphore {1}", _resourceName, _mutexId);
                }
                else
                {
                    Log.InfoFormat("{0} is already in use, semaphore {1} is NOT locked for the caller", _resourceName, _mutexId);
                    IsLocked = false;
                    _semaphore.Dispose();
                    _semaphore = null;
                }
            }
            catch (UnauthorizedAccessException e)
            {
                Log.ErrorFormat("{0} is most likely already running for a different user in the same session, can't create/get semaphore {1} due to error.", _resourceName, _mutexId);
                Log.Error(e);
                IsLocked = false;
                _semaphore?.Dispose();
                _semaphore = null;
            }
            catch (Exception ex)
            {
                Log.ErrorFormat("Problem obtaining the Semaphore {1} for {0}, assuming it was already taken!", _resourceName, _mutexId);
                Log.Error(ex);
                IsLocked = false;
                _semaphore?.Dispose();
                _semaphore = null;
            }

            return IsLocked;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposedValue, 1) != 0)
                return;

            if (_semaphore != null)
            {
                try
                {
                    if (disposing && Interlocked.Exchange(ref _isLocked, 0) == 1)
                    {
                        try
                        {
                            _semaphore.Release();
                            Log.InfoFormat("Released Semaphore {0} for {1}", _mutexId, _resourceName);
                        }
                        catch (SemaphoreFullException ex)
                        {
                            Log.WarnFormat("Semaphore {0} for {1} was already at full count during release: {2}", _mutexId, _resourceName, ex.Message);
                        }
                    }

                    if (disposing)
                    {
                        _semaphore.Dispose();
                        _semaphore = null;
                    }
                }
                catch (Exception ex)
                {
                    if (disposing)
                    {
                        Log.ErrorFormat("Error releasing Semaphore {0} for {1}", _mutexId, _resourceName);
                        Log.Error(ex);
                    }
                }
            }
        }

        ~ResourceMutex()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
