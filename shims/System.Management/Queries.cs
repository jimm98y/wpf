// The query objects. They only hold and format the query text; Wql.cs parses and runs it.

using System.Collections.Specialized;
using System.Text;

namespace System.Management
{
    public abstract partial class ManagementQuery
    {
        private string _language = "WQL";
        private string _query = "";

        internal ManagementQuery(string language, string query)
        {
            QueryLanguage = language;
            QueryString = query;
        }

        public virtual string QueryLanguage
        {
            get => _language;
            set => _language = value ?? "";
        }

        public virtual string QueryString
        {
            get => _query;
            set
            {
                ParseQuery(value ?? "");
                _query = value ?? "";
            }
        }

        public abstract object Clone();

        protected internal virtual void ParseQuery(string query) { }

        // For subclasses that build their text from parts (SelectQuery).
        internal void SetQueryText(string query) => _query = query ?? "";
    }

    public partial class ObjectQuery : ManagementQuery
    {
        public ObjectQuery() { }

        public ObjectQuery(string query) : base("WQL", query) { }

        public ObjectQuery(string language, string query) : base(language, query) { }

        public override object Clone() => new ObjectQuery(QueryLanguage, QueryString);
    }

    public partial class WqlObjectQuery
    {
        public WqlObjectQuery() { }

        public WqlObjectQuery(string query) : base(query) { }

        public override string QueryLanguage => base.QueryLanguage;

        public override object Clone() => new WqlObjectQuery(QueryString);
    }

    public partial class SelectQuery : WqlObjectQuery
    {
        private string _className = "";
        private string _condition = "";
        private bool _isSchemaQuery;
        private StringCollection _selected = new StringCollection();

        public SelectQuery() : this(null) { }

        public SelectQuery(bool isSchemaQuery, string condition)
        {
            if (!isSchemaQuery) throw new ArgumentException(null, nameof(isSchemaQuery));
            _isSchemaQuery = true;
            _condition = condition ?? "";
            BuildQuery();
        }

        public SelectQuery(string queryOrClassName)
        {
            if (queryOrClassName != null && queryOrClassName.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase))
            {
                QueryString = queryOrClassName;
            }
            else
            {
                _className = queryOrClassName ?? "";
                BuildQuery();
            }
        }

        public SelectQuery(string className, string condition) : this(className, condition, null) { }

        public SelectQuery(string className, string condition, string[] selectedProperties)
        {
            _className = className ?? "";
            _condition = condition ?? "";
            if (selectedProperties != null) _selected.AddRange(selectedProperties);
            BuildQuery();
        }

        public string ClassName
        {
            get => _className;
            set { _className = value ?? ""; BuildQuery(); }
        }

        public string Condition
        {
            get => _condition;
            set { _condition = value ?? ""; BuildQuery(); }
        }

        public bool IsSchemaQuery
        {
            get => _isSchemaQuery;
            set { _isSchemaQuery = value; BuildQuery(); }
        }

        public override string QueryString
        {
            get { BuildQuery(); return base.QueryString; }
            set => base.QueryString = value;
        }

        public StringCollection SelectedProperties
        {
            get => _selected;
            set { _selected = value ?? new StringCollection(); BuildQuery(); }
        }

        protected internal void BuildQuery()
        {
            var sb = new StringBuilder("select ");
            if (_isSchemaQuery)
            {
                sb.Append("* from meta_class");
            }
            else
            {
                if (_selected.Count == 0) sb.Append('*');
                else
                {
                    for (int i = 0; i < _selected.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        sb.Append(_selected[i]);
                    }
                }
                sb.Append(" from ").Append(_className);
            }
            if (_condition.Length > 0) sb.Append(" where ").Append(_condition);
            SetQueryText(sb.ToString());
        }

        public override object Clone()
        {
            var q = new SelectQuery();
            q._className = _className;
            q._condition = _condition;
            q._isSchemaQuery = _isSchemaQuery;
            foreach (string s in _selected) q._selected.Add(s);
            q.BuildQuery();
            return q;
        }

        protected internal override void ParseQuery(string query)
        {
            Wql.Select parsed = Wql.ParseSelect(query);
            _isSchemaQuery = parsed.ClassName.Equals("meta_class", StringComparison.OrdinalIgnoreCase);
            _className = _isSchemaQuery ? "" : parsed.ClassName;
            _condition = parsed.ConditionText;
            _selected = new StringCollection();
            if (parsed.Properties != null) foreach (string p in parsed.Properties) _selected.Add(p);
        }
    }
}
