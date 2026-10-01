#nullable enable
using System;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using freesnip.editor.Services;
using Xunit;

namespace freesnip.tests
{
    public class EditorDocumentTests
    {
        [Fact]
        public void InitialState_IsEmpty()
        {
            using var doc = new EditorDocument();
            Assert.Null(doc.Image);
            Assert.False(doc.HasImage);
            Assert.Equal(0, doc.Width);
            Assert.Equal(0, doc.Height);
            Assert.Equal(0, doc.Size.Width);
            Assert.Equal(0, doc.Size.Height);
            Assert.False(doc.TryGetImageSize(out var w, out var h));
            Assert.Equal(0, w);
            Assert.Equal(0, h);
        }

        [Fact]
        public void SetImage_UpdatesDimensionsAndDisposesPrevious()
        {
            using var doc = new EditorDocument();
            var img1 = new Image<Rgba32>(100, 200);
            doc.SetImage(img1);

            Assert.True(doc.HasImage);
            Assert.Equal(100, doc.Width);
            Assert.Equal(200, doc.Height);
            Assert.True(doc.TryGetImageSize(out var w, out var h));
            Assert.Equal(100, w);
            Assert.Equal(200, h);

            var img2 = new Image<Rgba32>(300, 400);
            doc.SetImage(img2);

            Assert.Equal(300, doc.Width);
            Assert.Equal(400, doc.Height);
            Assert.Throws<ObjectDisposedException>(() => img1.CloneAs<Rgba32>());
        }

        [Fact]
        public void WithImage_ExecutesWhenImagePresent()
        {
            using var doc = new EditorDocument();
            Assert.Null(doc.WithImage(img => img.Width.ToString()));

            using var img = new Image<Rgba32>(50, 60);
            doc.SetImage(img);

            var result = doc.WithImage(i => $"{i.Width}x{i.Height}");
            Assert.Equal("50x60", result);
        }

        [Fact]
        public void MutateImage_ModifiesUnderLock()
        {
            using var doc = new EditorDocument();
            Assert.False(doc.MutateImage(img => { }));

            using var img = new Image<Rgba32>(10, 10);
            doc.SetImage(img);

            bool called = false;
            bool success = doc.MutateImage(i =>
            {
                called = true;
                Assert.Equal(10, i.Width);
            });

            Assert.True(success);
            Assert.True(called);
        }

        [Fact]
        public void ClearAndDispose_ResetsDocument()
        {
            var doc = new EditorDocument();
            var img = new Image<Rgba32>(20, 20);
            doc.SetImage(img);

            doc.Clear();
            Assert.False(doc.HasImage);
            Assert.Null(doc.Image);
            Assert.Throws<ObjectDisposedException>(() => img.CloneAs<Rgba32>());

            doc.Dispose();
            Assert.False(doc.HasImage);
        }

        [Fact]
        public void ConcurrentAccess_ThreadSafe()
        {
            using var doc = new EditorDocument();
            using var img = new Image<Rgba32>(100, 100);
            doc.SetImage(img);

            Parallel.For(0, 100, i =>
            {
                Assert.True(doc.HasImage);
                Assert.Equal(100, doc.Width);
                doc.WithImage(im => im.Bounds().ToString());
            });
        }
    }
}

