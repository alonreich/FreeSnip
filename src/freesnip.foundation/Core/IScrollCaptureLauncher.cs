using Avalonia.Controls;
using System.Threading.Tasks;

namespace freesnip.foundation.core
{
    public interface IScrollCaptureLauncher
    {
        Task StartAsync(Window ownerWindow = null);
    }
}
