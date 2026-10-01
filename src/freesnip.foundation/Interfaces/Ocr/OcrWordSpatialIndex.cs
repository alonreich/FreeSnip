using System;
using System.Collections.Generic;
using freesnip.native.foundation;

namespace freesnip.helpers
{
    public sealed class OcrWordSpatialIndex
    {
        private const int CellSize = 96;
        private readonly Dictionary<long, List<IndexedWord>> _cells;
        private readonly List<IndexedWord> _words;

        private OcrWordSpatialIndex(List<IndexedWord> words, Dictionary<long, List<IndexedWord>> cells)
        {
            _words = words;
            _cells = cells;
        }

        public static OcrWordSpatialIndex Create(IReadOnlyList<freesnip.foundation.interfaces.Ocr.OcrWord> words)
        {
            var indexed = new List<IndexedWord>();
            var cells = new Dictionary<long, List<IndexedWord>>();
            if (words == null)
            {
                return new OcrWordSpatialIndex(indexed, cells);
            }

            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (word == null || word.Bounds.Width <= 0 || word.Bounds.Height <= 0)
                {
                    continue;
                }

                var indexedWord = new IndexedWord(i, word);
                indexed.Add(indexedWord);
                int left = FloorCell(word.Bounds.Left);
                int right = FloorCell(word.Bounds.Right);
                int top = FloorCell(word.Bounds.Top);
                int bottom = FloorCell(word.Bounds.Bottom);
                for (int y = top; y <= bottom; y++)
                {
                    for (int x = left; x <= right; x++)
                    {
                        long key = Key(x, y);
                        if (!cells.TryGetValue(key, out var list))
                        {
                            list = new List<IndexedWord>();
                            cells.Add(key, list);
                        }

                        list.Add(indexedWord);
                    }
                }
            }

            return new OcrWordSpatialIndex(indexed, cells);
        }

        public freesnip.foundation.interfaces.Ocr.OcrWord FindContaining(POINT point)
        {
            return FindContainingIndexed(point)?.Word;
        }

        public int FindClosestIndex(int x, int y, double maxDistanceSquared)
        {
            IndexedWord best = null;
            double bestDistance = maxDistanceSquared;
            int cellX = FloorCell(x);
            int cellY = FloorCell(y);
            for (int radius = 0; radius <= 2; radius++)
            {
                for (int cy = cellY - radius; cy <= cellY + radius; cy++)
                {
                    for (int cx = cellX - radius; cx <= cellX + radius; cx++)
                    {
                        if (Math.Abs(cx - cellX) != radius && Math.Abs(cy - cellY) != radius)
                        {
                            continue;
                        }

                        if (!_cells.TryGetValue(Key(cx, cy), out var words))
                        {
                            continue;
                        }

                        foreach (var word in words)
                        {
                            if (word.Word.Bounds.Contains(x, y))
                            {
                                return word.Index;
                            }

                            double distance = DistanceToCenter(word.Word.Bounds, x, y);
                            if (distance < bestDistance)
                            {
                                bestDistance = distance;
                                best = word;
                            }
                        }
                    }
                }

                if (best != null)
                {
                    return best.Index;
                }
            }

            foreach (var word in _words)
            {
                double distance = DistanceToCenter(word.Word.Bounds, x, y);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = word;
                }
            }

            return best?.Index ?? -1;
        }

        private IndexedWord FindContainingIndexed(POINT point)
        {
            if (!_cells.TryGetValue(Key(FloorCell(point.X), FloorCell(point.Y)), out var words))
            {
                return null;
            }

            foreach (var word in words)
            {
                if (word.Word.Bounds.Contains(point))
                {
                    return word;
                }
            }

            return null;
        }

        private static double DistanceToCenter(RECT bounds, int x, int y)
        {
            double cx = bounds.Left + bounds.Width / 2.0;
            double cy = bounds.Top + bounds.Height / 2.0;
            double dx = cx - x;
            double dy = cy - y;
            return dx * dx + dy * dy;
        }

        private static int FloorCell(int value)
        {
            return value >= 0 ? value / CellSize : ((value + 1) / CellSize) - 1;
        }

        private static long Key(int x, int y)
        {
            return ((long)x << 32) ^ (uint)y;
        }

        private sealed class IndexedWord
        {
            public IndexedWord(int index, freesnip.foundation.interfaces.Ocr.OcrWord word)
            {
                Index = index;
                Word = word;
            }

            public int Index { get; }
            public freesnip.foundation.interfaces.Ocr.OcrWord Word { get; }
        }
    }
}
