using System.Threading.Tasks;

namespace freesnip.foundation.interfaces.Ocr
{
    public interface IOcrResultHandler
    {
        Task HandleOcrResult(string text);
    }
}
