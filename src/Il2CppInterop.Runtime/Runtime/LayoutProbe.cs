using System;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Runtime;

/// <summary>
/// Some games ship an il2cpp runtime that is patched relative to stock Unity while still reporting
/// a stock Unity version. Il2CppInterop picks its struct handlers from that reported version, so a
/// fork silently gets the wrong layout and every field past the insertion point reads garbage.
///
/// This probe measures the real layout of the running module instead of trusting the version
/// string: it asks the engine for a class/method through the il2cpp exports (which always use the
/// true offsets) and compares the answer with what the stock struct offsets yield.
///
/// Layouts confirmed against Endfield (明日方舟：终末地) GameAssembly.dll, build 2025-11-25:
///   Il2CppClass      - two extra bytes after `token`      (method_count 0x11C -> 0x11E)
///   Il2CppMethodInfo - one extra byte after `slot`        (parameters_count 0x52 -> 0x53)
/// </summary>
internal static class Il2CppLayoutProbe
{
    private const int StockClassMethodCountOffset = 0x11C;
    private const int StockMethodInfoParametersCountOffset = 0x52;

    private static bool s_ran;

    /// <summary>Extra bytes between `token` and `method_count` in <c>Il2CppClass</c>.</summary>
    public static int ClassStructPadding { get; private set; }

    /// <summary>Extra bytes between `slot` and `parameters_count` in <c>Il2CppMethodInfo</c>.</summary>
    public static int MethodInfoStructPadding { get; private set; }

    public static void Run()
    {
        if (s_ran) return;
        s_ran = true;

        try
        {
            var klass = FindProbeClass();
            if (klass == IntPtr.Zero)
            {
                Logger.Instance.LogWarning("il2cpp layout probe: no probe class found, assuming stock layout");
                return;
            }

            ProbeClassLayout(klass);
            ProbeMethodInfoLayout(klass);

            Logger.Instance.LogInformation("il2cpp layout probe: Il2CppClass +{ClassPad} byte(s), Il2CppMethodInfo +{MethodPad} byte(s)",
                ClassStructPadding, MethodInfoStructPadding);
        }
        catch (Exception ex)
        {
            Logger.Instance.LogWarning("il2cpp layout probe failed, assuming stock layout: {Message}", ex.Message);
        }
    }

    private static IntPtr FindProbeClass()
    {
        var corlib = IL2CPP.il2cpp_get_corlib();
        if (corlib == IntPtr.Zero) return IntPtr.Zero;

        string[] candidates = { "String", "Object", "Exception", "Int32" };
        foreach (var name in candidates)
        {
            var klass = IL2CPP.il2cpp_class_from_name(corlib, "System", name);
            if (klass != IntPtr.Zero) return klass;
        }

        return IntPtr.Zero;
    }

    private static void ProbeClassLayout(IntPtr klass)
    {
        var counted = CountMethods(klass);
        if (counted <= 0) return;

        var atStock = ReadUInt16(klass + StockClassMethodCountOffset);
        var atShifted = ReadUInt16(klass + StockClassMethodCountOffset + 2);

        if (atStock == counted) ClassStructPadding = 0;
        else if (atShifted == counted) ClassStructPadding = 2;
        else
            Logger.Instance.LogWarning(
                "il2cpp layout probe: neither 0x{Stock:X} nor 0x{Shifted:X} matches the engine's method count {Count}",
                atStock, atShifted, counted);
    }

    private static void ProbeMethodInfoLayout(IntPtr klass)
    {
        int stockVotes = 0, shiftedVotes = 0;
        var iter = IntPtr.Zero;

        for (var i = 0; i < 400; i++)
        {
            var method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
            if (method == IntPtr.Zero) break;

            var paramCount = (int)IL2CPP.il2cpp_method_get_param_count(method);
            if (paramCount == 0 || paramCount > 32) continue; // 0 is ambiguous often enough to skip

            if (ReadByte(method + StockMethodInfoParametersCountOffset) == paramCount) stockVotes++;
            if (ReadByte(method + StockMethodInfoParametersCountOffset + 1) == paramCount) shiftedVotes++;
        }

        if (stockVotes == 0 && shiftedVotes == 0) return;

        MethodInfoStructPadding = shiftedVotes > stockVotes ? 1 : 0;

        if (shiftedVotes == stockVotes)
            Logger.Instance.LogWarning(
                "il2cpp layout probe: ambiguous Il2CppMethodInfo layout (stock={Stock} shifted={Shifted})",
                stockVotes, shiftedVotes);
    }

    private static int CountMethods(IntPtr klass)
    {
        var count = 0;
        var iter = IntPtr.Zero;
        while (true)
        {
            var method = IL2CPP.il2cpp_class_get_methods(klass, ref iter);
            if (method == IntPtr.Zero) break;
            if (++count > 10000) break;
        }

        return count;
    }

    private static ushort ReadUInt16(IntPtr address) => unchecked((ushort)Marshal.ReadInt16(address));

    private static byte ReadByte(IntPtr address) => Marshal.ReadByte(address);
}
