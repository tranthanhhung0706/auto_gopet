// Sinh Il2CppAssemblies cho MelonLoader từ DummyDll của Il2CppDumper GUI,
// thay cho bước Cpp2IL (Cpp2IL chưa hỗ trợ metadata v108).
// Mô phỏng MelonLoader v0.7.3 Dependencies/Il2CppAssemblyGenerator/Packages/Il2CppInterop.cs
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Serialized;
using Il2CppInterop.Common;
using Il2CppInterop.Generator;
using Il2CppInterop.Generator.Runners;
using Microsoft.Extensions.Logging;

if (args.Length < 4)
{
    Console.WriteLine("usage: InteropGen <dummyDllDir> <unityDepsDir> <GameAssembly.dll> <outDir>");
    return 1;
}
string dummyDir = args[0], unityDir = args[1], gameAssembly = args[2], outDir = args[3];

var resolver = new Resolver();
var input = Directory.GetFiles(dummyDir, "*.dll")
    .Select(f => ModuleDefinition.FromFile(f, new ModuleReaderParameters { ModuleResolver = resolver }))
    .Select(m => { resolver.Add(m); return m; })
    .Select(m => m.Assembly)
    .ToList();
Console.WriteLine($"Loaded {input.Count} dummy assemblies");

// Il2CppDumper đôi khi để các kiểu lồng nhau (vd. <StandardName>e__FixedBuffer) ở top-level,
// gây trùng tên khi .NET load assembly đã sinh. Đổi tên các bản trùng.
foreach (var asm in input)
foreach (var module in asm.Modules)
{
    var seen = new HashSet<string>();
    foreach (var type in module.TopLevelTypes)
    {
        var full = type.FullName;
        if (seen.Add(full)) continue;
        int n = 1;
        while (!seen.Add($"{full}_dup{n}")) n++;
        Console.WriteLine($"Rename duplicate {asm.Name}: {full} -> {type.Name}_dup{n}");
        type.Name = $"{type.Name}_dup{n}";
    }
    foreach (var type in module.GetAllTypes())
    {
        var nested = new HashSet<string>();
        foreach (var t in type.NestedTypes)
        {
            if (nested.Add(t.Name)) continue;
            int n = 1;
            while (!nested.Add($"{t.Name}_dup{n}")) n++;
            Console.WriteLine($"Rename duplicate nested {type.FullName}/{t.Name} -> _dup{n}");
            t.Name = $"{t.Name}_dup{n}";
        }
    }
}

var trusted = (string)AppDomain.CurrentDomain.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
AppDomain.CurrentDomain.SetData("TRUSTED_PLATFORM_ASSEMBLIES",
    trusted + Path.PathSeparator + string.Join(Path.PathSeparator, Directory.GetFiles(unityDir, "*.dll")));

Directory.CreateDirectory(outDir);
var opts = new GeneratorOptions
{
    GameAssemblyPath = gameAssembly,
    Source = input,
    OutputDir = outDir,
    UnityBaseLibsDir = unityDir,
    Parallel = true,
    Il2CppPrefixMode = GeneratorOptions.PrefixMode.OptOut,
};

Il2CppInteropGenerator.Create(opts)
    .AddLogger(new ConsoleLogger())
    .AddInteropAssemblyGenerator()
    .Run();

// Hash giống MelonUtils.ComputeSimpleSHA512Hash (hex in hoa)
var hash = string.Concat(SHA512.HashData(File.ReadAllBytes(gameAssembly)).Select(b => b.ToString("X2")));
File.WriteAllText(Path.Combine(outDir, "..", "GameAssemblyHash.txt"), hash);
Console.WriteLine("Done. Hash: " + hash);
return 0;

class Resolver : INetModuleResolver
{
    private readonly Dictionary<string, ModuleDefinition> _cache = new();
    public void Add(ModuleDefinition m) => _cache[m.Name] = m;
    public ModuleDefinition Resolve(string name) => _cache.GetValueOrDefault(name);
}

class ConsoleLogger : ILogger
{
    public void Log<TState>(LogLevel l, EventId id, TState s, Exception e, Func<TState, Exception, string> f)
    {
        if (l >= LogLevel.Information) Console.WriteLine($"[{l}] {f(s, e)}");
    }
    public bool IsEnabled(LogLevel l) => l >= LogLevel.Information;
    public IDisposable BeginScope<TState>(TState s) => null;
}
