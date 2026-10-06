// The masks the mask designer offers before anyone writes one of their own.
//
// Windows discovers further descriptors from the project through ITypeDiscoveryService; this is the
// built-in set, which is what a designer sees in an ordinary project.

using System.Collections.Generic;
using System.Globalization;

namespace System.Windows.Forms.Design
{
	internal class MaskDescriptorTemplate : MaskDescriptor
	{
		private readonly string mask;
		private readonly string name;
		private readonly string sample;
		private readonly Type validating_type;
		private readonly CultureInfo culture;

		internal MaskDescriptorTemplate (string mask, string name, string sample, Type validatingType,
						 CultureInfo culture)
		{
			this.mask = mask;
			this.name = name;
			this.sample = sample;
			this.validating_type = validatingType;
			this.culture = culture;
		}

		public override string Mask { get { return mask; } }
		public override string Name { get { return name; } }
		public override string Sample { get { return sample; } }
		public override Type ValidatingType { get { return validating_type; } }
		public override CultureInfo Culture { get { return culture; } }

		/// <summary>The standard masks for a culture. Only the English set is carried here, as the
		/// invariant fallback: the rest differ from it only in the separators the samples use, and a
		/// designer writes those masks directly.</summary>
		internal static List<MaskDescriptor> Standard (CultureInfo culture)
		{
			if (culture == null)
				culture = CultureInfo.CurrentCulture;

			var list = new List<MaskDescriptor> ();
			list.Add (new MaskDescriptorTemplate ("00000", "Numeric (5-digits)", "12345", typeof (int), culture));
			list.Add (new MaskDescriptorTemplate ("(999) 000-0000", "Phone number", "5745550123", null, culture));
			list.Add (new MaskDescriptorTemplate ("000-0000", "Phone number no area code", "5550123", null, culture));
			list.Add (new MaskDescriptorTemplate ("00/00/0000", "Short date", "12112003", typeof (DateTime), culture));
			list.Add (new MaskDescriptorTemplate ("00/00/0000 90:00", "Short date and time (US)", "121120031120",
							      typeof (DateTime), culture));
			list.Add (new MaskDescriptorTemplate ("000-00-0000", "Social security number", "000001234", null, culture));
			list.Add (new MaskDescriptorTemplate ("90:00", "Time (US)", "1120", typeof (DateTime), culture));
			list.Add (new MaskDescriptorTemplate ("00:00", "Time (European/Military)", "2320", typeof (DateTime), culture));
			list.Add (new MaskDescriptorTemplate ("00000-9999", "Zip Code", "980526399", null, culture));
			return list;
		}

		public override string ToString ()
		{
			return Name;
		}
	}
}
