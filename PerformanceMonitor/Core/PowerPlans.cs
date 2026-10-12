using System.Runtime.InteropServices;
using System.Text;

namespace PerformanceMonitor.Core;

public sealed record PowerPlan(Guid Id, string Name, bool IsActive)
{
    public override string ToString() => Name;
}

public static class PowerPlans
{
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    private const uint ACCESS_SCHEME = 16;

    public static Guid? GetActive()
    {
        if (Native.PowerGetActiveScheme(IntPtr.Zero, out var ptr) != 0) return null;
        try { return Marshal.PtrToStructure<Guid>(ptr); }
        finally { Native.LocalFree(ptr); }
    }

    public static List<PowerPlan> GetAll()
    {
        var active = GetActive();
        var list = new List<PowerPlan>();
        for (uint i = 0; ; i++)
        {
            uint size = 16;
            var buffer = new byte[16];
            if (Native.PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, i, buffer, ref size) != 0) break;
            var id = new Guid(buffer);
            list.Add(new PowerPlan(id, ReadName(id), id == active));
        }
        return list;
    }

    private static string ReadName(Guid id)
    {
        uint size = 0;
        Native.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0) return id.ToString();
        var buffer = new byte[size];
        if (Native.PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return id.ToString();
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    public static bool SetActive(Guid id) => Native.PowerSetActiveScheme(IntPtr.Zero, ref id) == 0;

    /// <summary>Bester verfügbarer Leistungsplan (Ultimative Leistung &gt; Höchstleistung).</summary>
    public static PowerPlan? GetBestPerformancePlan()
    {
        var all = GetAll();
        return all.FirstOrDefault(p => p.Id == UltimatePerformance) ?? all.FirstOrDefault(p => p.Id == HighPerformance);
    }
}
