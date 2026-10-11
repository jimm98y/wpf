//
// System.Drawing.Pens.cs
//
// Authors:
//   Matt Stump (mstump@swfo.arizona.edu)
//   Ravindra (rkumar@novell.com)
//   Jordi Mas i Hernandez <jordi@ximian.com>
//
// Copyright (C) 2002 Ximian, Inc.  http://www.ximian.com
// Copyright (C) 2004-2005 Novell, Inc. http://www.novell.com
//

//
// Copyright (C) 2004-2005 Novell, Inc (http://www.novell.com)
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

namespace System.Drawing
{
	// One set per thread, as System.Drawing.Common keeps them (Gdip.ThreadData): a brush or pen
	// one thread disposes or changes is that thread's alone.
	public sealed class Pens
	{
		[ThreadStatic] private static Pen aliceblue;
		[ThreadStatic] private static Pen antiquewhite;
		[ThreadStatic] private static Pen aqua;
		[ThreadStatic] private static Pen aquamarine;
		[ThreadStatic] private static Pen azure;
		[ThreadStatic] private static Pen beige;
		[ThreadStatic] private static Pen bisque;
		[ThreadStatic] private static Pen black;
		[ThreadStatic] private static Pen blanchedalmond;
		[ThreadStatic] private static Pen blue;
		[ThreadStatic] private static Pen blueviolet;
		[ThreadStatic] private static Pen brown;
		[ThreadStatic] private static Pen burlywood;
		[ThreadStatic] private static Pen cadetblue;
		[ThreadStatic] private static Pen chartreuse;
		[ThreadStatic] private static Pen chocolate;
		[ThreadStatic] private static Pen coral;
		[ThreadStatic] private static Pen cornflowerblue;
		[ThreadStatic] private static Pen cornsilk;
		[ThreadStatic] private static Pen crimson;
		[ThreadStatic] private static Pen cyan;
		[ThreadStatic] private static Pen darkblue;
		[ThreadStatic] private static Pen darkcyan;
		[ThreadStatic] private static Pen darkgoldenrod;
		[ThreadStatic] private static Pen darkgray;
		[ThreadStatic] private static Pen darkgreen;
		[ThreadStatic] private static Pen darkkhaki;
		[ThreadStatic] private static Pen darkmagenta;
		[ThreadStatic] private static Pen darkolivegreen;
		[ThreadStatic] private static Pen darkorange;
		[ThreadStatic] private static Pen darkorchid;
		[ThreadStatic] private static Pen darkred;
		[ThreadStatic] private static Pen darksalmon;
		[ThreadStatic] private static Pen darkseagreen;
		[ThreadStatic] private static Pen darkslateblue;
		[ThreadStatic] private static Pen darkslategray;
		[ThreadStatic] private static Pen darkturquoise;
		[ThreadStatic] private static Pen darkviolet;
		[ThreadStatic] private static Pen deeppink;
		[ThreadStatic] private static Pen deepskyblue;
		[ThreadStatic] private static Pen dimgray;
		[ThreadStatic] private static Pen dodgerblue;
		[ThreadStatic] private static Pen firebrick;
		[ThreadStatic] private static Pen floralwhite;
		[ThreadStatic] private static Pen forestgreen;
		[ThreadStatic] private static Pen fuchsia;
		[ThreadStatic] private static Pen gainsboro;
		[ThreadStatic] private static Pen ghostwhite;
		[ThreadStatic] private static Pen gold;
		[ThreadStatic] private static Pen goldenrod;
		[ThreadStatic] private static Pen gray;
		[ThreadStatic] private static Pen green;
		[ThreadStatic] private static Pen greenyellow;
		[ThreadStatic] private static Pen honeydew;
		[ThreadStatic] private static Pen hotpink;
		[ThreadStatic] private static Pen indianred;
		[ThreadStatic] private static Pen indigo;
		[ThreadStatic] private static Pen ivory;
		[ThreadStatic] private static Pen khaki;
		[ThreadStatic] private static Pen lavender;
		[ThreadStatic] private static Pen lavenderblush;
		[ThreadStatic] private static Pen lawngreen;
		[ThreadStatic] private static Pen lemonchiffon;
		[ThreadStatic] private static Pen lightblue;
		[ThreadStatic] private static Pen lightcoral;
		[ThreadStatic] private static Pen lightcyan;
		[ThreadStatic] private static Pen lightgoldenrodyellow;
		[ThreadStatic] private static Pen lightgray;
		[ThreadStatic] private static Pen lightgreen;
		[ThreadStatic] private static Pen lightpink;
		[ThreadStatic] private static Pen lightsalmon;
		[ThreadStatic] private static Pen lightseagreen;
		[ThreadStatic] private static Pen lightskyblue;
		[ThreadStatic] private static Pen lightslategray;
		[ThreadStatic] private static Pen lightsteelblue;
		[ThreadStatic] private static Pen lightyellow;
		[ThreadStatic] private static Pen lime;
		[ThreadStatic] private static Pen limegreen;
		[ThreadStatic] private static Pen linen;
		[ThreadStatic] private static Pen magenta;
		[ThreadStatic] private static Pen maroon;
		[ThreadStatic] private static Pen mediumaquamarine;
		[ThreadStatic] private static Pen mediumblue;
		[ThreadStatic] private static Pen mediumorchid;
		[ThreadStatic] private static Pen mediumpurple;
		[ThreadStatic] private static Pen mediumseagreen;
		[ThreadStatic] private static Pen mediumslateblue;
		[ThreadStatic] private static Pen mediumspringgreen;
		[ThreadStatic] private static Pen mediumturquoise;
		[ThreadStatic] private static Pen mediumvioletred;
		[ThreadStatic] private static Pen midnightblue;
		[ThreadStatic] private static Pen mintcream;
		[ThreadStatic] private static Pen mistyrose;
		[ThreadStatic] private static Pen moccasin;
		[ThreadStatic] private static Pen navajowhite;
		[ThreadStatic] private static Pen navy;
		[ThreadStatic] private static Pen oldlace;
		[ThreadStatic] private static Pen olive;
		[ThreadStatic] private static Pen olivedrab;
		[ThreadStatic] private static Pen orange;
		[ThreadStatic] private static Pen orangered;
		[ThreadStatic] private static Pen orchid;
		[ThreadStatic] private static Pen palegoldenrod;
		[ThreadStatic] private static Pen palegreen;
		[ThreadStatic] private static Pen paleturquoise;
		[ThreadStatic] private static Pen palevioletred;
		[ThreadStatic] private static Pen papayawhip;
		[ThreadStatic] private static Pen peachpuff;
		[ThreadStatic] private static Pen peru;
		[ThreadStatic] private static Pen pink;
		[ThreadStatic] private static Pen plum;
		[ThreadStatic] private static Pen powderblue;
		[ThreadStatic] private static Pen purple;
		[ThreadStatic] private static Pen red;
		[ThreadStatic] private static Pen rosybrown;
		[ThreadStatic] private static Pen royalblue;
		[ThreadStatic] private static Pen saddlebrown;
		[ThreadStatic] private static Pen salmon;
		[ThreadStatic] private static Pen sandybrown;
		[ThreadStatic] private static Pen seagreen;
		[ThreadStatic] private static Pen seashell;
		[ThreadStatic] private static Pen sienna;
		[ThreadStatic] private static Pen silver;
		[ThreadStatic] private static Pen skyblue;
		[ThreadStatic] private static Pen slateblue;
		[ThreadStatic] private static Pen slategray;
		[ThreadStatic] private static Pen snow;
		[ThreadStatic] private static Pen springgreen;
		[ThreadStatic] private static Pen steelblue;
		[ThreadStatic] private static Pen tan;
		[ThreadStatic] private static Pen teal;
		[ThreadStatic] private static Pen thistle;
		[ThreadStatic] private static Pen tomato;
		[ThreadStatic] private static Pen transparent;
		[ThreadStatic] private static Pen turquoise;
		[ThreadStatic] private static Pen violet;
		[ThreadStatic] private static Pen wheat;
		[ThreadStatic] private static Pen white;
		[ThreadStatic] private static Pen whitesmoke;
		[ThreadStatic] private static Pen yellow;
		[ThreadStatic] private static Pen yellowgreen;

		private Pens () { }

		public static Pen AliceBlue {
			get {
				if (aliceblue == null) {
					aliceblue = new Pen (Color.AliceBlue);
					aliceblue.isModifiable = false;
				}
				return aliceblue;
			}
		}

		public static Pen AntiqueWhite {
			get {
				if (antiquewhite == null) {
					antiquewhite = new Pen (Color.AntiqueWhite);
					antiquewhite.isModifiable = false;
				}
				return antiquewhite;
			}
		}

		public static Pen Aqua {
			get {
				if (aqua == null) {
					aqua = new Pen (Color.Aqua);
					aqua.isModifiable = false;
				}
				return aqua;
			}
		}

		public static Pen Aquamarine {
			get {
				if (aquamarine == null) {
					aquamarine = new Pen (Color.Aquamarine);
					aquamarine.isModifiable = false;
				}
				return aquamarine;
			}
		}

		public static Pen Azure {
			get {
				if (azure == null) {
					azure = new Pen (Color.Azure);
					azure.isModifiable = false;
				}
				return azure;
			}
		}

		public static Pen Beige {
			get {
				if (beige == null) {
					beige = new Pen (Color.Beige);
					beige.isModifiable = false;
				}
				return beige;
			}
		}

		public static Pen Bisque {
			get {
				if (bisque == null) {
					bisque = new Pen (Color.Bisque);
					bisque.isModifiable = false;
				}
				return bisque;
			}
		}

		public static Pen Black {
			get {
				if (black == null) {
					black = new Pen (Color.Black);
					black.isModifiable = false;
				}
				return black;
			}
		}

		public static Pen BlanchedAlmond {
			get {
				if (blanchedalmond == null) {
					blanchedalmond = new Pen (Color.BlanchedAlmond);
					blanchedalmond.isModifiable = false;
				}
				return blanchedalmond;
			}
		}

		public static Pen Blue {
			get {
				if (blue == null) {
					blue = new Pen (Color.Blue);
					blue.isModifiable = false;
				}
				return blue;
			}
		}

		public static Pen BlueViolet {
			get {
				if (blueviolet == null) {
					blueviolet = new Pen (Color.BlueViolet);
					blueviolet.isModifiable = false;
				}
				return blueviolet;
			}
		}

		public static Pen Brown {
			get {
				if (brown == null) {
					brown = new Pen (Color.Brown);
					brown.isModifiable = false;
				}
				return brown;
			}
		}

		public static Pen BurlyWood {
			get {
				if (burlywood == null) {
					burlywood = new Pen (Color.BurlyWood);
					burlywood.isModifiable = false;
				}
				return burlywood;
			}
		}

		public static Pen CadetBlue {
			get {
				if (cadetblue == null) {
					cadetblue = new Pen (Color.CadetBlue);
					cadetblue.isModifiable = false;
				}
				return cadetblue;
			}
		}

		public static Pen Chartreuse {
			get {
				if (chartreuse == null) {
					chartreuse = new Pen (Color.Chartreuse);
					chartreuse.isModifiable = false;
				}
				return chartreuse;
			}
		}

		public static Pen Chocolate {
			get {
				if (chocolate == null) {
					chocolate = new Pen (Color.Chocolate);
					chocolate.isModifiable = false;
				}
				return chocolate;
			}
		}

		public static Pen Coral {
			get {
				if (coral == null) {
					coral = new Pen (Color.Coral);
					coral.isModifiable = false;
				}
				return coral;
			}
		}

		public static Pen CornflowerBlue {
			get {
				if (cornflowerblue == null) {
					cornflowerblue = new Pen (Color.CornflowerBlue);
					cornflowerblue.isModifiable = false;
				}
				return cornflowerblue;
			}
		}

		public static Pen Cornsilk {
			get {
				if (cornsilk == null) {
					cornsilk = new Pen (Color.Cornsilk);
					cornsilk.isModifiable = false;
				}
				return cornsilk;
			}
		}

		public static Pen Crimson {
			get {
				if (crimson == null) {
					crimson = new Pen (Color.Crimson);
					crimson.isModifiable = false;
				}
				return crimson;
			}
		}

		public static Pen Cyan {
			get {
				if (cyan == null) {
					cyan = new Pen (Color.Cyan);
					cyan.isModifiable = false;
				}
				return cyan;
			}
		}

		public static Pen DarkBlue {
			get {
				if (darkblue == null) {
					darkblue = new Pen (Color.DarkBlue);
					darkblue.isModifiable = false;
				}
				return darkblue;
			}
		}

		public static Pen DarkCyan {
			get {
				if (darkcyan == null) {
					darkcyan = new Pen (Color.DarkCyan);
					darkcyan.isModifiable = false;
				}
				return darkcyan;
			}
		}

		public static Pen DarkGoldenrod {
			get {
				if (darkgoldenrod == null) {
					darkgoldenrod = new Pen (Color.DarkGoldenrod);
					darkgoldenrod.isModifiable = false;
				}
				return darkgoldenrod;
			}
		}

		public static Pen DarkGray {
			get {
				if (darkgray == null) {
					darkgray = new Pen (Color.DarkGray);
					darkgray.isModifiable = false;
				}
				return darkgray;
			}
		}

		public static Pen DarkGreen {
			get {
				if (darkgreen == null) {
					darkgreen = new Pen (Color.DarkGreen);
					darkgreen.isModifiable = false;
				}
				return darkgreen;
			}
		}

		public static Pen DarkKhaki {
			get {
				if (darkkhaki == null) {
					darkkhaki = new Pen (Color.DarkKhaki);
					darkkhaki.isModifiable = false;
				}
				return darkkhaki;
			}
		}

		public static Pen DarkMagenta {
			get {
				if (darkmagenta == null) {
					darkmagenta = new Pen (Color.DarkMagenta);
					darkmagenta.isModifiable = false;
				}
				return darkmagenta;
			}
		}

		public static Pen DarkOliveGreen {
			get {
				if (darkolivegreen == null) {
					darkolivegreen = new Pen (Color.DarkOliveGreen);
					darkolivegreen.isModifiable = false;
				}
				return darkolivegreen;
			}
		}

		public static Pen DarkOrange {
			get {
				if (darkorange == null) {
					darkorange = new Pen (Color.DarkOrange);
					darkorange.isModifiable = false;
				}
				return darkorange;
			}
		}

		public static Pen DarkOrchid {
			get {
				if (darkorchid == null) {
					darkorchid = new Pen (Color.DarkOrchid);
					darkorchid.isModifiable = false;
				}
				return darkorchid;
			}
		}

		public static Pen DarkRed {
			get {
				if (darkred == null) {
					darkred = new Pen (Color.DarkRed);
					darkred.isModifiable = false;
				}
				return darkred;
			}
		}

		public static Pen DarkSalmon {
			get {
				if (darksalmon == null) {
					darksalmon = new Pen (Color.DarkSalmon);
					darksalmon.isModifiable = false;
				}
				return darksalmon;
			}
		}

		public static Pen DarkSeaGreen {
			get {
				if (darkseagreen == null) {
					darkseagreen = new Pen (Color.DarkSeaGreen);
					darkseagreen.isModifiable = false;
				}
				return darkseagreen;
			}
		}

		public static Pen DarkSlateBlue {
			get {
				if (darkslateblue == null) {
					darkslateblue = new Pen (Color.DarkSlateBlue);
					darkslateblue.isModifiable = false;
				}
				return darkslateblue;
			}
		}

		public static Pen DarkSlateGray {
			get {
				if (darkslategray == null) {
					darkslategray = new Pen (Color.DarkSlateGray);
					darkslategray.isModifiable = false;
				}
				return darkslategray;
			}
		}

		public static Pen DarkTurquoise {
			get {
				if (darkturquoise == null) {
					darkturquoise = new Pen (Color.DarkTurquoise);
					darkturquoise.isModifiable = false;
				}
				return darkturquoise;
			}
		}

		public static Pen DarkViolet {
			get {
				if (darkviolet == null) {
					darkviolet = new Pen (Color.DarkViolet);
					darkviolet.isModifiable = false;
				}
				return darkviolet;
			}
		}

		public static Pen DeepPink {
			get {
				if (deeppink == null) {
					deeppink = new Pen (Color.DeepPink);
					deeppink.isModifiable = false;
				}
				return deeppink;
			}
		}

		public static Pen DeepSkyBlue {
			get {
				if (deepskyblue == null) {
					deepskyblue = new Pen (Color.DeepSkyBlue);
					deepskyblue.isModifiable = false;
				}
				return deepskyblue;
			}
		}

		public static Pen DimGray {
			get {
				if (dimgray == null) {
					dimgray = new Pen (Color.DimGray);
					dimgray.isModifiable = false;
				}
				return dimgray;
			}
		}

		public static Pen DodgerBlue {
			get {
				if (dodgerblue == null) {
					dodgerblue = new Pen (Color.DodgerBlue);
					dodgerblue.isModifiable = false;
				}
				return dodgerblue;
			}
		}

		public static Pen Firebrick {
			get {
				if (firebrick == null) {
					firebrick = new Pen (Color.Firebrick);
					firebrick.isModifiable = false;
				}
				return firebrick;
			}
		}

		public static Pen FloralWhite {
			get {
				if (floralwhite == null) {
					floralwhite = new Pen (Color.FloralWhite);
					floralwhite.isModifiable = false;
				}
				return floralwhite;
			}
		}

		public static Pen ForestGreen {
			get {
				if (forestgreen == null) {
					forestgreen = new Pen (Color.ForestGreen);
					forestgreen.isModifiable = false;
				}
				return forestgreen;
			}
		}

		public static Pen Fuchsia {
			get {
				if (fuchsia == null) {
					fuchsia = new Pen (Color.Fuchsia);
					fuchsia.isModifiable = false;
				}
				return fuchsia;
			}
		}

		public static Pen Gainsboro {
			get {
				if (gainsboro == null) {
					gainsboro = new Pen (Color.Gainsboro);
					gainsboro.isModifiable = false;
				}
				return gainsboro;
			}
		}

		public static Pen GhostWhite {
			get {
				if (ghostwhite == null) {
					ghostwhite = new Pen (Color.GhostWhite);
					ghostwhite.isModifiable = false;
				}
				return ghostwhite;
			}
		}

		public static Pen Gold {
			get {
				if (gold == null) {
					gold = new Pen (Color.Gold);
					gold.isModifiable = false;
				}
				return gold;
			}
		}

		public static Pen Goldenrod {
			get {
				if (goldenrod == null) {
					goldenrod = new Pen (Color.Goldenrod);
					goldenrod.isModifiable = false;
				}
				return goldenrod;
			}
		}

		public static Pen Gray {
			get {
				if (gray == null) {
					gray = new Pen (Color.Gray);
					gray.isModifiable = false;
				}				
				return gray;
			}
		}

		public static Pen Green {
			get {
				if (green == null) {
					green = new Pen (Color.Green);
					green.isModifiable = false;
				}
				return green;
			}
		}

		public static Pen GreenYellow {
			get {
				if (greenyellow == null) {
					greenyellow = new Pen (Color.GreenYellow);
					greenyellow.isModifiable = false;
				}
				return greenyellow;
			}
		}

		public static Pen Honeydew {
			get {
				if (honeydew == null) {
					honeydew = new Pen (Color.Honeydew);
					honeydew.isModifiable = false;
				}
				return honeydew;
			}
		}

		public static Pen HotPink {
			get {
				if (hotpink == null) {
					hotpink = new Pen (Color.HotPink);
					hotpink.isModifiable = false;
				}
				return hotpink;
			}
		}

		public static Pen IndianRed {
			get {
				if (indianred == null) {
					indianred = new Pen (Color.IndianRed);
					indianred.isModifiable = false;
				}
				return indianred;
			}
		}

		public static Pen Indigo {
			get {
				if (indigo == null) {
					indigo = new Pen (Color.Indigo);
					indigo.isModifiable = false;
				}
				return indigo;
			}
		}

		public static Pen Ivory {
			get {
				if (ivory == null) {
					ivory = new Pen (Color.Ivory);
					ivory.isModifiable = false;
				}
				return ivory;
			}
		}

		public static Pen Khaki {
			get {
				if (khaki == null) {
					khaki = new Pen (Color.Khaki);
					khaki.isModifiable = false;
				}
				return khaki;
			}
		}

		public static Pen Lavender {
			get {
				if (lavender == null) {
					lavender = new Pen (Color.Lavender);
					lavender.isModifiable = false;
				}
				return lavender;
			}
		}

		public static Pen LavenderBlush {
			get {
				if (lavenderblush == null) {
					lavenderblush = new Pen (Color.LavenderBlush);
					lavenderblush.isModifiable = false;
				}
				return lavenderblush;
			}
		}

		public static Pen LawnGreen {
			get {
				if (lawngreen == null) {
					lawngreen = new Pen (Color.LawnGreen);
					lawngreen.isModifiable = false;
				}
				return lawngreen;
			}
		}

		public static Pen LemonChiffon {
			get {
				if (lemonchiffon == null) {
					lemonchiffon = new Pen (Color.LemonChiffon);
					lemonchiffon.isModifiable = false;
				}
				return lemonchiffon;
			}
		}

		public static Pen LightBlue {
			get {
				if (lightblue == null) {
					lightblue = new Pen (Color.LightBlue);
					lightblue.isModifiable = false;
				}
				return lightblue;
			}
		}

		public static Pen LightCoral {
			get {
				if (lightcoral == null) {
					lightcoral = new Pen (Color.LightCoral);
					lightcoral.isModifiable = false;
				}
				return lightcoral;
			}
		}

		public static Pen LightCyan {
			get {
				if (lightcyan == null) {
					lightcyan = new Pen (Color.LightCyan);
					lightcyan.isModifiable = false;
				}
				return lightcyan;
			}
		}

		public static Pen LightGoldenrodYellow {
			get {
				if (lightgoldenrodyellow == null) {
					lightgoldenrodyellow = new Pen (Color.LightGoldenrodYellow);
					lightgoldenrodyellow.isModifiable = false;
				}
				return lightgoldenrodyellow;
			}
		}

		public static Pen LightGray {
			get {
				if (lightgray == null) {
					lightgray = new Pen (Color.LightGray);
					lightgray.isModifiable = false;
				}
				return lightgray;
			}
		}

		public static Pen LightGreen {
			get {
				if (lightgreen == null) {
					lightgreen = new Pen (Color.LightGreen);
					lightgreen.isModifiable = false;
				}
				return lightgreen;
			}
		}

		public static Pen LightPink {
			get {
				if (lightpink == null) {
					lightpink = new Pen (Color.LightPink);
					lightpink.isModifiable = false;
				}
				return lightpink;
			}
		}

		public static Pen LightSalmon {
			get {
				if (lightsalmon == null) {
					lightsalmon = new Pen (Color.LightSalmon);
					lightsalmon.isModifiable = false;
				}
				return lightsalmon;
			}
		}

		public static Pen LightSeaGreen {
			get {
				if (lightseagreen == null) {
					lightseagreen = new Pen (Color.LightSeaGreen);
					lightseagreen.isModifiable = false;
				}
				return lightseagreen;
			}
		}

		public static Pen LightSkyBlue {
			get {
				if (lightskyblue == null) {
					lightskyblue = new Pen (Color.LightSkyBlue);
					lightskyblue.isModifiable = false;
				}
				return lightskyblue;
			}
		}

		public static Pen LightSlateGray {
			get {
				if (lightslategray == null) {
					lightslategray = new Pen (Color.LightSlateGray);
					lightslategray.isModifiable = false;
				}
				return lightslategray;
			}
		}

		public static Pen LightSteelBlue {
			get {
				if (lightsteelblue == null) {
					lightsteelblue = new Pen (Color.LightSteelBlue);
					lightsteelblue.isModifiable = false;
				}
				return lightsteelblue;
			}
		}

		public static Pen LightYellow {
			get {
				if (lightyellow == null) {
					lightyellow = new Pen (Color.LightYellow);
					lightyellow.isModifiable = false;
				}
				return lightyellow;
			}
		}

		public static Pen Lime {
			get {
				if (lime == null) {
					lime = new Pen (Color.Lime);
					lime.isModifiable = false;
				}
				return lime;
			}
		}

		public static Pen LimeGreen {
			get {
				if (limegreen == null) {
					limegreen = new Pen (Color.LimeGreen);
					limegreen.isModifiable = false;
				}
				return limegreen;
			}
		}

		public static Pen Linen {
			get {
				if (linen == null) {
					linen = new Pen (Color.Linen);
					linen.isModifiable = false;
				}
				return linen;
			}
		}

		public static Pen Magenta {
			get {
				if (magenta == null) {
					magenta = new Pen (Color.Magenta);
					magenta.isModifiable = false;
				}
				return magenta;
			}
		}

		public static Pen Maroon {
			get {
				if (maroon == null) {
					maroon = new Pen (Color.Maroon);
					maroon.isModifiable = false;
				}
				return maroon;
			}
		}

		public static Pen MediumAquamarine {
			get {
				if (mediumaquamarine == null) {
					mediumaquamarine = new Pen (Color.MediumAquamarine);
					mediumaquamarine.isModifiable = false;
				}
				return mediumaquamarine;
			}
		}

		public static Pen MediumBlue {
			get {
				if (mediumblue == null) {
					mediumblue = new Pen (Color.MediumBlue);
					mediumblue.isModifiable = false;
				}
				return mediumblue;
			}
		}

		public static Pen MediumOrchid {
			get {
				if (mediumorchid == null) {
					mediumorchid = new Pen (Color.MediumOrchid);
					mediumorchid.isModifiable = false;
				}
				return mediumorchid;
			}
		}

		public static Pen MediumPurple {
			get {
				if (mediumpurple == null) {
					mediumpurple = new Pen (Color.MediumPurple);
					mediumpurple.isModifiable = false;
				}
				return mediumpurple;
			}
		}

		public static Pen MediumSeaGreen {
			get {
				if (mediumseagreen == null) {
					mediumseagreen = new Pen (Color.MediumSeaGreen);
					mediumseagreen.isModifiable = false;
				}
				return mediumseagreen;
			}
		}

		public static Pen MediumSlateBlue {
			get {
				if (mediumslateblue == null) {
					mediumslateblue = new Pen (Color.MediumSlateBlue);
					mediumslateblue.isModifiable = false;
				}
				return mediumslateblue;
			}
		}

		public static Pen MediumSpringGreen {
			get {
				if (mediumspringgreen == null) {
					mediumspringgreen = new Pen (Color.MediumSpringGreen);
					mediumspringgreen.isModifiable = false;
				}
				return mediumspringgreen;
			}
		}

		public static Pen MediumTurquoise {
			get {
				if (mediumturquoise == null) {
					mediumturquoise = new Pen (Color.MediumTurquoise);
					mediumturquoise.isModifiable = false;
				}
				return mediumturquoise;
			}
		}

		public static Pen MediumVioletRed {
			get {
				if (mediumvioletred == null) {
					mediumvioletred = new Pen (Color.MediumVioletRed);
					mediumvioletred.isModifiable = false;
				}
				return mediumvioletred;
			}
		}

		public static Pen MidnightBlue {
			get {
				if (midnightblue == null) {
					midnightblue = new Pen (Color.MidnightBlue);
					midnightblue.isModifiable = false;
				}
				return midnightblue;
			}
		}

		public static Pen MintCream {
			get {
				if (mintcream == null) {
					mintcream = new Pen (Color.MintCream);
					mintcream.isModifiable = false;
				}
				return mintcream;
			}
		}

		public static Pen MistyRose {
			get {
				if (mistyrose == null) {
					mistyrose = new Pen (Color.MistyRose);
					mistyrose.isModifiable = false;
				}
				return mistyrose;
			}
		}

		public static Pen Moccasin {
			get {
				if (moccasin == null) {
					moccasin = new Pen (Color.Moccasin);
					moccasin.isModifiable = false;
				}
				return moccasin;
			}
		}

		public static Pen NavajoWhite {
			get {
				if (navajowhite == null) {
					navajowhite = new Pen (Color.NavajoWhite);
					navajowhite.isModifiable = false;
				}
				return navajowhite;
			}
		}

		public static Pen Navy {
			get {
				if (navy == null) {
					navy = new Pen (Color.Navy);
					navy.isModifiable = false;
				}
				return navy;
			}
		}

		public static Pen OldLace {
			get {
				if (oldlace == null) {
					oldlace = new Pen (Color.OldLace);
					oldlace.isModifiable = false;
				}
				return oldlace;
			}
		}

		public static Pen Olive {
			get {
				if (olive == null) {
					olive = new Pen (Color.Olive);
					olive.isModifiable = false;
				}
				return olive;
			}
		}

		public static Pen OliveDrab {
			get {
				if (olivedrab == null) {
					olivedrab = new Pen (Color.OliveDrab);
					olivedrab.isModifiable = false;
				}
				return olivedrab;
			}
		}

		public static Pen Orange {
			get {
				if (orange == null) {
					orange = new Pen (Color.Orange);
					orange.isModifiable = false;
				}
				return orange;
			}
		}

		public static Pen OrangeRed {
			get {
				if (orangered == null) {
					orangered = new Pen (Color.OrangeRed);
					orangered.isModifiable = false;
				}
				return orangered;
			}
		}

		public static Pen Orchid {
			get {
				if (orchid == null) {
					orchid = new Pen (Color.Orchid);
					orchid.isModifiable = false;
				}
				return orchid;
			}
		}

		public static Pen PaleGoldenrod {
			get {
				if (palegoldenrod == null) {
					palegoldenrod = new Pen (Color.PaleGoldenrod);
					palegoldenrod.isModifiable = false;
				}
				return palegoldenrod;
			}
		}

		public static Pen PaleGreen {
			get {
				if (palegreen == null) {
					palegreen = new Pen (Color.PaleGreen);
					palegreen.isModifiable = false;
				}
				return palegreen;
			}
		}

		public static Pen PaleTurquoise {
			get {
				if (paleturquoise == null) {
					paleturquoise = new Pen (Color.PaleTurquoise);
					paleturquoise.isModifiable = false;
				}
				return paleturquoise;
			}
		}

		public static Pen PaleVioletRed {
			get {
				if (palevioletred == null) {
					palevioletred = new Pen (Color.PaleVioletRed);
					palevioletred.isModifiable = false;
				}
				return palevioletred;
			}
		}

		public static Pen PapayaWhip {
			get {
				if (papayawhip == null) {
					papayawhip = new Pen (Color.PapayaWhip);
					papayawhip.isModifiable = false;
				}
				return papayawhip;
			}
		}

		public static Pen PeachPuff {
			get {
				if (peachpuff == null) {
					peachpuff = new Pen (Color.PeachPuff);
					peachpuff.isModifiable = false;
				}
				return peachpuff;
			}
		}

		public static Pen Peru {
			get {
				if (peru == null) {
					peru = new Pen (Color.Peru);
					peru.isModifiable = false;
				}
				return peru;
			}
		}

		public static Pen Pink {
			get {
				if (pink == null) {
					pink = new Pen (Color.Pink);
					pink.isModifiable = false;
				}
				return pink;
			}
		}

		public static Pen Plum {
			get {
				if (plum == null) {
					plum = new Pen (Color.Plum);
					plum.isModifiable = false;
				}
				return plum;
			}
		}

		public static Pen PowderBlue {
			get {
				if (powderblue == null) {
					powderblue = new Pen (Color.PowderBlue);
					powderblue.isModifiable = false;
				}
				return powderblue;
			}
		}

		public static Pen Purple {
			get {
				if (purple == null) {
					purple = new Pen (Color.Purple);
					purple.isModifiable = false;
				}
				return purple;
			}
		}

		public static Pen Red {
			get {
				if (red == null) {
					red = new Pen (Color.Red);
					red.isModifiable = false;
				}
				return red;
			}
		}

		public static Pen RosyBrown {
			get {
				if (rosybrown == null) {
					rosybrown = new Pen (Color.RosyBrown);
					rosybrown.isModifiable = false;
				}
				return rosybrown;
			}
		}

		public static Pen RoyalBlue {
			get {
				if (royalblue == null) {
					royalblue = new Pen (Color.RoyalBlue);
					royalblue.isModifiable = false;
				}
				return royalblue;
			}
		}

		public static Pen SaddleBrown {
			get {
				if (saddlebrown == null) {
					saddlebrown = new Pen (Color.SaddleBrown);
					saddlebrown.isModifiable = false;
				}
				return saddlebrown;
			}
		}

		public static Pen Salmon {
			get {
				if (salmon == null) {
					salmon = new Pen (Color.Salmon);
					salmon.isModifiable = false;
				}
				return salmon;
			}
		}

		public static Pen SandyBrown {
			get {
				if (sandybrown == null) {
					sandybrown = new Pen (Color.SandyBrown);
					sandybrown.isModifiable = false;
				}
				return sandybrown;
			}
		}

		public static Pen SeaGreen {
			get {
				if (seagreen == null) {
					seagreen = new Pen (Color.SeaGreen);
					seagreen.isModifiable = false;
				}
				return seagreen;
			}
		}

		public static Pen SeaShell {
			get {
				if (seashell == null) {
					seashell = new Pen (Color.SeaShell);
					seashell.isModifiable = false;
				}
				return seashell;
			}
		}

		public static Pen Sienna {
			get {
				if (sienna == null) {
					sienna = new Pen (Color.Sienna);
					sienna.isModifiable = false;
				}
				return sienna;
			}
		}

		public static Pen Silver {
			get {
				if (silver == null) {
					silver = new Pen (Color.Silver);
					silver.isModifiable = false;
				}
				return silver;
			}
		}

		public static Pen SkyBlue {
			get {
				if (skyblue == null) {
					skyblue = new Pen (Color.SkyBlue);
					skyblue.isModifiable = false;
				}
				return skyblue;
			}
		}

		public static Pen SlateBlue {
			get {
				if (slateblue == null) {
					slateblue = new Pen (Color.SlateBlue);
					slateblue.isModifiable = false;
				}
				return slateblue;
			}
		}

		public static Pen SlateGray {
			get {
				if (slategray == null) {
					slategray = new Pen (Color.SlateGray);
					slategray.isModifiable = false;
				}
				return slategray;
			}
		}

		public static Pen Snow {
			get {
				if (snow == null) {
					snow = new Pen (Color.Snow);
					snow.isModifiable = false;
				}
				return snow;
			}
		}

		public static Pen SpringGreen {
			get {
				if (springgreen == null) {
					springgreen = new Pen (Color.SpringGreen);
					springgreen.isModifiable = false;
				}
				return springgreen;
			}
		}

		public static Pen SteelBlue {
			get {
				if (steelblue == null) {
					steelblue = new Pen (Color.SteelBlue);
					steelblue.isModifiable = false;
				}
				return steelblue;
			}
		}

		public static Pen Tan {
			get {
				if (tan == null) {
					tan = new Pen (Color.Tan);
					tan.isModifiable = false;
				}
				return tan;
			}
		}

		public static Pen Teal {
			get {
				if (teal == null) {
					teal = new Pen (Color.Teal);
					teal.isModifiable = false;
				}
				return teal;
			}
		}

		public static Pen Thistle {
			get {
				if (thistle == null) {
					thistle = new Pen (Color.Thistle);
					thistle.isModifiable = false;
				}
				return thistle;
			}
		}

		public static Pen Tomato {
			get {
				if (tomato == null) {
					tomato = new Pen (Color.Tomato);
					tomato.isModifiable = false;
				}
				return tomato;
			}
		}

		public static Pen Transparent {
			get {
				if (transparent == null) {
					transparent = new Pen (Color.Transparent);
					transparent.isModifiable = false;
				}
				return transparent;
			}
		}

		public static Pen Turquoise {
			get {
				if (turquoise == null) {
					turquoise = new Pen (Color.Turquoise);
					turquoise.isModifiable = false;
				}
				return turquoise;
			}
		}

		public static Pen Violet {
			get {
				if (violet == null) {
					violet = new Pen (Color.Violet);
					violet.isModifiable = false;
				}
				return violet;
			}
		}

		public static Pen Wheat {
			get {
				if (wheat == null) {
					wheat = new Pen (Color.Wheat);
					wheat.isModifiable = false;
				}
				return wheat;
			}
		}

		public static Pen White {
			get {
				if (white == null) {
					white = new Pen (Color.White);
					white.isModifiable = false;
				}
				return white;
			}
		}

		public static Pen WhiteSmoke {
			get {
				if (whitesmoke == null) {
					whitesmoke = new Pen (Color.WhiteSmoke);
					whitesmoke.isModifiable = false;
				}
				return whitesmoke;
			}
		}

		public static Pen Yellow {
			get {
				if (yellow == null) {
					yellow = new Pen (Color.Yellow);
					yellow.isModifiable = false;
				}
				return yellow;
			}
		}

		public static Pen YellowGreen {
			get {
				if (yellowgreen == null) {
					yellowgreen = new Pen (Color.YellowGreen);
					yellowgreen.isModifiable = false;
				}
				return yellowgreen;
				
			}
		}
	}
}
