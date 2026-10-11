// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// EmbeddedContent is the registry hosted (WinForms) scenes are published into for the WPF compositor
// to overlay. It used to keep ONE set per publishing thread and replace it whole on every publish, so
// two hosting mechanisms on one page -- the gallery's own WinForms card and a real WindowsFormsHost,
// each publishing from its own render tick -- erased each other: only the last writer was on screen.
// Each host now publishes under an owner and replaces only its own set.
//

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Wpf.Interop.WebGpu.Composition;
using Xunit;

namespace WgpuInterop.Tests.Compositing
{
    [Collection("EmbeddedContent")]          // the registry is process-wide
    public sealed class EmbeddedContentTests : IDisposable
    {
        private readonly object _a = new(), _b = new();

        public void Dispose()
        {
            EmbeddedContent.Withdraw(_a);
            EmbeddedContent.Withdraw(_b);
            EmbeddedContent.Set(null);
            EmbeddedContent.SetCaret(0, 0, 0, 0, false);
        }

        private static EmbeddedItem Item(SceneVisual scene, float x, IntPtr window = default)
            => new EmbeddedItem { Scene = scene, DeviceX = x, DeviceY = 0, DeviceW = 10, DeviceH = 10, Window = window };

        /// <summary>The hosted scenes the composite places, in order (each is wrapped in a placement node).</summary>
        private static List<SceneVisual> Hosted(SceneVisual composite, SceneVisual wpfRoot)
        {
            if (ReferenceEquals(composite, wpfRoot)) return new List<SceneVisual>();
            Assert.Same(wpfRoot, composite.Children[0]);
            return composite.Children.Skip(1)
                .Where(p => p.Children.Count == 1)
                .Select(p => p.Children[0])
                .ToList();
        }

        [Fact]
        public void TwoOwnersOnOneThreadBothCompose()
        {
            var root = new SceneVisual();
            SceneVisual sa = new(), sb = new();
            EmbeddedContent.Set(_a, new[] { Item(sa, 0) });
            EmbeddedContent.Set(_b, new[] { Item(sb, 20) });

            // A republish by either owner replaces only its own set.
            EmbeddedContent.Set(_a, new[] { Item(sa, 5) });
            Assert.Equal(new[] { sa, sb }, Hosted(EmbeddedContent.Compose(root, IntPtr.Zero), root));

            EmbeddedContent.Set(_b, new[] { Item(sb, 25) });
            Assert.Equal(new[] { sa, sb }, Hosted(EmbeddedContent.Compose(root, IntPtr.Zero), root));
        }

        [Fact]
        public void WithdrawingOneOwnerKeepsTheOther()
        {
            var root = new SceneVisual();
            SceneVisual sa = new(), sb = new();
            EmbeddedContent.Set(_a, new[] { Item(sa, 0) });
            EmbeddedContent.Set(_b, new[] { Item(sb, 20) });

            EmbeddedContent.Set(_a, null);
            Assert.Equal(new[] { sb }, Hosted(EmbeddedContent.Compose(root, IntPtr.Zero), root));

            EmbeddedContent.Withdraw(_b);
            Assert.Same(root, EmbeddedContent.Compose(root, IntPtr.Zero));
            Assert.False(EmbeddedContent.Any);
        }

        [Fact]
        public void ThreadScopedPublishDoesNotEraseAnOwnersSet()
        {
            var root = new SceneVisual();
            SceneVisual sa = new(), st = new();
            EmbeddedContent.Set(_a, new[] { Item(sa, 0) });
            EmbeddedContent.Set(new[] { Item(st, 20) });         // the old, ownerless overload
            Assert.Equal(new[] { sa, st }, Hosted(EmbeddedContent.Compose(root, IntPtr.Zero), root));

            EmbeddedContent.Set(null);
            Assert.Equal(new[] { sa }, Hosted(EmbeddedContent.Compose(root, IntPtr.Zero), root));
        }

        [Fact]
        public void EachOwnersCaretIsDrawnAndItsSetIsKeptPerWindow()
        {
            var root = new SceneVisual();
            SceneVisual sa = new(), sb = new();
            IntPtr w1 = (IntPtr)0x1001, w2 = (IntPtr)0x1002;
            EmbeddedContent.Set(_a, new[] { Item(sa, 0, w1) });
            EmbeddedContent.Set(_b, new[] { Item(sb, 0, w2) });
            EmbeddedContent.SetCaret(_a, w1, 1, 1, 1, 8, true);
            EmbeddedContent.SetCaret(_b, w2, 3, 1, 1, 8, true);

            SceneVisual c1 = EmbeddedContent.Compose(root, w1);
            Assert.Equal(new[] { sa }, Hosted(c1, root));
            Assert.Single(c1.Children.Skip(1), n => n.Content.Count == 1);   // a's caret only

            // Hiding a's caret leaves its scenes, and b's caret, alone.
            EmbeddedContent.SetCaret(_a, w1, 0, 0, 0, 0, false);
            Assert.DoesNotContain(EmbeddedContent.Compose(root, w1).Children.Skip(1), n => n.Content.Count == 1);
            Assert.Single(EmbeddedContent.Compose(root, w2).Children.Skip(1), n => n.Content.Count == 1);
            Assert.Equal(new[] { sa }, Hosted(EmbeddedContent.Compose(root, w1), root));
        }
    }
}
