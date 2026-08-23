using System;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;

var path = "/home/caf/.nuget/packages/docker.dotnet/3.125.15/lib/netstandard2.1/Docker.DotNet.dll";
using var fs = File.OpenRead(path);
using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
var md = pe.GetMetadataReader();
foreach (var h in md.TypeDefinitions)
{
    var td = md.GetTypeDefinition(h);
    var name = md.GetString(td.Name);
    var ns = md.GetString(td.Namespace);
    if (ns != "Docker.DotNet.Models") continue;
    var props = td.GetMethods()
        .Select(mh => md.GetMethodDefinition(mh))
        .Select(m => md.GetString(m.Name))
        .Where(mn => mn.StartsWith("get_"))
        .Select(mn => mn[4..])
        .ToList();
    if (props.Contains("ID") || props.Contains("StartedAt") || props.Contains("Created"))
    {
        Console.WriteLine($"{ns}.{name}: [{string.Join(", ", props.OrderBy(x => x, StringComparer.Ordinal))}]");
    }
}
