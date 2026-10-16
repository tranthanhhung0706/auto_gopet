// Disassembler nhỏ cho GameAssembly.dll, gắn tên hàm/chuỗi từ script.json của Il2CppDumper.
//   Disasm fn <rva|tên>       : in mã asm của một hàm
//   Disasm xref <rva|tên>     : liệt kê các hàm gọi tới hàm đó
//   Disasm find <chuỗi con>   : tìm hàm theo tên
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Iced.Intel;

const ulong ImageBase = 0x180000000;
string root = Environment.GetEnvironmentVariable("GOPET_ROOT") ?? @"D:\gopetUnity\goPetHD (1)";
string dll = Path.Combine(root, "goPetHD", "GameAssembly.dll");
string script = Path.Combine(root, "dump", "script.json");

var bytes = File.ReadAllBytes(dll);
var pe = new PEReader(new MemoryStream(bytes));
var sections = pe.PEHeaders.SectionHeaders.ToArray();

// Tên theo RVA
var names = new Dictionary<ulong, string>();
var methodStarts = new SortedSet<ulong>();
using (var doc = JsonDocument.Parse(File.ReadAllBytes(script)))
{
    var r = doc.RootElement;
    foreach (var m in r.GetProperty("ScriptMethod").EnumerateArray())
    {
        var a = m.GetProperty("Address").GetUInt64();
        names.TryAdd(a, m.GetProperty("Name").GetString());
        methodStarts.Add(a);
    }
    foreach (var s in r.GetProperty("ScriptString").EnumerateArray())
        names.TryAdd(s.GetProperty("Address").GetUInt64(), "\"" + s.GetProperty("Value").GetString().Replace("\n", "\\n") + "\"");
    foreach (var s in r.GetProperty("ScriptMetadata").EnumerateArray())
        names.TryAdd(s.GetProperty("Address").GetUInt64(), s.GetProperty("Name").GetString());
    foreach (var s in r.GetProperty("ScriptMetadataMethod").EnumerateArray())
        names.TryAdd(s.GetProperty("Address").GetUInt64(), s.GetProperty("Name").GetString());
    foreach (var a in r.GetProperty("Addresses").EnumerateArray())
        methodStarts.Add(a.GetUInt64());
}
var starts = methodStarts.ToArray();

int FileOff(ulong rva)
{
    foreach (var s in sections)
        if (rva >= (ulong)s.VirtualAddress && rva < (ulong)(s.VirtualAddress + Math.Max(s.VirtualSize, s.SizeOfRawData)))
            return (int)(rva - (ulong)s.VirtualAddress + (ulong)s.PointerToRawData);
    return -1;
}

ulong FuncEnd(ulong rva)
{
    int i = Array.BinarySearch(starts, rva);
    if (i < 0) i = ~i; else i++;
    return i < starts.Length ? Math.Min(starts[i], rva + 0x4000) : rva + 0x1000;
}

ulong Resolve(string arg)
{
    if (arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
    {
        var v = Convert.ToUInt64(arg[2..], 16);
        return v >= ImageBase ? v - ImageBase : v;
    }
    var hit = names.Where(kv => kv.Value == arg).Select(kv => kv.Key).FirstOrDefault();
    if (hit == 0) throw new Exception("Không tìm thấy: " + arg);
    return hit;
}

string Name(ulong rva) => names.TryGetValue(rva, out var n) ? n : null;

IEnumerable<Instruction> Decode(ulong rva, ulong end)
{
    int off = FileOff(rva);
    if (off < 0) yield break;
    var reader = new ByteArrayCodeReader(bytes, off, (int)(end - rva));
    var dec = Decoder.Create(64, reader, ImageBase + rva);
    while (dec.IP < ImageBase + end)
    {
        dec.Decode(out var ins);
        yield return ins;
    }
}

string Annotate(in Instruction ins)
{
    ulong target = 0;
    if (ins.IsIPRelativeMemoryOperand) target = ins.IPRelativeMemoryAddress;
    else if (ins.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch or FlowControl.ConditionalBranch
             && ins.Op0Kind is OpKind.NearBranch64) target = ins.NearBranch64;
    if (target == 0) return "";
    var n = Name(target - ImageBase);
    return n != null ? "   ; " + n : "";
}

if (args.Length < 2) { Console.WriteLine("Disasm fn|xref|find <arg>"); return; }
var fmt = new IntelFormatter();
fmt.Options.FirstOperandCharIndex = 8;
var outp = new StringOutput();

switch (args[0])
{
    case "fn":
        foreach (var a in args.Skip(1))
        {
            var rva = Resolve(a);
            var end = FuncEnd(rva);
            Console.WriteLine($"==== {Name(rva) ?? "?"}  RVA 0x{rva:X}  (len 0x{end - rva:X})");
            foreach (var ins in Decode(rva, end))
            {
                fmt.Format(ins, outp);
                Console.WriteLine($"{ins.IP - ImageBase:X8}  {outp.ToStringAndReset(),-60}{Annotate(ins)}");
                if (ins.Mnemonic == Mnemonic.Int3) break;
            }
        }
        break;
    case "xref":
    {
        var target = Resolve(args[1]) + ImageBase;
        for (int i = 0; i < starts.Length; i++)
        {
            ulong s = starts[i], e = i + 1 < starts.Length ? starts[i + 1] : s + 0x1000;
            if (e - s > 0x20000) continue;
            foreach (var ins in Decode(s, e))
                if ((ins.FlowControl is FlowControl.Call or FlowControl.UnconditionalBranch && ins.Op0Kind == OpKind.NearBranch64 && ins.NearBranch64 == target)
                    || (ins.IsIPRelativeMemoryOperand && ins.IPRelativeMemoryAddress == target))
                {
                    Console.WriteLine($"0x{s:X}  {Name(s) ?? "?"}  @0x{ins.IP - ImageBase:X}");
                    break;
                }
        }
        break;
    }
    case "find":
        foreach (var kv in names.Where(kv => kv.Value.Contains(args[1], StringComparison.OrdinalIgnoreCase)).Take(200))
            Console.WriteLine($"0x{kv.Key:X}  {kv.Value}");
        break;
}
