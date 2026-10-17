// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.Resources.AssemblyNamesTypeResolutionService): what a ResX reader given
// assembly names resolves types through. GetAssembly loads by name and, failing that, from a listed assembly's
// CodeBase; GetType tries the qualified name and then every listed assembly, the one named in the qualified name
// first; ReferenceAssembly is not supported. Mono threw NotImplementedException from GetAssembly,
// ReferenceAssembly and GetPathOfAssembly, and its GetType never looked in the listed assemblies.

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Reflection;

namespace System.Resources
{
	sealed class AssemblyNamesTypeResolutionService : ITypeResolutionService
	{
		private AssemblyName [] _names;
		private ConcurrentDictionary<AssemblyName, Assembly> _cachedAssemblies;
		private ConcurrentDictionary<string, Type> _cachedTypes;

		public AssemblyNamesTypeResolutionService (AssemblyName [] names)
		{
			_names = names;
		}

		public Assembly GetAssembly (AssemblyName name)
		{
			return GetAssembly (name, true);
		}

		public Assembly GetAssembly (AssemblyName name, bool throwOnError)
		{
			_cachedAssemblies ??= new ConcurrentDictionary<AssemblyName, Assembly> ();
			if (_cachedAssemblies.TryGetValue (name, out Assembly result) && result != null)
				return result;
			try {
				result = Assembly.Load (name.FullName);
			} catch {
				result = null;
			}
			if (result != null) {
				_cachedAssemblies [name] = result;
			} else if (_names != null) {
				foreach (AssemblyName assemblyName in _names) {
					if (!assemblyName.Equals (name))
						continue;
					try {
						result = Assembly.LoadFrom (GetPathOfAssembly (assemblyName));
						if (result != null)
							_cachedAssemblies [assemblyName] = result;
					} catch {
						if (throwOnError)
							throw;
					}
				}
			}
			return result;
		}

		public string GetPathOfAssembly (AssemblyName name)
		{
#pragma warning disable SYSLIB0044
			return name.CodeBase ?? string.Empty;
#pragma warning restore SYSLIB0044
		}

		public Type GetType (string name)
		{
			return GetType (name, true);
		}

		public Type GetType (string name, bool throwOnError)
		{
			return GetType (name, throwOnError, false);
		}

		public Type GetType (string name, bool throwOnError, bool ignoreCase)
		{
			_cachedTypes ??= new ConcurrentDictionary<string, Type> (StringComparer.Ordinal);
			if (_cachedTypes.TryGetValue (name, out Type result) && result != null)
				return result;

			// .NET asks Type.GetType only for a qualified name; a simple one (a core type) is asked too, as this
			// reader always did.
			result = Type.GetType (name, false, ignoreCase);

			if (result == null && _names != null) {
				// An assembly-qualified name: search its own assembly first.
				int pos = name.IndexOf (',');
				if (pos > 0 && pos < name.Length - 1) {
					AssemblyName assemblyName = null;
					try {
						assemblyName = new AssemblyName (name.Substring (pos + 1).Trim ());
					} catch {
					}
					if (assemblyName != null) {
						var assemblyList = new List<AssemblyName> (_names.Length);
						foreach (AssemblyName asmName in _names) {
							if (string.Equals (assemblyName.Name, asmName.Name, StringComparison.OrdinalIgnoreCase))
								assemblyList.Insert (0, asmName);
							else
								assemblyList.Add (asmName);
						}
						_names = assemblyList.ToArray ();
					}
				}

				foreach (AssemblyName assemblyName in _names) {
					Assembly assembly = GetAssembly (assemblyName, false);
					if (assembly != null) {
						result = assembly.GetType (name, false, ignoreCase);
						if (result == null) {
							int indexOfComma = name.IndexOf (',');
							if (indexOfComma != -1)
								result = assembly.GetType (name.Substring (0, indexOfComma), false, ignoreCase);
						}
					}
					if (result != null)
						break;
				}
			}

			if (result == null && throwOnError)
				throw new ArgumentException (string.Format ("Could not find a type for a name. The type name was '{0}'.", name));
			if (result != null && result.Assembly == typeof (object).Assembly)
				_cachedTypes [name] = result;   // only framework types, which do not change
			return result;
		}

		public void ReferenceAssembly (AssemblyName name)
		{
			throw new NotSupportedException ();
		}
	}
}
