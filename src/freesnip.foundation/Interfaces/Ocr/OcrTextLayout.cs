using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using freesnip.native.foundation;

namespace freesnip.helpers
{
    public static class OcrTextLayout
    {
        private const char HebrewStart = (char)0x0590;
        private const char HebrewEnd = (char)0x05FF;

        public static bool ContainsHebrew(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Any(c => c >= HebrewStart && c <= HebrewEnd);
        }

        public static bool ContainsLatin(string text)
        {
            return !string.IsNullOrEmpty(text) && text.Any(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
        }

        public static string BuildVisualSelectionText(IEnumerable<freesnip.foundation.interfaces.Ocr.OcrWord> words)
        {
            var lines = BuildLines(words);
            if (lines.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var line in lines)
            {
                string lineText = BuildLineText(line);
                if (string.IsNullOrWhiteSpace(lineText))
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                }

                builder.Append(lineText.Normalize(NormalizationForm.FormC));
            }

            return builder.ToString().Trim();
        }

        public static freesnip.foundation.interfaces.Ocr.OcrInformation MergeByLanguage(freesnip.foundation.interfaces.Ocr.OcrInformation hebrewSource, freesnip.foundation.interfaces.Ocr.OcrInformation englishSource)
        {
            if (HasNoWords(hebrewSource) && HasNoWords(englishSource))
            {
                return englishSource ?? hebrewSource;
            }

            if (HasNoWords(hebrewSource))
            {
                NormalizeTextFromWordsWhenEmpty(englishSource);
                return englishSource;
            }

            if (HasNoWords(englishSource))
            {
                NormalizeTextFromWordsWhenEmpty(hebrewSource);
                return hebrewSource;
            }

            bool hasHebrew = hebrewSource.Words.Any(word => ContainsHebrew(word.Text));
            bool primaryCarriesConfidence = hebrewSource.Words.Any(word => word != null && word.HasConfidence);
            if (!hasHebrew && !primaryCarriesConfidence)
            {
                NormalizeTextFromWordsWhenEmpty(englishSource);
                return englishSource;
            }

            bool hasEnglish = englishSource.Words.Any(word => ContainsLatin(word.Text));
            if (!hasEnglish)
            {
                NormalizeTextFromWordsWhenEmpty(hebrewSource);
                return hebrewSource;
            }

            var mergedWords = new List<freesnip.foundation.interfaces.Ocr.OcrWord>();
            var supersededByConfidence = new HashSet<freesnip.foundation.interfaces.Ocr.OcrWord>();

            foreach (var word in hebrewSource.Words)
            {
                if (ContainsHebrew(word.Text))
                {
                    mergedWords.Add(word);
                    continue;
                }

                var rival = FindOverlap(word.Bounds, englishSource.Words);
                if (rival == null)
                {
                    mergedWords.Add(word);
                    continue;
                }

                if (WinsOnConfidence(word, rival))
                {
                    supersededByConfidence.Add(rival);
                    mergedWords.Add(word);
                }
            }

            foreach (var word in englishSource.Words)
            {
                if (!ContainsHebrew(word.Text) && !supersededByConfidence.Contains(word))
                {
                    mergedWords.Add(word);
                }
            }

            SanitizeOcrWords(mergedWords);

            var result = new freesnip.foundation.interfaces.Ocr.OcrInformation { Words = mergedWords };
            result.Text = BuildVisualSelectionText(mergedWords);
            return result;
        }

        public static void NormalizeTextFromWordsWhenEmpty(freesnip.foundation.interfaces.Ocr.OcrInformation information)
        {
            if (information == null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(information.Text) && information.Words != null && information.Words.Count > 0)
            {
                information.Text = BuildVisualSelectionText(information.Words);
            }
            else if (information.Text != null)
            {
                information.Text = information.Text.Normalize(NormalizationForm.FormC);
            }
        }

        private static void SanitizeOcrWords(List<freesnip.foundation.interfaces.Ocr.OcrWord> words)
        {
            if (words == null) return;
            for (int i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (word == null || string.IsNullOrWhiteSpace(word.Text)) continue;
                int letters = word.Text.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'));
                int digits = word.Text.Count(c => c >= '0' && c <= '9');
                if (letters == 0 || digits == 0) continue;

                if (letters > digits)
                {
                    words[i] = word with { Text = word.Text.Replace('0', 'O').Replace('1', 'l').Replace('5', 'S') };
                }
                else if (digits > letters)
                {
                    words[i] = word with { Text = word.Text.Replace('O', '0').Replace('o', '0').Replace('l', '1').Replace('I', '1').Replace('S', '5').Replace('s', '5').Replace('Z', '2').Replace('z', '2') };
                }
            }
        }

        private static bool HasNoWords(freesnip.foundation.interfaces.Ocr.OcrInformation information)
        {
            return information?.Words == null || information.Words.Count == 0;
        }

        private static List<List<freesnip.foundation.interfaces.Ocr.OcrWord>> BuildLines(IEnumerable<freesnip.foundation.interfaces.Ocr.OcrWord> words)
        {
            var normalizedWords = words?
                .Where(word => word != null && !string.IsNullOrWhiteSpace(word.Text) && word.Bounds.Width > 0 && word.Bounds.Height > 0)
                .OrderBy(word => word.Bounds.Top)
                .ThenBy(word => word.Bounds.Left)
                .ToList() ?? new List<freesnip.foundation.interfaces.Ocr.OcrWord>();

            var lines = new List<List<freesnip.foundation.interfaces.Ocr.OcrWord>>();
            foreach (var word in normalizedWords)
            {
                int wordCenterY = word.Bounds.Top + word.Bounds.Height / 2;
                List<freesnip.foundation.interfaces.Ocr.OcrWord> target = null;
                int bestDelta = int.MaxValue;

                foreach (var line in lines)
                {
                    int lineCenterY = (int)line.Average(existing => existing.Bounds.Top + existing.Bounds.Height / 2);
                    int tolerance = Math.Max(6, (int)line.Average(existing => Math.Max(1, existing.Bounds.Height)) / 2);
                    int delta = Math.Abs(lineCenterY - wordCenterY);
                    if (delta <= tolerance && delta < bestDelta)
                    {
                        bestDelta = delta;
                        target = line;
                    }
                }

                if (target == null)
                {
                    lines.Add(new List<freesnip.foundation.interfaces.Ocr.OcrWord> { word });
                }
                else
                {
                    target.Add(word);
                }
            }

            return lines.OrderBy(line => line.Min(word => word.Bounds.Top)).ToList();
        }

        private static string BuildLineText(List<freesnip.foundation.interfaces.Ocr.OcrWord> line)
        {
            if (line == null || line.Count == 0)
            {
                return string.Empty;
            }

            int hebrewChars = line.Sum(word => word.Text.Count(c => c >= HebrewStart && c <= HebrewEnd));
            int latinChars = line.Sum(word => word.Text.Count(c => (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z')));
            bool rtlDominant = hebrewChars > latinChars;
            var visual = line.OrderBy(word => word.Bounds.Left).ToList();
            var runs = BuildDirectionalRuns(visual);

            if (rtlDominant)
            {
                runs.Reverse();
            }

            var ordered = new List<freesnip.foundation.interfaces.Ocr.OcrWord>();
            foreach (var run in runs)
            {
                bool runIsHebrew = run.Any(word => ContainsHebrew(word.Text));
                IEnumerable<freesnip.foundation.interfaces.Ocr.OcrWord> orderedRun = runIsHebrew ? run.OrderByDescending(word => word.Bounds.Left) : run.OrderBy(word => word.Bounds.Left);
                if (rtlDominant && !runIsHebrew)
                {
                    orderedRun = run.OrderBy(word => word.Bounds.Left);
                }

                foreach (var word in orderedRun)
                {
                    var finalWord = word;
                    if (runIsHebrew && word != null)
                    {
                        finalWord = word with { Text = SwapParentheses(word.Text) };
                    }
                    ordered.Add(finalWord);
                }
            }

            return string.Join(" ", ordered.Select(word => word.Text.Trim())).Trim();
        }

        public static string SwapParentheses(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            char[] chars = text.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (chars[i] == '(') chars[i] = ')';
                else if (chars[i] == ')') chars[i] = '(';
                else if (chars[i] == '[') chars[i] = ']';
                else if (chars[i] == ']') chars[i] = '[';
                else if (chars[i] == '{') chars[i] = '}';
                else if (chars[i] == '}') chars[i] = '{';
                else if (chars[i] == '<') chars[i] = '>';
                else if (chars[i] == '>') chars[i] = '<';
            }
            return new string(chars);
        }

        private static List<List<freesnip.foundation.interfaces.Ocr.OcrWord>> BuildDirectionalRuns(List<freesnip.foundation.interfaces.Ocr.OcrWord> visual)
        {
            var runs = new List<List<freesnip.foundation.interfaces.Ocr.OcrWord>>();
            foreach (var word in visual)
            {
                bool isHebrew = ContainsHebrew(word.Text);
                if (runs.Count == 0)
                {
                    runs.Add(new List<freesnip.foundation.interfaces.Ocr.OcrWord> { word });
                    continue;
                }

                var lastRun = runs[^1];
                bool lastHebrew = lastRun.Any(existing => ContainsHebrew(existing.Text));
                if (lastHebrew == isHebrew)
                {
                    lastRun.Add(word);
                }
                else
                {
                    runs.Add(new List<freesnip.foundation.interfaces.Ocr.OcrWord> { word });
                }
            }

            return runs;
        }

        private const float ConfidenceOverrideThreshold = 0.80f;

        private static bool WinsOnConfidence(freesnip.foundation.interfaces.Ocr.OcrWord challenger, freesnip.foundation.interfaces.Ocr.OcrWord incumbent)
        {
            if (challenger == null || incumbent == null) return false;
            if (!challenger.HasConfidence) return false;
            if (challenger.Confidence < ConfidenceOverrideThreshold) return false;

            string a = (challenger.Text ?? string.Empty).Trim();
            string b = (incumbent.Text ?? string.Empty).Trim();
            if (string.Equals(a, b, StringComparison.Ordinal)) return false;

            if (incumbent.HasConfidence) return challenger.Confidence > incumbent.Confidence;
            return true;
        }

        private static freesnip.foundation.interfaces.Ocr.OcrWord FindOverlap(RECT bounds, IEnumerable<freesnip.foundation.interfaces.Ocr.OcrWord> words)
        {
            foreach (var word in words)
            {
                if (word == null) continue;
                if (IntersectionRatio(bounds, word.Bounds) >= 0.35) return word;
            }

            return null;
        }

        private static double IntersectionRatio(RECT a, RECT b)
        {
            int left = Math.Max(a.Left, b.Left);
            int top = Math.Max(a.Top, b.Top);
            int right = Math.Min(a.Right, b.Right);
            int bottom = Math.Min(a.Bottom, b.Bottom);
            if (right <= left || bottom <= top)
            {
                return 0;
            }

            double area = (right - left) * (bottom - top);
            double minArea = Math.Max(1, Math.Min(a.Width * a.Height, b.Width * b.Height));
            return area / minArea;
        }
    }
}
