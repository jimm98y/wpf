//
// System.Drawing.Region.cs
//
// Author:
//	Miguel de Icaza (miguel@ximian.com)
//      Jordi Mas i Hernandez (jordi@ximian.com)
//
// Copyright (C) 2003 Ximian, Inc. http://www.ximian.com
// Copyright (C) 2004,2006 Novell, Inc. http://www.novell.com
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

using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace System.Drawing
{
	public sealed class Region : MarshalByRefObject, IDisposable
	{
                private IntPtr nativeRegion = IntPtr.Zero;
                
		public Region()
		{
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipCreateRegion (out nativeRegion);
			GDIPlus.CheckStatus (status);
		}

                internal Region(IntPtr native)
		{
                        nativeRegion = native; 
                }
                
		public Region (GraphicsPath path)
		{
			if (path == null)
				throw new ArgumentNullException ("path");
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipCreateRegionPath (path.nativePath, out nativeRegion);
			GDIPlus.CheckStatus (status);
		}

		public Region (Rectangle rect)                
		{
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipCreateRegionRectI (ref rect, out nativeRegion);
			GDIPlus.CheckStatus (status);
		}

		public Region (RectangleF rect)
		{
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipCreateRegionRect (ref rect, out nativeRegion);
			GDIPlus.CheckStatus (status);
		}

		public Region (RegionData rgnData)
		{
			if (rgnData == null)
				throw new ArgumentNullException ("rgnData");
			// a NullReferenceException can be throw for rgnData.Data.Length (if rgnData.Data is null) just like MS
			if (rgnData.Data.Length == 0)
				throw new ArgumentException ("rgnData");
			if (!GDIPlus.Initialized) return;
			Status status = GDIPlus.GdipCreateRegionRgnData (rgnData.Data, rgnData.Data.Length, out nativeRegion);
			GDIPlus.CheckStatus (status);
		}
		
		//                                                                                                     
		// Union
		//

		public void Union (GraphicsPath path)
		{
			if (nativeRegion == IntPtr.Zero) return;
			if (path == null)
				throw new ArgumentNullException ("path");
			Status status = GDIPlus.GdipCombineRegionPath (nativeRegion, path.nativePath, CombineMode.Union);
                        GDIPlus.CheckStatus (status);                        
		}


		public void Union (Rectangle rect)
		{                                    
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRectI (nativeRegion, ref rect, CombineMode.Union);
                        GDIPlus.CheckStatus (status);
		}

		public void Union (RectangleF rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRect (nativeRegion, ref rect, CombineMode.Union);
                        GDIPlus.CheckStatus (status);
		}

		public void Union (Region region)
		{
			if (nativeRegion == IntPtr.Zero) return;
			if (region == null)
				throw new ArgumentNullException ("region");
                        Status status = GDIPlus.GdipCombineRegionRegion (nativeRegion, region.NativeObject, CombineMode.Union);
                        GDIPlus.CheckStatus (status);
		}                                                                                         

		
		//
		// Intersect
		//
		public void Intersect (GraphicsPath path)
                {
			if (nativeRegion == IntPtr.Zero) return;
			if (path == null)
				throw new ArgumentNullException ("path");
                        Status status = GDIPlus.GdipCombineRegionPath (nativeRegion, path.nativePath, CombineMode.Intersect);
                        GDIPlus.CheckStatus (status);  
		}

		public void Intersect (Rectangle rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRectI (nativeRegion, ref rect, CombineMode.Intersect);
                        GDIPlus.CheckStatus (status);
		}

		public void Intersect (RectangleF rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRect (nativeRegion, ref rect, CombineMode.Intersect);
                        GDIPlus.CheckStatus (status);
		}

                public void Intersect (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
                        Status status = GDIPlus.GdipCombineRegionRegion (nativeRegion, region.NativeObject, CombineMode.Intersect);
                        GDIPlus.CheckStatus (status);
		}

		//
		// Complement
		//
		public void Complement (GraphicsPath path)
		{
			if (nativeRegion == IntPtr.Zero) return;
			if (path == null)
				throw new ArgumentNullException ("path");
                        Status status = GDIPlus.GdipCombineRegionPath (nativeRegion, path.nativePath, CombineMode.Complement);
                        GDIPlus.CheckStatus (status);  
		}

		public void Complement (Rectangle rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRectI (nativeRegion, ref rect, CombineMode.Complement);
                        GDIPlus.CheckStatus (status);
		}

		public void Complement (RectangleF rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRect (nativeRegion, ref rect, CombineMode.Complement);
                        GDIPlus.CheckStatus (status);
		}

                public void Complement (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
                        Status status = GDIPlus.GdipCombineRegionRegion (nativeRegion, region.NativeObject, CombineMode.Complement);
                        GDIPlus.CheckStatus (status);
		}

		//
		// Exclude
		//
		public void Exclude (GraphicsPath path)
		{
			if (nativeRegion == IntPtr.Zero) return;
			if (path == null)
				throw new ArgumentNullException ("path");
                        Status status = GDIPlus.GdipCombineRegionPath (nativeRegion, path.nativePath, CombineMode.Exclude);
                        GDIPlus.CheckStatus (status);                                                   
		}

		public void Exclude (Rectangle rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRectI (nativeRegion, ref rect, CombineMode.Exclude);
                        GDIPlus.CheckStatus (status);
		}

		public void Exclude (RectangleF rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRect (nativeRegion, ref rect, CombineMode.Exclude);
                        GDIPlus.CheckStatus (status);
		}

                public void Exclude (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
                        Status status = GDIPlus.GdipCombineRegionRegion (nativeRegion, region.NativeObject, CombineMode.Exclude);
                        GDIPlus.CheckStatus (status);
		}

		//
		// Xor
		//
		public void Xor (GraphicsPath path)
		{
			if (nativeRegion == IntPtr.Zero) return;
			if (path == null)
				throw new ArgumentNullException ("path");
                        Status status = GDIPlus.GdipCombineRegionPath (nativeRegion, path.nativePath, CombineMode.Xor);
                        GDIPlus.CheckStatus (status);  
		}

		public void Xor (Rectangle rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRectI (nativeRegion, ref rect, CombineMode.Xor);
                        GDIPlus.CheckStatus (status);
		}

		public void Xor (RectangleF rect)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipCombineRegionRect (nativeRegion, ref rect, CombineMode.Xor);
                        GDIPlus.CheckStatus (status);
		}

                public void Xor (Region region)
		{
			if (region == null)
				throw new ArgumentNullException ("region");
                        Status status = GDIPlus.GdipCombineRegionRegion (nativeRegion, region.NativeObject, CombineMode.Xor);
                        GDIPlus.CheckStatus (status); 
		}

		//
		// GetBounds
		//
		public RectangleF GetBounds (Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return new RectangleF (-4194304f, -4194304f, 8388608f, 8388608f);
			if (g == null)
				throw new ArgumentNullException ("g");
			// A Graphics that is recording has no native object to hand GDI+, so ask the region for
			// its own scans instead -- the same fallback IsEmpty and IsInfinite already take.
			// Control.Invalidate (Region) goes through here, and threw for every caller of it.
			if (NoNativeGraphics (g)) {
				RectangleF[] scans = GetRegionScans (identity);
				if (scans.Length == 0)
					return RectangleF.Empty;
				RectangleF union = scans[0];
				for (int i = 1; i < scans.Length; i++)
					union = RectangleF.Union (union, scans[i]);
				return union;
			}

                        RectangleF rect = new Rectangle();
                        
                        Status status = GDIPlus.GdipGetRegionBounds (nativeRegion, g.NativeObject, ref rect);
                        GDIPlus.CheckStatus (status);

                        return rect;
                }

		//
		// Translate
		//
		public void Translate (int dx, int dy)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipTranslateRegionI (nativeRegion, dx, dy);
                        GDIPlus.CheckStatus (status);   
		}

		public void Translate (float dx, float dy)
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipTranslateRegion (nativeRegion, dx, dy);
                        GDIPlus.CheckStatus (status);
		}

		//
		// IsVisible
		//
		public bool IsVisible (int x, int y, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
                        bool result;
                        
		    	Status status = GDIPlus.GdipIsVisibleRegionPointI (nativeRegion, x, y, ptr, out result);
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (int x, int y, int width, int height)
		{
			if (nativeRegion == IntPtr.Zero) return true;
		        bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRectI (nativeRegion, x, y,
                                width, height, IntPtr.Zero, out result);

                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (int x, int y, int width, int height, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
		        bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRectI (nativeRegion, x, y,
                                width, height, ptr, out result);

                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (Point point)
		{
			if (nativeRegion == IntPtr.Zero) return true;
		        bool result;

		    	Status status = GDIPlus.GdipIsVisibleRegionPointI (nativeRegion, point.X, point.Y,
                                IntPtr.Zero, out result);
                                
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (PointF point)
		{
			if (nativeRegion == IntPtr.Zero) return true;
		       bool result;

		    	Status status = GDIPlus.GdipIsVisibleRegionPoint (nativeRegion, point.X, point.Y,
                                IntPtr.Zero, out result);

                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (Point point, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
                        bool result;

		    	Status status = GDIPlus.GdipIsVisibleRegionPointI (nativeRegion, point.X, point.Y,
                                ptr, out result);

                        GDIPlus.CheckStatus (status);

                        return result;                                                      
		}

		public bool IsVisible (PointF point, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
		        bool result;

		    	Status status = GDIPlus.GdipIsVisibleRegionPoint (nativeRegion, point.X, point.Y,
                                ptr, out result);

                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (Rectangle rect)
		{
			if (nativeRegion == IntPtr.Zero) return true;
		        bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRectI (nativeRegion, rect.X, rect.Y,
                                rect.Width, rect.Height, IntPtr.Zero, out result);

                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (RectangleF rect)
		{
			if (nativeRegion == IntPtr.Zero) return true;
                        bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRect (nativeRegion, rect.X, rect.Y,
                                rect.Width, rect.Height, IntPtr.Zero, out result);

                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (Rectangle rect, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
		        bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRectI (nativeRegion, rect.X, rect.Y,
                                rect.Width, rect.Height, ptr, out result);
                        
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (RectangleF rect, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
			bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRect (nativeRegion, rect.X, rect.Y,
                                rect.Width, rect.Height, ptr, out result);
                                
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (float x, float y)
		{
			if (nativeRegion == IntPtr.Zero) return true;
                        bool result;

		    	Status status = GDIPlus.GdipIsVisibleRegionPoint (nativeRegion, x, y, IntPtr.Zero, out result);
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (float x, float y, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
		        bool result;

		    	Status status = GDIPlus.GdipIsVisibleRegionPoint (nativeRegion, x, y, ptr, out result);
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (float x, float y, float width, float height)
		{
			if (nativeRegion == IntPtr.Zero) return true;
		        bool result;
                        
                        Status status = GDIPlus.GdipIsVisibleRegionRect (nativeRegion, x, y, width, height, IntPtr.Zero, out result);
                        GDIPlus.CheckStatus (status);

                        return result;
		}

		public bool IsVisible (float x, float y, float width, float height, Graphics g) 
		{
			if (nativeRegion == IntPtr.Zero) return true;
			IntPtr ptr = (g == null) ? IntPtr.Zero : g.NativeObject;
                        bool result;

                        Status status = GDIPlus.GdipIsVisibleRegionRect (nativeRegion, x, y, width, height, ptr, out result);
                        GDIPlus.CheckStatus (status);

                        return result;
		}


		//
		// Miscellaneous
		//

		// A recording Graphics -- GPU-raster mode, where drawing is captured as a scene instead of
		// rasterised -- is not a GDI+ surface at all: its native handle is zero. Both queries below
		// hand that handle straight to gdiplus, which answers InvalidParameter, so simply asking
		// whether a region was empty threw. Painting a LinkLabel asks exactly that, so every link
		// label on this stack failed to draw and reported a GDI+ error instead.
		//
		// The graphics argument contributes nothing but a world transform to these two questions,
		// and a recording surface starts at the identity. So when there is no GDI+ graphics to
		// consult, answer from the region's own scan list, which takes a matrix rather than a
		// surface -- no offscreen bitmap has to be conjured up in order to ask.
		static readonly Matrix identity = new Matrix ();

		bool NoNativeGraphics (Graphics g)
		{
			if (g == null)
				throw new ArgumentNullException ("g");
			return g.NativeObject == IntPtr.Zero;
		}

		public bool IsEmpty(Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return false;
			if (NoNativeGraphics (g))
				return GetRegionScans (identity).Length == 0;


                        bool result;               

                        Status status = GDIPlus.GdipIsEmptyRegion (nativeRegion, g.NativeObject, out result);
                        GDIPlus.CheckStatus (status);

                        return result;                        
		}

		public bool IsInfinite(Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return true;
			if (NoNativeGraphics (g)) {
				// GDI+ represents "infinite" as the single scan (-4194304, -4194304) 8388608 square.
				RectangleF[] scans = GetRegionScans (identity);
				return scans.Length == 1 && scans[0].X <= -4194304f && scans[0].Y <= -4194304f
					&& scans[0].Width >= 8388608f && scans[0].Height >= 8388608f;
			}


                        bool result;

                        Status status = GDIPlus.GdipIsInfiniteRegion (nativeRegion, g.NativeObject, out result);
                        GDIPlus.CheckStatus (status);

                        return result;  
		}

		public void MakeEmpty()
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipSetEmpty (nativeRegion);
                        GDIPlus.CheckStatus (status);               
		}

		public void MakeInfinite()
		{
			if (nativeRegion == IntPtr.Zero) return;
                        Status status = GDIPlus.GdipSetInfinite (nativeRegion);
                        GDIPlus.CheckStatus (status);                      
		}
		
		public bool Equals(Region region, Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return false;
			if (region == null)
				throw new ArgumentNullException ("region");
			if (g == null)
				throw new ArgumentNullException ("g");

			bool result;
			
			Status status = GDIPlus.GdipIsEqualRegion (nativeRegion, region.NativeObject,
                           g.NativeObject, out result);                                   
                           
                        GDIPlus.CheckStatus (status);                      
                        
			return result;			
		}
		
		public static Region FromHrgn (IntPtr hrgn)
		{
			if (hrgn == IntPtr.Zero)
				throw new ArgumentException ("hrgn");

			IntPtr handle;
			Status status = GDIPlus.GdipCreateRegionHrgn (hrgn, out handle);
			GDIPlus.CheckStatus (status);

			return new Region (handle);
		}
		
		
		public IntPtr GetHrgn (Graphics g)
		{
			if (nativeRegion == IntPtr.Zero) return IntPtr.Zero;
			// Our WindowsForms implementation uses null to avoid
			// creating a Graphics context when not needed
#if false
			// this is MS behaviour
			if (g == null)
				throw new ArgumentNullException ("g");
#else
			// this is an hack for MWF (libgdiplus would reject that)
			if (g == null)
				return nativeRegion;
#endif
			IntPtr handle = IntPtr.Zero;
			Status status = GDIPlus.GdipGetRegionHRgn (nativeRegion, g.NativeObject, ref handle);
			GDIPlus.CheckStatus (status);
			return handle;
		}
		
		
		public RegionData GetRegionData()
		{
			if (nativeRegion == IntPtr.Zero) return null;
			int size, filled;			
			
			Status status = GDIPlus.GdipGetRegionDataSize (nativeRegion, out size);                  
                        GDIPlus.CheckStatus (status);                      
                        
                        byte[] buff = new byte [size];			
                        
			status = GDIPlus.GdipGetRegionData (nativeRegion, buff, size, out filled);
			GDIPlus.CheckStatus (status);                      
			
			RegionData rgndata = new RegionData (buff);
			
			return rgndata;
		}
		
		
		public RectangleF[] GetRegionScans(Matrix matrix)
		{
			if (nativeRegion == IntPtr.Zero) return new RectangleF [0];
			if (matrix == null)
				throw new ArgumentNullException ("matrix");

			int cnt;
			
			Status status = GDIPlus.GdipGetRegionScansCount (nativeRegion, out cnt, matrix.NativeObject);                  
                        GDIPlus.CheckStatus (status);                                 
                        
                        if (cnt == 0)
                        	return new RectangleF[0];
                                                
                        RectangleF[] rects = new RectangleF [cnt];					
                        int size = Marshal.SizeOf (rects[0]);                  
                        
                        IntPtr dest = Marshal.AllocHGlobal (size * cnt);			
			try {
				status = GDIPlus.GdipGetRegionScans (nativeRegion, dest, out cnt, matrix.NativeObject);
				GDIPlus.CheckStatus (status);
			}
			finally {
				// note: Marshal.FreeHGlobal is called from GDIPlus.FromUnManagedMemoryToRectangles
				GDIPlus.FromUnManagedMemoryToRectangles (dest, rects);
			}
			return rects;			
		}		

		public void Transform(Matrix matrix)
		{
			if (nativeRegion == IntPtr.Zero) return;
			if (matrix == null)
				throw new ArgumentNullException ("matrix");

			Status status = GDIPlus.GdipTransformRegion (nativeRegion, matrix.NativeObject);
			GDIPlus.CheckStatus (status);                      				
		}		
		
		public Region Clone()
		{
			if (nativeRegion == IntPtr.Zero) return new Region ();
			IntPtr cloned;
				
			Status status = GDIPlus.GdipCloneRegion (nativeRegion, out cloned);
			GDIPlus.CheckStatus (status);
				
			return new Region (cloned); 
		}

		public void Dispose ()
		{
			if (nativeRegion == IntPtr.Zero) return;
			DisposeHandle ();
			System.GC.SuppressFinalize (this);
		}

		private void DisposeHandle ()
		{
			if (nativeRegion != IntPtr.Zero) {
				GDIPlus.GdipDeleteRegion (nativeRegion);
				nativeRegion = IntPtr.Zero;
			}
		}

		~Region ()
		{
			DisposeHandle ();
		}

                internal IntPtr NativeObject
                {
			get{
				return nativeRegion;
			}
			set	{
				nativeRegion = value;
			}
		}
		// why is this a instance method ? and not static ?
		public void ReleaseHrgn (IntPtr regionHandle)		
		{
			if (regionHandle == IntPtr.Zero) 
				throw new ArgumentNullException ("regionHandle");

			Status status = Status.Ok;
			if (GDIPlus.RunningOnUnix ()) {
				// for libgdiplus HRGN == GpRegion* 
				status = GDIPlus.GdipDeleteRegion (regionHandle);
			} else {
				// ... but on Windows HRGN are (old) GDI objects
				if (!GDIPlus.DeleteObject (regionHandle))
					status = Status.InvalidParameter;
			}
			GDIPlus.CheckStatus (status);
		}
	}
}
