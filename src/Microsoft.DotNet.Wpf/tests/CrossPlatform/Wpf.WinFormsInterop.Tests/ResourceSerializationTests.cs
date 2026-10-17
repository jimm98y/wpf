// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// The resource half of CodeDom designer serialization (ResourceCodeDomSerializer and CodeDomSerializerBase's
// SerializeToResourceExpression / SerializeResource / DeserializePropertiesFromResources), ported from
// dotnet/winforms -- every one threw NotImplementedException in Mono, so a designer could not save or load a
// localizable form. A value goes to the IResourceService's ResX writer under "owner.Property", the code reads it
// back through a ComponentResourceManager declared once, and loading sets the property from the resources.
//

using System;
using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design;
using System.ComponentModel.Design.Serialization;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Resources;
using Xunit;
using SWF = System.Windows.Forms;

namespace Wpf.WinFormsInterop.Tests
{
    public class ResourceSerializationTests
    {
        private sealed class ResourceService : IResourceService, IServiceProvider
        {
            private readonly Dictionary<string, MemoryStream> _written = new();
            public object? GetService(Type t) => t == typeof(IResourceService) ? this : null;
            public IResourceWriter GetResourceWriter(CultureInfo info)
            {
                var stream = new MemoryStream();
                _written[info.Name] = stream;
                return new ResXResourceWriter(new KeepOpen(stream));
            }
            public IResourceReader? GetResourceReader(CultureInfo info)
                => _written.TryGetValue(info.Name, out MemoryStream? s) && s.Length > 0 ? new ResXResourceReader(new MemoryStream(s.ToArray())) : null;
            public string Resx(CultureInfo info) => System.Text.Encoding.UTF8.GetString(_written[info.Name].ToArray());
        }

        /// <summary>A stream whose Close leaves the bytes readable.</summary>
        private sealed class KeepOpen : Stream
        {
            private readonly MemoryStream _inner;
            public KeepOpen(MemoryStream inner) => _inner = inner;
            public override bool CanRead => false; public override bool CanSeek => true; public override bool CanWrite => true;
            public override long Length => _inner.Length;
            public override long Position { get => _inner.Position; set => _inner.Position = value; }
            public override void Flush() => _inner.Flush();
            public override int Read(byte[] b, int o, int c) => throw new NotSupportedException();
            public override long Seek(long o, SeekOrigin s) => _inner.Seek(o, s);
            public override void SetLength(long v) => _inner.SetLength(v);
            public override void Write(byte[] b, int o, int c) => _inner.Write(b, o, c);
        }

        /// <summary>The protected resource methods, reachable.</summary>
        private sealed class Serializer : CodeDomSerializer
        {
            public CodeExpression? ToResource(IDesignerSerializationManager m, object v) => SerializeToResourceExpression(m, v);
            public void Write(IDesignerSerializationManager m, string name, object v) => SerializeResource(m, name, v);
            public void Load(IDesignerSerializationManager m, object v) => DeserializePropertiesFromResources(m, v, null!);
        }

        [Fact]
        public void ALocalizableValue_GoesToTheResources_AndComesBack()
        {
            var resources = new ResourceService();
            var root = new SWF.Label();
            var ser = new Serializer();
            var statements = new CodeStatementCollection();
            CodeExpression? expr;

            var session = new DesignerSerializationManager(resources);
            IDesignerSerializationManager manager = session;
            using (session.CreateSession())
            {
                manager.SetName(root, "Form1");
                manager.Context.Push(new RootContext(new CodeThisReferenceExpression(), root));
                manager.Context.Push(statements);
                manager.Context.Push(new ExpressionContext(new CodePropertyReferenceExpression(new CodeThisReferenceExpression(), "Text"), typeof(string), root));
                expr = ser.ToResource(manager, "hello");
                manager.Context.Pop();
                ser.Write(manager, "Form1.Tag", "a tag");
            }

            // resources.GetString("$this.Text"), after one declaration of the resource manager.
            var invoke = Assert.IsType<CodeMethodInvokeExpression>(expr);
            Assert.Equal("GetString", invoke.Method.MethodName);
            Assert.Equal("$this.Text", ((CodePrimitiveExpression)invoke.Parameters[0]).Value);
            var decl = Assert.IsType<CodeVariableDeclarationStatement>(Assert.Single(statements.Cast<CodeStatement>()));
            Assert.Equal("resources", decl.Name);
            Assert.Contains("ComponentResourceManager", decl.Type.BaseType);

            string resx = resources.Resx(CultureInfo.InvariantCulture);
            Assert.Contains("$this.Text", resx);
            Assert.Contains("hello", resx);
            Assert.Contains("Form1.Tag", resx);

            // Loading: the property is set from its resource.
            var loaded = new SWF.Label();
            var readerSession = new DesignerSerializationManager(resources);
            IDesignerSerializationManager reader = readerSession;
            using (readerSession.CreateSession())
            {
                reader.Context.Push(new RootContext(new CodeThisReferenceExpression(), loaded));
                ser.Load(reader, loaded);
            }
            Assert.Equal("hello", loaded.Text);
        }

        [Fact]
        public void AValueWithoutAContext_IsNumbered()
        {
            var resources = new ResourceService();
            var ser = new Serializer();
            var session = new DesignerSerializationManager(resources);
            IDesignerSerializationManager manager = session;
            CodeExpression? a, b;
            using (session.CreateSession())
            {
                manager.Context.Push(new CodeStatementCollection());
                a = ser.ToResource(manager, 42);
                b = ser.ToResource(manager, 43);
            }
            // (int)resources.GetObject("resource1"), then "resource2".
            var castA = Assert.IsType<CodeCastExpression>(a);
            Assert.Equal("resource1", ((CodePrimitiveExpression)((CodeMethodInvokeExpression)castA.Expression).Parameters[0]).Value);
            Assert.Equal("resource2", ((CodePrimitiveExpression)((CodeMethodInvokeExpression)((CodeCastExpression)b!).Expression).Parameters[0]).Value);
        }
    }
}
