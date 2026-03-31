// Eluant API compatibility shim — backed by MoonSharp
// These interfaces match the Eluant.ObjectBinding API surface used by OpenRA.
// OpenRA's scripting code (40 files) uses these interfaces unchanged.

namespace Eluant.ObjectBinding
{
	public interface ILuaTableBinding
	{
		LuaValue this[LuaRuntime runtime, LuaValue key] { get; set; }
	}

	public interface ILuaAdditionBinding
	{
		LuaValue Add(LuaRuntime runtime, LuaValue left, LuaValue right);
	}

	public interface ILuaSubtractionBinding
	{
		LuaValue Subtract(LuaRuntime runtime, LuaValue left, LuaValue right);
	}

	public interface ILuaMultiplicationBinding
	{
		LuaValue Multiply(LuaRuntime runtime, LuaValue left, LuaValue right);
	}

	public interface ILuaDivisionBinding
	{
		LuaValue Divide(LuaRuntime runtime, LuaValue left, LuaValue right);
	}

	public interface ILuaUnaryMinusBinding
	{
		LuaValue Minus(LuaRuntime runtime);
	}

	public interface ILuaEqualityBinding
	{
		LuaValue Equals(LuaRuntime runtime, LuaValue left, LuaValue right);
	}

	public interface ILuaToStringBinding
	{
		LuaValue ToString(LuaRuntime runtime);
	}

	public interface ILuaLessThanBinding
	{
		LuaValue LessThan(LuaRuntime runtime, LuaValue left, LuaValue right);
	}

	public interface ILuaLessThanOrEqualToBinding
	{
		LuaValue LessThanOrEqualTo(LuaRuntime runtime, LuaValue left, LuaValue right);
	}
}
