using System;
using System.IO;
using System.Linq;
using PetDock.AtlasImporter;

if (args.Length == 0 || args.Contains("--help") || args.Contains("-h"))
{
    Console.Error.WriteLine("usage: PetDock.AtlasImporter --in <atlas-pet-dir> [--out <dir>]");
    return 1;
}

string? input = GetArg(args, "--in");
if (input is null || !Directory.Exists(input))
{
    Console.Error.WriteLine("atlas-import error=missing-or-not-found --in");
    return 1;
}

string source = input;
string output = GetArg(args, "--out") ?? Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "atlas-import");
var importer = new AtlasPackImporter();
if (!importer.CanImport(source))
{
    Console.Error.WriteLine("atlas-import error=not-an-atlas-pack");
    return 1;
}

try
{
    importer.Import(source, output, progress =>
        Console.Error.WriteLine($"atlas-progress {progress.Done}/{progress.Total}"), default);
    Console.Out.WriteLine($"imported out={output}");
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"atlas-import error={error.GetType().Name} message={error.Message}");
    return 1;
}

static string? GetArg(string[] args, string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
