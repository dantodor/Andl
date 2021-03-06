﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Andl.Runtime {
  public class SqlTemplater : Templater {

    static Dictionary<string, string> _templates = new Dictionary<string, string> {
      { "Create",         "DROP TABLE IF EXISTS [<table>] \n" +
                          "CREATE TABLE [<table>] ( <coldefs>, UNIQUE ( <colnames> ) ON CONFLICT IGNORE )" },
      { "SelectAll",      "SELECT <namelist> FROM <select>" },
      { "SelectAs",       "SELECT DISTINCT <namelist> FROM <select>" },
      { "SelectAsGroup",  "SELECT DISTINCT <namelist> FROM <select> <groupby>" },
      { "SelectJoin",     "SELECT DISTINCT <namelist> FROM <select1> JOIN <select2> <using>" },
      { "SelectAntijoin", "SELECT DISTINCT <namelist> FROM <select1> [_a_] WHERE NOT EXISTS (SELECT 1 FROM <select2> [_b_] WHERE <nameeqlist>)" },
      { "SelectSet",      "<select1> <setop> <select2>" },
      { "SelectSetName",  "SELECT DISTINCT <namelist> FROM <select1> <setop> SELECT <namelist> FROM <select2>" },
      { "SelectCount",    "SELECT COUNT(*) FROM <select>" },
      { "SelectOneWhere", "SELECT 1 <whereexist>" },

      { "InsertNamed",    "INSERT INTO [<table>] ( <namelist> ) <select>" },
      { "InsertSelect",   "INSERT INTO [<table>] <select>" },
      { "InsertValues",   "INSERT INTO [<table>] ( <namelist> ) VALUES ( <valuelist> )" },
      { "InsertJoin",     "INSERT INTO [<table>] ( <namelist> ) <select>" },
      { "Delete",         "DELETE FROM [<table>] WHERE <pred>" },
      { "Update",         "UPDATE [<table>] SET <namesetlist> WHERE <pred>" },

      { "WhereExist",     "WHERE <not> EXISTS ( <select1> <setop> <select2> )" },
      { "WhereExist2",    "WHERE <not> EXISTS ( <select1> <setop> <select2> ) AND <not> EXISTS ( <select2> <setop> <select1> )" },
      { "Where",          "WHERE <expr>" },
      { "Having",         "HAVING <expr>" },
      { "Using",          "USING ( <namelist> )" },

      { "OrderBy",        "ORDER BY <ordcols>" },
      { "GroupBy",        "GROUP BY <grpcols>" },
      { "Limit",          "LIMIT <limit>" },
      { "LimitOffset",    "LIMIT <limit> OFFSET <offset>" },
      { "EvalFunc",       "<func>(<lookups>)" },
      { "Coldef",         "[<colname>] <coltype>" },
      { "Name",           "[<name>]" },
      { "NameEq",         "[_a_].[<name>] = [_b_].[<name>]" },
      { "NameAs",         "[<name1>] AS [<name2>]" },
      { "NameSet",        "[<name1>] = [<name2>]" },
      { "Value",          "<value>" },
      { "Param",          "?<param>" },
    };

    public static SqlTemplater Create(string template) {
      var t = new SqlTemplater { Template = _templates[template] };
      return t;
    }

    public static string Process(string template, Dictionary<string, SubstituteDelegate> dict, int index = 0) {
      return Create(template).Process(dict, index).ToString();
    }
    
  }
  /// <summary>
  /// Implement Sql generation
  /// 
  /// Uses Templater and SqlTarget
  /// </summary>
  public class SqlGen {
    // Types suitable for use in column definitions
    // see https://www.sqlite.org/datatype3.html S2.2
    // TODO: refactor
    static Dictionary<DataType, string> _datatypetosql = new Dictionary<DataType, string> {
      { DataTypes.Binary, "BLOB" },
      { DataTypes.Bool, "BOOLEAN" },
      { DataTypes.Number, "TEXT" },
      { DataTypes.Text, "TEXT" },
      { DataTypes.Time, "TEXT" },
      { DataTypes.Table, "BLOB" },
      { DataTypes.Row, "BLOB" },
      { DataTypes.User, "BLOB" },
    };

    static Dictionary<JoinOps, string> _joinoptosql = new Dictionary<JoinOps, string> {
      { JoinOps.UNION, "UNION" },
      { JoinOps.INTERSECT, "INTERSECT" },
      { JoinOps.MINUS, "EXCEPT" },
    };

    public const string Ascending = "ASC";
    public const string Descending = "DESC";

    // functions to convert value into SQL literal format
    public delegate string ValueToSqlDelegate(TypedValue value);
    static Dictionary<DataType, ValueToSqlDelegate> _valuetosql = new Dictionary<DataType, ValueToSqlDelegate> {
      { DataTypes.Binary, x => BinaryLiteral(((BinaryValue)x).Value) },
      { DataTypes.Bool, x => ((BoolValue)x).Value ? "1" : "0" },
      { DataTypes.Number, x => x.Format() },
      { DataTypes.Row, x => Quote(x.ToString()) },
      { DataTypes.Time, x => Quote(x.Format()) },
      { DataTypes.Text, x => Quote(x.ToString()) },
      { DataTypes.Table, x => Quote(x.ToString()) },
      { DataTypes.User, x => Quote(x.ToString()) },
    };

    int AccBase { get; set; }

    //----- sql generating fragments

    public string Combine(params string[] args) {
      StringBuilder sb = new StringBuilder();
      foreach (var arg in args) {
        if (arg != null) {
          if (sb.Length > 0)
            sb.Append(" ");
          sb.Append(arg);
        }
      }
      return sb.ToString();
    }

    // return the corresponding Sql type 
    public string ColumnType(DataType datatype) {
      Logger.Assert(_datatypetosql.ContainsKey(datatype.BaseType), datatype);
      return _datatypetosql[datatype.BaseType];
    }

    // return the corresponding join op
    public string JoinOp(JoinOps joinop) {
      Logger.Assert(_joinoptosql.ContainsKey(joinop), joinop);
      return _joinoptosql[joinop];
    }

    // return a literal Sql value
    public static string ColumnValue(TypedValue value) {
      return _valuetosql[value.DataType](value);
    }

    // Quote an Sql literal string
    public static string Quote(string value) {
      return "'" + value.Replace("'", "''") + "'";
    }

    public static string BinaryLiteral(byte[] bytes) {
      StringBuilder sb = new StringBuilder();
      foreach (var b in bytes)
        sb.Append(b.ToString("x2"));
      return "x'" + sb + "'";
    }

    public static byte[] ToBinary(ExpressionBlock expr) {
      using (var writer = PersistWriter.Create()) {
        writer.Write(expr);
        return writer.ToArray();
      }
    }

    //public static string BinaryLiteral(ExpressionBlock expr) {
    //  return BinaryLiteral(ToBinary(expr));
    //}

    public static string FuncName(ExpressionBlock expr) {
      if (expr.IsOpen) return "EVAL" + expr.Serial.ToString();
      if (expr.HasFold) return "EVALA" + expr.Serial.ToString();
      return null;
    }

    static int _tempid = 0;

    public string TempName() {
      //return "_temp_T_" + (++_tempid).ToString();
      return "temp.T_" + (++_tempid).ToString();
    }

    // generate a plain delimited name list
    public string NameList(string[] names) {
      if (names.Length == 0) return "_dummy_";
      var coldict = new Dictionary<string, SubstituteDelegate> {
        { "name", (x) => names[x] },
      };
      return SqlTemplater.Create("Name").Process(coldict, names.Length, ", ").ToString();
    }

    public string NameList(DataHeading heading) {
      return NameList(heading.Columns.Select(x => x.Name).ToArray());
    }

    public string NameEqList(string[] names) {
      if (names.Length == 0) return "1=1";
      var coldict = new Dictionary<string, SubstituteDelegate> {
        { "name", (x) => names[x] },
      };
      return SqlTemplater.Create("NameEq").Process(coldict, names.Length, " AND ").ToString();
    }

    public string NameEqList(DataHeading heading) {
      return NameEqList(heading.Columns.Select(x => x.Name).ToArray());
    }

    //public string NameSetList(ExpressionBlock[] exprs) {
    //  var coldict = new Dictionary<string, SubstituteDelegate> {
    //    { "name1", (x) => exprs[x].Name },
    //    { "name2", (x) => EvalFunc(exprs[x]) },
    //  };
    //  return SqlTemplater.Create("NameSet").Process(coldict, exprs.Length, ", ").ToString();
    //}

    // Hand-crafted AS terms
    public string NameSetList(ExpressionBlock[] exprs) {
      var ss = new List<string>();
      foreach (var expr in exprs) {
        if (expr.IsProject) { } 
        else if (expr.IsRename)
          ss.Add(String.Format("[{0}] = [{1}]", expr.Name, expr.OldName));
        else
          ss.Add(String.Format("[{0}] = {1}", expr.Name, EvalFunc(expr)));
      }
      return string.Join(", ", ss);
    }

    // Hand-crafted AS terms
    public string NameAsList(ExpressionBlock[] exprs) {
      if (exprs.Length == 0) return "NULL as _dummy_";
      var sb = new StringBuilder();
      foreach (var expr in exprs) {
        if (sb.Length > 0) sb.AppendFormat(", ");
        if (expr.IsProject)
          sb.AppendFormat("[{0}]", expr.Name);
        else if (expr.IsRename)
          sb.AppendFormat("[{0}] AS [{1}]", expr.OldName, expr.Name);
        else
          sb.AppendFormat("{0} AS [{1}]", EvalFunc(expr), expr.Name);
      }
      return sb.ToString();
    }

    public string ColDefs(DataHeading heading) {
      if (heading.Degree == 0) return "[_dummy_] TEXT";
      var coldict = new Dictionary<string, SubstituteDelegate> {
        { "colname", (x) => heading.Columns[x].Name },
        { "coltype", (x) => ColumnType(heading.Columns[x].DataType) },
      };
      return SqlTemplater.Create("Coldef").Process(coldict, heading.Degree, ", ").ToString();
    }

    public string ColOrders(ExpressionBlock[] exprs) {
      var coldict = new Dictionary<string, SubstituteDelegate> {
        { "colname", (x) => exprs[x].Name },
        { "coltype", (x) => exprs[x].IsDesc ? Descending : Ascending },
      };
      return SqlTemplater.Create("Coldef").Process(coldict, exprs.Length, ", ").ToString();
    }

    public string ValueList(int howmany, SubstituteDelegate subs) {
      if (howmany == 0) return "NULL";
      var coldict = new Dictionary<string, SubstituteDelegate> {
        { "value", (x) => subs(x) },
      };
      return SqlTemplater.Create("Value").Process(coldict, howmany, ", ").ToString();
    }

    public string ParamList(DataHeading heading) {
      if (heading.Degree == 0) return "NULL";
      var coldict = new Dictionary<string, SubstituteDelegate> {
        { "param", (x) => (x+1).ToString() },
      };
      return SqlTemplater.Create("Param").Process(coldict, heading.Degree, ", ").ToString();
    }

    public string EvalFunc(ExpressionBlock expr) {
      var lookups = (expr.NumArgs == 0) ? "" : NameList(expr.Lookup.Columns.Select(c => c.Name).ToArray());
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "func", (x) => FuncName(expr) },
        { "lookups", (x) => lookups },
      };
      return SqlTemplater.Process("EvalFunc", dict);
    }

    public string Where(ExpressionBlock expr) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "expr", (x) => EvalFunc(expr) },  // FIX: predicate
      };
      return SqlTemplater.Process("Where", dict);
    }

    public string Having(ExpressionBlock expr) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "expr", (x) => EvalFunc(expr) },  // FIX: predicate
      };
      return SqlTemplater.Process("Having", dict);
    }

    public string OrderBy(ExpressionBlock[] exprs) {
      if (exprs.Length == 0) return "";
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "ordcols", (x) => ColOrders(exprs) },
      };
      return SqlTemplater.Process("OrderBy", dict);
    }

    public string GroupBy(ExpressionBlock[] exprs) {
      if (exprs.Length == 0) return "";
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "grpcols", (x) => NameList(DataHeading.Create(exprs)) },
      };
      return SqlTemplater.Process("GroupBy", dict);
    }

    public string Limit(int limit) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "limit", (x) => limit.ToString() },
      };
      return SqlTemplater.Process("Limit", dict);
    }

    public string Limit(int limit, int offset) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "limit", (x) => limit.ToString() },
        { "offset", (x) => offset.ToString() },
      };
      return SqlTemplater.Process("LimitOffset", dict);
    }

    public string SelectAs(string tableorquery, ExpressionBlock[] exprs) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select", (x) => tableorquery },
        { "namelist", (x) => NameAsList(exprs) },
      };
      return SqlTemplater.Process("SelectAs", dict);
    }

    public string SelectAsGroup(string tableorquery, ExpressionBlock[] exprs) {
      var groups = exprs.Where(e => !e.HasFold).ToArray();
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select", (x) => tableorquery },
        { "namelist", (x) => NameAsList(exprs) },
        { "groupby", (x) => GroupBy(groups) },
      };
      return SqlTemplater.Process("SelectAsGroup", dict);
    }

    // Sql to insert multiple rows of data into named table with binding
    public string InsertValuesParam(string tablename, DataTable other) {
      var namelist = NameList(other.Heading);
      var paramlist = ParamList(other.Heading);
      var dict = new Dictionary<string, SubstituteDelegate> {
          { "table", (x) => tablename },
          { "namelist", (x) => namelist },
          { "valuelist", (x) => paramlist },
        };
      return SqlTemplater.Process("InsertValues", dict);
    }

    // Sql to insert rows of data into named table as literals
    public string InsertValues(string tablename, DataTable other) {
      var namelist = NameList(other.Heading);
      var tmpl = SqlTemplater.Create("InsertValues");
      var rowno = 0;
      foreach (var row in other.GetRows()) {
        var values = row.Values;
        var valuelist = ValueList(other.Heading.Degree, x => ColumnValue(values[x]));
        var dict = new Dictionary<string, SubstituteDelegate> {
          { "table", (x) => tablename },
          { "namelist", (x) => namelist },
          { "valuelist", (x) => valuelist },
        };
        if (rowno > 0) tmpl.Append("\n");
        tmpl.Process(dict);
      }
      return tmpl.ToString();
    }

    // Sql to delete according to predicate
    public string Delete(string tablename, ExpressionBlock pred) {
      var dict = new Dictionary<string, SubstituteDelegate> {
          { "table", (x) => tablename },
          { "pred", (x) => EvalFunc(pred) },
        };
      return SqlTemplater.Process("Delete", dict);
    }

    // Sql to udpate according to predicate and expressions
    public string Update(string tablename, ExpressionBlock pred, ExpressionBlock[] exprs) {
      var dict = new Dictionary<string, SubstituteDelegate> {
          { "table", (x) => tablename },
          { "pred", (x) => EvalFunc(pred) },
          { "namesetlist", (x) => NameSetList(exprs) },
        };
      return SqlTemplater.Process("Update", dict);
    }

    // Generate Sql to create this table using name and columns provided
    public string CreateTable(string name, DataTable other) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "table", (x) => name },
        { "coldefs", (x) => ColDefs(other.Heading) },
        { "colnames", (x) => NameList(other.Heading) },
      };
      return SqlTemplater.Process("Create", dict);
    }

    public string SelectAll(string tablename, DataTableSql other) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select", (x) => tablename },
        { "namelist", (x) => NameList(other.Heading) },
      };
      return SqlTemplater.Process("SelectAll", dict);
    }

    // Actually insert query results into this table
    public string InsertSelect(string tablename, string query) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "table", (x) => tablename },
        { "select", (x) => query },
      };
      return SqlTemplater.Process("InsertSelect", dict);
    }

    // Actually insert query results into this table
    public string InsertNamed(string tablename, DataHeading heading, string query) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "table", (x) => tablename },
        { "namelist", (x) => NameList(heading) },
        { "select", (x) => query },
      };
      return SqlTemplater.Process("InsertNamed", dict);
    }

    // Generate Sql to insert query results into this table
    public string InsertJoin(string name, DataHeading heading, string query, JoinOps joinop) {
      // ignore joinop for now
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "namelist", (x) => NameList(heading) },
        { "table", (x) => name },
        { "select", (x) => query },
      };
      return SqlTemplater.Process("InsertJoin", dict);
    }

    // Using namelist, but nothing for empty heading
    public string Using(DataHeading heading) {
      if (heading.Degree == 0) return "";
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "namelist", (x) => NameList(heading) },
      };
      return SqlTemplater.Process("Using", dict);
    }

    // Note: using <nameeqlist> triggers ambiguous column name
    public string SelectJoin(string leftsql, string rightsql, DataHeading newheading, DataHeading joinheading) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select1", (x) => leftsql },
        { "select2", (x) => rightsql },
        { "namelist", (x) => NameList(newheading) },
        { "using", (x) => Using(joinheading) },
      };
      return SqlTemplater.Process("SelectJoin", dict);
    }

    public string SelectAntijoin(string leftsql, string rightsql, DataHeading newheading, DataHeading joinheading) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select1", (x) => leftsql },
        { "select2", (x) => rightsql },
        { "namelist", (x) => NameList(newheading) },
        { "nameeqlist", (x) => NameEqList(joinheading) },
      };
      return SqlTemplater.Process("SelectAntijoin", dict);
    }

    public string SelectSet(string leftsql, string rightsql, DataHeading newheading, JoinOps joinop) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select1", (x) => leftsql },
        { "select2", (x) => rightsql },
        { "namelist", (x) => NameList(newheading) },
        { "setop", (x) => JoinOp(joinop) },
      };
      return SqlTemplater.Process("SelectSetName", dict);
    }

    public string SelectOneWhere(string leftsql, string rightsql, JoinOps joinop, bool equal) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "whereexist", (x) => WhereExist(leftsql, rightsql, joinop, true, equal) },
      };
      return SqlTemplater.Process("SelectOneWhere", dict);
    }

    public string WhereExist(string leftsql, string rightsql, JoinOps joinop, bool not, bool twice) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "not", (x) => not ? "NOT" : "" },
        { "select1", (x) => leftsql },
        { "select2", (x) => rightsql },
        { "setop", (x) => JoinOp(joinop) },
      };
      return SqlTemplater.Process(twice ? "WhereExist2" : "WhereExist", dict);
    }

    public string SelectCount(string sql) {
      var dict = new Dictionary<string, SubstituteDelegate> {
        { "select", (x) => sql },
      };
      return SqlTemplater.Process("SelectCount", dict);
    }

  }
}
