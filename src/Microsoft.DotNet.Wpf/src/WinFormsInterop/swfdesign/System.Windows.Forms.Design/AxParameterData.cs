//
// System.Windows.Forms.Design.AxParameterData.cs
//
// Author:
//   Gert Driesen (drieseng@users.sourceforge.net)
//
// (C) 2004 Novell
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

using System;
using System.CodeDom;
using System.Reflection;

// AxParameterData is plain reflection over a method's parameters, which the ActiveX wrapper generator uses to
// write its wrapper's signatures; it needs no COM, so it is implemented here as the .NET Framework has it (the
// Mono stub threw from every constructor). A name is stored with its first letter lowered, and the type name is
// the C# one: a by-ref parameter's element type, nested types joined with '.'.
namespace System.Windows.Forms.Design
{
	public class AxParameterData
	{
		public AxParameterData (ParameterInfo info) : this (info, false)
		{
		}

		public AxParameterData (ParameterInfo info, bool ignoreByRefs)
		{
			if (info == null)
				throw new ArgumentNullException (nameof (info));
			Name = info.Name;
			type = info.ParameterType;
			typeName = MapTypeName (type);
			isByRef = type.IsByRef && !ignoreByRefs;
			isIn = info.IsIn && !ignoreByRefs;
			isOut = info.IsOut && !isIn && !ignoreByRefs;
			isOptional = info.IsOptional;
		}

		public AxParameterData (string inname, string typeName)
		{
			Name = inname;
			this.typeName = typeName;
		}

		public AxParameterData (string inname, Type type)
		{
			Name = inname;
			this.type = type;
			typeName = MapTypeName (type);
		}

		public static AxParameterData[] Convert (ParameterInfo[] infos)
		{
			return Convert (infos, false);
		}

		public static AxParameterData[] Convert (ParameterInfo[] infos, bool ignoreByRefs)
		{
			if (infos == null)
				return new AxParameterData [0];
			var parameters = new AxParameterData [infos.Length];
			for (int i = 0; i < infos.Length; i++)
				parameters [i] = new AxParameterData (infos [i], ignoreByRefs);
			return parameters;
		}

		private static string MapTypeName (Type type)
		{
			if (type == null)
				return null;
			if (type.IsByRef)
				type = type.GetElementType ();
			return (type.FullName ?? type.Name).Replace ('+', '.');
		}

		public FieldDirection Direction {
			get {
				if (this.IsOut)
					return FieldDirection.Out;

				if (this.IsByRef)
					return FieldDirection.Ref;

				return FieldDirection.In;
			}
		}
		public bool IsByRef {
			get {
				return isByRef;
			}
		}

		public bool IsIn {
			get {
				return isIn;
			}
		}

		public bool IsOptional {
			get {
				return isOptional;
			}
		}

		public bool IsOut {
			get {
				return isOut;
			}
		}

		public string Name {
			get {
				return name;
			}
			set {
				if (!string.IsNullOrEmpty (value) && char.IsUpper (value [0]))
					name = char.ToLower (value [0]) + value.Substring (1);
				else
					name = value;
			}
		}

		public Type ParameterType {
			get {
				return type;
			}
		}

		public string TypeName {
			get {
				if (typeName == null)
					typeName = MapTypeName (type);
				return typeName;
			}
		}

		private bool isByRef;
		private bool isIn;
		private bool isOptional;
		private bool isOut;
		private string name;
		private Type type;
		private string typeName;
	}
}
