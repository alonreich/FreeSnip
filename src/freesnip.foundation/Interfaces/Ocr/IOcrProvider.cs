using SixLabors.ImageSharp;
using System.Threading;
using System.Threading.Tasks;

namespace freesnip.foundation.interfaces.Ocr
{
    public interface IOcrProvider
    {
        string EngineId { get; }
        string DisplayName { get; }
        bool HasRequiredLanguages();
        Task<OcrInformation> DoOcrAsync(Image image, CancellationToken cancellationToken, bool isAlreadyOwned = false);
        Task<OcrInformation> DoOcrAsync(Image image);
    }
}
