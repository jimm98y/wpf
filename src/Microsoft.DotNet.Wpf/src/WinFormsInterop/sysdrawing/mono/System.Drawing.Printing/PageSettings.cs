//
// System.Drawing.PageSettings.cs
//
// Authors:
//   Dennis Hayes (dennish@Raytek.com)
//   Herve Poussineau (hpoussineau@fr.st)
//   Andreas Nahr (ClassDevelopment@A-SoftTech.com)
//
// (C) 2002 Ximian, Inc
//

//
// Copyright (C) 2004 Novell, Inc (http://www.novell.com)
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
using System.Runtime.InteropServices;

namespace System.Drawing.Printing
{
	[Serializable]
	public class PageSettings : ICloneable
	{
		internal bool color;
		internal bool landscape;
		internal PaperSize paperSize;
		internal PaperSource paperSource;
		internal PrinterResolution printerResolution;

		// create a new default Margins object (is 1 inch for all margins)
		Margins margins = new Margins();
#pragma warning disable 649
		float hardMarginX;
		float hardMarginY;
		RectangleF printableArea;		
		PrinterSettings printerSettings;
#pragma warning restore 649
		
		public PageSettings() : this(new PrinterSettings())
		{
		}
		
		public PageSettings(PrinterSettings printerSettings)
		{
			PrinterSettings = printerSettings;
			
			this.color = printerSettings.DefaultPageSettings.color;
			this.landscape = printerSettings.DefaultPageSettings.landscape;
			this.paperSize = printerSettings.DefaultPageSettings.paperSize;
			this.paperSource = printerSettings.DefaultPageSettings.paperSource;
			this.printerResolution = printerSettings.DefaultPageSettings.printerResolution;
		}
		
		// used by PrinterSettings.DefaultPageSettings
		internal PageSettings(PrinterSettings printerSettings, bool color, bool landscape, PaperSize paperSize, PaperSource paperSource, PrinterResolution printerResolution)
		{
			PrinterSettings = printerSettings;
			this.color = color;
			this.landscape = landscape;
			this.paperSize = paperSize;
			this.paperSource = paperSource;
			this.printerResolution = printerResolution;
		}

		//props
		// The page, as .NET has it: the paper turned for the orientation, in hundredths of an inch.
		// (Mono answered the area inside the margins here, which is MarginBounds' job.)
		public Rectangle Bounds{
			get{
				int width = this.paperSize.Width;
				int height = this.paperSize.Height;
				return this.landscape ? new Rectangle (0, 0, height, width) : new Rectangle (0, 0, width, height);
			}
		}

		public bool Color{
			get{
				if (!this.printerSettings.IsValid)
					throw new InvalidPrinterException(this.printerSettings);
				return color;
			}
			set{
				color = value;
			}
		}
		
		public bool Landscape {
			get{
				if (!this.printerSettings.IsValid)
					throw new InvalidPrinterException(this.printerSettings);
				return landscape;
			}
			set{
				landscape = value;
			}
		}
		
		public Margins Margins{
			get{
				if (!this.printerSettings.IsValid)
					throw new InvalidPrinterException(this.printerSettings);
				return margins;
			}
			set{
				margins = value;
			}
		}
		
		public PaperSize PaperSize{
			get{
				if (!this.printerSettings.IsValid)
					throw new InvalidPrinterException(this.printerSettings);
				return paperSize;
			}
			set{
				if (value != null)
					paperSize = value;
			}
		}
		
		public PaperSource PaperSource{
			get{
				if (!this.printerSettings.IsValid)
					throw new InvalidPrinterException(this.printerSettings);
				return paperSource;
			}
			set{
				if (value != null)
					paperSource = value;
			}
		}
		
		public PrinterResolution PrinterResolution{
			get{
				if (!this.printerSettings.IsValid)
					throw new InvalidPrinterException(this.printerSettings);
				return printerResolution;
			}
			set{
				if (value != null)
					printerResolution = value;
			}
		}
		
		public PrinterSettings PrinterSettings{
			get{
				return printerSettings;
			}
			set{
				printerSettings = value;
			}
		}		
		// What the printer cannot mark at the paper's edges, and what it can: read from the device in
		// this page's mode on Windows, from the print system's imageable area elsewhere. Mono never
		// set any of the three, so every page reported a printer that could print to the very edge.
		public float HardMarginX {
			get {
				EnsureDevice ();
				return hardMarginX;
			}
		}

		public float HardMarginY {
			get {
				EnsureDevice ();
				return hardMarginY;
			}
		}

		public RectangleF PrintableArea {
			get {
				EnsureDevice ();
				return printableArea;
			}
		}

		/// <summary>The margins without the printer check Margins makes: a document laid out for a
		/// PDF needs no printer to have them.</summary>
		internal Margins MarginsUnchecked => margins;

		bool device_known;
		(bool Landscape, int W, int H, string Printer) device_for;

		void EnsureDevice ()
		{
			var key = (landscape, paperSize?.Width ?? 0, paperSize?.Height ?? 0, printerSettings?.PrinterName);
			if (device_known && device_for == key)
				return;
			device_known = true;
			device_for = key;
			PrintPageGeometry.Device (this, out _, out _, out RectangleF printable);
			hardMarginX = printable.X;
			hardMarginY = printable.Y;
			printableArea = printable;
		}


		public object Clone ()
		{
			// We do a deep copy
			PrinterResolution pres = new PrinterResolution (this.printerResolution.Kind, this.printerResolution.X, this.printerResolution.Y);
			PaperSource psource = new PaperSource (this.paperSource.Kind, this.paperSource.SourceName);
			PaperSize psize = new PaperSize (this.paperSize.PaperName, this.paperSize.Width, this.paperSize.Height);
			psize.RawKind = (int)this.paperSize.Kind;

			PageSettings ps = new PageSettings (this.printerSettings, this.color, this.landscape,
					psize, psource, pres);
			ps.Margins = (Margins) this.margins.Clone ();
			return ps;
		}


		public void CopyToHdevmode (IntPtr hdevmode){
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("A DEVMODE is a Windows printer structure.");
			DevModeInterop.CopyToHdevmode (this, hdevmode);
		}

		public void SetHdevmode (IntPtr hdevmode){
			if (!OperatingSystem.IsWindows ())
				throw new PlatformNotSupportedException ("A DEVMODE is a Windows printer structure.");
			if (hdevmode == IntPtr.Zero)
				throw new ArgumentException (nameof (hdevmode));
			DevModeInterop.SetPageHdevmode (this, hdevmode);
		}	

		public override string ToString(){
			string ret = "[PageSettings: Color={0}";
			ret += ", Landscape={1}";
			ret += ", Margins={2}";
			ret += ", PaperSize={3}";
			ret += ", PaperSource={4}";
			ret += ", PrinterResolution={5}";
			ret += "]";
			
			return String.Format(ret, this.color, this.landscape, this.margins, this.paperSize, this.paperSource, this.printerResolution);
		}
	}
}
