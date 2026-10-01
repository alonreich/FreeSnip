namespace freesnip.foundation.Interfaces
{
    /// <summary>
    /// Service contract decoupling global application background services lifecycle.
    /// </summary>
    public interface IAppLifecycleCoordinator
    {
        void StartServices();
        void StopServices();
    }
}