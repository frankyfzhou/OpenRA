// Eluant API compatibility shim — LuaValue type hierarchy backed by MoonSharp
using System;
using System.Collections;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace Eluant
{
	/// <summary>
	/// Abstract base for all Lua values. Mirrors the Eluant LuaValue API.
	/// Disposal is a no-op in MoonSharp (GC-managed), but kept for source compat.
	/// </summary>
	public abstract class LuaValue : IDisposable
	{
		public virtual void Dispose() { }

		public virtual bool ToBoolean() => throw new LuaException($"Cannot convert {GetType().Name} to boolean");
		public virtual double? ToNumber() => throw new LuaException($"Cannot convert {GetType().Name} to number");

		/// <summary>
		/// Try to extract the wrapped CLR object (for LuaCustomClrObject).
		/// </summary>
		public virtual bool TryGetClrObject(out object obj)
		{
			obj = null;
			return false;
		}

		/// <summary>
		/// Returns a copy of this reference. In MoonSharp (GC-managed), returns self.
		/// Eluant required explicit ref-counting; MoonSharp does not.
		/// </summary>
		public virtual LuaValue CopyReference() => this;

		/// <summary>Convert this shim value to a MoonSharp DynValue.</summary>
		internal abstract DynValue ToDynValue(Script script);

		/// <summary>Convert a MoonSharp DynValue to a shim LuaValue.</summary>
		internal static LuaValue FromDynValue(DynValue dv, LuaRuntime runtime)
		{
			if (dv == null || dv.IsNil() || dv.IsVoid())
				return LuaNil.Instance;

			return dv.Type switch
			{
				DataType.Number => new LuaNumber(dv.Number),
				DataType.String => new LuaString(dv.String),
				DataType.Boolean => new LuaBoolean(dv.Boolean),
				DataType.Function or DataType.ClrFunction => new LuaFunction(dv, runtime),
				DataType.Table => new LuaTable(dv.Table, runtime),
				DataType.UserData when dv.UserData?.Object != null => new LuaCustomClrObject(dv.UserData.Object),
				DataType.Tuple => FromDynValue(dv.Tuple.Length > 0 ? dv.Tuple[0] : DynValue.Nil, runtime),
				_ => LuaNil.Instance,
			};
		}

		// Implicit conversions matching Eluant's API
		public static implicit operator LuaValue(int value) => new LuaNumber(value);
		public static implicit operator LuaValue(double value) => new LuaNumber(value);
		public static implicit operator LuaValue(bool value) => new LuaBoolean(value);
		public static implicit operator LuaValue(string value) => value == null ? LuaNil.Instance : new LuaString(value);
	}

	public sealed class LuaNil : LuaValue
	{
		public static readonly LuaNil Instance = new();
		LuaNil() { }

		public override bool ToBoolean() => false;
		public override string ToString() => "nil";
		internal override DynValue ToDynValue(Script script) => DynValue.Nil;
	}

	public sealed class LuaBoolean : LuaValue
	{
		readonly bool value;
		public LuaBoolean(bool value) { this.value = value; }

		public override bool ToBoolean() => value;
		public override string ToString() => value ? "true" : "false";
		internal override DynValue ToDynValue(Script script) => DynValue.NewBoolean(value);
	}

	public sealed class LuaNumber : LuaValue
	{
		readonly double value;
		public LuaNumber(double value) { this.value = value; }

		public override bool ToBoolean() => true;
		public override double? ToNumber() => value;
		public override string ToString() => value.ToString();
		internal override DynValue ToDynValue(Script script) => DynValue.NewNumber(value);
	}

	public sealed class LuaString : LuaValue
	{
		readonly string value;
		public LuaString(string value) { this.value = value; }

		public override bool ToBoolean() => true;
		public override string ToString() => value;
		internal override DynValue ToDynValue(Script script) => DynValue.NewString(value);
	}

	/// <summary>
	/// Wraps a CLR object for Lua consumption. OpenRA pushes Actor, CVec, WPos etc. through this.
	/// Uses MoonSharp UserData internally; metamethods dispatch to ILua*Binding interfaces.
	/// </summary>
	public sealed class LuaCustomClrObject : LuaValue
	{
		readonly object clrObject;

		public LuaCustomClrObject(object obj)
		{
			clrObject = obj ?? throw new ArgumentNullException(nameof(obj));
		}

		public override bool TryGetClrObject(out object obj)
		{
			obj = clrObject;
			return true;
		}

		public override bool ToBoolean() => true;
		public override string ToString() => clrObject.ToString();

		internal override DynValue ToDynValue(Script script)
		{
			// Register the CLR type with MoonSharp if not already registered
			var type = clrObject.GetType();
			if (!UserData.IsTypeRegistered(type))
				UserData.RegisterType(type, new EluantUserDataDescriptor(type));

			return UserData.Create(clrObject);
		}
	}

	/// <summary>
	/// Wraps a MoonSharp function DynValue. Provides Call() returning LuaVararg.
	/// </summary>
	public sealed class LuaFunction : LuaValue
	{
		internal readonly DynValue DynFunc;
		internal readonly LuaRuntime Runtime;

		internal LuaFunction(DynValue func, LuaRuntime runtime)
		{
			DynFunc = func;
			Runtime = runtime;
		}

		public LuaVararg Call(params LuaValue[] args)
		{
			var script = Runtime.Script;
			var dynArgs = new DynValue[args.Length];
			for (var i = 0; i < args.Length; i++)
				dynArgs[i] = args[i].ToDynValue(script);

			var result = script.Call(DynFunc, dynArgs);
			return LuaVararg.FromDynValue(result, Runtime);
		}

		public override bool ToBoolean() => true;
		public override string ToString() => "function";
		internal override DynValue ToDynValue(Script script) => DynFunc;
	}

	/// <summary>
	/// Variable-length list of LuaValues. Used as function call results and method parameters.
	/// </summary>
	public sealed class LuaVararg : LuaValue, IReadOnlyList<LuaValue>
	{
		readonly LuaValue[] values;

		public LuaVararg(LuaValue[] values)
		{
			this.values = values ?? [];
		}

		public int Count => values.Length;
		public LuaValue this[int index] => index < values.Length ? values[index] : LuaNil.Instance;

		public override void Dispose()
		{
			foreach (var v in values)
				v?.Dispose();
		}

		public IEnumerator<LuaValue> GetEnumerator() => ((IEnumerable<LuaValue>)values).GetEnumerator();
		IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();

		internal override DynValue ToDynValue(Script script)
		{
			var dvs = new DynValue[values.Length];
			for (var i = 0; i < values.Length; i++)
				dvs[i] = values[i].ToDynValue(script);
			return DynValue.NewTuple(dvs);
		}

		internal new static LuaVararg FromDynValue(DynValue dv, LuaRuntime runtime)
		{
			if (dv == null || dv.IsNilOrNan() || dv.IsVoid())
				return new LuaVararg([]);

			if (dv.Type == DataType.Tuple)
			{
				var items = new LuaValue[dv.Tuple.Length];
				for (var i = 0; i < dv.Tuple.Length; i++)
					items[i] = LuaValue.FromDynValue(dv.Tuple[i], runtime);
				return new LuaVararg(items);
			}

			return new LuaVararg([LuaValue.FromDynValue(dv, runtime)]);
		}

		public override string ToString() => $"LuaVararg[{Count}]";
	}
}
