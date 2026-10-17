// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (System.ComponentModel.Design.DesignerCommandSet): Verbs and ActionLists are what
// GetCommands answers for "Verbs" and "ActionLists", and the base answers null. (The Mono version kept two empty
// collections of its own and threw from GetCommands.)

using System.Collections;

namespace System.ComponentModel.Design
{
	public class DesignerCommandSet
	{
		private protected const string VerbsCommand = "Verbs";
		private protected const string ActionListsCommand = "ActionLists";

		public DesignerCommandSet ()
		{
		}

		public virtual ICollection GetCommands (string name) => null;

		public DesignerVerbCollection Verbs => (DesignerVerbCollection) GetCommands (VerbsCommand);

		public DesignerActionListCollection ActionLists => (DesignerActionListCollection) GetCommands (ActionListsCommand);
	}
}
