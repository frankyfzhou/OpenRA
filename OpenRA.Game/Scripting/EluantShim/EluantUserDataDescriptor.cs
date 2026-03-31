// Eluant API compatibility shim — MoonSharp UserData descriptor for CLR objects
// Dispatches Lua metamethods (__index, __add, etc.) to ILua*Binding interfaces.
using System;
using System.Collections.Generic;
using Eluant.ObjectBinding;
using MoonSharp.Interpreter;
using MoonSharp.Interpreter.Interop;

namespace Eluant
{
	/// <summary>
	/// Custom MoonSharp UserData descriptor that bridges Eluant's binding interfaces.
	/// When Lua scripts access properties, call metamethods, or perform arithmetic
	/// on CLR objects, this descriptor routes those operations through the
	/// ILuaTableBinding, ILuaAdditionBinding, etc. interfaces.
	/// </summary>
	public sealed class EluantUserDataDescriptor : IUserDataDescriptor
	{
		readonly Type type;

		// Cache MetaIndex results per metaname so closures are allocated once per type,
		// not once per MetaIndex call. Without this cache, every Lua arithmetic/comparison
		// operation creates a new closure — unbounded allocation on hot paths.
		readonly Dictionary<string, DynValue> metaCache = [];

		public EluantUserDataDescriptor(Type type)
		{
			this.type = type;
		}

		public string Name => type.FullName;
		public Type Type => type;

		public DynValue Index(Script script, object obj, DynValue index, bool isDirectIndexing)
		{
			if (obj is ILuaTableBinding tableBinding)
			{
				var runtime = ScriptRuntimeMap.GetRuntime(script);
				var key = LuaValue.FromDynValue(index, runtime);
				try
				{
					var result = tableBinding[runtime, key];
					return result.ToDynValue(script);
				}
				catch (LuaException ex)
				{
					throw new ScriptRuntimeException(ex.Message);
				}
			}

			return DynValue.Nil;
		}

		public bool SetIndex(Script script, object obj, DynValue index, DynValue value, bool isDirectIndexing)
		{
			if (obj is ILuaTableBinding tableBinding)
			{
				var runtime = ScriptRuntimeMap.GetRuntime(script);
				var key = LuaValue.FromDynValue(index, runtime);
				var val = LuaValue.FromDynValue(value, runtime);
				try
				{
					tableBinding[runtime, key] = val;
					return true;
				}
				catch (LuaException ex)
				{
					throw new ScriptRuntimeException(ex.Message);
				}
			}

			return false;
		}

		public string AsString(object obj)
		{
			if (obj is ILuaToStringBinding tsBinding)
			{
				// Create a dummy runtime for the ToString call — it doesn't use the runtime
				var result = tsBinding.ToString(null);
				return result?.ToString() ?? obj.ToString();
			}

			return obj?.ToString() ?? "nil";
		}

		public DynValue MetaIndex(Script script, object obj, string metaname)
		{
			if (metaCache.TryGetValue(metaname, out var cached))
				return cached;

			var result = BuildMetaIndex(script, obj, metaname);
			if (result != null)
				metaCache[metaname] = result;

			return result;
		}

		DynValue BuildMetaIndex(Script script, object obj, string metaname)
		{
			return metaname switch
			{
				"__add" => MakeBinaryMetamethod<ILuaAdditionBinding>(script, obj,
					static (binding, runtime, l, r) => binding.Add(runtime, l, r)),

				"__sub" => MakeBinaryMetamethod<ILuaSubtractionBinding>(script, obj,
					static (binding, runtime, l, r) => binding.Subtract(runtime, l, r)),

				"__mul" => MakeBinaryMetamethod<ILuaMultiplicationBinding>(script, obj,
					static (binding, runtime, l, r) => binding.Multiply(runtime, l, r)),

				"__div" => MakeBinaryMetamethod<ILuaDivisionBinding>(script, obj,
					static (binding, runtime, l, r) => binding.Divide(runtime, l, r)),

				"__eq" => MakeBinaryMetamethod<ILuaEqualityBinding>(script, obj,
					static (binding, runtime, l, r) => binding.Equals(runtime, l, r)),

				"__lt" => MakeBinaryMetamethod<ILuaLessThanBinding>(script, obj,
					static (binding, runtime, l, r) => binding.LessThan(runtime, l, r)),

				"__le" => MakeBinaryMetamethod<ILuaLessThanOrEqualToBinding>(script, obj,
					static (binding, runtime, l, r) => binding.LessThanOrEqualTo(runtime, l, r)),

				"__unm" => MakeUnaryMetamethod(script, obj),

				"__tostring" => MakeToStringMetamethod(script, obj),

				_ => null,
			};
		}

		// Closures do NOT capture 'obj'. Instead they extract the binding from args
		// at call time. This allows caching one closure per (type, metaname) —
		// the same closure works for any instance of the type.
		DynValue MakeBinaryMetamethod<T>(Script script, object obj,
			Func<T, LuaRuntime, LuaValue, LuaValue, LuaValue> handler) where T : class
		{
			if (obj is not T)
				return null;

			return DynValue.NewCallback((ctx, args) =>
			{
				var runtime = ScriptRuntimeMap.GetRuntime(script);

				// The binding object is one of the two operands (Lua passes both to __add etc.)
				T binding = null;
				if (args[0].Type == DataType.UserData && args[0].UserData?.Object is T t0)
					binding = t0;
				else if (args.Count > 1 && args[1].Type == DataType.UserData && args[1].UserData?.Object is T t1)
					binding = t1;

				if (binding == null)
					return DynValue.Nil;

				var left = LuaValue.FromDynValue(args[0], runtime);
				var right = LuaValue.FromDynValue(args[1], runtime);
				try
				{
					var result = handler(binding, runtime, left, right);
					return result.ToDynValue(script);
				}
				catch (LuaException ex)
				{
					throw new ScriptRuntimeException(ex.Message);
				}
			});
		}

		DynValue MakeUnaryMetamethod(Script script, object obj)
		{
			if (obj is not ILuaUnaryMinusBinding)
				return null;

			return DynValue.NewCallback((ctx, args) =>
			{
				var runtime = ScriptRuntimeMap.GetRuntime(script);
				if (args[0].Type != DataType.UserData || args[0].UserData?.Object is not ILuaUnaryMinusBinding binding)
					return DynValue.Nil;

				try
				{
					var result = binding.Minus(runtime);
					return result.ToDynValue(script);
				}
				catch (LuaException ex)
				{
					throw new ScriptRuntimeException(ex.Message);
				}
			});
		}

		DynValue MakeToStringMetamethod(Script script, object obj)
		{
			if (obj is not ILuaToStringBinding)
				return null;

			return DynValue.NewCallback((ctx, args) =>
			{
				var runtime = ScriptRuntimeMap.GetRuntime(script);
				if (args[0].Type != DataType.UserData || args[0].UserData?.Object is not ILuaToStringBinding binding)
					return DynValue.NewString(obj?.ToString() ?? "nil");

				try
				{
					var result = binding.ToString(runtime);
					return result.ToDynValue(script);
				}
				catch (LuaException ex)
				{
					throw new ScriptRuntimeException(ex.Message);
				}
			});
		}

		public bool IsTypeCompatible(Type type, object obj)
		{
			return this.type.IsAssignableFrom(type);
		}
	}

	/// <summary>
	/// Maps MoonSharp Script instances to their LuaRuntime wrappers.
	/// Needed because MetaIndex/Index callbacks receive a Script but the binding
	/// interfaces expect a LuaRuntime.
	/// </summary>
	internal static class ScriptRuntimeMap
	{
		static readonly Dictionary<Script, LuaRuntime> Map = [];

		internal static void Register(Script script, LuaRuntime runtime)
		{
			Map[script] = runtime;
		}

		internal static LuaRuntime GetRuntime(Script script)
		{
			return Map.TryGetValue(script, out var runtime) ? runtime : null;
		}

		internal static void Unregister(Script script)
		{
			Map.Remove(script);
		}
	}
}
