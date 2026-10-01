#nullable enable
using System;
using Avalonia;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace freesnip.editor.Services
{
    public class EditorDocument : IDisposable
    {
        private Image? _image;
        private readonly object _syncRoot = new();

        public object SyncRoot => _syncRoot;

        public Image? Image
        {
            get
            {
                lock (_syncRoot)
                {
                    return _image;
                }
            }
        }

        public bool HasImage
        {
            get
            {
                lock (_syncRoot)
                {
                    return _image != null;
                }
            }
        }

        public int Width
        {
            get
            {
                lock (_syncRoot)
                {
                    return _image?.Width ?? 0;
                }
            }
        }

        public int Height
        {
            get
            {
                lock (_syncRoot)
                {
                    return _image?.Height ?? 0;
                }
            }
        }

        public Avalonia.Size Size
        {
            get
            {
                lock (_syncRoot)
                {
                    return _image != null ? new Avalonia.Size(_image.Width, _image.Height) : new Avalonia.Size(0, 0);
                }
            }
        }

        public bool TryGetImageSize(out int width, out int height)
        {
            lock (_syncRoot)
            {
                if (_image != null)
                {
                    width = _image.Width;
                    height = _image.Height;
                    return true;
                }
                width = 0;
                height = 0;
                return false;
            }
        }

        public void SetImage(Image? newImage)
        {
            Image? previous;
            lock (_syncRoot)
            {
                previous = _image;
                _image = newImage;
            }
            previous?.Dispose();
        }

        public T? WithImage<T>(Func<Image, T> work) where T : class
        {
            lock (_syncRoot)
            {
                return _image == null ? null : work(_image);
            }
        }

        public bool MutateImage(Action<Image> work)
        {
            lock (_syncRoot)
            {
                if (_image == null) return false;
                work(_image);
                return true;
            }
        }

        public void Clear()
        {
            SetImage(null);
        }

        public void Dispose()
        {
            Clear();
        }
    }
}
