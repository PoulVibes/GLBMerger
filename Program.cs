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
        if (args.Length > 0 && args[0] == "--ground-fix")
            return GroundFix(args);
        if (args.Length > 0 && args[0] == "--trim-clip")
            return TrimClip(args);
        if (args.Length > 0 && args[0] == "--smooth-normals")
            return SmoothNormalsFiles(args);

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

    // Headless: GlbMerger --smooth-normals [--dry-run] <a.glb> [b.glb ...]
    // Rebuilds every vertex normal as smooth shading, in place, at the same crease angle the
    // merge applies. See ModelSmoother.SmoothNormals.
    private static int SmoothNormalsFiles(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        var paths = args.Skip(1).Where(a => a != "--dry-run").ToList();
        if (paths.Count == 0)
        {
            Console.Error.WriteLine("usage: GlbMerger --smooth-normals [--dry-run] <a.glb> [b.glb ...]");
            return 2;
        }

        try
        {
            foreach (var path in paths)
            {
                var model = ModelRoot.Load(path);
                int changed = ModelSmoother.SmoothNormals(model, ModelSmoother.DefaultCreaseAngle);
                Console.WriteLine($"{path}: {changed} normal(s) {(dryRun ? "would change" : "changed")}");
                if (!dryRun && changed > 0) SaveReplacing(model, path);
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

    // Headless: GlbMerger --ground-fix [--dry-run] <Clip1,Clip2,...> <a.glb> [b.glb ...]
    // Raises each named clip's Hips wherever its skinned mesh dips under the floor, in place -
    // the Animation Trim editor's Keep Above Ground, for a batch of player models. See
    // AnimationGroundFix. Inches assume the game's 6 ft player.
    private static int GroundFix(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        var rest = args.Skip(1).Where(a => a != "--dry-run").ToList();
        if (rest.Count < 2)
        {
            Console.Error.WriteLine("usage: GlbMerger --ground-fix [--dry-run] <Clip1,Clip2,...> <a.glb> [b.glb ...]");
            return 2;
        }
        var clips = rest[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            foreach (var path in rest.Skip(1))
            {
                var model = ModelRoot.Load(path);
                bool changed = false;
                Console.WriteLine(path);
                foreach (var clip in clips)
                {
                    if (!model.LogicalAnimations.Any(a => a.Name == clip)) { Console.WriteLine($"  {clip,-14} not in this file"); continue; }
                    var r = dryRun ? AnimationGroundFix.Measure(model, clip) : AnimationGroundFix.Apply(model, clip);
                    string In(float units) => AnimationGroundFix.Result.Inches(units, r.Height).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                    Console.WriteLine($"  {clip,-14} under {In(r.DeepestBefore),5} in at {r.DeepestAt:0.00}s (floor: {r.FloorFrom}), "
                        + $"{r.FramesRaised}/{r.Keys} frames raised, up to {In(r.LargestRaise)} in, {In(r.EndRaise)} on the last"
                        + (dryRun ? " (dry run)" : $", now under {In(r.DeepestAfter)} in"));
                    changed |= !dryRun && r.LargestRaise > 0f;
                }
                if (changed) SaveReplacing(model, path);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    // Headless: GlbMerger --trim-clip [--dry-run] <Clip> <firstFrame> <lastFrame> <a.glb> [b.glb ...]
    // Crops one clip to [first, last] in place - the Animation Trim editor's Save Animation
    // Adjustments without the loop/smoothing options, for a batch of player models. Frames are that
    // editor's numbering (every distinct keyframe time across the clip's channels). A loop setting
    // already on the clip is kept, its loop-back time clamped into the shorter clip.
    private static int TrimClip(string[] args)
    {
        bool dryRun = args.Contains("--dry-run");
        var rest = args.Skip(1).Where(a => a != "--dry-run").ToList();
        if (rest.Count < 4 || !int.TryParse(rest[1], out int first) || !int.TryParse(rest[2], out int last) || last <= first)
        {
            Console.Error.WriteLine("usage: GlbMerger --trim-clip [--dry-run] <Clip> <firstFrame> <lastFrame> <a.glb> [b.glb ...]");
            return 2;
        }
        string clip = rest[0];

        try
        {
            foreach (var path in rest.Skip(3))
            {
                var model = ModelRoot.Load(path);
                var anim = model.LogicalAnimations.FirstOrDefault(a => a.Name == clip);
                if (anim == null) { Console.WriteLine($"{path}: {clip} not in this file"); continue; }

                var times = AnimationGroundFix.FrameTimes(anim);
                if (last >= times.Length) { Console.WriteLine($"{path}: {clip} has only {times.Length} frames - skipped"); continue; }
                Console.WriteLine($"{path}: {clip} frames {first}-{last} of {times.Length - 1} "
                    + $"({times[first]:0.000}s-{times[last]:0.000}s, was {times[^1]:0.000}s){(dryRun ? " (dry run)" : "")}");
                if (dryRun) continue;

                bool hadLoop = GlbMergeService.HasAnimationLoopExtras(model, clip);
                var (loop, loopTime) = GlbMergeService.GetAnimationLoop(model, clip);
                GlbMergeService.TrimAnimation(model, clip, first, last);
                if (hadLoop)
                    GlbMergeService.SetAnimationLoop(model, clip, loop, Math.Clamp(loopTime - times[first], 0f, times[last] - times[first]));
                SaveReplacing(model, path);
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
