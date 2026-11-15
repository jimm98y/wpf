// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// .NET's ResXResourceSet: a resource set read from a .resx file or stream by ResXResourceReader. Mono's drove
// ResourceSet's protected Reader and Table, which .NET no longer has; .NET's needs only the constructor that takes a
// reader.

using System.IO;

namespace System.Resources
{
	public class ResXResourceSet : ResourceSet
	{
		public ResXResourceSet (string fileName) : base (new ResXResourceReader (fileName))
		{
		}

		public ResXResourceSet (Stream stream) : base (new ResXResourceReader (stream))
		{
		}

		public override Type GetDefaultReader () => typeof (ResXResourceReader);

		public override Type GetDefaultWriter () => typeof (ResXResourceWriter);
	}
}
