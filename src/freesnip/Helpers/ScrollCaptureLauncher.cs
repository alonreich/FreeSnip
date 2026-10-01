using System.Threading.Tasks;
using Avalonia.Controls;
using freesnip.forms;
using freesnip.foundation.core;

namespace freesnip.helpers
{
    internal sealed class ScrollCaptureLauncher : IScrollCaptureLauncher
    {
        public Task StartAsync(Window ownerWindow = null)
        {
            return ScrollCaptureWindow.StartAsync(ownerWindow);
        }
    }
}
