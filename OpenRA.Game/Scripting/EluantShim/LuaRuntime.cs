// Eluant API compatibility shim — LuaRuntime backed by MoonSharp Script
using System;
using MoonSharp.Interpreter;

namespace Eluant
{
	/// <summary>
	/// Wraps a MoonSharp Script instance. Provides the Eluant LuaRuntime API surface.
	/// </summary>
	public class LuaRuntime : IDisposable
	{
		internal readonly Script Script;
		LuaTable globals;

		public LuaRuntime()
		{
			Script = new Script(CoreModules.Preset_SoftSandbox);
			ScriptRuntimeMap.Register(Script, this);
		}

		public LuaTable Globals => globals ??= new LuaTable(Script.Globals, this);

		/// <summary>
		/// Execute a chunk of Lua code. Matches Eluant's DoBuffer(code, name).
		/// </summary>
		public LuaVararg DoBuffer(string code, string chunkName)
		{
			var result = Script.DoString(code, codeFriendlyName: chunkName);
			return LuaVararg.FromDynValue(result, this);
		}

		/// <summary>
		/// Create a LuaFunction that wraps a .NET delegate.
		/// Handles simple delegates (Action, Action&lt;string&gt;, etc.) and
		/// Lua-aware delegates (Func&lt;LuaVararg, LuaValue&gt;-like via reflection).
		/// </summary>
		public LuaFunction CreateFunctionFromDelegate(Delegate d)
		{
			var callback = WrapDelegate(d);
			var dv = DynValue.NewCallback(callback);
			return new LuaFunction(dv, this);
		}

		/// <summary>Create a new empty Lua table.</summary>
		public LuaTable CreateTable()
		{
			var table = new Table(Script);
			return new LuaTable(table, this);
		}

		Func<ScriptExecutionContext, CallbackArguments, DynValue> WrapDelegate(Delegate d)
		{
			var method = d.Method;
			var parameters = method.GetParameters();

			// Special case: single LuaVararg parameter → pass args directly
			if (parameters.Length == 1 && parameters[0].ParameterType == typeof(LuaVararg))
			{
				return (ctx, args) =>
				{
					var luaArgs = new LuaValue[args.Count];
					for (var i = 0; i < args.Count; i++)
						luaArgs[i] = LuaValue.FromDynValue(args[i], this);

					var vararg = new LuaVararg(luaArgs);
					var result = d.DynamicInvoke(vararg);

					if (result is LuaValue lv)
						return lv.ToDynValue(Script);

					return DynValue.Nil;
				};
			}

			// General case: marshal each parameter individually
			return (ctx, args) =>
			{
				var clrArgs = new object[parameters.Length];
				for (var i = 0; i < parameters.Length; i++)
				{
					if (i >= args.Count)
					{
						clrArgs[i] = parameters[i].HasDefaultValue ? parameters[i].DefaultValue : null;
						continue;
					}

					var dv = args[i];
					var pt = parameters[i].ParameterType;

					if (pt == typeof(string))
						clrArgs[i] = dv.IsNil() ? null : dv.String;
					else if (pt == typeof(int))
						clrArgs[i] = (int)dv.Number;
					else if (pt == typeof(double))
						clrArgs[i] = dv.Number;
					else if (pt == typeof(bool))
						clrArgs[i] = dv.Boolean;
					else if (pt == typeof(LuaValue))
						clrArgs[i] = LuaValue.FromDynValue(dv, this);
					else if (pt == typeof(LuaFunction))
						clrArgs[i] = new LuaFunction(dv, this);
					else if (pt == typeof(LuaTable))
						clrArgs[i] = dv.Type == DataType.Table ? new LuaTable(dv.Table, this) : null;
					else
						clrArgs[i] = dv.ToObject();
				}

				var result = d.DynamicInvoke(clrArgs);

				if (result is LuaValue luaResult)
					return luaResult.ToDynValue(Script);

				if (result is DynValue dvResult)
					return dvResult;

				if (result == null)
					return DynValue.Nil;

				return DynValue.FromObject(Script, result);
			};
		}

		public virtual void Dispose()
		{
			ScriptRuntimeMap.Unregister(Script);
		}
	}

	/// <summary>
	/// LuaRuntime with memory tracking. MoonSharp doesn't support native memory limits,
	/// so MaxMemoryUse/MemoryUse are approximate via GC tracking.
	/// Instruction counting IS supported via Script.Options.InstructionLimit.
	/// </summary>
	public sealed class MemoryConstrainedLuaRuntime : LuaRuntime
	{
		/// <summary>
		/// Approximate current memory usage. Uses GC total memory as a proxy.
		/// </summary>
		public long MemoryUse => GC.GetTotalMemory(false);

		/// <summary>
		/// Maximum allowed memory. Setting this is advisory only in MoonSharp.
		/// We don't enforce a hard limit since WASM has its own memory bounds.
		/// </summary>
		public long MaxMemoryUse { get; set; }
	}
}
