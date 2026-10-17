// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Ported from dotnet/winforms (ResourceCodeDomSerializer and its SerializationResourceManager): how a CodeDom
// designer writes localizable property values to the form's resources and reads them back. A value goes to the
// IResourceService's writer for the design culture (with the invariant culture kept complete when a culture other
// than it is being designed), the code gets `resources.GetString("name.Property")` / `(T)resources.GetObject(...)`
// and a `ComponentResourceManager resources = new ComponentResourceManager(typeof(Root))` declaration the first time,
// and metadata (design-time-only values) goes to the ResX metadata section.
//
// .NET also records each value in the serialization ComponentCache so an unchanged component can be re-emitted
// without being serialized again; this stack's serializers keep no such cache, so nothing is recorded.

using System.CodeDom;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Runtime.Serialization;

namespace System.ComponentModel.Design.Serialization
{
	internal class ResourceCodeDomSerializer : CodeDomSerializer
	{
		private static ResourceCodeDomSerializer s_defaultSerializer;

		internal static new ResourceCodeDomSerializer Default => s_defaultSerializer ??= new ResourceCodeDomSerializer ();

		private const string ResourceManagerName = "resources";

		private static T GetContext<T> (IDesignerSerializationManager manager) where T : class
			=> manager.Context [typeof (T)] as T;

		public override string GetTargetComponentName (CodeStatement statement, CodeExpression expression, Type type)
		{
			string name = null;
			if (statement is CodeExpressionStatement es && es.Expression is CodeMethodInvokeExpression methodInvokeEx
			    && string.Equals (methodInvokeEx.Method?.MethodName, "ApplyResources", StringComparison.OrdinalIgnoreCase)
			    && methodInvokeEx.Parameters.Count > 0) {
				// resources.ApplyResources(this.button1, "button1"): the component it is called for.
				CodeExpression first = methodInvokeEx.Parameters [0];
				if (first is CodeFieldReferenceExpression fieldRef && fieldRef.TargetObject is CodeThisReferenceExpression)
					name = fieldRef.FieldName;
				else if (first is CodeVariableReferenceExpression varRef)
					name = varRef.VariableName;
			}
			if (string.IsNullOrEmpty (name))
				name = base.GetTargetComponentName (statement, expression, type);
			return name;
		}

		public override object Deserialize (IDesignerSerializationManager manager, object codeObject)
		{
			if (manager == null)
				throw new ArgumentNullException (nameof (manager));
			if (codeObject == null)
				throw new ArgumentNullException (nameof (codeObject));

			object instance = null;
			if (codeObject is CodeExpression expression) {
				instance = DeserializeExpression (manager, null, expression);
			} else if (codeObject is CodeStatementCollection statements) {
				foreach (CodeStatement element in statements) {
					// The resource manager's declaration is ours to make: it is not a plain parse of the code.
					if (element is CodeVariableDeclarationStatement statement) {
						if (statement.Name.Equals (ResourceManagerName))
							instance = CreateResourceManager (manager);
					} else if (instance == null) {
						instance = DeserializeStatementToInstance (manager, element);
					} else {
						DeserializeStatement (manager, element);
					}
				}
			} else if (!(codeObject is CodeStatement)) {
				throw new ArgumentException (string.Format ("The element type {0} is not supported. Supported types: CodeExpression, CodeStatement, CodeStatementCollection.", codeObject.GetType ().Name));
			}
			return instance;
		}

		private static SerializationResourceManager CreateResourceManager (IDesignerSerializationManager manager)
		{
			SerializationResourceManager sm = GetResourceManager (manager);
			if (!sm.DeclarationAdded) {
				sm.DeclarationAdded = true;
				manager.SetName (sm, ResourceManagerName);
			}
			return sm;
		}

		protected override object DeserializeInstance (IDesignerSerializationManager manager, Type type, object [] parameters, string name, bool addToContainer)
		{
			if (manager == null)
				throw new ArgumentNullException (nameof (manager));
			if (type == null)
				throw new ArgumentNullException (nameof (type));
			if (name != null && name.Equals (ResourceManagerName) && typeof (ResourceManager).IsAssignableFrom (type))
				return CreateResourceManager (manager);
			return manager.CreateInstance (type, parameters, name, addToContainer);
		}

		public static object DeserializeInvariant (IDesignerSerializationManager manager, string resourceName)
			=> GetResourceManager (manager).GetObject (resourceName, true);

		private static Type GetCastType (IDesignerSerializationManager manager, object value)
		{
			if (GetContext<ExpressionContext> (manager) is ExpressionContext tree)
				return tree.ExpressionType;
			if (value != null) {
				Type castTo = value.GetType ();
				while (!castTo.IsPublic && !castTo.IsNestedPublic)
					castTo = castTo.BaseType;
				return castTo;
			}
			return null;
		}

		public static IDictionaryEnumerator GetEnumerator (IDesignerSerializationManager manager, CultureInfo culture)
			=> GetResourceManager (manager).GetEnumerator (culture);

		public static IDictionaryEnumerator GetMetadataEnumerator (IDesignerSerializationManager manager)
			=> GetResourceManager (manager).GetMetadataEnumerator ();

		private static SerializationResourceManager GetResourceManager (IDesignerSerializationManager manager)
		{
			if (!(GetContext<SerializationResourceManager> (manager) is SerializationResourceManager sm)) {
				sm = new SerializationResourceManager (manager);
				manager.Context.Append (sm);
			}
			return sm;
		}

		public override object Serialize (IDesignerSerializationManager manager, object value)
			=> Serialize (manager, value, false, false, true);

		public object Serialize (IDesignerSerializationManager manager, object value, bool shouldSerializeInvariant)
			=> Serialize (manager, value, false, shouldSerializeInvariant, true);

		public object Serialize (IDesignerSerializationManager manager, object value, bool shouldSerializeInvariant, bool ensureInvariant)
			=> Serialize (manager, value, false, shouldSerializeInvariant, ensureInvariant);

		private CodeExpression Serialize (IDesignerSerializationManager manager, object value, bool forceInvariant, bool shouldSerializeInvariant, bool ensureInvariant)
		{
			SerializationResourceManager sm = GetResourceManager (manager);
			CodeStatementCollection statements = GetContext<CodeStatementCollection> (manager);

			// The first culture-sensitive value emits the resource manager's declaration. Invariant values hold data
			// that cannot be written as code and need none.
			if (!forceInvariant) {
				if (!sm.DeclarationAdded) {
					sm.DeclarationAdded = true;
					if (statements != null) {
						CodeExpression [] parameters;
						if (GetContext<RootContext> (manager) is RootContext rootCtx)
							parameters = new CodeExpression [] { new CodeTypeOfExpression (manager.GetName (rootCtx.Value)) };
						else
							parameters = new CodeExpression [] { new CodePrimitiveExpression (ResourceManagerName) };
						CodeExpression init = new CodeObjectCreateExpression (typeof (ComponentResourceManager), parameters);
						statements.Add (new CodeVariableDeclarationStatement (typeof (ComponentResourceManager), ResourceManagerName, init));
						SetExpression (manager, sm, new CodeVariableReferenceExpression (ResourceManagerName));
						sm.ExpressionAdded = true;
					}
				} else if (!sm.ExpressionAdded) {
					if (GetExpression (manager, sm) == null)
						SetExpression (manager, sm, new CodeVariableReferenceExpression (ResourceManagerName));
					sm.ExpressionAdded = true;
				}
			}

			ExpressionContext tree = GetContext<ExpressionContext> (manager);
			string resourceName = sm.SetValue (manager, tree, value, forceInvariant, shouldSerializeInvariant, ensureInvariant);

			// A string is GetString; anything else GetObject with a cast.
			bool needCast = !(value is string || (tree != null && tree.ExpressionType == typeof (string)));
			var methodInvoke = new CodeMethodInvokeExpression {
				Method = new CodeMethodReferenceExpression (new CodeVariableReferenceExpression (ResourceManagerName), needCast ? "GetObject" : "GetString"),
			};
			methodInvoke.Parameters.Add (new CodePrimitiveExpression (resourceName));
			if (needCast && GetCastType (manager, value) is Type castTo)
				return new CodeCastExpression (castTo, methodInvoke);
			return methodInvoke;
		}

		public object SerializeInvariant (IDesignerSerializationManager manager, object value, bool shouldSerializeValue)
			=> Serialize (manager, value, true, shouldSerializeValue, true);

		public static void SerializeMetadata (IDesignerSerializationManager manager, string name, object value, bool shouldSerializeValue)
			=> GetResourceManager (manager).SetMetadata (manager, name, value, shouldSerializeValue);

		public static void WriteResource (IDesignerSerializationManager manager, string name, object value)
			=> GetResourceManager (manager).SetValue (manager, name, value, forceInvariant: false, shouldSerializeInvariant: false, ensureInvariant: true);

		public static void WriteResourceInvariant (IDesignerSerializationManager manager, string name, object value)
			=> GetResourceManager (manager).SetValue (manager, name, value, forceInvariant: true, shouldSerializeInvariant: true, ensureInvariant: true);

		/// <summary>The resource manager of one serialization: what is read from and written to the IResourceService for
		/// the design culture and the invariant one.</summary>
		internal class SerializationResourceManager : ComponentResourceManager
		{
			private readonly IDesignerSerializationManager _manager;
			private bool _checkedLocalizationLanguage;
			private CultureInfo _localizationLanguage;
			private IResourceWriter _writer;
			private CultureInfo _readCulture;
			private readonly Dictionary<string, int> _nameTable = new Dictionary<string, int> ();
			private Dictionary<CultureInfo, Dictionary<string, object>> _resourceSets;
			private Dictionary<string, object> _metadata;
			private Dictionary<string, object> _mergedMetadata;
			private object _rootComponent;
			private HashSet<object> _propertyFillAdded;
			private bool _invariantCultureResourcesDirty;
			private bool _metadataResourcesDirty;

			public SerializationResourceManager (IDesignerSerializationManager manager)
			{
				_manager = manager;
				manager.SerializationComplete += OnSerializationComplete;
			}

			public bool DeclarationAdded { get; set; }

			public bool ExpressionAdded { get; set; }

			/// <summary>The root component's LoadLanguage: the culture being designed (none: the invariant one).</summary>
			private CultureInfo LocalizationLanguage {
				get {
					if (_checkedLocalizationLanguage)
						return _localizationLanguage;
					if (_manager.Context [typeof (RootContext)] is RootContext rootCtx
					    && TypeDescriptor.GetProperties (rootCtx.Value) ["LoadLanguage"]?.GetValue (rootCtx.Value) is CultureInfo ci)
						_localizationLanguage = ci;
					_checkedLocalizationLanguage = true;
					return _localizationLanguage;
				}
			}

			private CultureInfo ReadCulture => _readCulture ??= LocalizationLanguage ?? CultureInfo.InvariantCulture;

			private Dictionary<CultureInfo, Dictionary<string, object>> ResourceTable
				=> _resourceSets ??= new Dictionary<CultureInfo, Dictionary<string, object>> ();

			private object RootComponent => _rootComponent ??= (_manager.Context [typeof (RootContext)] as RootContext)?.Value;

			private IResourceService ResourceService => _manager.GetService (typeof (IResourceService)) as IResourceService;

			private IResourceWriter Writer {
				get {
					if (_writer == null)
						_writer = ResourceService?.GetResourceWriter (ReadCulture) ?? new ResourceWriter (new MemoryStream ());
					return _writer;
				}
			}

			public bool AddPropertyFill (object value)
			{
				_propertyFillAdded ??= new HashSet<object> ();
				return _propertyFillAdded.Add (value);
			}

			public override void ApplyResources (object value, string objectName, CultureInfo culture)
			{
				culture ??= ReadCulture;
				System.Windows.Forms.Control control = value as System.Windows.Forms.Control;
				control?.SuspendLayout ();
				base.ApplyResources (value, objectName, culture);
				control?.ResumeLayout (false);
			}

			private enum CompareValue { Same, Different, New }

			private CompareValue CompareWithParentValue (string name, object value)
				=> ReadCulture.Equals (CultureInfo.InvariantCulture) ? CompareValue.Different : CompareWithParentValue (ReadCulture, name, value);

			private CompareValue CompareWithParentValue (CultureInfo culture, string name, object value)
			{
				CultureInfo parent = culture.Parent;
				Dictionary<string, object> resourceSet = GetResourceSet (culture);
				if (resourceSet != null && resourceSet.TryGetValue (name, out object parentValue))
					return parentValue == null || !parentValue.Equals (value) ? CompareValue.Different : CompareValue.Same;
				if (culture.Equals (CultureInfo.InvariantCulture))
					return CompareValue.New;
				return CompareWithParentValue (parent, name, value);
			}

			private Dictionary<string, object> CreateResourceSet (IResourceReader reader, CultureInfo culture)
			{
				var result = new Dictionary<string, object> ();
				try {
					IDictionaryEnumerator resEnum = reader.GetEnumerator ();
					while (resEnum.MoveNext ())
						result [(string) resEnum.Key] = resEnum.Value;
				} catch (Exception e) {
					string message = string.IsNullOrEmpty (e.Message) ? e.GetType ().Name : e.Message;
					Exception se = culture.Equals (CultureInfo.InvariantCulture)
						? new SerializationException (string.Format ("Invalid resx file. Could not read the invariant culture resources: {0}", message), e)
						: new SerializationException (string.Format ("Invalid resx file. Could not read resources for culture {0}: {1}", culture, message), e);
					_manager.ReportError (se);
				}
				return result;
			}

			public IDictionaryEnumerator GetMetadataEnumerator ()
			{
				if (_mergedMetadata == null) {
					Dictionary<string, object> metaData = GetMetadata ();
					if (metaData != null) {
						Dictionary<string, object> resourceSet = GetResourceSet (CultureInfo.InvariantCulture);
						if (resourceSet != null)
							foreach (KeyValuePair<string, object> item in resourceSet)
								if (!metaData.ContainsKey (item.Key))
									metaData.Add (item.Key, item.Value);
						_mergedMetadata = metaData;
					}
				}
				return _mergedMetadata?.GetEnumerator ();
			}

			public IDictionaryEnumerator GetEnumerator (CultureInfo culture) => GetResourceSet (culture)?.GetEnumerator ();

			private Dictionary<string, object> GetMetadata ()
			{
				if (_metadata != null)
					return _metadata;
				IResourceReader reader = ResourceService?.GetResourceReader (CultureInfo.InvariantCulture);
				if (reader != null) {
					try {
						if (reader is ResXResourceReader resxReader) {
							_metadata = new Dictionary<string, object> ();
							IDictionaryEnumerator de = resxReader.GetMetadataEnumerator ();
							while (de.MoveNext ())
								_metadata [(string) de.Key] = de.Value;
						}
					} finally {
						reader.Close ();
					}
				}
				return _metadata;
			}

			public override object GetObject (string resourceName) => GetObject (resourceName, false);

			/// <summary>The value for the design culture, or the nearest parent culture's.</summary>
			public object GetObject (string resourceName, bool forceInvariant)
			{
				CultureInfo culture = forceInvariant ? CultureInfo.InvariantCulture : ReadCulture;
				CultureInfo lastCulture;
				object value = null;
				do {
					GetResourceSet (culture)?.TryGetValue (resourceName, out value);
					lastCulture = culture;
					culture = culture.Parent;
				} while (value == null && !lastCulture.Equals (culture));
				return value;
			}

			private Dictionary<string, object> GetResourceSet (CultureInfo culture)
			{
				if (!ResourceTable.TryGetValue (culture, out Dictionary<string, object> resourceSet)) {
					IResourceService resSvc = ResourceService;
					if (resSvc != null) {
						IResourceReader reader = resSvc.GetResourceReader (culture);
						if (reader != null) {
							try {
								resourceSet = CreateResourceSet (reader, culture);
							} finally {
								reader.Close ();
							}
						} else if (culture.Equals (CultureInfo.InvariantCulture)) {
							resourceSet = new Dictionary<string, object> ();
						}
						ResourceTable [culture] = resourceSet;
					}
				}
				return resourceSet;
			}

			public override ResourceSet GetResourceSet (CultureInfo culture, bool createIfNotExists, bool tryParents)
			{
				if (culture == null)
					throw new ArgumentNullException (nameof (culture));
				return createIfNotExists ? new CodeDomResourceSet () : null;
			}

			public override string GetString (string resourceName) => GetObject (resourceName, false) as string;

			private void OnSerializationComplete (object sender, EventArgs e)
			{
				_writer?.Close ();
				_writer = null;
				if (!_invariantCultureResourcesDirty && !_metadataResourcesDirty)
					return;
				IResourceService service = ResourceService;
				if (service == null) {
					_invariantCultureResourcesDirty = _metadataResourcesDirty = false;
					return;
				}
				IResourceWriter invariantWriter = service.GetResourceWriter (CultureInfo.InvariantCulture);
				try {
					if (ResourceTable.TryGetValue (CultureInfo.InvariantCulture, out Dictionary<string, object> resourceSet) && resourceSet != null)
						foreach (KeyValuePair<string, object> item in resourceSet)
							invariantWriter.AddResource (item.Key, item.Value);
					_invariantCultureResourcesDirty = false;
					if (invariantWriter is ResXResourceWriter resxWriter && _metadata != null)
						foreach (KeyValuePair<string, object> de in _metadata)
							resxWriter.AddMetadata (de.Key, de.Value);
					_metadataResourcesDirty = false;
				} finally {
					invariantWriter.Close ();
				}
			}

			private static bool IsSerializable (object value)
			{
#pragma warning disable SYSLIB0050
				return value == null || value.GetType ().IsSerializable;
#pragma warning restore SYSLIB0050
			}

			public void SetMetadata (IDesignerSerializationManager manager, string resourceName, object value, bool shouldSerializeValue)
			{
				// A value that cannot be serialized cannot go into resources.
				if (!IsSerializable (value))
					return;
				if (ReadCulture.Equals (CultureInfo.InvariantCulture)) {
					if (shouldSerializeValue) {
						if (Writer is ResXResourceWriter resxWriter)
							resxWriter.AddMetadata (resourceName, value);
						else
							Writer.AddResource (resourceName, value);
					}
					return;
				}
				// Designing another culture: metadata belongs to the invariant culture.
				IResourceWriter invariantWriter = ResourceService?.GetResourceWriter (CultureInfo.InvariantCulture);
				Dictionary<string, object> invariant = GetResourceSet (CultureInfo.InvariantCulture);
				Dictionary<string, object> metadata;
				if (invariantWriter == null || invariantWriter is ResXResourceWriter) {
					metadata = GetMetadata ();
					if (metadata == null)
						metadata = _metadata = new Dictionary<string, object> ();
					invariant?.Remove (resourceName);
					_metadataResourcesDirty = true;
				} else {
					metadata = invariant;
					_invariantCultureResourcesDirty = true;
				}
				if (metadata != null) {
					if (shouldSerializeValue)
						metadata [resourceName] = value;
					else
						metadata.Remove (resourceName);
				}
				_mergedMetadata = null;
			}

			public void SetValue (IDesignerSerializationManager manager, string resourceName, object value, bool forceInvariant, bool shouldSerializeInvariant, bool ensureInvariant)
			{
				if (!IsSerializable (value))
					return;
				if (forceInvariant) {
					if (ReadCulture.Equals (CultureInfo.InvariantCulture)) {
						if (shouldSerializeInvariant)
							Writer.AddResource (resourceName, value);
					} else {
						Dictionary<string, object> resourceSet = GetResourceSet (CultureInfo.InvariantCulture);
						if (resourceSet != null) {
							if (shouldSerializeInvariant)
								resourceSet [resourceName] = value;
							else
								resourceSet.Remove (resourceName);
						}
						_invariantCultureResourcesDirty = true;
					}
					return;
				}

				switch (CompareWithParentValue (resourceName, value)) {
				case CompareValue.Same:
					break;
				case CompareValue.Different:
					Writer.AddResource (resourceName, value);
					break;
				case CompareValue.New:
					if (ensureInvariant) {
						Dictionary<string, object> resourceSet = GetResourceSet (CultureInfo.InvariantCulture);
						if (resourceSet != null)
							resourceSet [resourceName] = value;
						_invariantCultureResourcesDirty = true;
						Writer.AddResource (resourceName, value);
					} else {
						bool writeValue = true, writeInvariant = false;
						if (manager.Context [typeof (PropertyDescriptor)] is PropertyDescriptor prop
						    && manager.Context [typeof (ExpressionContext)] is ExpressionContext tree
						    && tree.Expression is CodePropertyReferenceExpression) {
							writeValue = prop.ShouldSerializeValue (tree.Owner);
							writeInvariant = !prop.CanResetValue (tree.Owner);
						}
						if (writeValue) {
							Writer.AddResource (resourceName, value);
							if (writeInvariant) {
								Dictionary<string, object> resourceSet = GetResourceSet (CultureInfo.InvariantCulture);
								if (resourceSet != null)
									resourceSet [resourceName] = value;
								_invariantCultureResourcesDirty = true;
							}
						}
					}
					break;
				}
			}

			/// <summary>Names the value after its owner and member ("$this.Text", "button1.Image"), numbered when the
			/// name is taken or nothing names it ("resource1"), and stores it.</summary>
			public string SetValue (IDesignerSerializationManager manager, ExpressionContext tree, object value, bool forceInvariant, bool shouldSerializeInvariant, bool ensureInvariant)
			{
				string nameBase;
				bool appendCount = false;
				if (tree != null) {
					if (tree.Owner == RootComponent) {
						nameBase = "$this";
					} else {
						nameBase = manager.GetName (tree.Owner);
						if (nameBase == null && manager.GetService (typeof (IReferenceService)) is IReferenceService referenceService)
							nameBase = referenceService.GetName (tree.Owner);
					}
					string expressionName;
					if (tree.Expression is CodePropertyReferenceExpression codeProperty) {
						expressionName = codeProperty.PropertyName;
					} else if (tree.Expression is CodeFieldReferenceExpression codeField) {
						expressionName = codeField.FieldName;
					} else if (tree.Expression is CodeMethodReferenceExpression codeMethod) {
						expressionName = codeMethod.MethodName;
						if (expressionName.StartsWith ("Set", StringComparison.InvariantCulture))
							expressionName = expressionName.Substring (3);
					} else {
						expressionName = null;
					}
					nameBase ??= "resource";
					if (expressionName != null)
						nameBase += "." + expressionName;
				} else {
					nameBase = "resource";
					appendCount = true;
				}

				string resourceName = nameBase;
				int count = 0;
				if (appendCount || _nameTable.ContainsKey (nameBase)) {
					_nameTable.TryGetValue (nameBase, out count);
					do {
						count++;
						resourceName = nameBase + count.ToString (CultureInfo.InvariantCulture);
					} while (_nameTable.ContainsKey (resourceName));
				}
				SetValue (manager, resourceName, value, forceInvariant, shouldSerializeInvariant, ensureInvariant);
				_nameTable [resourceName] = count;
				return resourceName;
			}

			private sealed class CodeDomResourceSet : ResourceSet
			{
			}
		}
	}
}
