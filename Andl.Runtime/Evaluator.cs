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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Andl.Runtime {
  public enum Opcodes {
    NOP,
    CALL,       // call with fixed args
    CALLV,      // call with variable args (CodeValue)
    CALLVT,     // call with variable args (TypedValue)
    LDACC,      // load value from accumulator by number
    LDACCBLK,   // load current accumulator block as object value
    LDAGG,      // load aggregation value
    LDCAT,      // load value from catalog by name
    LDCATR,     // load raw (unevaluated) value from catalog by name
    LDCOMP,     // load value from UDT component by name
    LDFIELD,    // load field value via lookup
    LDLOOKUP,   // load current lookup as object value
    LDSEG,      // load segment of code to be executed as arg
    LDVALUE,    // load actual value
    LDFIELDT,   // load value from tuple by name
    EOS,        // end of statement, pop value as return
  };

  public struct ByteCode {
    public byte[] bytes;
    public int Length { get { return bytes == null ? 0 : bytes.Length; } }
    public void Add(byte[] morecode) {
      var temp = new List<byte>(bytes);
      temp.AddRange(morecode);
      bytes = temp.ToArray();
    }
    public override string ToString() {
      return String.Format("Code: [{0}]", Length);
    }
  }

  /// <summary>
  /// Join operation implemented by this function
  /// Note: LCR must be numerically same as MergeOps
  /// </summary>
  [Flags]
  public enum JoinOps {
    // basic values
    NUL, LEFT = 1, COMMON = 2, RIGHT = 4,
    SETL = 8, SETC = 16, SETR = 32,
    ANTI = 64, SET = 128, REV = 256, ERROR = 512,
    // mask combos
    MERGEOPS = LEFT | COMMON | RIGHT,
    SETOPS = SETL | SETC | SETR,
    OTHEROPS = ANTI | REV | ERROR,
    // joins
    JOIN = LEFT | COMMON | RIGHT,
    COMPOSE = LEFT | RIGHT,
    DIVIDE = LEFT,
    RDIVIDE = RIGHT,
    SEMIJOIN = LEFT | COMMON,
    RSEMIJOIN = RIGHT | COMMON,
    // antijoins
    ANTIJOIN = ANTI | LEFT | COMMON,
    ANTIJOINL = ANTI | LEFT,
    RANTIJOIN = ANTI | RIGHT | COMMON | REV,
    RANTIJOINR = ANTI | RIGHT | REV,
    // set
    UNION = SET | COMMON | SETL | SETC | SETR,
    INTERSECT = SET | COMMON | SETC,
    SYMDIFF = SET | COMMON | SETL | SETR,
    MINUS = SET | COMMON | SETL,
    RMINUS = SET | COMMON | SETR | REV,
  };

  public interface ILookupValue {
    bool LookupValue(string name, ref TypedValue value);
  }

  /// <summary>
  /// Implements the runtime for expression evaluation.
  /// Stack based scode.
  /// 
  /// Dependencies: 
  ///   catalog for name lookup
  ///   builtin for method calls
  /// </summary>
  public class Evaluator {
    //BUG: obsolete, used by DataTableLocal!!!
    public static Evaluator Current { get; private set; }
    public TextWriter Output { get; private set; }
    public TextReader Input { get; private set; }

    CatalogPrivate _catalog;
    Builtin _builtin;

    // runtime
    Stack<ILookupValue> _lookups = new Stack<ILookupValue>();
    List<object> _scode = new List<object>();
    Stack<TypedValue> _stack = new Stack<TypedValue>();

    // Create with catalog 
    public static Evaluator Create(CatalogPrivate catalog, TextWriter output, TextReader input) {
      DataTypes.Init();
      var ev = new Evaluator() {
        _catalog = catalog,
        Output = output,
        Input = input,
      };
      ev._builtin = Builtin.Create(catalog, ev);
      return ev;
    }

    // Common entry point for executing code
    public TypedValue Exec(ByteCode code, ILookupValue lookup = null, TypedValue aggregate = null, AccumulatorBlock accblock = null) {
      Logger.WriteLine(5, "Exec {0} {1} {2} {3}", code.Length, lookup, aggregate, accblock);
      if (code.Length == 0) return VoidValue.Void;
      if (lookup != null) PushLookup(lookup);
      Current = this;
      TypedValue retval = null;
      try {
        retval = Run(code, aggregate, accblock);
      } catch (TargetInvocationException ex) {
        throw ex.InnerException;
      }
      if (lookup != null) PopLookup();
      return retval;
    }

    // used by Invoke
    public void PushLookup(ILookupValue lookup) {
      Logger.WriteLine(4, "Push lookup {0}", lookup);
      _lookups.Push(lookup);
    }

    public void PopLookup() {
      Logger.WriteLine(4, "Pop lookup {0}", _lookups.Peek());
      _lookups.Pop();
    }

    // Perform a value lookup for project
    public TypedValue Lookup(string name, ILookupValue lookup = null) {
      if (lookup != null)
        _lookups.Push(lookup);
      var value = TypedValue.Empty;
      var ok = LookupValue(name, ref value);
      Logger.Assert(ok, name);
      if (lookup != null)
        _lookups.Pop();
      return value;
    }

    ///=================================================================
    /// Implementation
    /// 

    void PopStack(int n) {
      while (n-- > 0)
        _stack.Pop();
    }

    void PushStack(TypedValue value) {
      _stack.Push(value);
    }

    // Find value for token in lookup by name
    bool LookupValue(string token, ref TypedValue value) {
      foreach (var lookup in _lookups) {
        if (lookup.LookupValue(token, ref value))
          return true;
      }
      return false;
    }

    // Evaluation engine for ByteCode
    TypedValue Run(ByteCode bcode, TypedValue aggregate, AccumulatorBlock accblock) {
      TypedValue retval = null;
      var reader = PersistReader.Create(bcode.bytes);
      while (reader.More) {
        var opcode = reader.ReadOpcode();
        switch (opcode) {
        // Known literal, do not translate into value
        case Opcodes.LDVALUE:
          PushStack(reader.ReadValue());
          break;
        // Known catalog variable, look up value
        case Opcodes.LDCAT:
          var val = _catalog.GetValue(reader.ReadString());
          if (val.DataType == DataTypes.Code)
            val = this.Exec((val as CodeValue).Value.Code);
          if (val.DataType != DataTypes.Void)
            _stack.Push(val);
          break;
        case Opcodes.LDCATR:
          PushStack(_catalog.GetValue(reader.ReadString()));
          break;
        // Load value obtained using lookup by name
        case Opcodes.LDFIELD:
          var value = TypedValue.Empty;
          var ok = LookupValue(reader.ReadString(), ref value);
          Logger.Assert(ok, opcode);
          PushStack(value);
          break;
        // Load aggregate value or use specified start value if not available
        case Opcodes.LDAGG:
          var agvalue = reader.ReadValue();
          PushStack(aggregate ?? agvalue);
          break;
        // load accumulator by index, or fixed value if not available
        case Opcodes.LDACC:
          var accnum = reader.ReadInteger();
          var defval = reader.ReadValue();
          PushStack(accblock == null ? defval : accblock[accnum]);
          break;
        // Load a segment of code for later call, with this evaluator packaged in
        case Opcodes.LDSEG:
          var expr = reader.ReadExpr();
          var cvalue = CodeValue.Create(ExpressionEval.Create(this, expr));
          PushStack(cvalue);
          break;
        case Opcodes.LDLOOKUP:
          var bv = TypedValue.Create(_lookups.Peek() as object);
          PushStack(bv);
          break;
        case Opcodes.LDACCBLK:
          var acb = TypedValue.Create(accblock as object);
          PushStack(acb);
          break;
        case Opcodes.LDCOMP:
          var udtval = _stack.Pop() as UserValue;
          var compval = udtval.GetComponentValue(reader.ReadString());
          PushStack(compval);
          break;
        case Opcodes.LDFIELDT:
          var tupval = _stack.Pop() as TupleValue;
          var fldval = tupval.GetFieldValue(reader.ReadString());
          PushStack(fldval);
          break;
        // Call a function, fixed or variable arg count
        case Opcodes.CALL:
        case Opcodes.CALLV:
        case Opcodes.CALLVT:
          var name = reader.ReadString();
          var meth = typeof(Builtin).GetMethod(name);
          var nargs = reader.ReadByte();
          var nvargs = reader.ReadByte();
          var args = new object[nargs];
          var argx = args.Length - 1;
          if (opcode == Opcodes.CALLV) {
            var vargs = new CodeValue[nvargs];
            for (var j = vargs.Length - 1; j >= 0; --j)
              vargs[j] = _stack.Pop() as CodeValue;
            args[argx--] = vargs;
          } else if (opcode == Opcodes.CALLVT) {
            var vargs = new TypedValue[nvargs];
            for (var j = vargs.Length - 1; j >= 0; --j)
              vargs[j] = _stack.Pop() as TypedValue;
            args[argx--] = vargs;
          }
          for (; argx >= 0; --argx)
            args[argx] = _stack.Pop();
          var ret = meth.Invoke(_builtin, args) as TypedValue;
          _stack.Push(ret);
          //if (ret.DataType != DataTypes.Void)
          //  _stack.Push(ret);
          break;
        case Opcodes.EOS:
          retval = _stack.Pop();
          //retval = (_stack.Count > 0) ? _stack.Pop() : VoidValue.Void;
          break;
        default:
          throw new NotImplementedException(opcode.ToString());
        }
      }
      if (retval == null) retval = _stack.Pop();
      //Logger.Assert(retval != null, "stack");
      return retval;
    }

#if SCODE_ENABLED
    // Evaluation engine for SCode (objects)
    void Run(IReadOnlyList<object> scode, TypedValue aggregate, AccumulatorBlock accblock) {
      for (var pc = 0; pc < scode.Count; ) {
        var opcode = (Opcodes)scode[pc++];
        switch (opcode) {
        // Known literal, do not translate into value
        case Opcodes.LDVALUE:
          PushStack(scode[pc] as TypedValue);
          pc += 1;
          break;
        // Known catalog variable, look up value
        case Opcodes.LDCAT:
          var val = _catalog.GetValue(scode[pc] as string);
          if (val.DataType == DataTypes.Code)
            val = this.Exec((val as CodeValue).Value.Code);
          PushStack(val);
          pc += 1;
          break;
        case Opcodes.LDCATR:
          PushStack(_catalog.GetValue(scode[pc] as string));
          pc += 1;
          break;
        // Load value obtained using lookup by name
        case Opcodes.LDFIELD:
          var value = TypedValue.Empty;
          var ok = LookupValue(scode[pc] as string, ref value);
          Logger.Assert(ok, opcode);
          PushStack(value);
          pc += 1;
          break;
        // Load aggregate value or use specified start value if not available
        case Opcodes.LDAGG:
          if (aggregate == null) { // seed value
            aggregate = scode[pc] as TypedValue;
          }
          Logger.Assert(aggregate.DataType != null, aggregate.DataType);
          pc += 1;
          PushStack(aggregate);
          break;
        // load accumulator by index, or fixed value if not available
        case Opcodes.LDACC:
          var accnum = (int)scode[pc];
          var defval = scode[pc+1] as TypedValue;
          pc += 2;
          PushStack(accblock == null ? defval : accblock[accnum]);
          break;
        // Load a segment of code for later call
        case Opcodes.LDSEG:
          var cb = scode[pc] as CodeValue;
          //cb.Value.Evaluator = this;
          PushStack(cb);
          pc += 1;
          break;
        case Opcodes.LDLOOKUP:
          var bv = TypedValue.Create(_lookups.Peek() as object);
          PushStack(bv);
          break;
        case Opcodes.LDACCBLK:
          var acb = TypedValue.Create(accblock as object);
          PushStack(acb);
          break;
        case Opcodes.LDCOMP:
          var udtval = _stack.Pop() as UserValue;
          var compval = udtval.GetComponentValue(scode[pc] as string);
          PushStack(compval);
          pc += 1;
          break;
        // Call a function, fixed or variable arg count
        case Opcodes.CALL:
        case Opcodes.CALLV:
        case Opcodes.CALLVT:
          var meth = scode[pc] as MethodInfo;
          var rtype = scode[pc + 1] as DataType;
          var args = new object[meth.GetParameters().Length];
          var argx = args.Length - 1;
          if (opcode == Opcodes.CALLV) {
            var vargs = new CodeValue[(int)scode[pc + 2]];
            for (var j = vargs.Length - 1; j >= 0; --j)
              vargs[j] = _stack.Pop() as CodeValue;
            args[argx--] = vargs;
          } else if (opcode == Opcodes.CALLVT) {
              var vargs = new TypedValue[(int)scode[pc + 2]];
              for (var j = vargs.Length - 1; j >= 0; --j)
                vargs[j] = _stack.Pop() as TypedValue;
              args[argx--] = vargs;
          }
          for ( ; argx >= 0; --argx)
            args[argx] = _stack.Pop();
          var ret = meth.Invoke(rtype, args);
          if (rtype != DataTypes.Void)
            _stack.Push(ret as TypedValue);
          pc += (opcode == Opcodes.CALL) ?  2 : 3;
          break;
        default:
          throw new NotImplementedException(opcode.ToString());
        }
      }
    }
#endif
  }
}
