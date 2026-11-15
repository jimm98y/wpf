// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Data put on the clipboard, or dragged, as JSON (SetDataAsJson, DoDragDropAsJson), ported from .NET's JsonData<T>
// (System.Private.Windows.Core): the value's UTF-8 JSON and its type, read back by TryGetData<T>.

using System.Text.Json;

namespace System.Windows.Forms
{
	internal interface IJsonData
	{
		byte [] JsonBytes { get; set; }
		string InnerTypeAssemblyQualifiedName { get; }
		object Deserialize ();
	}

	[Serializable]
	internal struct JsonData<T> : IJsonData
	{
		public byte [] JsonBytes { get; set; }

		public readonly string InnerTypeAssemblyQualifiedName => typeof (T).AssemblyQualifiedName;

		public readonly object Deserialize ()
		{
			object result;
			try {
				result = JsonSerializer.Deserialize<T> (JsonBytes);
			} catch (Exception ex) {
				result = new NotSupportedException (ex.Message);
			}
			return result ?? throw new InvalidOperationException ();
		}
	}

	internal static class JsonData
	{
		internal const string CannotJsonSerializeDataObject = "An object of type 'DataObject' will serialize as empty. Use 'DataObject.SetDataAsJson' APIs on your object to JSON-serialize the data within your object, then use the '{0}' API with your object.";
		internal const string CannotJsonSerializeDataObjectDirectly = "'DataObject' can not be serialized as JSON directly. Use 'DataObject.SetDataAsJson' APIs to add JSON serialized data to an existing DataObject.";
		internal const string CannotJsonSerializePredefinedFormat = "Predefined formats should not be serialized as JSON.";

		internal static IJsonData Create<T> (T data) => new JsonData<T> { JsonBytes = JsonSerializer.SerializeToUtf8Bytes (data) };
	}
}
