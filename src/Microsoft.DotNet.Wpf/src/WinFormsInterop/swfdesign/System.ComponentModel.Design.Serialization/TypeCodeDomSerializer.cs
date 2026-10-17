//
// System.ComponentModel.Design.Serialization.TypeCodeDomSerializer
//
// Authors:
//	  Ivan N. Zlatev (contact i-nZ.net)
//
// (C) 2007 Ivan N. Zlatev

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
using System.Collections;
using System.ComponentModel;
using System.ComponentModel.Design;

using System.CodeDom;

// Serialize and Deserialize as dotnet/winforms has them. Mono's Serialize built the members' statements and then
// dropped them (the declaration came back empty), and Deserialize threw. A declaration is: the root's type as its
// base type, a field per member, and one initialize method (the parameterless constructor) holding the members'
// statements and then the root's.
namespace System.ComponentModel.Design.Serialization
{
	public class TypeCodeDomSerializer : CodeDomSerializerBase
	{
		private static readonly object s_initMethodKey = new object ();

		public TypeCodeDomSerializer ()
		{
		}

		public virtual CodeTypeDeclaration Serialize (IDesignerSerializationManager manager, object root, ICollection members)
		{
			if (root == null)
				throw new ArgumentNullException ("root");
			if (manager == null)
				throw new ArgumentNullException ("manager");

			RootContext rootContext = new RootContext (new CodeThisReferenceExpression (), root);
			StatementContext statementContext = new StatementContext ();
			if (members != null)
				statementContext.StatementCollection.Populate (members);
			statementContext.StatementCollection.Populate (root);
			CodeTypeDeclaration declaration = new CodeTypeDeclaration (manager.GetName (root));
			declaration.BaseTypes.Add (root.GetType ());

			manager.Context.Push (rootContext);
			manager.Context.Push (statementContext);
			manager.Context.Push (declaration);

			try {
				if (members != null) {
					foreach (object member in members)
						if (member != root)
							base.SerializeToExpression (manager, member);
				}
				// The root is "this" (the RootContext), so its serializer answers that expression and writes
				// nothing: its properties and events are written here, as RootCodeDomSerializer does.
				CodeStatementCollection rootStatements = statementContext.StatementCollection [root];
				string rootName = manager.GetName (root);
				if (!string.IsNullOrEmpty (rootName)) {
					rootStatements.Add (new CodeCommentStatement (string.Empty));
					rootStatements.Add (new CodeCommentStatement (rootName));
					rootStatements.Add (new CodeCommentStatement (string.Empty));
				}
				base.SerializeProperties (manager, rootStatements, root, new Attribute [0]);
				base.SerializeEvents (manager, rootStatements, root, new Attribute [0]);
				IntegrateStatements (manager, root, members, statementContext, declaration);
			} finally {
				manager.Context.Pop ();
				manager.Context.Pop ();
				manager.Context.Pop ();
			}

			return declaration;
		}

		private void IntegrateStatements (IDesignerSerializationManager manager, object root, ICollection members,
						  StatementContext statementContext, CodeTypeDeclaration declaration)
		{
			CodeMemberMethod method = GetInitializeMethod (manager, declaration, root);
			if (method == null)
				return;
			if (members != null) {
				foreach (object member in members) {
					if (member == root)
						continue;
					string name = manager.GetName (member);
					if (name != null && !HasField (declaration, name)) {
						var field = new CodeMemberField (TypeDescriptor.GetClassName (member) ?? member.GetType ().FullName, name);
						field.Attributes = MemberAttributes.Private;
						if (TypeDescriptor.GetProperties (member) ["Modifiers"] is PropertyDescriptor modifiers &&
						    modifiers.PropertyType == typeof (MemberAttributes))
							field.Attributes = (MemberAttributes) modifiers.GetValue (member);
						declaration.Members.Add (field);
					}
					if (statementContext.StatementCollection.ContainsKey (member) && statementContext.StatementCollection [member] != null)
						method.Statements.AddRange (statementContext.StatementCollection [member]);
				}
			}
			if (statementContext.StatementCollection.ContainsKey (root) && statementContext.StatementCollection [root] != null)
				method.Statements.AddRange (statementContext.StatementCollection [root]);
			if (!declaration.Members.Contains (method))
				declaration.Members.Add (method);
		}

		private static bool HasField (CodeTypeDeclaration declaration, string name)
		{
			foreach (CodeTypeMember member in declaration.Members) {
				if (member is CodeMemberField && member.Name == name)
					return true;
			}
			return false;
		}

		public virtual object Deserialize (IDesignerSerializationManager manager, CodeTypeDeclaration declaration)
		{
			if (manager == null)
				throw new ArgumentNullException ("manager");
			if (declaration == null)
				throw new ArgumentNullException ("declaration");

			// The root is the first base type that is a class.
			Type baseType = null;
			foreach (CodeTypeReference typeReference in declaration.BaseTypes) {
				Type type = manager.GetType (typeReference.BaseType);
				if (type != null && !type.IsInterface) {
					baseType = type;
					break;
				}
			}
			if (baseType == null) {
				string names = string.Empty;
				foreach (CodeTypeReference typeReference in declaration.BaseTypes)
					names += (names.Length > 0 ? ", " : string.Empty) + typeReference.BaseType;
				throw new CodeDomSerializerException ("Could not find type '" + names + "'. Please make sure that the assembly that contains this type is referenced.", manager);
			}

			object root = manager.CreateInstance (baseType, null, declaration.Name, true);
			RootContext rootContext = new RootContext (new CodeThisReferenceExpression (), root);
			manager.Context.Push (rootContext);
			try {
				// A field with an initializer is created by it.
				foreach (CodeTypeMember member in declaration.Members) {
					if (member is CodeMemberField field && field.InitExpression != null)
						DeserializeExpression (manager, field.Name, field.InitExpression);
				}
				CodeMemberMethod [] methods = GetInitializeMethods (manager, declaration);
				if (methods != null) {
					foreach (CodeMemberMethod method in methods) {
						foreach (CodeStatement statement in method.Statements)
							DeserializeStatement (manager, statement);
					}
				}
			} finally {
				manager.Context.Pop ();
			}
			return root;
		}

		protected virtual CodeMemberMethod GetInitializeMethod (IDesignerSerializationManager manager, CodeTypeDeclaration declaration, object value)
		{
			if (value == null)
				throw new ArgumentNullException ("value");
			if (declaration == null)
				throw new ArgumentNullException ("declaration");
			if (manager == null)
				throw new ArgumentNullException ("manager");

			// One per declaration, whoever asks.
			if (!(declaration.UserData [s_initMethodKey] is CodeConstructor ctor)) {
				ctor = new CodeConstructor { Attributes = MemberAttributes.Public };
				declaration.UserData [s_initMethodKey] = ctor;
			}
			return ctor;
		}

		protected virtual CodeMemberMethod[] GetInitializeMethods (IDesignerSerializationManager manager, CodeTypeDeclaration declaration)
		{
			if (manager == null)
				throw new ArgumentNullException ("manager");
			if (declaration == null)
				throw new ArgumentNullException ("declaration");

			// The declaration's parameterless constructor.
			foreach (CodeTypeMember member in declaration.Members) {
				if (member is CodeConstructor ctor && ctor.Parameters.Count == 0)
					return new CodeMemberMethod [] { ctor };
			}
			return new CodeMemberMethod [0];
		}
	}
}
