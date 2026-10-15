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

        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
        return 0;
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

            target.SaveGLB(args.Length > 4 ? args[4] : args[1]);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }
}
