// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (ExceptionCollection). Mono's constructor threw NotImplementedException after storing
// the list -- so a designer loader could never report its load errors with one.

using System.Collections;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace System.ComponentModel.Design
{
	public sealed class ExceptionCollection : Exception
	{
		private readonly List<Exception> _exceptions;

		public ExceptionCollection (ArrayList exceptions)
		{
			if (exceptions == null)
				return;
			var list = new List<Exception> (exceptions.Count);
			foreach (object e in exceptions) {
				if (!(e is Exception ex))
					throw new ArgumentException ("All objects in the collection must be of type Exception.", nameof (exceptions));
				list.Add (ex);
			}
			_exceptions = list;
		}

		/// <summary>A copy of the exceptions (null when constructed with none), as .NET hands it out.</summary>
		public ArrayList Exceptions => _exceptions == null ? null : new ArrayList (_exceptions);

		[Obsolete ("Formatter-based serialization is obsolete.", DiagnosticId = "SYSLIB0051")]
		public override void GetObjectData (SerializationInfo info, StreamingContext context)
		{
			throw new PlatformNotSupportedException ();
		}
	}
}
