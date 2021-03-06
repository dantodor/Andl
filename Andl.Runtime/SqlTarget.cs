﻿/// Andl is A New Data Language. See andl.org.
///
/// Copyright © David M. Bennett 2015 as an unpublished work. All rights reserved.
///
/// If you have received this file directly from me then you are hereby granted 
/// permission to use it for personal study. For any other use you must ask my 
/// permission. Not to be copied, distributed or used commercially without my 
/// explicit written permission.
///
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Andl.Sqlite;
using System.Runtime.InteropServices;

namespace Andl.Runtime {
  /// <summary>
  /// Implement an SQL target, perhaps one of many
  /// </summary>
  /// 
  public class SqlTarget {
    // Sql generator
    public static SqlGen SqlGen { get { return _sqlgen; } }
    // true if more data to read
    public bool HasData { get { return _statement.HasData; } }
    // dictionary of known expressions subject to callback
    public static Dictionary<int, ExpressionEval> ExprDict { get; set; }

    public static SqliteDatabase Database { get { return _database; } }

    //--- statics
    static SqlGen _sqlgen;
    // configured to use this database
    static SqliteDatabase _database;
    // statement used by the current instance
    SqliteStatement _statement;

    // functions to convert between DataType value and native object value, indexed by base type
    // Very important they round trip correctly, but up here all the objects are the right type.

    // boxed object => DataType
    public static readonly Dictionary<DataType, Func<object, DataType, TypedValue>> FromObjectDict = new Dictionary<DataType, Func<object, DataType, TypedValue>>() {
      { DataTypes.Binary, (v, dt) => BinaryValue.Create(v as byte[]) },
      { DataTypes.Bool, (v, dt) =>   BoolValue.Create((bool)v) },
      { DataTypes.Number, (v, dt) => NumberValue.Create((decimal)v) },
      { DataTypes.Row, (v, dt) =>    PersistReader.FromBinary(v as byte[], dt) },
      { DataTypes.Table, (v, dt) =>  PersistReader.FromBinary(v as byte[], dt) },
      { DataTypes.Text, (v, dt) =>   TextValue.Create(v as string) },
      { DataTypes.Time, (v, dt) =>   TimeValue.Create((DateTime)v) },
      { DataTypes.User, (v, dt) =>   PersistReader.FromBinary(v as byte[], dt) },
    };

    // DataType => boxed object
    public static readonly Dictionary<DataType, Func<TypedValue, object>> ToObjectDict = new Dictionary<DataType, Func<TypedValue, object>> {
      { DataTypes.Binary, (v) => (v as BinaryValue).Value },
      { DataTypes.Bool, (v) => (v as BoolValue).Value },
      { DataTypes.Number, (v) => (v as NumberValue).Value },
      { DataTypes.Row, (v) => PersistWriter.ToBinary(v) },
      { DataTypes.Table, (v) => PersistWriter.ToBinary(v) },
      { DataTypes.Text, (v) => (v as TextValue).Value },
      { DataTypes.Time, (v) => (v as TimeValue).Value },
      { DataTypes.User, (v) => PersistWriter.ToBinary(v) },
    };

    // Convert from Common type to DataType
    public static Dictionary<SqlCommonType, DataType> FromSqlCommon = new Dictionary<SqlCommonType, DataType> {
      //{ SqlCommonType.None, DataTypes.Unknown },
      { SqlCommonType.Binary, DataTypes.Binary },
      { SqlCommonType.Bool, DataTypes.Bool },
      { SqlCommonType.Number, DataTypes.Number },
      { SqlCommonType.Text, DataTypes.Text },
      { SqlCommonType.Time, DataTypes.Time },
    };

    // Select an SQL common type for each DataType
    public static Dictionary<DataType, SqlCommonType> ToSqlCommon = new Dictionary<DataType, SqlCommonType> {
      //{ DataTypes.Unknown, SqlCommonType.None },
      { DataTypes.Binary, SqlCommonType.Binary },
      { DataTypes.Bool, SqlCommonType.Bool },
      { DataTypes.Number, SqlCommonType.Number },
      { DataTypes.Text, SqlCommonType.Text },
      { DataTypes.Time, SqlCommonType.Time },
      { DataTypes.Row, SqlCommonType.Binary },
      { DataTypes.Table, SqlCommonType.Binary },
      { DataTypes.User, SqlCommonType.Binary },
    };

    // Configure the target - statics only
    public static void Configure(SqliteDatabase database) {
      _sqlgen = new SqlGen();
      _database = database;
      ExprDict = new Dictionary<int, ExpressionEval>();
    }

    // create a target instance
    public static SqlTarget Create() {
      return new SqlTarget();
    }

    public void Begin() {
      if (_database.Nesting == 0)
        Logger.WriteLine(2, ">>>{0}", "BEGIN;");
      _database.Begin();
    }

    public void Commit() {
      _database.Commit();
      if (_database.Nesting == 0)
        Logger.WriteLine(2, ">>>{0}", "COMMIT;");
    }

    public void Abort() {
      _database.Abort();
      Logger.WriteLine(2, ">>>{0}", "ABORT;");
    }

    public void OpenStatement() {
      if (_statement == null)
        _statement = _database.CreateStatement();
      if (!_statement.IsPrepared) {
        Logger.WriteLine(3, "Open Statement {0}", _database.Nesting);
      }
    }

    public void CloseStatement() {
      if (_statement.IsPrepared) {
        _statement.Close();
        Logger.WriteLine(3, "Close Statement {0}", _database.Nesting);
      }
    }

    // Register a set of expressions
    // simplest is to allocate big enough accumulator block for all to use
    public bool RegisterExpressions(params ExpressionEval[] exprs) {
      var numacc = exprs.Where(e => e.HasFold).Sum(e => e.AccumCount);
      foreach (var expr in exprs)
        if (!RegisterExpression(expr, numacc))
          return false;
        return true;
    }

    public bool RegisterExpression(ExpressionEval expr, int naccum) {
      if (!ExprDict.ContainsKey(expr.Serial)) {
        var args = expr.Lookup.Columns.Select(c => ToSqlCommon[c.DataType]).ToArray();
        var retn = ToSqlCommon[expr.DataType];
        ExprDict[expr.Serial] = expr;
        var name = SqlGen.FuncName(expr);
        Logger.WriteLine(3, "Register {0} naccum={1} expr='{2}'", name, naccum, expr);
        if (expr.IsOpen)
          return _database.CreateFunction(SqlGen.FuncName(expr), FuncTypes.Open, expr.Serial, args, retn);
        else if (expr.HasFold)
          return _database.CreateAggFunction(SqlGen.FuncName(expr), expr.Serial, naccum, args, retn);
      }
      return true;
    }

    // execute a command -- no return
    public void ExecuteCommand(string sqls) {
      foreach (var sql in sqls.Split('\n')) {
        Logger.WriteLine(2, ">>>{0}", sql);
        if (!_statement.ExecuteCommand(sql))
          throw new SqlException("command failed code={0} message={1}", _database.LastResult, _database.LastMessage);
      }
    }

    public void ExecuteQuery(string sql) {
      Logger.WriteLine(2, ">>>{0}", sql);
      if (!_statement.ExecuteQuery(sql))
        throw new SqlException("query failed code={0} message={1}", _database.LastResult, _database.LastMessage);
    }

    public void ExecutePrepare(string sql) {
      Logger.WriteLine(2, ">>>{0}", sql);
      if (!_statement.Prepare(sql))
        throw new SqlException("prepare failed code={0} message={1}", _database.LastResult, _database.LastMessage);
    }

    public void ExecuteSend(TypedValue[] values) {
      Logger.WriteLine(3, "Send <{0}>", String.Join(", ", values.Select(v => v.ToString())));
      var ctypes = values.Select(v => ToSqlCommon[v.DataType.BaseType]).ToArray();
      var ovalues = values.Select(v => ToObjectDict[v.DataType.BaseType](v)).ToArray();
      Logger.WriteLine(3, "Send ct={0} v={1}", Util.Join(",", ctypes), Util.Join(",", ovalues));
      if (!_statement.PutValues(ctypes, ovalues))
        throw new SqlException("send failed code={0} message={1}", _database.LastResult, _database.LastMessage);
    }

    public void Fetch() {
      Logger.Write(3, "Fetch");
      if (!_statement.Fetch())
        throw new SqlException("fetch failed code={0} message={1}", _database.LastResult, _database.LastMessage);
    }

    // get values from fetch to match a heading
    public void GetData(DataHeading heading, TypedValue[] values) {
      Logger.WriteLine(4, "GetData {0}", heading);
      var ovalues = new object[heading.Degree];
      var atypes = heading.Columns.Select(c => ToSqlCommon[c.DataType.BaseType]).ToArray();
      if (!_statement.GetValues(atypes, ovalues))
        throw new SqlException("get data failed code={0} message={1}", _database.LastResult, _database.LastMessage);
      for (int i = 0; i < heading.Degree; ++i) {
        var datatype = heading.Columns[i].DataType;
        values[i] = (ovalues[i] == null) ? datatype.DefaultValue()
          : FromObjectDict[datatype.BaseType](ovalues[i], datatype);
      }
    }

    // get a single int scalar
    public void GetData(out int value) {
      Logger.WriteLine(3, "GetData int");
      var ctypes = new SqlCommonType[] { SqlCommonType.Integer };
      var fields = new object[1];
      if (!_statement.GetValues(ctypes, fields))
        throw new SqlException("get data failed code={0} message={1}", _database.LastResult, _database.LastMessage);
      value = (int)fields[0];
    }

    // get a single bool scalar
    public void GetData(out bool value) {
      Logger.WriteLine(3, "GetData bool");
      value = HasData;
    }

    ///-------------------------------------------------------------------
    ///
    /// Catalog info
    /// 

    // Construct and return a heading for a database table
    // return null if not found or error
    public DataHeading GetTableHeading(string table) {
      Tuple<string, SqlCommonType>[] columns;
      if (_database == null || !_database.GetTableColumns(table, out columns) || columns.Length == 0)
        return null;
      var cols = columns.Select(c => DataColumn.Create(c.Item1, FromSqlCommon[c.Item2]));
      return DataHeading.Create(cols);    // ignore column order
    }

  }

  //////////////////////////////////////////////////////////////////////
  /// <summary>
  /// Implement an evaluator that can be called from Sql
  /// </summary>
  public class SqlEvaluator : ISqlEvaluateSerial {
    SqliteDatabase _database { get { return SqlTarget.Database; } }

    public static SqlEvaluator Create() {
      return new SqlEvaluator();
    }

    // Convert accum block to binary
    public static byte[] ToBinary(AccumulatorBlock accblk) {
      using (var writer = PersistWriter.Create()) {
        writer.Write(accblk);
        return writer.ToArray();
      }
    }

    // Convert accum block from binary
    public static AccumulatorBlock FromBinary(byte[] bytes) {
      using (var reader = PersistReader.Create(bytes)) {
        return reader.ReadAccum();
      }
    }

    // Callback function to evaluate a non-aggregate expression
    public object EvalSerialOpen(int serial, FuncTypes functype, object[] values) {
      return EvaluateCommon(SqlTarget.ExprDict[serial], functype, values, IntPtr.Zero, 0);
    }

    // Callback function to evaluate an expression
    public object EvalSerialAggOpen(int serial, FuncTypes functype, object[] values, IntPtr accptr, int naccum) {
      return EvaluateCommon(SqlTarget.ExprDict[serial], functype, values, accptr, naccum);
    }

    // Callback function to finalise an aggregation
    // No value arguments, so uses the value stored in the accumulator result field
    // TODO: handle default value
    public object EvalSerialAggFinal(int serial, FuncTypes functype, IntPtr accptr) {
      var expr = SqlTarget.ExprDict[serial];
      var accblk = GetAccum(accptr, 0);
      FreeAccum(accptr);
      if (accblk.Result == null)
        return SqlTarget.ToObjectDict[expr.DataType.BaseType](expr.DataType.DefaultValue());
      return SqlTarget.ToObjectDict[accblk.Result.DataType.BaseType](accblk.Result);
    }

    // Callback function to evaluate an expression
    public object EvaluateCommon(ExpressionEval expr, FuncTypes functype, object[] values, IntPtr accptr, int naccum) {
      var lookup = GetLookup(expr, values);
      TypedValue retval = null;
      switch (functype) {
      case FuncTypes.Open:
        retval = expr.EvalOpen(lookup);
        break;
      case FuncTypes.Predicate:
        retval = expr.EvalPred(lookup);
        break;
      case FuncTypes.Aggregate:
      case FuncTypes.Ordered:
        var accblk = GetAccum(accptr, naccum);
        retval = expr.EvalHasFold(lookup, accblk as AccumulatorBlock);
        accblk.Result = retval;
        PutAccum(accptr, accblk);
        break;
      }
      return SqlTarget.ToObjectDict[retval.DataType.BaseType](retval);
    }
    
    // create a lookup for an expression from a set of values
    LookupHolder GetLookup(ExpressionEval expr, object[] ovalues) {
      // build the lookup
      var lookup = new LookupHolder();
      for (int i = 0; i < expr.NumArgs; ++i) {
        //var value = TypedValue.Parse(expr.Lookup.Columns[i].DataType, values[i]);
        var datatype = expr.Lookup.Columns[i].DataType;
        var value = (ovalues[i] == null) ? datatype.DefaultValue()
          : SqlTarget.FromObjectDict[datatype.BaseType](ovalues[i], datatype);
        lookup.LookupDict.Add(expr.Lookup.Columns[i].Name, value);
      }
      return lookup;
    }

    // return (and possibly allocate) an accumulator block
    AccumulatorBlock GetAccum(IntPtr accptr, int naccum) {
      if (accptr == IntPtr.Zero)
        return AccumulatorBlock.Create(naccum);
      var lenptr = (LenPtrPair)Marshal.PtrToStructure(accptr, typeof(LenPtrPair));
      if (lenptr.Length == 0)
        return AccumulatorBlock.Create(naccum);
      var accbytes = new byte[lenptr.Length];
      Marshal.Copy(lenptr.Pointer, accbytes, 0, lenptr.Length);
      AccumulatorBlock accblk = FromBinary(accbytes);
      return accblk;
    }

    // save (and possibly allocate) an accumulator block
    void PutAccum(IntPtr accptr, AccumulatorBlock accblk) {
      var lenptr = (LenPtrPair)Marshal.PtrToStructure(accptr, typeof(LenPtrPair));
      // HACK: fill unused slots with valid values so persist will work
      // TODO: avoid empty slots
      for (int i = 0; i < accblk.Accumulators.Length; ++i)
        if (accblk.Accumulators[i] == null)
          accblk.Accumulators[i] = TypedValue.Empty;
      var accbytes = ToBinary(accblk);
      lenptr.Length = accbytes.Length;
      lenptr.Pointer = _database.MemRealloc(lenptr.Pointer, accbytes.Length);
      Marshal.Copy(accbytes, 0, lenptr.Pointer, lenptr.Length);
      Marshal.StructureToPtr(lenptr, accptr, false);
    }

    // possible free an accumulator block
    void FreeAccum(IntPtr accptr) {
      if (accptr != IntPtr.Zero) {
        var lenptr = (LenPtrPair)Marshal.PtrToStructure(accptr, typeof(LenPtrPair));
        _database.MemFree(lenptr.Pointer);
      }
    }
  }
  
  /// <summary>
  /// stub class to hold call via delegate
  /// </summary>
  public class LookupHolder : ILookupValue {
    public Dictionary<string, TypedValue> LookupDict = new Dictionary<string, TypedValue>();
    public bool LookupValue(string name, ref TypedValue value) {
      if (!LookupDict.ContainsKey(name)) return false;
      //Logger.Assert(LookupDict.ContainsKey(name), name);
      value = LookupDict[name];
      return true;
    }
  }

  
}
