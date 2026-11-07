using System.Drawing;
using System.Globalization;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using SharpGLTF.Schema2;

namespace GlbMerger;

public sealed class BandPerformanceEditor : JointOrientationEditor
{
    public BandPerformanceEditor(ModelRoot model, bool darkMode = false, string? sourcePath = null)
        : base(model, darkMode, bandMode: true, sourcePath: sourcePath) { }
}

public partial class JointOrientationEditor
{
    private readonly bool _bandMode;
    private readonly string? _bandSourcePath;
    private ModelRoot? _bandInstrument;
    private string? _bandInstrumentPath;
    private TextBox _bandInstrumentName = null!;
    private NumericUpDown _bandSourceBpm = null!, _bandPreviewBpm = null!, _bandTime = null!;
    private TrackBar _bandScrub = null!;
    private CheckBox _bandShowInstrument = null!;
    private Label _bandBeat = null!;
    private bool _bandLoaded, _suppressBandSeek;
    private int _bandLoadVersion;
    private string? _bandPreviewFolder;
    private readonly List<string> _bandTempFiles = new();

    private void BuildBandControls(Panel panel, ref int top)
    {
        var y = top;
        void AddLabel(string text)
        {
            var label = new Label { Text = text, Left = 12, Top = y, AutoSize = true, MaximumSize = new Size(325, 0) };
            panel.Controls.Add(label); y += label.Height + 5;
        }
        Button AddButton(string text, EventHandler action)
        {
            var button = new Button { Text = text, Left = 12, Top = y, Width = 320, Height = 32, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new Size(320, 32) };
            button.Click += action; panel.Controls.Add(button); y += button.Height + 6; return button;
        }
        AddLabel("Band Performance — character + instrument");
        AddLabel("Load matching rigged equipment. Shared joints and sockets follow your live corrections.");
        _bandInstrumentName = new TextBox { Left = 12, Top = y, Width = 320, ReadOnly = true, Text = "No instrument loaded" };
        panel.Controls.Add(_bandInstrumentName); y += _bandInstrumentName.Height + 6;
        AddButton("Load Instrument…", async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "glTF binary (*.glb)|*.glb", Title = "Load this performer's rigged instrument" };
            if (_bandSourcePath != null) dialog.InitialDirectory = Path.GetDirectoryName(_bandSourcePath);
            if (dialog.ShowDialog(this) == DialogResult.OK) await LoadBandInstrumentAsync(dialog.FileName, true);
        });
        _bandShowInstrument = new CheckBox { Left = 12, Top = y, AutoSize = true, Text = "Show instrument", Checked = true };
        _bandShowInstrument.CheckedChanged += (_, _) => { if (_viewerReady) _webView.CoreWebView2.ExecuteScriptAsync($"setBandInstrumentVisible({(_bandShowInstrument.Checked ? "true" : "false")});"); };
        panel.Controls.Add(_bandShowInstrument); y += _bandShowInstrument.Height + 6;
        AddLabel("Source BPM                       Preview BPM");
        _bandSourceBpm = new NumericUpDown { Left = 12, Top = y, Width = 110, Minimum = 20, Maximum = 300, Value = 120 };
        _bandPreviewBpm = new NumericUpDown { Left = 180, Top = y, Width = 110, Minimum = 20, Maximum = 300, Value = 138 };
        _bandSourceBpm.ValueChanged += (_, _) => PushBandTempo();
        _bandPreviewBpm.ValueChanged += (_, _) => PushBandTempo();
        panel.Controls.Add(_bandSourceBpm); panel.Controls.Add(_bandPreviewBpm); y += _bandSourceBpm.Height + 6;
        AddLabel("Clip time (seconds) — scrub to pause and edit");
        _bandScrub = new TrackBar { Left = 12, Top = y, Width = 320, Height = 35, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None };
        _bandScrub.ValueChanged += (_, _) => { if (!_suppressBandSeek) SeekBandTime((double)_bandScrub.Value / 1000 * CurrentBandDuration()); };
        panel.Controls.Add(_bandScrub); y += _bandScrub.Height + 2;
        _bandTime = new NumericUpDown { Left = 12, Top = y, Width = 110, DecimalPlaces = 3, Increment = .033m, Minimum = 0, Maximum = 600 };
        _bandTime.ValueChanged += (_, _) => { if (!_suppressBandSeek) SeekBandTime((double)_bandTime.Value); };
        _bandBeat = new Label { Left = 135, Top = y + 4, AutoSize = true, Text = "Beat 1.00" };
        panel.Controls.Add(_bandTime); panel.Controls.Add(_bandBeat); y += _bandTime.Height + 8;
        AddButton("Export Character + Instrument…", (_, _) => ExportBandPair());
        AddLabel("Export pair saves both GLBs. Main Save saves only the character. Rotation/position controls are below.");
        y += 8;
        top = y;
    }

    private double CurrentBandDuration() => _animDropdown.SelectedIndex > 0
        ? _model.LogicalAnimations.FirstOrDefault(a => a.Name == (string)_animDropdown.SelectedItem!)?.Duration ?? 0 : 0;

    private void PushBandTempo()
    {
        if (_bandMode && _viewerReady)
            _webView.CoreWebView2.ExecuteScriptAsync($"setBandPlaybackRate({((double)_bandPreviewBpm.Value / (double)_bandSourceBpm.Value).ToString(CultureInfo.InvariantCulture)});");
    }

    private void SeekBandTime(double seconds)
    {
        if (!_bandLoaded || !_viewerReady) return;
        seconds = Math.Clamp(seconds, 0, CurrentBandDuration());
        _paused = true; _btnPause.Text = "Resume";
        _webView.CoreWebView2.ExecuteScriptAsync($"seekBandTime({seconds.ToString(CultureInfo.InvariantCulture)});");
        UpdateBandTime(seconds);
    }

    private void UpdateBandTime(double seconds)
    {
        _suppressBandSeek = true;
        try
        {
            if (!_bandTime.Focused) _bandTime.Value = Math.Clamp((decimal)seconds, _bandTime.Minimum, _bandTime.Maximum);
            if (!_bandScrub.Capture) _bandScrub.Value = (int)Math.Clamp(seconds / Math.Max(.001, CurrentBandDuration()) * 1000, 0, 1000);
            _bandBeat.Text = $"Beat {1 + seconds * (double)_bandSourceBpm.Value / 60:0.00}";
        }
        finally { _suppressBandSeek = false; }
    }

    private async Task LoadBandInstrumentAsync(string path, bool showError)
    {
        var version = ++_bandLoadVersion;
        try
        {
            var instrument = await Task.Run(() => ModelRoot.Load(path));
            if (IsDisposed || version != _bandLoadVersion) return;
            BandPairService.Validate(_model, instrument);
            _bandInstrument = instrument; _bandInstrumentPath = path;
            _bandInstrumentName.Text = Path.GetFileName(path);
            await PushBandInstrumentAsync();
        }
        catch (Exception ex)
        {
            if (IsDisposed) return;
            _lblStatus.Text = "Instrument: " + ex.Message;
            if (showError) MessageBox.Show(this, ex.Message, "Band Performance", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void TryAutoLoadBandInstrument()
    {
        if (!_bandMode || _bandSourcePath == null) return;
        var directory = Path.GetDirectoryName(_bandSourcePath)!;
        var name = Path.GetFileNameWithoutExtension(_bandSourcePath);
        var candidates = new List<string> {
            Path.Combine(directory, name + "Equipment.glb"),
            Path.Combine(directory, "..", "Instruments", name + "Equipment.glb")
        };
        if (name.Equals("BaseDrummer", StringComparison.OrdinalIgnoreCase))
            candidates.Add(Path.Combine(directory, "..", "Instruments", "BassDrummerEquipment.glb"));
        var found = candidates.FirstOrDefault(File.Exists);
        if (found != null) _ = LoadBandInstrumentAsync(Path.GetFullPath(found), false);
    }

    private async Task PushBandInstrumentAsync()
    {
        if (!_bandLoaded || !_viewerReady || _bandInstrument == null || _bandPreviewFolder == null || IsDisposed) return;
        var path = Path.Combine(_bandPreviewFolder, "instrument-" + Guid.NewGuid().ToString("N") + ".glb");
        _bandInstrument.SaveGLB(path); _bandTempFiles.Add(path);
        var url = "https://appassets.local/" + Path.GetFileName(path);
        await _webView.CoreWebView2.ExecuteScriptAsync($"loadBandInstrument({JsonSerializer.Serialize(url)});");
    }

    private bool HandleBandMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!_bandMode || IsDisposed) return false;
        try
        {
            using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = message.RootElement;
            if (!root.TryGetProperty("action", out var action)) return false;
            switch (action.GetString())
            {
                case "bandReady":
                    _bandLoaded = true;
                    OnAnimationSelected(); PushBandTempo(); _ = PushBandInstrumentAsync(); return true;
                case "bandTime":
                    UpdateBandTime(root.GetProperty("time").GetDouble()); return true;
                case "bandInstrumentReady":
                    _lblStatus.Text = "Instrument synchronized. Pause or scrub, select a joint, then adjust rotation/position.";
                    _webView.CoreWebView2.ExecuteScriptAsync($"setBandInstrumentVisible({(_bandShowInstrument.Checked ? "true" : "false")});");
                    return true;
                case "bandInstrumentError":
                    _lblStatus.Text = root.GetProperty("message").GetString(); return true;
            }
        }
        catch (JsonException) { }
        return false;
    }

    private void ExportBandPair()
    {
        if (_bandInstrument == null) { MessageBox.Show(this, "Load the matching instrument first.", "Band Performance"); return; }
        if (_unsaved) SaveAdjustments(false);
        using var folder = new FolderBrowserDialog { Description = "Export paired Characters and Instruments folders", UseDescriptionForTitle = true };
        if (folder.ShowDialog(this) != DialogResult.OK) return;
        var name = _bandSourcePath == null ? "BandMember" : Path.GetFileNameWithoutExtension(_bandSourcePath);
        // Keep the equipment's established performer ID (including BassDrummer
        // when its manually edited character source is named BaseDrummer).
        var instrumentName = Path.GetFileNameWithoutExtension(_bandInstrumentPath);
        if (instrumentName?.EndsWith("Equipment", StringComparison.OrdinalIgnoreCase) == true) name = instrumentName[..^9];
        var characterPath = Path.Combine(folder.SelectedPath, "Characters", name + ".glb");
        var instrumentPath = Path.Combine(folder.SelectedPath, "Instruments", name + "Equipment.glb");
        if ((File.Exists(characterPath) || File.Exists(instrumentPath)) && MessageBox.Show(this,
            $"Replace this exported pair?\n\n{characterPath}\n{instrumentPath}", "Export Band Pair", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        try
        {
            var result = BandPairService.Export(_model, _bandInstrument, folder.SelectedPath, name);
            _lblStatus.Text = $"Exported {Path.GetFileName(result.Character)} and {Path.GetFileName(result.Instrument)}.";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Band export failed", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ++_bandLoadVersion;
            foreach (var file in _bandTempFiles) try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            if (_bandPreviewFolder != null) try { Directory.Delete(_bandPreviewFolder); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        base.Dispose(disposing);
    }
}
