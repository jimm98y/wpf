// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (CodeDomSerializerException). The Mono constructors taking a serialization manager
// threw NotImplementedException, as did GetObjectData; .NET 10's GetObjectData throws PlatformNotSupportedException
// (binary serialization is gone), and its Exception constructors take the inner exception's message.

using System.CodeDom;
using System.Runtime.Serialization;

namespace System.ComponentModel.Design.Serialization
{
	public class CodeDomSerializerException : SystemException
	{
		public CodeDomSerializerException (string message, CodeLinePragma linePragma)
			: base (message)
		{
			LinePragma = linePragma;
		}

		public CodeDomSerializerException (Exception ex, CodeLinePragma linePragma)
			: base (ex?.Message, ex)
		{
			LinePragma = linePragma;
		}

		public CodeDomSerializerException (string message, IDesignerSerializationManager manager)
			: base (message)
		{
			if (manager == null)
				throw new ArgumentNullException (nameof (manager));
		}

		public CodeDomSerializerException (Exception ex, IDesignerSerializationManager manager)
			: base (ex?.Message, ex)
		{
			if (manager == null)
				throw new ArgumentNullException (nameof (manager));
		}

		protected CodeDomSerializerException (SerializationInfo info, StreamingContext context)
		{
			throw new PlatformNotSupportedException ();
		}

		public CodeLinePragma LinePragma { get; }

		[Obsolete ("Formatter-based serialization is obsolete.", DiagnosticId = "SYSLIB0051")]
		public override void GetObjectData (SerializationInfo info, StreamingContext context)
		{
			throw new PlatformNotSupportedException ();
		}
	}
}
