using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Plays the merged result back and crops an animation down to a chosen first/last frame.
    // Trimming mutates the shared in-memory merge result directly (same in-place-edit pattern as
    // the other three editors), so there's no separate "commit"/export step of its own - whatever
    // is in the model when MainForm's Save button is next clicked is what ends up in the file.
    //
    // One of the four modes hosted by ModelEditorForm (see EditorMode there), which owns the
    // window chrome shared by all four, so this control only contributes its own left-hand
    // controls and 3D preview.
    //
    // Unlike the other three editors (raw Three.js, so they can drive individual bones), this one
    // only ever needs whole-clip playback, so it uses the <model-viewer> web component and gets
    // glTF PBR rendering, environment lighting, color-space correction and camera framing for
    // free. The playback controls that used to be drawn as an HTML overlay inside that page now
    // live in the WinForms panel on the left, like every other mode's controls do - the page
    // exposes a handful of window.* functions (see the script below) that the panel drives via
    // ExecuteScriptAsync instead.
    public class AnimationTrimEditor : UserControl
    {
        // The live in-memory merge result - trimming writes straight onto this.
        private readonly ModelRoot _model;

        private WebView2 _webView = null!;
        private ComboBox _animDropdown = null!;
        private ComboBox _matDropdown = null!;
        private TrackBar _sliderStart = null!, _sliderEnd = null!;
        private Label _lblStart = null!, _lblEnd = null!, _lblStatus = null!;
        private Button _btnPause = null!, _btnPlayRange = null!, _btnApplyTrim = null!;
        private Panel _trimPanel = null!;

        // True only between the page reporting its model as loaded and the next reload - guards
        // every ExecuteScriptAsync, since the window.* helpers don't exist before then.
        private bool _viewerReady;
        private bool _paused;
        private bool _rangePlaying;
        private bool _suppressSliderEvents;

        // Animation name -> the distinct keyframe times actually present across its channels.
        // That's the only frame numbering available (the source file stores no FPS), and it's the
        // same numbering GlbMergeService.TrimAnimation applies its start/end frames against.
        // Recomputed after every trim so trimming again works from the new, shorter clip.
        private Dictionary<string, float[]> _frameTimes;

        // Each reload writes a NEW file rather than overwriting the one <model-viewer> already
        // loaded - a same-name rewrite races the browser's own file access and its cache, so the
        // reload could show the pre-trim clip (or fail outright). Old copies are cleaned up
        // best-effort as each new one is written.
        private int _previewVersion;
        private string? _previewPath;

        public AnimationTrimEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;
            _frameTimes = ComputeAnimFrameTimes(model);

            Dock = DockStyle.Fill;

            BuildUi();
            PopulateAnimationList();
            PopulateMaterialList();

            // The 3D preview is a WebView2 rendering its own already-dark scene, so only the
            // surrounding WinForms control panel needs theming.
            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 340, AutoScroll = true };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
            };

            FlowLayoutPanel MakeRow() => new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = new Padding(0, 0, 0, 6),
            };

            flow.Controls.Add(new Label { Text = "Animation:", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            var animRow = MakeRow();
            _animDropdown = new ComboBox { Width = 200, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 8, 10) };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();
            _btnPause = new Button { Text = "Pause", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(82, 0), Margin = new Padding(3, 3, 3, 10) };
            _btnPause.Click += (s, e) => TogglePause();
            animRow.Controls.Add(_animDropdown);
            animRow.Controls.Add(_btnPause);
            flow.Controls.Add(animRow);

            flow.Controls.Add(new Label { Text = "Isolate material:", AutoSize = true, Margin = new Padding(3, 0, 3, 4) });
            _matDropdown = new ComboBox { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(3, 3, 3, 12) };
            _matDropdown.SelectedIndexChanged += (s, e) => PushMaterialIsolation();
            flow.Controls.Add(_matDropdown);

            // Everything trim-related lives in one panel so selecting "None (static base pose)" -
            // or an animation with too few keyframes to trim - can disable the whole block at once.
            // Row tops come from named constants rather than eyeballed offsets, for the same
            // reason the other editors' panels do: a hand-picked pitch and a hand-picked panel
            // height drift apart and start clipping the last row.
            const int kLabelToSlider = 22;
            const int kSliderHeight = 45;
            const int kRowPitch = kLabelToSlider + kSliderHeight + 9;
            const int kHeaderHeight = 24;
            const int kButtonPitch = 34;

            int startRowTop = kHeaderHeight;
            int endRowTop = startRowTop + kRowPitch;
            int buttonsTop = endRowTop + kRowPitch;

            _trimPanel = new Panel { Width = 300, Height = buttonsTop + 2 * kButtonPitch + 6, Margin = new Padding(0) };

            var lblTrimHeader = new Label { Text = "Trim animation frames:", Left = 3, Top = 0, AutoSize = true };

            _lblStart = new Label { Text = "First frame: 0", Left = 3, Top = startRowTop, AutoSize = true };
            _sliderStart = new TrackBar { Left = 3, Top = startRowTop + kLabelToSlider, Width = 290, Height = kSliderHeight, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None };
            _sliderStart.ValueChanged += (s, e) => OnTrimSliderChanged(movedStart: true);

            _lblEnd = new Label { Text = "Last frame: 0", Left = 3, Top = endRowTop, AutoSize = true };
            _sliderEnd = new TrackBar { Left = 3, Top = endRowTop + kLabelToSlider, Width = 290, Height = kSliderHeight, Minimum = 0, Maximum = 0, TickStyle = TickStyle.None };
            _sliderEnd.ValueChanged += (s, e) => OnTrimSliderChanged(movedStart: false);

            var (btnMinusStart, btnPlusStart) = SliderNudge.Attach(_sliderStart);
            var (btnMinusEnd, btnPlusEnd) = SliderNudge.Attach(_sliderEnd);

            // Loops playback between just the two handles, so this shows what the clip will look
            // like after Apply cuts everything outside that window - rather than the full
            // untrimmed clip the animation dropdown plays.
            _btnPlayRange = new Button { Text = "Play Trimmed Range", Left = 3, Top = buttonsTop, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(290, 0) };
            _btnPlayRange.Click += (s, e) => ToggleRangePreview();

            _btnApplyTrim = new Button { Text = "Apply Trim to Merge", Left = 3, Top = buttonsTop + kButtonPitch, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(290, 0) };
            _btnApplyTrim.Click += (s, e) => ApplyTrim();

            _trimPanel.Controls.AddRange(new Control[]
            {
                lblTrimHeader,
                _lblStart, _sliderStart, btnMinusStart, btnPlusStart,
                _lblEnd, _sliderEnd, btnMinusEnd, btnPlusEnd,
                _btnPlayRange, _btnApplyTrim
            });
            flow.Controls.Add(_trimPanel);

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(300, 0),
                Margin = new Padding(3, 10, 3, 10), ForeColor = System.Drawing.Color.LightGreen
            };
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private void PopulateAnimationList()
        {
            _animDropdown.Items.Add("None (static base pose)");
            foreach (var name in AnimationNames()) _animDropdown.Items.Add(name);

            // Defaults to the first real animation rather than the static pose: this mode exists
            // to trim animations, so there's nothing to do here until one is selected.
            _animDropdown.SelectedIndex = _animDropdown.Items.Count > 1 ? 1 : 0;
        }

        private void PopulateMaterialList()
        {
            _matDropdown.Items.Add("All materials");
            for (int i = 0; i < _model.LogicalMaterials.Count; i++)
            {
                var name = _model.LogicalMaterials[i].Name;
                _matDropdown.Items.Add(string.IsNullOrEmpty(name) ? $"Material {i + 1}" : name);
            }

            // Isolating the first material (rather than showing all of them) is the long-standing
            // default here - a merged model's whole point is usually one swapped-in texture set,
            // and seeing that on its own is what this dropdown is for.
            _matDropdown.SelectedIndex = _matDropdown.Items.Count > 1 ? 1 : 0;
        }

        private IEnumerable<string> AnimationNames() =>
            _model.LogicalAnimations.Select(a => a.Name ?? $"Anim_{a.LogicalIndex}");

        // null while "None (static base pose)" is selected.
        private string? CurrentAnimationName() =>
            _animDropdown.SelectedIndex > 0 ? (string)_animDropdown.Items[_animDropdown.SelectedIndex]! : null;

        private float[] CurrentFrameTimes()
        {
            var name = CurrentAnimationName();
            if (name == null) return Array.Empty<float>();
            return _frameTimes.TryGetValue(name, out var times) ? times : Array.Empty<float>();
        }

        private void OnAnimationSelected()
        {
            StopRangePreview();
            var name = CurrentAnimationName();
            _paused = false;
            _btnPause.Text = "Pause";
            _btnPause.Enabled = name != null;

            ConfigureTrimControls();
            Exec(name == null ? "setAnimation('');" : $"setAnimation('{EscapeJs(name)}');");
        }

        // Re-ranges the two frame sliders for whatever animation is selected now, or disables the
        // whole trim block for the static pose / a clip with fewer than two distinct keyframe
        // times (nothing meaningful to cut).
        private void ConfigureTrimControls()
        {
            var times = CurrentFrameTimes();
            bool trimmable = times.Length >= 2;
            _trimPanel.Enabled = trimmable;

            _suppressSliderEvents = true;
            try
            {
                int lastIndex = trimmable ? times.Length - 1 : 0;
                // Maximum is raised before Value is moved and lowered after, since a TrackBar
                // clamps Value into whatever range it currently has - shrinking the range first
                // would silently drag the values down with it.
                _sliderStart.Maximum = Math.Max(_sliderStart.Maximum, lastIndex);
                _sliderEnd.Maximum = Math.Max(_sliderEnd.Maximum, lastIndex);
                _sliderStart.Value = 0;
                _sliderEnd.Value = lastIndex;
                _sliderStart.Maximum = lastIndex;
                _sliderEnd.Maximum = lastIndex;
            }
            finally
            {
                _suppressSliderEvents = false;
            }

            UpdateTrimLabels();
        }

        private void OnTrimSliderChanged(bool movedStart)
        {
            if (_suppressSliderEvents) return;

            // Keep at least one frame between the handles - TrimAnimation clamps to the same rule,
            // so letting them cross here would just show a range that isn't what gets applied.
            _suppressSliderEvents = true;
            try
            {
                if (_sliderStart.Value >= _sliderEnd.Value)
                {
                    if (movedStart) _sliderStart.Value = Math.Max(0, _sliderEnd.Value - 1);
                    else _sliderEnd.Value = Math.Min(_sliderEnd.Maximum, _sliderStart.Value + 1);
                }
            }
            finally
            {
                _suppressSliderEvents = false;
            }

            UpdateTrimLabels();

            // Scrubbing to the handle just moved is the whole point of the sliders - it's how the
            // user sees which frame they're actually cutting at.
            StopRangePreview();
            _paused = true;
            _btnPause.Text = "Resume";
            var times = CurrentFrameTimes();
            int frame = movedStart ? _sliderStart.Value : _sliderEnd.Value;
            if (frame < times.Length) Exec($"seekTo({Inv(times[frame])});");
        }

        private void UpdateTrimLabels()
        {
            var times = CurrentFrameTimes();
            string At(int frame) => frame < times.Length ? $" ({times[frame].ToString("0.000", CultureInfo.InvariantCulture)}s)" : "";
            _lblStart.Text = $"First frame: {_sliderStart.Value}{At(_sliderStart.Value)}";
            _lblEnd.Text = $"Last frame: {_sliderEnd.Value}{At(_sliderEnd.Value)} of {Math.Max(times.Length - 1, 0)}";
        }

        private void TogglePause()
        {
            StopRangePreview();
            _paused = !_paused;
            _btnPause.Text = _paused ? "Resume" : "Pause";
            Exec($"setPaused({(_paused ? "true" : "false")});");
        }

        private void ToggleRangePreview()
        {
            if (_rangePlaying)
            {
                StopRangePreview();
                _paused = true;
                _btnPause.Text = "Resume";
                Exec("setPaused(true);");
                return;
            }

            var times = CurrentFrameTimes();
            if (_sliderStart.Value >= times.Length || _sliderEnd.Value >= times.Length) return;

            _rangePlaying = true;
            _paused = false;
            _btnPause.Text = "Pause";
            _btnPlayRange.Text = "Stop Trimmed Range";
            Exec($"playRange({Inv(times[_sliderStart.Value])}, {Inv(times[_sliderEnd.Value])});");
        }

        private void StopRangePreview()
        {
            if (!_rangePlaying) return;
            _rangePlaying = false;
            _btnPlayRange.Text = "Play Trimmed Range";
            Exec("stopRange();");
        }

        private void PushMaterialIsolation()
        {
            // Item 0 is "All materials", so the logical material index is one less than the
            // selected index, and -1 means "isolate nothing".
            Exec($"isolateMaterial({_matDropdown.SelectedIndex - 1});");
        }

        // Crops the selected clip on the shared merge result, then reloads the preview so the
        // viewer plays the trimmed clip instead of the stale pre-trim one still in the browser.
        private void ApplyTrim()
        {
            var name = CurrentAnimationName();
            if (name == null) return;

            StopRangePreview();

            try
            {
                GlbMergeService.TrimAnimation(_model, name, _sliderStart.Value, _sliderEnd.Value);

                // Re-derived from the model, so trimming this clip again in the same session works
                // from the post-trim frame numbering instead of stale pre-trim bounds.
                _frameTimes = ComputeAnimFrameTimes(_model);
                ConfigureTrimControls();

                _lblStatus.Text = $"Trimmed '{name}' to {CurrentFrameTimes().Length} frame(s). " +
                    "Included the next time you save the merge.";

                ReloadPreview();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to trim animation: {ex.Message}",
                    "Trim Animation", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this
            // fire-and-forget startup may still be mid-await, so both the await itself and
            // everything after it have to tolerate that.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            // Same virtual-host mapping the other editors use: expose the temp folder under an
            // https origin so the page can fetch the preview GLB without tripping cross-origin
            // blocks.
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            string previewFileName = WritePreviewFile();

            // <model-viewer> handles glTF PBR rendering, environment lighting, color-space
            // correction and camera framing; everything below just exposes the few playback hooks
            // the WinForms panel needs in order to drive it.
            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <title>GLTF Runtime Inspector</title>
                <script type='module' src='https://ajax.googleapis.com/ajax/libs/model-viewer/3.4.0/model-viewer.min.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    model-viewer { width: 100%; height: 100%; --poster-color: transparent; }
                </style>
            </head>
            <body>
                <model-viewer id='viewer' src='https://appassets.local/" + previewFileName + @"' camera-controls
                    shadow-intensity='1' environment-image='neutral' exposure='1'>
                </model-viewer>

                <script>
                    var viewer = document.querySelector('#viewer');
                    var loaded = false;
                    var rangeActive = false, rangeStart = 0, rangeEnd = 0, rangeRaf = null;

                    // The .NET side holds all the state (which clip, which material, paused or
                    // not), so the page never tries to remember any of it across a reload - it
                    // just announces that it's ready and gets told what to show.
                    viewer.addEventListener('load', function () {
                        (viewer.model.materials || []).forEach(function (mat) {
                            // model-viewer's default environment lighting makes glTF's default
                            // (metallic=1, roughness=1) materials look like shiny chrome. This is a
                            // preview-only tweak (it never touches the saved file) that caps
                            // metallic and floors roughness so the model reads as matte plastic
                            // instead, which is more representative for most assets.
                            if (mat.pbrMetallicRoughness) {
                                var pbr = mat.pbrMetallicRoughness;
                                pbr.setMetallicFactor(Math.min(pbr.metallicFactor, 0.15));
                                pbr.setRoughnessFactor(Math.max(pbr.roughnessFactor, 0.7));
                            }
                        });
                        loaded = true;
                        window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                    });

                    window.setAnimation = function (name) {
                        stopRangeInternal();
                        if (!loaded) return;
                        if (name) { viewer.animationName = name; viewer.currentTime = 0; viewer.play(); }
                        else { viewer.pause(); viewer.currentTime = 0; }
                    };

                    window.setPaused = function (p) {
                        if (!loaded) return;
                        if (p) viewer.pause(); else viewer.play();
                    };

                    window.seekTo = function (t) {
                        stopRangeInternal();
                        if (!loaded) return;
                        viewer.pause();
                        viewer.currentTime = t;
                    };

                    function stopRangeInternal() {
                        rangeActive = false;
                        if (rangeRaf !== null) { cancelAnimationFrame(rangeRaf); rangeRaf = null; }
                    }

                    function rangeTick() {
                        if (!rangeActive) return;
                        if (viewer.currentTime < rangeStart || viewer.currentTime >= rangeEnd) viewer.currentTime = rangeStart;
                        rangeRaf = requestAnimationFrame(rangeTick);
                    }

                    window.playRange = function (a, b) {
                        if (!loaded) return;
                        stopRangeInternal();
                        rangeStart = a; rangeEnd = b; rangeActive = true;
                        viewer.currentTime = a;
                        viewer.play();
                        rangeRaf = requestAnimationFrame(rangeTick);
                    };

                    window.stopRange = function () { stopRangeInternal(); };

                    // -1 shows everything; anything else fades every OTHER material almost fully
                    // out rather than hiding it outright, so the isolated one still reads in
                    // context.
                    window.isolateMaterial = function (idx) {
                        if (!loaded) return;
                        (viewer.model.materials || []).forEach(function (mat, i) {
                            var visible = idx < 0 || i === idx;
                            mat.setAlphaMode(visible ? 'OPAQUE' : 'BLEND');
                            if (mat.pbrMetallicRoughness) {
                                var factor = mat.pbrMetallicRoughness.baseColorFactor || [1, 1, 1, 1];
                                mat.pbrMetallicRoughness.setBaseColorFactor([factor[0], factor[1], factor[2], visible ? 1 : 0.05]);
                            }
                        });
                    };

                    // Points the viewer at a freshly written GLB after a trim. 'load' fires again
                    // afterward, which is what re-announces readiness so .NET can re-push state.
                    window.reloadModel = function (url) {
                        stopRangeInternal();
                        loaded = false;
                        viewer.src = url;
                    };
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(htmlContent);
        }

        // Dumps the current in-memory model to a fresh temp file for <model-viewer> to load, and
        // returns just the file name (the virtual host maps the folder, not the file).
        private string WritePreviewFile()
        {
            var previous = _previewPath;
            _previewPath = Path.Combine(Path.GetTempPath(), $"glbmerger_trim_preview_{_previewVersion++}.glb");
            _model.SaveGLB(_previewPath);

            if (previous != null)
            {
                try { File.Delete(previous); }
                catch (IOException) { /* still held by the browser - harmless, it's a temp file */ }
                catch (UnauthorizedAccessException) { }
            }

            return Path.GetFileName(_previewPath);
        }

        private void ReloadPreview()
        {
            if (_webView.CoreWebView2 == null) return;

            _viewerReady = false;
            string fileName = WritePreviewFile();
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"reloadModel('https://appassets.local/{EscapeJs(fileName)}');");
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            string? action;
            try
            {
                action = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Action;
            }
            catch
            {
                return;
            }

            if (action != "ready" || IsDisposed) return;

            _viewerReady = true;
            PushViewerState();
        }

        // Re-applies everything the panel currently says, in one go - run once the page reports
        // its model loaded, both on first load and after each post-trim reload, since the page
        // deliberately keeps no state of its own across that.
        private void PushViewerState()
        {
            PushMaterialIsolation();

            var name = CurrentAnimationName();
            Exec(name == null ? "setAnimation('');" : $"setAnimation('{EscapeJs(name)}');");

            _paused = false;
            _rangePlaying = false;
            _btnPause.Text = "Pause";
            _btnPlayRange.Text = "Play Trimmed Range";
        }

        private void Exec(string script)
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(script);
        }

        private static Dictionary<string, float[]> ComputeAnimFrameTimes(ModelRoot model)
        {
            var result = new Dictionary<string, float[]>();
            foreach (var anim in model.LogicalAnimations)
            {
                var name = anim.Name ?? $"Anim_{anim.LogicalIndex}";
                var times = new SortedSet<float>();
                foreach (var ch in anim.Channels)
                {
                    if (ch.TargetNodePath == PropertyPath.translation)
                        foreach (var k in ch.GetTranslationSampler().GetLinearKeys()) times.Add(k.Key);
                    else if (ch.TargetNodePath == PropertyPath.rotation)
                        foreach (var k in ch.GetRotationSampler().GetLinearKeys()) times.Add(k.Key);
                    else if (ch.TargetNodePath == PropertyPath.scale)
                        foreach (var k in ch.GetScaleSampler().GetLinearKeys()) times.Add(k.Key);
                }
                result[name] = times.ToArray();
            }
            return result;
        }

        private static string Inv(float v) => v.ToString("0.######", CultureInfo.InvariantCulture);

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
        }
    }
}
