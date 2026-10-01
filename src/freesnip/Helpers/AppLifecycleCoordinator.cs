using freesnip.foundation.core;
using freesnip.foundation.Interfaces;

namespace freesnip.helpers
{
    public class AppLifecycleCoordinator : IAppLifecycleCoordinator
    {
        public void StartServices()
        {
            ExecutionTrace.Start();
            RetentionHelper.Start();
            HotkeyManager.Start();
        }

        public void StopServices()
        {
            RetentionHelper.Stop();
            ExecutionTrace.Stop();
            HotkeyManager.Stop();
        }
    }
}