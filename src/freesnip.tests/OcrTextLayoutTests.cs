using System.Collections.Generic;
using freesnip.foundation.interfaces.Ocr;
using freesnip.helpers;
using freesnip.native.foundation;
using Xunit;

namespace freesnip.tests
{
    public class OcrTextLayoutTests
    {
        [Fact]
        public void ContainsHebrew_DetectsHebrewCharacters()
        {
            Assert.True(OcrTextLayout.ContainsHebrew("שלום עולם"));
            Assert.True(OcrTextLayout.ContainsHebrew("Hello עולם"));
            Assert.False(OcrTextLayout.ContainsHebrew("Hello World 123"));
            Assert.False(OcrTextLayout.ContainsHebrew(null!));
            Assert.False(OcrTextLayout.ContainsHebrew(""));
        }

        [Fact]
        public void ContainsLatin_DetectsLatinCharacters()
        {
            Assert.True(OcrTextLayout.ContainsLatin("Hello"));
            Assert.True(OcrTextLayout.ContainsLatin("שלום Hello"));
            Assert.False(OcrTextLayout.ContainsLatin("שלום עולם 123"));
            Assert.False(OcrTextLayout.ContainsLatin(null!));
            Assert.False(OcrTextLayout.ContainsLatin(""));
        }

        [Fact]
        public void SwapParentheses_InvertsBracketTypes()
        {
            Assert.Equal("(abc)", OcrTextLayout.SwapParentheses(")abc("));
            Assert.Equal("[123]", OcrTextLayout.SwapParentheses("]123["));
            Assert.Equal("{xyz}", OcrTextLayout.SwapParentheses("}xyz{"));
            Assert.Equal("<tag>", OcrTextLayout.SwapParentheses(">tag<"));
            Assert.Equal("plain text", OcrTextLayout.SwapParentheses("plain text"));
        }

        [Fact]
        public void BuildVisualSelectionText_HebrewRtlOrdering()
        {
            var word1 = new OcrWord { Text = "עולם", Bounds = RECT.FromXYWH(0, 0, 40, 20) };
            var word2 = new OcrWord { Text = "שלום", Bounds = RECT.FromXYWH(50, 0, 40, 20) };

            var words = new List<OcrWord> { word1, word2 };
            string text = OcrTextLayout.BuildVisualSelectionText(words);

            Assert.Equal("שלום עולם", text);
        }

        [Fact]
        public void BuildVisualSelectionText_EnglishLtrOrdering()
        {
            var word1 = new OcrWord { Text = "World", Bounds = RECT.FromXYWH(60, 0, 40, 20) };
            var word2 = new OcrWord { Text = "Hello", Bounds = RECT.FromXYWH(0, 0, 40, 20) };

            var words = new List<OcrWord> { word1, word2 };
            string text = OcrTextLayout.BuildVisualSelectionText(words);

            Assert.Equal("Hello World", text);
        }

        [Fact]
        public void BuildVisualSelectionText_MultiLineGrouped()
        {
            var w1 = new OcrWord { Text = "LineOne", Bounds = RECT.FromXYWH(0, 10, 50, 20) };
            var w2 = new OcrWord { Text = "LineTwo", Bounds = RECT.FromXYWH(0, 50, 50, 20) };

            var words = new List<OcrWord> { w2, w1 };
            string text = OcrTextLayout.BuildVisualSelectionText(words);

            Assert.Contains("LineOne", text);
            Assert.Contains("LineTwo", text);
            Assert.True(text.IndexOf("LineOne") < text.IndexOf("LineTwo"));
        }

        [Fact]
        public void OcrWord_WithOffset_ReturnsNewInstanceWithoutMutatingOriginal()
        {
            var original = new OcrWord { Text = "Word", Bounds = RECT.FromXYWH(10, 20, 30, 40), Confidence = 0.95f };
            var offset = original.WithOffset(5, 10);

            Assert.NotSame(original, offset);
            Assert.Equal("Word", offset.Text);
            Assert.Equal(15, offset.Bounds.Left);
            Assert.Equal(30, offset.Bounds.Top);
            Assert.Equal(30, offset.Bounds.Width);
            Assert.Equal(40, offset.Bounds.Height);
            Assert.Equal(0.95f, offset.Confidence);

            Assert.Equal(10, original.Bounds.Left);
            Assert.Equal(20, original.Bounds.Top);
        }

        [Fact]
        public void OcrInformation_WithOffset_ReturnsNewInstanceWithoutMutatingOriginal()
        {
            var w1 = new OcrWord { Text = "A", Bounds = RECT.FromXYWH(0, 0, 10, 10) };
            var info = new OcrInformation { Text = "A", Words = new List<OcrWord> { w1 } };

            var offsetInfo = info.WithOffset(20, 30);

            Assert.NotSame(info, offsetInfo);
            Assert.Equal(20, offsetInfo.Words[0].Bounds.Left);
            Assert.Equal(30, offsetInfo.Words[0].Bounds.Top);

            Assert.Equal(0, info.Words[0].Bounds.Left);
            Assert.Equal(0, info.Words[0].Bounds.Top);
        }

        [Fact]
        public void OcrInformation_Offset_HandlesNullGracefully()
        {
            var info = new OcrInformation { Text = "Empty", Words = null! };
            info.Offset(10, 10);

            var infoWithNullWord = new OcrInformation { Text = "Test", Words = new List<OcrWord> { null! } };
            infoWithNullWord.Offset(10, 10);
            Assert.Null(infoWithNullWord.Words[0]);
        }
    }
}

