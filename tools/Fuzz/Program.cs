using Fuzz;
using SharpFuzz;

// Fuzzes ClassicMac's readers with libFuzzer through SharpFuzz (PLAN "Fuzzing"; the build workflow's fuzz job).
// Usage:
//   Fuzz <target>                        one target, under libfuzzer-dotnet with the ClassicMac assemblies instrumented
//   Fuzz seeds <output> [repository]     writes the seed corpora, a folder per target
//   Fuzz replay <target> <file|folder>…  runs inputs without libFuzzer, as a crash is reproduced
//   Fuzz trace <target> <file|folder>…   replays, printing each step of a writer target (hfs-edit's edits)
// Targets: container, resource-fork, resource, code, pef, pict, tiff, wav, first-aid; the writers' ndif-write,
// hfs-edit, first-aid-repair, volume-session, wrappers, pict-write (FuzzTargets, WriterTargets, SessionTargets).
switch (args)
{
    case ["seeds", var output, ..]:
        {
            var repository = args.Length > 2 ? args[2] : Repository();
            foreach (var (target, count) in FuzzSeeds.Write(output, repository))
            {
                Console.WriteLine($"{target}: {count}");
            }

            return 0;
        }
    case ["replay", var name, .. var paths] when FuzzTargets.All.TryGetValue(name, out var target) && paths.Length > 0:
        return FuzzTargets.Replay(target, paths, Console.Out);
    case ["trace", var name, .. var paths] when FuzzTargets.All.TryGetValue(name, out var target) && paths.Length > 0:
        WriterTargets.Trace = Console.WriteLine;
        return FuzzTargets.Replay(target, paths, Console.Out);
    case [var name] when FuzzTargets.All.TryGetValue(name, out var target):
        Fuzzer.LibFuzzer.Run(span => FuzzTargets.Run(target, span.ToArray()));
        return 0;
    default:
        Console.Error.WriteLine("Usage: Fuzz <target> | seeds <output> [repository] | replay|trace <target> <file|folder>...");
        Console.Error.WriteLine("Targets: " + string.Join(", ", FuzzTargets.All.Keys));
        return 2;
}

// The repository: the folder above this program's that holds ClassicMac.slnx.
static string Repository()
{
    for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
    {
        if (File.Exists(Path.Combine(folder.FullName, "ClassicMac.slnx")))
        {
            return folder.FullName;
        }
    }

    throw new DirectoryNotFoundException("ClassicMac.slnx not found above " + AppContext.BaseDirectory);
}
