// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

//
// System.Drawing.Common's ToolboxBitmapAttribute: an image looked up as a resource of the type (its
// name, then .bmp and .ico), from a file, or System.Drawing's own default component image when there
// is none -- never null for a type, never an exception for a bad name or file. The 96-dpi virtual
// screen this stack lays out on needs no logical-to-device scaling.
//

using System.Diagnostics.CodeAnalysis;
using System.Drawing.Imaging;
using System.IO;

namespace System.Drawing
{
	[AttributeUsage (AttributeTargets.Class)]
	public class ToolboxBitmapAttribute : Attribute
	{
		private Image _smallImage;
		private Image _largeImage;

		private static readonly Size s_largeSize = new Size (32, 32);
		private static readonly Size s_smallSize = new Size (16, 16);

		public static readonly ToolboxBitmapAttribute Default = new ToolboxBitmapAttribute ((Image) null, (Image) null);

		private static readonly ToolboxBitmapAttribute s_defaultComponent = DefaultComponent ();

		public ToolboxBitmapAttribute (string imageFile)
			: this (GetImageFromFile (imageFile, large: false), GetImageFromFile (imageFile, large: true))
		{
		}

		public ToolboxBitmapAttribute (Type t)
			: this (GetImageFromResource (t, null, large: false), GetImageFromResource (t, null, large: true))
		{
		}

		public ToolboxBitmapAttribute (Type t, string name)
			: this (GetImageFromResource (t, name, large: false), GetImageFromResource (t, name, large: true))
		{
		}

		private ToolboxBitmapAttribute (Image smallImage, Image largeImage)
		{
			_smallImage = smallImage;
			_largeImage = largeImage;
		}

		static ToolboxBitmapAttribute DefaultComponent ()
		{
			Stream s = typeof (ToolboxBitmapAttribute).Assembly.GetManifestResourceStream ("System.Drawing.DefaultComponent.bmp");
			if (s == null)
				return new ToolboxBitmapAttribute ((Image) null, (Image) null);
			var bitmap = new Bitmap (s);
			MakeBackgroundAlphaZero (bitmap);
			return new ToolboxBitmapAttribute (bitmap, null);
		}

		public override bool Equals ([NotNullWhen (true)] object value)
		{
			if (value == this)
				return true;
			if (value is ToolboxBitmapAttribute other)
				return other._smallImage == _smallImage && other._largeImage == _largeImage;
			return false;
		}

		public override int GetHashCode () => base.GetHashCode ();

		public Image GetImage (object component) => GetImage (component, large: true);

		public Image GetImage (object component, bool large)
		{
			if (component == null)
				return null;
			return GetImage (component.GetType (), large);
		}

		public Image GetImage (Type type) => GetImage (type, large: false);

		public Image GetImage (Type type, bool large) => GetImage (type, null, large);

		public Image GetImage (Type type, string imgName, bool large)
		{
			if ((large && _largeImage == null) || (!large && _smallImage == null)) {
				Image image = large ? _largeImage : _smallImage;
				if (image == null)
					image = GetImageFromResource (type, imgName, large);
				if (large && _largeImage == null && _smallImage != null)
					image = new Bitmap ((Bitmap) _smallImage, s_largeSize.Width, s_largeSize.Height);
				if (image is Bitmap b)
					MakeBackgroundAlphaZero (b);
				if (image == null) {
					image = s_defaultComponent.GetImage (type, large);
					if (image != null)
						image = (Image) image.Clone ();
				}
				if (large)
					_largeImage = image;
				else
					_smallImage = image;
			}
			Image result = large ? _largeImage : _smallImage;
			if (Equals (Default)) {
				_largeImage = null;
				_smallImage = null;
			}
			return result;
		}

		static bool Critical (Exception e) => e is NullReferenceException || e is StackOverflowException || e is OutOfMemoryException
			|| e is System.Threading.ThreadAbortException || e is IndexOutOfRangeException || e is AccessViolationException;

		static Bitmap GetIconFromStream (Stream stream, bool large)
		{
			if (stream == null)
				return null;
			try {
				using (Icon original = new Icon (stream))
				using (Icon icon = new Icon (original, large ? s_largeSize : s_smallSize))
					return icon.ToBitmap ();
			} catch (Exception e) when (!Critical (e)) {
				return null;
			}
		}

		static Image GetImageFromFile (string imageFile, bool large)
		{
			try {
				if (imageFile != null) {
					string extension = Path.GetExtension (imageFile);
					if (extension != null && string.Equals (extension, ".ico", StringComparison.OrdinalIgnoreCase)) {
						using (FileStream stream = File.OpenRead (imageFile))
							return GetIconFromStream (stream, large);
					}
					if (!large)
						return Image.FromFile (imageFile);
				}
			} catch (Exception e) when (!Critical (e)) {
			}
			return null;
		}

		static Stream ResourceStream (Type t, string name) => t.Module.Assembly.GetManifestResourceStream (t, name);

		static Image GetBitmapFromResource (Type t, string bitmapName, bool large)
		{
			if (bitmapName == null)
				return null;
			try {
				Stream s = ResourceStream (t, bitmapName);
				if (s == null)
					return null;
				var bitmap = new Bitmap (s);
				MakeBackgroundAlphaZero (bitmap);
				return large ? new Bitmap (bitmap, s_largeSize.Width, s_largeSize.Height) : bitmap;
			} catch (Exception e) when (!Critical (e)) {
				return null;
			}
		}

		static Bitmap GetIconFromResource (Type t, string bitmapName, bool large)
			=> bitmapName == null ? null : GetIconFromStream (ResourceStream (t, bitmapName), large);

		public static Image GetImageFromResource (Type t, string imageName, bool large)
		{
			Image image = null;
			try {
				string name = imageName, ico = null, bmp = null, plain = null;
				if (name == null) {
					name = t.FullName;
					int dot = name.LastIndexOf ('.');
					if (dot != -1)
						name = name.Substring (dot + 1);
					plain = name;
					ico = name + ".ico";
					bmp = name + ".bmp";
				} else if (string.Equals (Path.GetExtension (imageName), ".ico", StringComparison.CurrentCultureIgnoreCase)) {
					ico = name;
				} else if (string.Equals (Path.GetExtension (imageName), ".bmp", StringComparison.CurrentCultureIgnoreCase)) {
					bmp = name;
				} else {
					plain = name;
					bmp = name + ".bmp";
					ico = name + ".ico";
				}
				if (plain != null)
					image = GetBitmapFromResource (t, plain, large);
				if (image == null && plain != null)
					image = GetIconFromResource (t, plain, large);
				if (image == null && bmp != null)
					image = GetBitmapFromResource (t, bmp, large);
				if (image == null && ico != null)
					image = GetIconFromResource (t, ico, large);
			} catch (Exception) {
			}
			return image;
		}

		static void MakeBackgroundAlphaZero (Bitmap img)
		{
			if (img.RawFormat.Guid == ImageFormat.Icon.Guid)
				return;
			Color bottomLeft = img.GetPixel (0, img.Height - 1);
			img.MakeTransparent ();
			img.SetPixel (0, img.Height - 1, Color.FromArgb (0, bottomLeft));
		}
	}
}
