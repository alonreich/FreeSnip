using System;
using System.Collections.Generic;
using Avalonia.Controls;
using freesnip.editor.Services;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace freesnip.tests
{
    public class EditorHistoryManagerTests
    {
        [Fact]
        public void InitialState_IsEmpty()
        {
            var manager = new EditorHistoryManager();

            Assert.Equal(0, manager.UndoCount);
            Assert.Equal(0, manager.RedoCount);
            Assert.False(manager.CanUndo);
            Assert.False(manager.CanRedo);
            Assert.Equal(0, manager.TotalUndoHistoryBytes());
        }

        [Fact]
        public void PushUndo_AddsSnapshot_ClearsRedo()
        {
            var manager = new EditorHistoryManager();
            var snapshot1 = new EditorSnapshot();
            var snapshot2 = new EditorSnapshot();

            manager.PushUndo(snapshot1);
            Assert.Equal(1, manager.UndoCount);
            Assert.True(manager.CanUndo);

            manager.TryUndo(new EditorSnapshot(), out _);
            Assert.Equal(1, manager.RedoCount);

            manager.PushUndo(snapshot2);
            Assert.Equal(0, manager.RedoCount);
            Assert.False(manager.CanRedo);
        }

        [Fact]
        public void PushUndo_EnforcesMaxStackSize()
        {
            var manager = new EditorHistoryManager();

            for (int i = 0; i < EditorHistoryManager.MaxStackSize + 10; i++)
            {
                manager.PushUndo(new EditorSnapshot());
            }

            Assert.Equal(EditorHistoryManager.MaxStackSize, manager.UndoCount);
        }

        [Fact]
        public void UndoRedo_Cycle_RestoresSnapshotsCorrectly()
        {
            var manager = new EditorHistoryManager();
            var snapshot1 = new EditorSnapshot();
            var snapshot2 = new EditorSnapshot();

            manager.PushUndo(snapshot1);
            manager.PushUndo(snapshot2);

            Assert.Equal(2, manager.UndoCount);

            var current = new EditorSnapshot();
            bool undid = manager.TryUndo(current, out var restoredUndo);

            Assert.True(undid);
            Assert.NotNull(restoredUndo);
            Assert.Same(snapshot2, restoredUndo);
            Assert.Equal(1, manager.UndoCount);
            Assert.Equal(1, manager.RedoCount);

            bool redid = manager.TryRedo(restoredUndo!, out var restoredRedo);
            Assert.True(redid);
            Assert.Same(current, restoredRedo);
            Assert.Equal(2, manager.UndoCount);
            Assert.Equal(0, manager.RedoCount);
        }

        [Fact]
        public void EnforceUndoMemoryBudget_TrimsOldestWhenExceedingCeiling()
        {
            var manager = new EditorHistoryManager();

            using var img1 = new Image<Rgba32>(1000, 1000);
            using var img2 = new Image<Rgba32>(1000, 1000);

            var snap1 = new EditorSnapshot { Image = img1.Clone() };
            var snap2 = new EditorSnapshot { Image = img2.Clone() };

            long bytes1 = EditorHistoryManager.EstimateSnapshotBytes(snap1);
            Assert.Equal(4_000_000L, bytes1);

            manager.PushUndo(snap1);
            manager.PushUndo(snap2);

            Assert.Equal(8_000_000L, manager.TotalUndoHistoryBytes());
            Assert.Equal(2, manager.UndoCount);
        }

        [Fact]
        public void Clear_DisposesAndEmptiesBothStacks()
        {
            int disposedCount = 0;
            var manager = new EditorHistoryManager(control => disposedCount++);

            var ctrl1 = new Canvas();
            var snap1 = new EditorSnapshot();
            snap1.Annotations.Add(ctrl1);

            manager.PushUndo(snap1);
            Assert.Equal(1, manager.UndoCount);

            manager.Clear();
            Assert.Equal(0, manager.UndoCount);
            Assert.Equal(0, manager.RedoCount);
            Assert.Equal(1, disposedCount);
        }

        [Fact]
        public void VectorOperations_DoNotAccumulateRasterMemory()
        {
            var manager = new EditorHistoryManager();

            for (int i = 0; i < 40; i++)
            {
                var vectorSnapshot = new EditorSnapshot
                {
                    Image = null,
                    Annotations = new List<Control>
                    {
                        new Avalonia.Controls.Shapes.Rectangle { Width = 100 + i, Height = 50 }
                    }
                };
                manager.PushUndo(vectorSnapshot);
            }

            Assert.Equal(40, manager.UndoCount);
            Assert.Equal(0L, manager.TotalUndoHistoryBytes());
            Assert.True(manager.TotalUndoHistoryBytes() < 50L * 1024 * 1024);
        }

        [Fact]
        public void NonDestructive_SnapshotRestoration_PreservesRepeatedUndoRedoFidelity()
        {
            var manager = new EditorHistoryManager();

            using var baseImg = new Image<Rgba32>(100, 100);
            var snap1 = new EditorSnapshot
            {
                Image = baseImg.Clone(),
                Annotations = new List<Control>
                {
                    new Canvas { Width = 10, Height = 10 },
                    new Canvas { Width = 20, Height = 20 }
                }
            };

            manager.PushUndo(snap1);

            var current = new EditorSnapshot
            {
                Image = null,
                Annotations = new List<Control>
                {
                    new Canvas { Width = 30, Height = 30 }
                }
            };

            for (int cycle = 0; cycle < 10; cycle++)
            {
                Assert.NotNull(manager.PeekUndo()?.Image);

                bool undid = manager.TryUndo(current, out var restoredUndo);
                Assert.True(undid);
                Assert.NotNull(restoredUndo);

                Assert.NotNull(restoredUndo.Image);
                Assert.NotNull(restoredUndo.TakeImage());
                Assert.Equal(2, restoredUndo.Annotations.Count);

                Assert.Same(current, manager.PeekRedo());

                bool redid = manager.TryRedo(restoredUndo, out var restoredRedo);
                Assert.True(redid);
                Assert.NotNull(restoredRedo);
                Assert.Same(current, restoredRedo);
                Assert.Single(restoredRedo.Annotations);
            }

            Assert.NotNull(snap1.Image);
            Assert.NotNull(snap1.TakeImage());
            Assert.Equal(2, snap1.Annotations.Count);
        }

        [Fact]
        public void MemoryBudget_SymmetricEviction_EvictsRedoBeforeUndo()
        {
            var manager = new EditorHistoryManager();

            var snapshots = new List<EditorSnapshot>();
            for (int i = 0; i < 6; i++)
            {
                using var img = new Image<Rgba32>(5000, 2000);
                var snap = new EditorSnapshot { Image = img.Clone() };
                snapshots.Add(snap);
                manager.PushUndo(snap);
            }

            Assert.Equal(6, manager.UndoCount);
            Assert.Equal(240_000_000L, manager.TotalUndoHistoryBytes());

            using var currentImg = new Image<Rgba32>(5000, 2000);
            var currentSnap = new EditorSnapshot { Image = currentImg.Clone() };
            manager.TryUndo(currentSnap, out var restored);
            Assert.NotNull(restored);
            Assert.Equal(5, manager.UndoCount);
            Assert.Equal(1, manager.RedoCount);

            using var currentImg2 = new Image<Rgba32>(10000, 2000);
            var currentSnap2 = new EditorSnapshot { Image = currentImg2.Clone() };
            manager.TryUndo(currentSnap2, out _);

            Assert.True(manager.TotalUndoHistoryBytes() <= EditorHistoryManager.MaxUndoHistoryBytes);
            Assert.Equal(4, manager.UndoCount);
            Assert.Equal(1, manager.RedoCount);
        }

        [Fact]
        public void PeekUndo_And_PeekRedo_ReturnTopSnapshotsWithoutMutating()
        {
            var manager = new EditorHistoryManager();
            var snap1 = new EditorSnapshot();
            var snap2 = new EditorSnapshot();

            Assert.Null(manager.PeekUndo());
            Assert.Null(manager.PeekRedo());

            manager.PushUndo(snap1);
            Assert.Same(snap1, manager.PeekUndo());
            Assert.Equal(1, manager.UndoCount);

            manager.PushUndo(snap2);
            Assert.Same(snap2, manager.PeekUndo());
            Assert.Equal(2, manager.UndoCount);

            var current = new EditorSnapshot();
            manager.TryUndo(current, out _);

            Assert.Same(snap1, manager.PeekUndo());
            Assert.Same(current, manager.PeekRedo());
            Assert.Equal(1, manager.UndoCount);
            Assert.Equal(1, manager.RedoCount);
        }
    }
}

