using System.Collections.Generic;
using freesnip.native.foundation;

namespace freesnip.foundation.interfaces.Ocr
{
    public record class OcrWord
    {
        public const float UnknownConfidence = -1f;

        public string Text { get; init; } = string.Empty;
        public RECT Bounds { get; init; }
        public float Confidence { get; init; } = UnknownConfidence;
        public bool HasConfidence => Confidence >= 0f;

        public OcrWord WithOffset(int x, int y) => this with { Bounds = Bounds.Offset(x, y) };
    }

    public record class OcrInformation
    {
        public string Text { get; set; } = string.Empty;
        public List<OcrWord> Words { get; init; } = new List<OcrWord>();

        /// <summary>
        /// Pure transformation returning a new OcrInformation instance with all word bounds offset.
        /// </summary>
        public OcrInformation WithOffset(int x, int y)
        {
            if (Words == null) return this with { };
            var newWords = new List<OcrWord>(Words.Count);
            for (int i = 0; i < Words.Count; i++)
            {
                var word = Words[i];
                newWords.Add(word != null ? word.WithOffset(x, y) : null!);
            }
            return this with { Words = newWords };
        }

        /// <summary>
        /// Null-safe offset for backwards compatibility; replaces internal elements with offset instances.
        /// </summary>
        public void Offset(int x, int y)
        {
            if (Words == null) return;
            for (int i = 0; i < Words.Count; i++)
            {
                if (Words[i] != null)
                {
                    Words[i] = Words[i].WithOffset(x, y);
                }
            }
        }
    }
}
