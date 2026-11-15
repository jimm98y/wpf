// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// The typed data of a data object (TryGetData<T>), as .NET's ITypedDataObject, DataObjectExtensions, IAsyncDropTarget
// and DropImageType have it.

using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;

namespace System.Windows.Forms
{
	public interface ITypedDataObject : IDataObject
	{
		bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> ([NotNullWhen (true), MaybeNullWhen (false)] out T data);

		bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, [NotNullWhen (true), MaybeNullWhen (false)] out T data);

		bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data);

		bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (string format, Func<TypeName, Type> resolver, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data);
	}

	public static class DataObjectExtensions
	{
		private static ITypedDataObject Typed (IDataObject dataObject)
		{
			if (dataObject == null)
				throw new ArgumentNullException (nameof (dataObject));
			if (dataObject is not ITypedDataObject typed)
				throw new NotSupportedException ($"Data object '{dataObject.GetType ().FullName}' doesn't implement the 'ITypedDataObject' interface and can't be read using 'TryGetData<T>(string, out T)' methods.");
			return typed;
		}

		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (this IDataObject dataObject, [NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			Typed (dataObject).TryGetData (out data);

		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (this IDataObject dataObject, string format, [NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			Typed (dataObject).TryGetData (format, out data);

		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (this IDataObject dataObject, string format, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			Typed (dataObject).TryGetData (format, autoConvert, out data);

		[CLSCompliant (false)]
		public static bool TryGetData<[DynamicallyAccessedMembers (DynamicallyAccessedMemberTypes.All)] T> (this IDataObject dataObject, string format, Func<TypeName, Type> resolver, bool autoConvert, [NotNullWhen (true), MaybeNullWhen (false)] out T data) =>
			Typed (dataObject).TryGetData (format, resolver, autoConvert, out data);
	}

	/// <summary>A drop target the drop is given to asynchronously (a shell drag of virtual files).</summary>
	public interface IAsyncDropTarget : IDropTarget
	{
		void OnAsyncDragDrop (DragEventArgs e);
	}

	/// <summary>The image shown with the drop description (DROPIMAGETYPE).</summary>
	public enum DropImageType
	{
		Invalid = -1,
		None = 0,
		Copy = 1,
		Move = 2,
		Link = 4,
		Label = 6,
		Warning = 7,
		NoImage = 8,
	}
}
