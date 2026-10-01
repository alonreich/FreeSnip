#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using Avalonia.Controls;
using ImageSharpImage = SixLabors.ImageSharp.Image;

namespace freesnip.editor.Services
{
    public sealed class EditorSnapshot
    {
        private ImageSharpImage? _image;

        public ImageSharpImage? Image
        {
            get => Volatile.Read(ref _image);
            init => _image = value;
        }

        public List<Control> Annotations { get; init; } = new List<Control>();

        public ImageSharpImage? TakeImage() => Volatile.Read(ref _image);

        public void ReleaseImage() => Interlocked.Exchange(ref _image, null)?.Dispose();
    }

    public sealed class EditorHistoryManager
    {
        public const int MaxStackSize = 40;
        public const long MaxUndoHistoryBytes = 250L * 1024 * 1024;

        private readonly LinkedList<EditorSnapshot> _undoStack = new LinkedList<EditorSnapshot>();
        private readonly LinkedList<EditorSnapshot> _redoStack = new LinkedList<EditorSnapshot>();
        private readonly Action<Control>? _disposeAnnotation;
        private long _totalBytes;

        public EditorHistoryManager(Action<Control>? disposeAnnotation = null)
        {
            _disposeAnnotation = disposeAnnotation;
        }

        public int UndoCount => _undoStack.Count;
        public int RedoCount => _redoStack.Count;
        public bool CanUndo => _undoStack.Count > 0;
        public bool CanRedo => _redoStack.Count > 0;

        public EditorSnapshot? PeekUndo() => _undoStack.Count > 0 ? _undoStack.Last!.Value : null;
        public EditorSnapshot? PeekRedo() => _redoStack.Count > 0 ? _redoStack.Last!.Value : null;

        public void PushUndo(EditorSnapshot snapshot)
        {
            _undoStack.AddLast(snapshot);
            Interlocked.Add(ref _totalBytes, EstimateSnapshotBytes(snapshot));

            if (_undoStack.Count > MaxStackSize)
            {
                var oldest = _undoStack.First!.Value;
                _undoStack.RemoveFirst();
                Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(oldest));
                DisposeSnapshot(oldest);
            }
            ClearRedo();
            EnforceMemoryBudget();
        }

        public bool TryUndo(EditorSnapshot currentSnapshot, out EditorSnapshot? previousSnapshot)
        {
            if (_undoStack.Count == 0)
            {
                previousSnapshot = null;
                return false;
            }

            previousSnapshot = _undoStack.Last!.Value;
            _undoStack.RemoveLast();
            Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(previousSnapshot));

            _redoStack.AddLast(currentSnapshot);
            Interlocked.Add(ref _totalBytes, EstimateSnapshotBytes(currentSnapshot));
            if (_redoStack.Count > MaxStackSize)
            {
                var oldest = _redoStack.First!.Value;
                _redoStack.RemoveFirst();
                Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(oldest));
                DisposeSnapshot(oldest);
            }

            EnforceMemoryBudget();
            return true;
        }

        public bool TryRedo(EditorSnapshot currentSnapshot, out EditorSnapshot? nextSnapshot)
        {
            if (_redoStack.Count == 0)
            {
                nextSnapshot = null;
                return false;
            }

            nextSnapshot = _redoStack.Last!.Value;
            _redoStack.RemoveLast();
            Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(nextSnapshot));

            _undoStack.AddLast(currentSnapshot);
            Interlocked.Add(ref _totalBytes, EstimateSnapshotBytes(currentSnapshot));
            if (_undoStack.Count > MaxStackSize)
            {
                var oldest = _undoStack.First!.Value;
                _undoStack.RemoveFirst();
                Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(oldest));
                DisposeSnapshot(oldest);
            }

            EnforceMemoryBudget();
            return true;
        }

        public void ClearRedo()
        {
            foreach (var snapshot in _redoStack)
            {
                Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(snapshot));
                DisposeSnapshot(snapshot);
            }
            _redoStack.Clear();
        }

        public void Clear()
        {
            foreach (var snapshot in _undoStack)
            {
                Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(snapshot));
                DisposeSnapshot(snapshot);
            }
            _undoStack.Clear();

            ClearRedo();
            Interlocked.Exchange(ref _totalBytes, 0L);
        }

        public static long EstimateSnapshotBytes(EditorSnapshot? snapshot)
        {
            var image = snapshot?.Image;
            return image != null ? (long)image.Width * image.Height * 4 : 0;
        }

        public long TotalUndoHistoryBytes() => Math.Max(0L, Interlocked.Read(ref _totalBytes));

        public void EnforceMemoryBudget()
        {
            while (TotalUndoHistoryBytes() > MaxUndoHistoryBytes)
            {
                if (_redoStack.Count > 0)
                {
                    var oldestRedo = _redoStack.First!.Value;
                    _redoStack.RemoveFirst();
                    Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(oldestRedo));
                    DisposeSnapshot(oldestRedo);
                }
                else if (_undoStack.Count > 1)
                {
                    var oldestUndo = _undoStack.First!.Value;
                    _undoStack.RemoveFirst();
                    Interlocked.Add(ref _totalBytes, -EstimateSnapshotBytes(oldestUndo));
                    DisposeSnapshot(oldestUndo);
                }
                else
                {
                    break;
                }
            }
        }

        public void EnforceUndoMemoryBudget() => EnforceMemoryBudget();

        public void DisposeSnapshot(EditorSnapshot? snapshot)
        {
            if (snapshot == null) return;
            snapshot.ReleaseImage();
            if (snapshot.Annotations != null)
            {
                foreach (var annotation in snapshot.Annotations)
                {
                    _disposeAnnotation?.Invoke(annotation);
                }
                snapshot.Annotations.Clear();
            }
        }
    }
}
