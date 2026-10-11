//
// System.ComponentModel.Design.Serialization.CodeDomLocalizationProvider
//
// Author:
//	Atsushi Enomoto (atsushi@ximian.com)
//
// Copyright (C) 2007 Novell, Inc.
//

//
// Permission is hereby granted, free of charge, to any person obtaining
// a copy of this software and associated documentation files (the
// "Software"), to deal in the Software without restriction, including
// without limitation the rights to use, copy, modify, merge, publish,
// distribute, sublicense, and/or sell copies of the Software, and to
// permit persons to whom the Software is furnished to do so, subject to
// the following conditions:
// 
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
// MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
// LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
// OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
// WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
//


using System.CodeDom;
using System.CodeDom.Compiler;
using System.ComponentModel;
using System.Globalization;
using System.Security.Permissions;

namespace System.ComponentModel.Design.Serialization
{
	public sealed class CodeDomLocalizationProvider : IDisposable, IDesignerSerializationProvider
	{
		public CodeDomLocalizationProvider (IServiceProvider provider, CodeDomLocalizationModel model)
			: this (provider, model, null)
		{
		}

		public CodeDomLocalizationProvider (IServiceProvider provider, CodeDomLocalizationModel model,
						   CultureInfo [] supportedCultures)
		{
			if (provider == null)
				throw new ArgumentNullException ("provider");

			Model = model;
			SupportedCultures = supportedCultures;
		}

		internal CodeDomLocalizationModel Model { get; private set; }

		internal CultureInfo [] SupportedCultures { get; private set; }

		public void Dispose ()
		{
		}

		// No opinion: whatever serializer the manager already resolved stays in place. Returning a
		// localizing serializer -- one that writes property values into a .resx and emits
		// resources.ApplyResources calls instead of inline assignments -- is not implemented yet, so
		// forms serialize the ordinary way and the designer's localization feature is simply absent.
		// Throwing from the CONSTRUCTOR, as this class used to, took the whole designer down with it:
		// a host builds one of these before it loads anything.
		object IDesignerSerializationProvider.GetSerializer (IDesignerSerializationManager manager,
								     object currentSerializer, Type objectType,
								     Type serializerType)
		{
			return null;
		}
	}
}
