//
// System.Windows.Forms.Design.CommandSet
//
// .NET's DocumentDesigner keeps its standard commands (cut, copy, paste, delete, align, ...) in a CommandSet, held in
// its _commandSet field, each command a CommandSet.CommandSetItem whose status is cached, per status handler, in the
// static s_commandStatusHash. A designer host that drops its own handlers from that cache when a document goes (by
// reflection, as .NET exposes no member for it) finds both here, as there. This designer adds its commands as
// DefaultMenuCommands (DocumentDesigner.Initialize) and caches no command status, so the cache stays empty.
//

using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;

namespace System.Windows.Forms.Design
{
	internal class CommandSet : IDisposable
	{
		private readonly ISite _site;

		public CommandSet (ISite site)
		{
			_site = site;
		}

		protected ISite Site => _site;

		public virtual void Dispose ()
		{
		}

		/// <summary>A command of the set: its status as its handler last answered it, kept per handler.</summary>
		protected class CommandSetItem : MenuCommand
		{
			// the status cache: status handler (EventHandler) -> its last answer
			private static readonly Hashtable s_commandStatusHash = new Hashtable ();

			public CommandSetItem (EventHandler handler, CommandID id) : base (handler, id)
			{
			}

			internal static Hashtable StatusCache => s_commandStatusHash;
		}
	}
}
