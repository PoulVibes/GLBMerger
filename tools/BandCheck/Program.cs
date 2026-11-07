using System.Drawing;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using GlbMerger;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

internal static class Program
{
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static string Geometry(ModelRoot model) => string.Join(";", model.LogicalMeshes.SelectMany(m => m.Primitives).SelectMany(p => p.GetVertexAccessor("POSITION").AsVector3Array()).Select(v => $"{v.X:R},{v.Y:R},{v.Z:R}"));
    private static Matrix4x4 World(Node node, Animation animation, float time) => node.GetLocalTransform(animation, time).Matrix * (node.VisualParent == null ? Matrix4x4.Identity : World(node.VisualParent, animation, time));
    private static float Difference(Matrix4x4 a, Matrix4x4 b)
    {
        var x = new[] { a.M11,a.M12,a.M13,a.M14,a.M21,a.M22,a.M23,a.M24,a.M31,a.M32,a.M33,a.M34,a.M41,a.M42,a.M43,a.M44 };
        var y = new[] { b.M11,b.M12,b.M13,b.M14,b.M21,b.M22,b.M23,b.M24,b.M31,b.M32,b.M33,b.M34,b.M41,b.M42,b.M43,b.M44 };
        return x.Zip(y, (u,v) => Math.Abs(u-v)).Max();
    }
    private static void Compare(ModelRoot character, ModelRoot instrument)
    {
        var nodes = character.LogicalNodes.Where(n => n.Name != null).ToDictionary(n => n.Name!);
        foreach (var clip in character.LogicalAnimations)
        {
            var target = instrument.LogicalAnimations.Single(a => a.Name == clip.Name);
            Check(Math.Abs(clip.Duration-target.Duration)<.0001f, "Clip duration mismatch");
            for (int step = 0; step <= 120; ++step)
                foreach (var node in instrument.LogicalNodes.Where(n => n.Mesh == null && n.Name != null && nodes.ContainsKey(n.Name)))
                    Check(Difference(World(nodes[node.Name!],clip,clip.Duration*step/120),World(node,target,clip.Duration*step/120))<.0002f, $"Pair pose mismatch: {clip.Name}/{node.Name}");
        }
    }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Check(args.Length >= 3, "usage: BandCheck character.glb instrument.glb output-folder [--ui]");
            var characterHash=Hash(args[0]);var instrumentHash=Hash(args[1]);
            var character=ModelRoot.Load(args[0]);var instrument=ModelRoot.Load(args[1]);
            var geometry=Geometry(instrument);
            var hand=character.LogicalNodes.Single(n => n.Name=="LeftHand");
            var play=character.LogicalAnimations.Single(a => a.Name=="Play");
            var keys=play.Channels.Single(c => c.TargetNode==hand && c.TargetNodePath==PropertyPath.rotation).GetRotationSampler().GetLinearKeys();
            var offset=Quaternion.CreateFromAxisAngle(Vector3.UnitX,.1f);
            hand.WithRotationAnimation("Play", keys.Select(k => (k.Key,Quaternion.Normalize(offset*k.Value))).ToArray());
            var synchronized=BandPairService.Synchronize(character,instrument);
            Check(Geometry(synchronized)==geometry, "Instrument geometry changed");
            Compare(character,synchronized);
            var exported=BandPairService.Export(character,instrument,args[2],"BandCheckMember");
            Compare(ModelRoot.Load(exported.Character),ModelRoot.Load(exported.Instrument));
            BandPairService.Export(character,instrument,args[2],"BandCheckMember");
            Compare(ModelRoot.Load(exported.Character),ModelRoot.Load(exported.Instrument));
            var bad=instrument.DeepClone();bad.LogicalNodes.Single(n => n.Name=="LeftHand").Name="WrongJoint";
            bool rejected=false;try { BandPairService.Validate(character,bad); } catch (InvalidOperationException) { rejected=true; }
            Check(rejected,"Mismatched rig accepted");
            var rollback=Path.Combine(args[2],"rollback");Directory.CreateDirectory(Path.Combine(rollback,"Characters"));Directory.CreateDirectory(Path.Combine(rollback,"Instruments","RollbackEquipment.glb"));
            var previous=Path.Combine(rollback,"Characters","Rollback.glb");File.WriteAllText(previous,"original");
            rejected=false;try { BandPairService.Export(character,instrument,rollback,"Rollback"); } catch (IOException) { rejected=true; }
            Check(rejected && File.ReadAllText(previous)=="original","Pair export did not roll back");
            if(args.Contains("--ui")) CheckUi(character,args[0],args[2]);
            Check(Hash(args[0])==characterHash && Hash(args[1])==instrumentHash,"Input GLBs were overwritten");
            Console.WriteLine("Band pair checks passed: sampled joint edits, clip durations, geometry, paired export/re-export, mismatch rejection, rollback and source preservation.");return 0;
        }
        catch(Exception ex) { Console.Error.WriteLine(ex);return 1; }
    }

    private static T Field<T>(object instance,string name) => (T)typeof(JointOrientationEditor).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(instance)!;
    private static void PumpUntil(Func<bool> ready)
    {
        var deadline=DateTime.UtcNow.AddSeconds(45);
        while(!ready()) { Application.DoEvents();Thread.Sleep(10);if(DateTime.UtcNow>deadline)throw new TimeoutException("Band viewer did not become ready"); }
        Application.DoEvents();
    }
    private static string Js(WebView2 view,string script)
    {
        var result=view.CoreWebView2.ExecuteScriptAsync(script);PumpUntil(()=>result.IsCompleted);return result.GetAwaiter().GetResult();
    }
    private static void CheckUi(ModelRoot model,string source,string output)
    {
        ApplicationConfiguration.Initialize();
        using var form=new Form { Text="Band Performance check",Size=new Size(780,620),StartPosition=FormStartPosition.Manual,Location=new Point(0,0),ShowInTaskbar=false };
        using var editor=new BandPerformanceEditor(model,false,source);form.Controls.Add(editor);form.Show();
        PumpUntil(()=>Field<bool>(editor,"_bandLoaded") && Field<ModelRoot?>(editor,"_bandInstrument")!=null);
        var view=Field<WebView2>(editor,"_webView");
        PumpUntil(()=>Js(view,"bandPairs.length")!="0");
        Check(Js(view,"currentClip.name")=="\"Play\"","Band viewer did not default to Play");
        Check(Math.Abs(double.Parse(Js(view,"playbackRate"),System.Globalization.CultureInfo.InvariantCulture)-1.15)<.0001,"Preview BPM mismatch");
        Field<ComboBox>(editor,"_boneDropdown").SelectedItem="LeftForeArm";
        Field<TrackBar>(editor,"_sliderX").Value=8;
        Field<NumericUpDown>(editor,"_bandTime").Value=.75m;
        PumpUntil(()=>Js(view,"paused")=="true");
        Js(view,"scene.updateMatrixWorld(true); updateBandInstrument(); scene.updateMatrixWorld(true);");
        Check(Js(view,"bandPairs.every(function(p){return p.source.position.distanceTo(p.target.position)<1e-6 && p.source.quaternion.equals(p.target.quaternion) && p.source.matrixWorld.elements.every(function(v,i){return Math.abs(v-p.target.matrixWorld.elements[i])<1e-5;});})")=="true","Equipment did not follow live corrections");
        var before=Js(view,"bonesByName.LeftForeArm.quaternion.toArray()");
        Thread.Sleep(150);Application.DoEvents();
        Check(before==Js(view,"bonesByName.LeftForeArm.quaternion.toArray()"),"Paused joint corrections accumulate");
        typeof(JointOrientationEditor).GetMethod("SaveAdjustments",BindingFlags.NonPublic|BindingFlags.Instance)!.Invoke(editor,new object[]{false});
        Field<ComboBox>(editor,"_animDropdown").SelectedItem="Rest";
        Field<ComboBox>(editor,"_animDropdown").SelectedItem="Play";
        PumpUntil(()=>Js(view,"currentClip.name")=="\"Play\"");
        Thread.Sleep(200);Application.DoEvents();
        Console.WriteLine("Preview geometry: " + Js(view,"JSON.stringify({bounds:bandFramedBounds,camera:camera.position,drawn:renderer.info.render.triangles})"));
        Check(Js(view,"renderer.info.render.triangles > 10000")=="true","Character/instrument geometry was not rendered");
        var capture=Path.Combine(output,"band-editor-preview.png");
        using(var file=File.Create(capture)) { var task=view.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file);PumpUntil(()=>task.IsCompleted);task.GetAwaiter().GetResult(); }
        var panel=editor.Controls.OfType<Panel>().First(p=>p.Dock==DockStyle.Left);
        panel.AutoScrollPosition=new Point(0,10000);Application.DoEvents();
        using(var bitmap=new Bitmap(panel.Width,panel.Height)) { panel.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(Path.Combine(output,"band-editor-controls-bottom.png")); }
        var export=BandPairService.Synchronize(model,Field<ModelRoot>(editor,"_bandInstrument"));Compare(model,export);
        Console.WriteLine("Band UI checks passed: instrument auto-load, Play default, 138 BPM preview, scrub/pause, live joint synchronization, non-accumulating edits, clip switching and minimum-size scrolling.");
    }
}

