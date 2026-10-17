// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// ImageListCodeDomSerializer, as the .NET Framework's designer has it (.NET no longer ships it): an ImageList is
// serialized as any component (its ImageStream goes to the resources), and then each image's key is written as
// `imageList1.Images.SetKeyName(i, "key")`, which the ImageStream does not carry. Deserialization is the
// component's. Both members of the Mono stub threw NotImplementedException.

using System;
using System.CodeDom;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;

namespace System.Windows.Forms.Design
{
	public class ImageListCodeDomSerializer : CodeDomSerializer
	{
		public ImageListCodeDomSerializer ()
		{
		}

		/// <summary>The serializer an ImageList would have without this one: its base type's.</summary>
		private static CodeDomSerializer BaseSerializer (IDesignerSerializationManager manager)
			=> manager.GetSerializer (typeof (ImageList).BaseType, typeof (CodeDomSerializer)) as CodeDomSerializer;

		public override object Deserialize (IDesignerSerializationManager manager, object codeObject)
		{
			if (manager == null)
				throw new ArgumentNullException (nameof (manager));
			if (codeObject == null)
				throw new ArgumentNullException (nameof (codeObject));
			return BaseSerializer (manager)?.Deserialize (manager, codeObject);
		}

		public override object Serialize (IDesignerSerializationManager manager, object value)
		{
			if (manager == null)
				throw new ArgumentNullException (nameof (manager));
			if (value == null)
				throw new ArgumentNullException (nameof (value));
			CodeDomSerializer baseSerializer = BaseSerializer (manager);
			if (baseSerializer == null)
				return null;
			object codeObject = baseSerializer.Serialize (manager, value);

			if (value is ImageList imageList && codeObject is CodeStatementCollection statements) {
				CodeExpression imageListExpression = GetExpression (manager, value);
				if (imageListExpression != null) {
					var images = new CodePropertyReferenceExpression (imageListExpression, "Images");
					var keys = imageList.Images.Keys;
					for (int i = 0; i < keys.Count; i++) {
						if (string.IsNullOrEmpty (keys [i]))
							continue;
						statements.Add (new CodeExpressionStatement (new CodeMethodInvokeExpression (images, "SetKeyName",
							new CodePrimitiveExpression (i), new CodePrimitiveExpression (keys [i]))));
					}
				}
			}
			return codeObject;
		}
	}
}
