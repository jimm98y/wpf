// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The clipboard's typed data, ported from .NET's Clipboard: TryGetData<T> and SetDataAsJson.

using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

namespace System.Windows.Forms
{
	public sealed partial class Clipboard
	{
		/// <summary>The clipboard's data under <paramref name="format"/> as a <typeparamref name="T"/>, the types it
		/// may be resolved through by <paramref name="resolver"/>.</summary>
		[CLSCompliant (false)]
		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, Func<TypeName, Type> resolver, [NotNullWhen (true), MaybeNullWhen (false)] out T data)
		{
			data = default;
			if (resolver == null)
				throw new ArgumentNullException (nameof (resolver));
			if (string.IsNullOrWhiteSpace (format) || !DataObject.IsValidTypeForFormat (typeof (T), format) || GetDataObject () is not { } dataObject)
				return false;
			return dataObject.TryGetData (format, resolver, autoConvert: false, out data);
		}

		/// <summary>The clipboard's data under <paramref name="format"/> as a <typeparamref name="T"/>.</summary>
		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, [NotNullWhen (true), MaybeNullWhen (false)] out T data)
		{
			data = default;
			if (string.IsNullOrWhiteSpace (format) || !DataObject.IsValidTypeForFormat (typeof (T), format) || GetDataObject () is not { } dataObject)
				return false;
			return dataObject.TryGetData (format, autoConvert: false, out data);
		}

		/// <summary>The clipboard's data under the full name of <typeparamref name="T"/>.</summary>
		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> ([NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			TryGetData (typeof (T).FullName, out data);

		/// <summary>Puts <paramref name="data"/> on the clipboard as JSON under <paramref name="format"/>.</summary>
		[RequiresUnreferencedCode ("Uses default System.Text.Json behavior which is not trim-compatible.")]
		public static void SetDataAsJson<T> (string format, T data)
		{
			if (data == null)
				throw new ArgumentNullException (nameof (data));
			if (string.IsNullOrWhiteSpace (format))
				throw new ArgumentException ("The value cannot be an empty string or composed entirely of whitespace.", nameof (format));
			DataObject dataObject = new DataObject ();
			dataObject.SetDataAsJson (format, data);
			SetDataObject (dataObject, copy: true);
		}
	}
}
