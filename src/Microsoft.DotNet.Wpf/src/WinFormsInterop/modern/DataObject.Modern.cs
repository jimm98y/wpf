// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// A DataObject's typed data, ported from .NET's DataObject and DataStore: SetDataAsJson, and TryGetData<T> reading
// back a value of the type asked for -- stored as itself, or as JSON -- under the format or, converting, under the
// formats it maps to (Text, UnicodeText and System.String; FileDrop and the file name formats).

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

namespace System.Windows.Forms
{
	public partial class DataObject : ITypedDataObject
	{
		private const string FileNameAnsi = "FileName", FileNameUnicode = "FileNameW";

		/// <summary>Stores <paramref name="data"/> as JSON under <paramref name="format"/>.</summary>
		[RequiresUnreferencedCode ("Uses default System.Text.Json behavior which is not trim-compatible.")]
		public void SetDataAsJson<T> (string format, T data) =>
			SetData (format, false, JsonFor (format, data));

		/// <summary>Stores <paramref name="data"/> as JSON under its type's full name.</summary>
		[RequiresUnreferencedCode ("Uses default System.Text.Json behavior which is not trim-compatible.")]
		public void SetDataAsJson<T> (T data) =>
			SetDataAsJson (typeof (T).FullName, data);

		private static IJsonData JsonFor<T> (string format, T data)
		{
			if (string.IsNullOrWhiteSpace (format))
				throw new ArgumentException ("The value cannot be an empty string or composed entirely of whitespace.", nameof (format));
			if (data == null)
				throw new ArgumentNullException (nameof (data));
			if (IsPredefinedFormat (format))
				throw new ArgumentException (JsonData.CannotJsonSerializePredefinedFormat, nameof (format));
			if (typeof (T).IsAssignableTo (typeof (DataObject)))
				throw new ArgumentException (JsonData.CannotJsonSerializeDataObjectDirectly, nameof (data));
			return JsonData.Create (data);
		}

		[CLSCompliant (false)]
		public bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, Func<TypeName, Type> resolver, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data)
		{
			if (resolver == null)
				throw new ArgumentNullException (nameof (resolver));
			return TryGetDataInternal (format, resolver, autoConvert, out data);
		}

		public bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			TryGetDataInternal (format, null, autoConvert, out data);

		public bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, [NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			TryGetDataInternal (format, null, true, out data);

		public bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> ([NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			TryGetDataInternal (typeof (T).FullName, null, true, out data);

		private bool TryGetDataInternal<T> (string format, Func<TypeName, Type> resolver, bool autoConvert, out T data)
		{
			data = default;
			if (string.IsNullOrWhiteSpace (format))
				return false;
			// the format and the type asked for must go together (text formats are strings, file formats string[],
			// bitmaps Image or Bitmap)
			if (!IsValidTypeForFormat (typeof (T), format))
				return false;
			return TryGetDataCore (format, resolver, autoConvert, out data);
		}

		/// <summary>The value of type <typeparamref name="T"/> under <paramref name="format"/> (or, with
		/// <paramref name="autoConvert"/>, the formats it maps to): stored as itself, or as JSON of it.</summary>
		[CLSCompliant (false)]
		protected virtual bool TryGetDataCore<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, Func<TypeName, Type> resolver, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data)
		{
			if (TryGetEntry (format, ref autoConvert, out data))
				return true;
			if (!autoConvert)
				return false;
			var mapped = new List<string> ();
			AddMappedFormats (format, mapped);
			foreach (string other in mapped) {
				if (TryGetEntry (other, ref autoConvert, out data))
					return true;
			}
			return false;
		}

		private bool TryGetEntry<T> (string format, ref bool autoConvert, out T data)
		{
			Entry entry = Entry.Find (entries, format);
			if (entry != null) {
				autoConvert |= entry.AutoConvert;
				if (entry.Data is T value) {
					data = value;
					return true;
				}
				if (entry.Data is JsonData<T> json) {
					data = (T) json.Deserialize ();
					return true;
				}
				// another data object stored whole (new DataObject(IDataObject)): what it holds
				if (entry.Data is ITypedDataObject inner && !ReferenceEquals (inner, this) && inner.TryGetData (format, false, out data))
					return true;
			}
			data = default;
			return false;
		}

		internal static bool IsValidTypeForFormat (Type type, string format)
		{
			switch (format) {
			case "Text": case "UnicodeText": case "System.String": case "Rich Text Format": case "HTML Format": case "OEMText":
				Check (typeof (string) == type);
				return true;
			case "FileDrop": case FileNameAnsi: case FileNameUnicode:
				Check (typeof (string []) == type);
				return true;
			case "Bitmap": case "System.Drawing.Bitmap":
				Check (type == typeof (System.Drawing.Bitmap) || type == typeof (System.Drawing.Image));
				return true;
			default:
				return true;
			}

			void Check (bool valid)
			{
				if (!valid)
					throw new NotSupportedException ($"The specified type '{type.FullName}' is not compatible with the specified format '{format}'.");
			}
		}

		private static void AddMappedFormats (string format, List<string> formats)
		{
			switch (format) {
			case "Text": formats.Add ("System.String"); formats.Add ("UnicodeText"); break;
			case "UnicodeText": formats.Add ("System.String"); formats.Add ("Text"); break;
			case "System.String": formats.Add ("Text"); formats.Add ("UnicodeText"); break;
			case "FileDrop": formats.Add (FileNameUnicode); formats.Add (FileNameAnsi); break;
			case FileNameUnicode: formats.Add ("FileDrop"); formats.Add (FileNameAnsi); break;
			case FileNameAnsi: formats.Add ("FileDrop"); formats.Add (FileNameUnicode); break;
			case "Bitmap": formats.Add ("System.Drawing.Bitmap"); break;
			case "System.Drawing.Bitmap": formats.Add ("Bitmap"); break;
			case "EnhancedMetafile": formats.Add ("System.Drawing.Imaging.Metafile"); break;
			case "System.Drawing.Imaging.Metafile": formats.Add ("EnhancedMetafile"); break;
			default:
				// formats are not case-sensitive (an OLE proxy's "TEXT")
				if (string.Equals (format, "Text", StringComparison.OrdinalIgnoreCase)) { formats.Add ("System.String"); formats.Add ("UnicodeText"); }
				else if (string.Equals (format, "UnicodeText", StringComparison.OrdinalIgnoreCase)) { formats.Add ("System.String"); formats.Add ("Text"); }
				else if (string.Equals (format, "System.String", StringComparison.OrdinalIgnoreCase)) { formats.Add ("Text"); formats.Add ("UnicodeText"); }
				break;
			}
		}

		internal static bool IsPredefinedFormat (string format) => format switch {
			"Text" or "UnicodeText" or "Rich Text Format" or "HTML Format" or "OEMText" or "FileDrop" or FileNameAnsi
				or FileNameUnicode or "System.String" or "System.Drawing.Bitmap" or "Csv" or "DeviceIndependentBitmap"
				or "DataInterchangeFormat" or "Locale" or "PenData" or "RiffAudio" or "SymbolicLink"
				or "TaggedImageFileFormat" or "WaveAudio" or "Bitmap" or "EnhancedMetafile" or "Palette" or "MetaFilePict" => true,
			_ => false,
		};
	}
}
