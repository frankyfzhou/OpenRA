// Eluant API compatibility shim — LuaTable backed by MoonSharp Table
using System;
using System.Collections;
using System.Collections.Generic;
using MoonSharp.Interpreter;

namespace Eluant
{
	/// <summary>
	/// Wraps a MoonSharp Table as an Eluant-compatible LuaTable.
	/// Supports key enumeration, indexer, Add, ContainsKey, Count.
	/// </summary>
	public sealed class LuaTable : LuaValue, IEnumerable<KeyValuePair<LuaValue, LuaValue>>
	{
		internal readonly Table MoonTable;
		readonly LuaRuntime runtime;

		internal LuaTable(Table table, LuaRuntime runtime)
		{
			MoonTable = table ?? throw new ArgumentNullException(nameof(table));
			this.runtime = runtime;
		}

		/// <summary>
		/// Get or set a value by key. Matches Eluant's LuaTable indexer.
		/// </summary>
		public LuaValue this[LuaValue key]
		{
			get
			{
				var dvKey = key.ToDynValue(MoonTable.OwnerScript);
				var dvVal = MoonTable.Get(dvKey);
				return LuaValue.FromDynValue(dvVal, runtime);
			}
			set
			{
				var dvKey = key.ToDynValue(MoonTable.OwnerScript);
				var dvVal = value?.ToDynValue(MoonTable.OwnerScript) ?? DynValue.Nil;
				MoonTable.Set(dvKey, dvVal);
			}
		}

		/// <summary>
		/// Get or set a value by string key. Used by Globals["name"].
		/// </summary>
		public LuaValue this[string key]
		{
			get
			{
				var dvVal = MoonTable.Get(key);
				return LuaValue.FromDynValue(dvVal, runtime);
			}
			set
			{
				var dvVal = value?.ToDynValue(MoonTable.OwnerScript) ?? DynValue.Nil;
				MoonTable.Set(key, dvVal);
			}
		}

		public void Add(LuaValue key, LuaValue value)
		{
			this[key] = value;
		}

		public void Add(string key, LuaValue value)
		{
			this[key] = value;
		}

		public bool ContainsKey(string key)
		{
			var dv = MoonTable.Get(key);
			return dv != null && !dv.IsNil();
		}

		public bool ContainsKey(LuaValue key)
		{
			return ContainsKey(key.ToString());
		}

		/// <summary>
		/// Returns all keys in the table. Matches Eluant's LuaTable.Keys.
		/// </summary>
		public IEnumerable<LuaValue> Keys
		{
			get
			{
				foreach (var pair in MoonTable.Pairs)
					yield return LuaValue.FromDynValue(pair.Key, runtime);
			}
		}

		public int Count
		{
			get
			{
				var count = 0;
				foreach (var _ in MoonTable.Pairs)
					count++;
				return count;
			}
		}

		public IEnumerator<KeyValuePair<LuaValue, LuaValue>> GetEnumerator()
		{
			foreach (var pair in MoonTable.Pairs)
			{
				var key = LuaValue.FromDynValue(pair.Key, runtime);
				var value = LuaValue.FromDynValue(pair.Value, runtime);
				yield return new KeyValuePair<LuaValue, LuaValue>(key, value);
			}
		}

		IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

		public override bool ToBoolean() => true;
		public override string ToString() => "table";
		internal override DynValue ToDynValue(Script script) => DynValue.NewTable(MoonTable);
	}
}
