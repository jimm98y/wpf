// TEMPORARY: the metafile entry points, as stubs, until the managed Metafile lands.
// Nothing here calls native GDI+.
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;

namespace System.Drawing
{
	internal partial class GDIPlus
	{
		internal static ulong GdiPlusToken = 0;
		internal unsafe static Status GdipSetPropertyItem (IntPtr image, GdipPropertyItem *propertyItem) { return Status.NotImplemented; }
		internal sealed class GdiPlusStreamHelper {
			public GdiPlusStreamHelper (Stream s, bool seekToOrigin) { }
			public delegate int D0 ();
			public object GetHeaderDelegate, GetBytesDelegate, PutBytesDelegate, SeekDelegate, CloseDelegate, SizeDelegate;
		}
		internal static Status GdipCloneImage (IntPtr image, out IntPtr imageclone) { imageclone = default; return Status.NotImplemented; }
		internal static Status GdipCreateMetafileFromEmf (IntPtr hEmf, bool deleteEmf, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipCreateMetafileFromFile (string filename, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipCreateMetafileFromStream (object stream, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipDisposeImage (IntPtr image) {  return Status.NotImplemented; }
		internal static Status GdipGetAllPropertyItems (IntPtr image, int bufferSize, int propNumbers, IntPtr items) {  return Status.NotImplemented; }
		internal static Status GdipGetEncoderParameterList (IntPtr image, ref Guid encoder, uint size, IntPtr buffer) {  return Status.NotImplemented; }
		internal static Status GdipGetEncoderParameterListSize (IntPtr image, ref Guid encoder, out uint size) { size = default; return Status.NotImplemented; }
		internal static Status GdipGetImageBounds (IntPtr image, out RectangleF source, ref GraphicsUnit unit) { source = default; return Status.NotImplemented; }
		internal static Status GdipGetImageDimension (IntPtr image, out float width, out float height) { width = default; height = default; return Status.NotImplemented; }
		internal static Status GdipGetImageFlags (IntPtr image, out int flag) { flag = default; return Status.NotImplemented; }
		internal static Status GdipGetImageHeight (IntPtr image, out uint height) { height = default; return Status.NotImplemented; }
		internal static Status GdipGetImageHorizontalResolution (IntPtr image, out float resolution) { resolution = default; return Status.NotImplemented; }
		internal static Status GdipGetImagePalette (IntPtr image, IntPtr palette, int size) {  return Status.NotImplemented; }
		internal static Status GdipGetImagePaletteSize (IntPtr image, out int size) { size = default; return Status.NotImplemented; }
		internal static Status GdipGetImagePixelFormat (IntPtr image, out PixelFormat format) { format = default; return Status.NotImplemented; }
		internal static Status GdipGetImageRawFormat (IntPtr image, out Guid format) { format = default; return Status.NotImplemented; }
		internal static Status GdipGetImageType (IntPtr image, out ImageType type) { type = default; return Status.NotImplemented; }
		internal static Status GdipGetImageVerticalResolution (IntPtr image, out float resolution) { resolution = default; return Status.NotImplemented; }
		internal static Status GdipGetImageWidth (IntPtr image, out uint width) { width = default; return Status.NotImplemented; }
		internal static Status GdipGetMetafileHeaderFromEmf (IntPtr hEmf, IntPtr header) {  return Status.NotImplemented; }
		internal static Status GdipGetMetafileHeaderFromFile (string filename, IntPtr header) {  return Status.NotImplemented; }
		internal static Status GdipGetMetafileHeaderFromMetafile (IntPtr metafile, IntPtr header) {  return Status.NotImplemented; }
		internal static Status GdipGetMetafileHeaderFromStream (object stream, IntPtr header) {  return Status.NotImplemented; }
		internal static Status GdipGetPropertyCount (IntPtr image, out uint propNumbers) { propNumbers = default; return Status.NotImplemented; }
		internal static Status GdipGetPropertyIdList (IntPtr image, uint propNumbers, int[] list) {  return Status.NotImplemented; }
		internal static Status GdipGetPropertyItem (IntPtr image, int propertyID, int propertySize, IntPtr buffer) {  return Status.NotImplemented; }
		internal static Status GdipGetPropertyItemSize (IntPtr image, int propertyID, out int propertySize) { propertySize = default; return Status.NotImplemented; }
		internal static Status GdipGetPropertySize (IntPtr image, out int bufferSize, out int propNumbers) { bufferSize = default; propNumbers = default; return Status.NotImplemented; }
		internal static Status GdipImageGetFrameCount (IntPtr image, ref Guid guidDimension, out uint count) { count = default; return Status.NotImplemented; }
		internal static Status GdipImageGetFrameDimensionsCount (IntPtr image, out uint count) { count = default; return Status.NotImplemented; }
		internal static Status GdipImageGetFrameDimensionsList (IntPtr image, Guid[] dimensionIDs, uint count) {  return Status.NotImplemented; }
		internal static Status GdipImageRotateFlip (IntPtr image, RotateFlipType rotateFlipType) {  return Status.NotImplemented; }
		internal static Status GdipImageSelectActiveFrame (IntPtr image, ref Guid guidDimension, int frameIndex) {  return Status.NotImplemented; }
		internal static Status GdipLoadImageFromStream (object stream, out IntPtr image) { image = default; return Status.NotImplemented; }
		internal static Status GdipPlayMetafileRecord (IntPtr metafile, EmfPlusRecordType recordType, int flags, int dataSize, byte[] data) {  return Status.NotImplemented; }
		internal static Status GdipRecordMetafile (IntPtr hdc, EmfType type, ref RectangleF frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileFileName (string filename, IntPtr hdc, EmfType type, ref RectangleF frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileFileNameI (string filename, IntPtr hdc, EmfType type, ref Rectangle frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileI (IntPtr hdc, EmfType type, ref Rectangle frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileStream (object stream, IntPtr hdc, EmfType type, ref RectangleF frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileStreamI (object stream, IntPtr hdc, EmfType type, ref Rectangle frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = default; return Status.NotImplemented; }
		internal static Status GdipRemovePropertyItem (IntPtr image, int propertyId) {  return Status.NotImplemented; }
		internal static Status GdipSaveAdd (IntPtr image, IntPtr encoderParameters) {  return Status.NotImplemented; }
		internal static Status GdipSaveAddImage (IntPtr image, IntPtr imagenew, IntPtr encoderParameters) {  return Status.NotImplemented; }
		internal static Status GdipSaveImageToFile (IntPtr image, string filename, ref Guid encoderClsID, IntPtr encoderParameters) {  return Status.NotImplemented; }
		internal static Status GdipSaveImageToStream (HandleRef image, object stream, ref Guid clsidEncoder, HandleRef encoderParams) {  return Status.NotImplemented; }
		internal static Status GdipSetImagePalette (IntPtr image, IntPtr palette) {  return Status.NotImplemented; }
		internal static Status GdipCreateMetafileFromDelegate_linux (object a, object b, object c, object d, object e, object f, out IntPtr metafile) { metafile = IntPtr.Zero; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileFromDelegateI_linux (object a, object b, object c, object d, object e, object f, IntPtr hdc, EmfType type, ref Rectangle frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = IntPtr.Zero; return Status.NotImplemented; }
		internal static Status GdipRecordMetafileFromDelegate_linux (object a, object b, object c, object d, object e, object f, IntPtr hdc, EmfType type, ref RectangleF frameRect, MetafileFrameUnit frameUnit, string description, out IntPtr metafile) { metafile = IntPtr.Zero; return Status.NotImplemented; }
		internal static Status GdipGetMetafileHeaderFromDelegate_linux (object a, object b, object c, object d, object e, object f, IntPtr header) { return Status.NotImplemented; }
		internal static Status GdipLoadImageFromDelegate_linux (object a, object b, object c, object d, object e, object f, out IntPtr image) { image = IntPtr.Zero; return Status.NotImplemented; }
		internal static Status GdipSaveImageToDelegate_linux (IntPtr image, object b, object c, object d, object e, object f, ref Guid encoder, IntPtr encoderParams) { return Status.NotImplemented; }
	}
}
