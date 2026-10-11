//
// System.Drawing.PrintDocument.cs
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
using System.ComponentModel;

namespace System.Drawing.Printing
{
	[DefaultEvent ("PrintPage"), DefaultProperty ("DocumentName")]
	[ToolboxItemFilter ("System.Drawing.Printing", ToolboxItemFilterType.Allow)]
	public class PrintDocument : System.ComponentModel.Component
	{
		private PageSettings defaultpagesettings;
		private PrinterSettings printersettings;
		private PrintController printcontroller;
		private string documentname;
		private bool originAtMargins = false; // .NET V1.1 Beta

		public PrintDocument() {
			documentname = "document"; //offical default.
			printersettings = new PrinterSettings(); // use default values
			defaultpagesettings = (PageSettings) printersettings.DefaultPageSettings.Clone ();
			printcontroller = new StandardPrintController();
		}

		// properties
		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[Browsable (false)]
		[SRDescription ("The settings for the current page.")]
		public PageSettings DefaultPageSettings{
			get{
				return defaultpagesettings;
			}
			set{
				defaultpagesettings = value;
			}
		}

		// Name of the document, not the file!
		[DefaultValue ("document")]
		[SRDescription ("The name of the document.")]
		public string DocumentName{
			get{
				return documentname;
			}
			set{
				documentname = value;
			}
		}

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[Browsable (false)]
		[SRDescription ("The print controller object.")]
		public PrintController PrintController{
			get{
				return printcontroller;
			}
			set{
				printcontroller = value;
			}
		}

		[DesignerSerializationVisibility (DesignerSerializationVisibility.Hidden)]
		[Browsable (false)]
		[SRDescription ("The current settings for the active printer.")]
		public PrinterSettings PrinterSettings{
			get{
				return printersettings;
			}
			set{
				printersettings = value == null ? new PrinterSettings () : value;
			}
		}

		[DefaultValue (false)]
		[SRDescription ("Determines if the origin is set at the specified margins.")]
		public bool OriginAtMargins{
			get{
				return originAtMargins;
			}
			set{
				originAtMargins = value;
			}
		}

		// methods
		// As .NET's PrintController.Print runs it: BeginPrint, the controller's start, a page per
		// PrintPage until HasMorePages is false or a page cancels, and EndPrint and the controller's
		// end whatever happens in between. The Graphics a page draws on is the CONTROLLER's -- a
		// recording one (see RecordedPrinting.cs). This used to make it itself, with
		// Graphics.FromHdc: native GDI+ on the printer DC, which is exactly what printing no longer
		// uses on any platform.
		public void Print(){
			PrintEventArgs printArgs = new PrintEventArgs();
			this.OnBeginPrint(printArgs);
			if (printArgs.Cancel)
				return;
			PrintController.OnStartPrint(this, printArgs);
			if (printArgs.Cancel) {
				this.OnEndPrint(printArgs);
				return;
			}

			bool cancelled = false;
			try {
				PrintPageEventArgs printPageArgs;
				do {
					QueryPageSettingsEventArgs queryPageSettingsArgs = new QueryPageSettingsEventArgs (
							DefaultPageSettings.Clone () as PageSettings);
					OnQueryPageSettings (queryPageSettingsArgs);
					if (queryPageSettingsArgs.Cancel) {
						cancelled = true;
						break;
					}

					PageSettings pageSettings = queryPageSettingsArgs.PageSettings;
					PrintPageGeometry.Bounds (pageSettings, out Rectangle pageBounds, out Rectangle marginBounds);
					printPageArgs = new PrintPageEventArgs (null, marginBounds, pageBounds, pageSettings);
					printPageArgs.GraphicsContext = printArgs.GraphicsContext;
					Graphics pg = PrintController.OnStartPage (this, printPageArgs);
					printPageArgs.SetGraphics (pg);
					try {
						if (!printPageArgs.Cancel)
							this.OnPrintPage (printPageArgs);
					} finally {
						PrintController.OnEndPage (this, printPageArgs);
					}
					if (printPageArgs.Cancel) {
						cancelled = true;
						break;
					}
				} while (printPageArgs.HasMorePages);
			} catch {
				cancelled = true;
				throw;
			} finally {
				try {
					this.OnEndPrint (printArgs);
					printArgs.Cancel |= cancelled;
				} finally {
					PrintController.OnEndPrint (this, printArgs);
				}
			}
		}

		public override string ToString(){
			return "[PrintDocument " + this.DocumentName + "]";
		}
		
		// events
		protected virtual void OnBeginPrint(PrintEventArgs e){
			//fire the event
			if (BeginPrint != null)
				BeginPrint(this, e);
		}
		
		protected virtual void OnEndPrint(PrintEventArgs e){
			//fire the event
			if (EndPrint != null)
				EndPrint(this, e);
		}
		
		protected virtual void OnPrintPage(PrintPageEventArgs e){
			//fire the event
			if (PrintPage != null)
				PrintPage(this, e);
		}
		
		protected virtual void OnQueryPageSettings(QueryPageSettingsEventArgs e){
			//fire the event
			if (QueryPageSettings != null)
				QueryPageSettings(this, e);
		}

		[SRDescription ("Raised when printing begins")]
		public event PrintEventHandler BeginPrint;

		[SRDescription ("Raised when printing ends")]
		public event PrintEventHandler EndPrint;

		[SRDescription ("Raised when printing of a new page begins")]
		public event PrintPageEventHandler PrintPage;

		[SRDescription ("Raised before printing of a new page begins")]
		public event QueryPageSettingsEventHandler QueryPageSettings;
	}
}
