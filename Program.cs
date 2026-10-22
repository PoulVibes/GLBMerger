using System;
using System.Linq;
using System.Windows.Forms;
using SharpGLTF.Schema2;

namespace GlbMerger;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--restore-clips")
            return RestoreClips(args);
        if (args.Length > 0 && args[0] == "--sync-scale-keys")
            return SyncScaleKeys(args);
        if (args.Length > 0 && args[0] == "--prune")
            return PruneFiles(args);

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
    }

    // Headless: GlbMerger --sync-scale-keys [--dry-run] <a.glb> [b.glb ...]
    // Sets every constant animation scale track that disagrees with its bone's rest scale (the
    // Model Adjuster's branch Size) to that rest scale, in place. See ScaleKeySync.Sync.
    private static int SyncScaleKeys(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        var paths = args.Skip(1).Where(a => a != "--dry-run").ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("usage: GlbMerger --sync-scale-keys [--dry-run] <a.glb> [b.glb ...]");
            return 2;
        }

        try
        {
            foreach (var path in paths)
            {
                var model = ModelRoot.Load(path);
                var skipped = new System.Collections.Generic.List<string>();
                var fixes = ScaleKeySync.Sync(model, skipped);
                Console.WriteLine($"{path}: {fixes.Count} track(s) {(dryRun ? "would change" : "changed")}");
                foreach (var f in fixes)
                    Console.WriteLine($"  {f.Clip,-10} {f.Node,-16} {f.From.X:F3} -> {f.To.X:F3}");
                foreach (var s in skipped)
                    Console.WriteLine($"  skipped (animated scale): {s}");
                if (!dryRun && fixes.Count > 0) SaveReplacing(model, path);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // Headless: GlbMerger --prune [--dry-run] <a.glb> [b.glb ...]
    // Drops animation samplers, accessors and buffer views nothing reads, in place. See GlbPrune.
    private static int PruneFiles(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        var paths = args.Skip(1).Where(a => a != "--dry-run").ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("usage: GlbMerger --prune [--dry-run] <a.glb> [b.glb ...]");
            return 2;
        }

        try
        {
            foreach (var path in paths)
            {
                var (bytes, s) = GlbPrune.Prune(System.IO.File.ReadAllBytes(path));
                Console.WriteLine($"{path}: {s.Samplers} sampler(s), {s.Accessors} accessor(s), "
                    + $"{s.BufferViews} buffer view(s), {s.Bytes / 1024.0:F1} KB {(dryRun ? "unused" : "removed")}");
                if (!dryRun && s.Any) WriteReplacing(bytes, path);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    private static void WriteReplacing(byte[] bytes, string path)
    {
        var temp = path + ".tmp.glb";
        System.IO.File.WriteAllBytes(temp, bytes);
        MoveReplacing(temp, path);
    }

    // Writes beside the file, then swaps it in: a scanner or indexer briefly mapping the original
    // made a direct overwrite fail ("user-mapped section open") on about one file in three.
    private static void SaveReplacing(ModelRoot model, string path)
    {
        var temp = path + ".tmp.glb";
        GlbPrune.Save(model, temp);
        MoveReplacing(temp, path);
    }

    private static void MoveReplacing(string temp, string path)
    {
        for (int attempt = 0; ; ++attempt)
        {
            try { System.IO.File.Move(temp, path, overwrite: true); return; }
            catch (System.IO.IOException) when (attempt < 20) { System.Threading.Thread.Sleep(250); }
        }
    }

    // Headless: GlbMerger --restore-clips <target.glb> <source.glb> <Clip1,Clip2,...> [out.glb]
    // Overwrites target in place when no out path is given. See AnimationRestore.
    private static int RestoreClips(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: GlbMerger --restore-clips <target.glb> <source.glb> <Clip1,Clip2,...> [out.glb]");
            return 2;
        }

        try
        {
            var target = ModelRoot.Load(args[1]);
            var source = ModelRoot.Load(args[2]);
            var clips = args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var r in AnimationRestore.Restore(target, source, clips))
            {
                Console.WriteLine($"{r.Clip}: {r.OldDuration:F3}s -> {r.NewDuration:F3}s ({r.Channels} channels)");
                if (r.Unmatched.Count > 0)
                    Console.WriteLine($"  unmatched source nodes: {string.Join(", ", r.Unmatched.Distinct())}");
            }

            GlbPrune.Save(target, args.Length > 4 ? args[4] : args[1]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
