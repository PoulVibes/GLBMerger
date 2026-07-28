using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    public class ViewerForm : Form
    {
        private WebView2 webView;
        // The live in-memory merge result (same object MainForm's Save button ultimately writes
        // out) - trimming mutates this directly, so it needs no separate "commit"/export step of
        // its own; whatever's here when Save is clicked is what ends up in the file.
        private readonly ModelRoot model;
        // Scratch copy of `model` on disk purely so the WebView2 <model-viewer> has a file to
        // load from - never the trim target itself.
        private readonly string previewGlbPath;

        public ViewerForm(ModelRoot model, string previewGlbPath, bool darkMode = false)
        {
            this.model = model;
            this.previewGlbPath = previewGlbPath;
            this.Text = "3D GLB Model Asset Inspector";
            this.Width = 900;
            this.Height = 700;
            this.StartPosition = FormStartPosition.CenterParent;

            webView = new WebView2 { Dock = DockStyle.Fill };
            this.Controls.Add(webView);

            // The WebView2 fills the whole client area with its own already-dark HTML, so this
            // only really shows during the brief moment before it's ready - kept for consistency
            // with the rest of the app rather than for any real visual impact here.
            ThemeManager.Apply(this, darkMode);

            this.Load += async (s, e) => await InitializeViewerAsync();
        }

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            // Initialize the embedded web browser environment
            await webView.EnsureCoreWebView2Async(null);

            // Map local system paths to a virtual origin so the webview can safely bypass cross-origin browser blocks
            string appFolder = AppContext.BaseDirectory;
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", appFolder, CoreWebView2HostResourceAccessKind.Allow);

            // Copy the target GLB file temporarily into the app asset pool so the HTML container can access it
            string targetGlbCopyPath = Path.Combine(appFolder, "preview.glb");
            try
            {
                if (File.Exists(targetGlbCopyPath)) File.Delete(targetGlbCopyPath);
                File.Copy(previewGlbPath, targetGlbCopyPath, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to cache file for 3D engine: {ex.Message}", "Viewer Engine Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Per-animation frame timing, read straight off `model`'s own keyframes (there's no
            // stored FPS to assume) so the trim sliders below can offer whole "frames" instead of
            // an arbitrary time scrubber. Keyed by the same name model-viewer's own
            // availableAnimations list uses, so the two line up in JS. Read from the live model
            // (not the on-disk copy) so it stays correct if trimming is applied more than once in
            // the same session.
            string animFrameTimesJson = JsonSerializer.Serialize(ComputeAnimFrameTimes(model));

            // <model-viewer> web component - handles glTF PBR rendering, environment lighting,
            // color-space correction, and camera framing automatically, so none of that needs
            // to be reimplemented by hand.
            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <title>GLTF Runtime Inspector</title>
                <script type='module' src='https://ajax.googleapis.com/ajax/libs/model-viewer/3.4.0/model-viewer.min.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; font-family: sans-serif; background: #23272a; }
                    model-viewer { width: 100%; height: 100%; --poster-color: transparent; }
                    #controls-overlay {
                        position: absolute; bottom: 20px; left: 20px;
                        background: rgba(0,0,0,0.85); color: #fff; padding: 15px;
                        border-radius: 8px; box-shadow: 0 4px 12px rgba(0,0,0,0.5);
                        max-width: 300px; border: 1px solid #4f545c;
                    }
                    h4 { margin: 0 0 10px 0; color: #7289da; font-size: 14px; text-transform: uppercase; letter-spacing: 0.5px; }
                    select { width: 100%; padding: 6px; margin-bottom: 12px; border-radius: 4px; background: #2f3136; color: white; border: 1px solid #202225; }
                    label { font-size: 12px; color: #b9bbbe; display: block; margin-bottom: 4px; }
                    #trim-section { margin-top: 4px; padding-top: 10px; border-top: 1px solid #4f545c; }
                    #trim-section[data-disabled='true'] { opacity: 0.4; pointer-events: none; }
                    input[type='range'] { width: 100%; margin: 0; accent-color: #7289da; }
                    #trim-labels { display: flex; justify-content: space-between; font-size: 11px; color: #b9bbbe; margin-bottom: 2px; }
                    .trim-btn {
                        width: 100%; margin-top: 8px; padding: 7px; border-radius: 4px; border: none;
                        background: #7289da; color: white; font-size: 12px; font-weight: bold; cursor: pointer;
                    }
                    .trim-btn:hover { background: #5b6eae; }
                    #btn-save-trim { background: #43b581; }
                    #btn-save-trim:hover { background: #359469; }
                </style>
            </head>
            <body>
                <model-viewer id='viewer' src='https://appassets.local/preview.glb' camera-controls
                    autoplay shadow-intensity='1' environment-image='neutral' exposure='1'>
                </model-viewer>

                <div id='controls-overlay'>
                    <h4>3D Controller</h4>

                    <label for='anim-selector'>Active Animation Track:</label>
                    <select id='anim-selector'>
                        <option value=''>None (Static Base Pose)</option>
                    </select>

                    <label for='material-selector'>Isolate Target Material:</label>
                    <select id='material-selector'>
                        <option value='all'>All Appended Textures</option>
                    </select>

                    <div id='trim-section' data-disabled='true'>
                        <label>Trim Animation Frames:</label>
                        <div id='trim-labels'>
                            <span id='trim-start-label'>First: 0</span>
                            <span id='trim-end-label'>Last: 0</span>
                        </div>
                        <input type='range' id='trim-start' min='0' max='0' value='0' step='1'>
                        <input type='range' id='trim-end' min='0' max='0' value='0' step='1'>
                        <button class='trim-btn' id='btn-preview-trim'>Play Trimmed Range</button>
                        <button class='trim-btn' id='btn-save-trim'>Apply Trim to Merge</button>
                    </div>
                </div>

                <script>
                    // Per-animation keyframe times (seconds), read from the source file's own
                    // channel data by the .NET side - drives the trim sliders' frame numbering.
                    var animFrameTimes = " + animFrameTimesJson + @";

                    var viewer = document.querySelector('#viewer');
                    var animSelect = document.querySelector('#anim-selector');
                    var matSelect = document.querySelector('#material-selector');
                    var trimSection = document.querySelector('#trim-section');
                    var trimStart = document.querySelector('#trim-start');
                    var trimEnd = document.querySelector('#trim-end');
                    var trimStartLabel = document.querySelector('#trim-start-label');
                    var trimEndLabel = document.querySelector('#trim-end-label');
                    var btnPreviewTrim = document.querySelector('#btn-preview-trim');
                    var btnSaveTrim = document.querySelector('#btn-save-trim');
                    var trimPreviewActive = false;
                    var trimPreviewRafId = null;

                    viewer.addEventListener('load', function () {
                        (viewer.availableAnimations || []).forEach(function (name) {
                            var opt = document.createElement('option');
                            opt.value = name;
                            opt.textContent = name;
                            animSelect.appendChild(opt);
                        });

                        (viewer.model.materials || []).forEach(function (mat, i) {
                            var opt = document.createElement('option');
                            opt.value = i;
                            opt.textContent = mat.name || ('Material ' + (i + 1));
                            matSelect.appendChild(opt);

                            // model-viewer's default environment lighting makes glTF's default
                            // (metallic=1, roughness=1) materials look like shiny chrome. This is
                            // a viewer-only preview tweak (doesn't touch the saved file) that caps
                            // metallic and floors roughness so the model reads as matte plastic
                            // instead, which is a more representative default for most assets.
                            if (mat.pbrMetallicRoughness) {
                                var pbr = mat.pbrMetallicRoughness;
                                pbr.setMetallicFactor(Math.min(pbr.metallicFactor, 0.15));
                                pbr.setRoughnessFactor(Math.max(pbr.roughnessFactor, 0.7));
                            }
                        });

                        // Default to isolating the first material instead of showing everything -
                        // dispatch 'change' so the isolation logic actually runs, not just the
                        // dropdown's displayed selection.
                        if (viewer.model.materials && viewer.model.materials.length > 0) {
                            matSelect.value = '0';
                            matSelect.dispatchEvent(new Event('change'));
                        }
                    });

                    animSelect.addEventListener('change', function (e) {
                        stopTrimPreview();
                        if (e.target.value) {
                            viewer.animationName = e.target.value;
                            viewer.play();
                        } else {
                            viewer.pause();
                            viewer.currentTime = 0;
                        }
                        setupTrimControls(e.target.value);
                    });

                    // Configures the trim sliders' range for the currently selected animation, or
                    // disables the whole section for 'None' / an animation with no frame data.
                    function setupTrimControls(animName) {
                        var times = animName ? animFrameTimes[animName] : null;
                        if (!times || times.length < 2) {
                            trimSection.setAttribute('data-disabled', 'true');
                            return;
                        }

                        trimSection.setAttribute('data-disabled', 'false');
                        var lastIndex = times.length - 1;
                        trimStart.min = 0; trimStart.max = lastIndex; trimStart.value = 0;
                        trimEnd.min = 0; trimEnd.max = lastIndex; trimEnd.value = lastIndex;
                        trimStartLabel.textContent = 'First: 0';
                        trimEndLabel.textContent = 'Last: ' + lastIndex;
                    }

                    function currentFrameTimes() {
                        return animFrameTimes[animSelect.value] || [];
                    }

                    trimStart.addEventListener('input', function () {
                        // Keep at least one frame between the two handles.
                        if (+trimStart.value >= +trimEnd.value) trimStart.value = +trimEnd.value - 1;
                        trimStartLabel.textContent = 'First: ' + trimStart.value;
                        stopTrimPreview();
                        viewer.pause();
                        viewer.currentTime = currentFrameTimes()[+trimStart.value] || 0;
                    });

                    trimEnd.addEventListener('input', function () {
                        if (+trimEnd.value <= +trimStart.value) trimEnd.value = +trimStart.value + 1;
                        trimEndLabel.textContent = 'Last: ' + trimEnd.value;
                        stopTrimPreview();
                        viewer.pause();
                        viewer.currentTime = currentFrameTimes()[+trimEnd.value] || 0;
                    });

                    // Loops playback between just the two trim handles, so 'Play Trimmed Range'
                    // shows what the clip will look like after Apply cuts everything outside
                    // that window - rather than the full untrimmed clip the anim dropdown plays.
                    function stopTrimPreview() {
                        trimPreviewActive = false;
                        if (trimPreviewRafId !== null) { cancelAnimationFrame(trimPreviewRafId); trimPreviewRafId = null; }
                        btnPreviewTrim.textContent = 'Play Trimmed Range';
                    }

                    function trimPreviewTick() {
                        if (!trimPreviewActive) return;
                        var times = currentFrameTimes();
                        var startT = times[+trimStart.value] || 0;
                        var endT = times[+trimEnd.value] || 0;
                        if (viewer.currentTime < startT || viewer.currentTime >= endT) {
                            viewer.currentTime = startT;
                        }
                        trimPreviewRafId = requestAnimationFrame(trimPreviewTick);
                    }

                    btnPreviewTrim.addEventListener('click', function () {
                        if (trimPreviewActive) {
                            stopTrimPreview();
                            viewer.pause();
                            return;
                        }
                        var times = currentFrameTimes();
                        trimPreviewActive = true;
                        viewer.currentTime = times[+trimStart.value] || 0;
                        viewer.play();
                        btnPreviewTrim.textContent = 'Pause';
                        trimPreviewRafId = requestAnimationFrame(trimPreviewTick);
                    });

                    btnSaveTrim.addEventListener('click', function () {
                        stopTrimPreview();
                        window.chrome.webview.postMessage(JSON.stringify({
                            action: 'trimAnimation',
                            animation: animSelect.value,
                            startFrame: +trimStart.value,
                            endFrame: +trimEnd.value
                        }));
                    });

                    // Called from .NET after a trim is applied - refreshes the slider bounds to
                    // match the animation's new (smaller) keyframe count, so trimming again in
                    // the same session works from the post-trim frame numbering, not the stale
                    // pre-trim one.
                    window.refreshTrimData = function (newFrameTimes, animName) {
                        animFrameTimes = newFrameTimes;
                        if (animSelect.value === animName) setupTrimControls(animName);
                    };

                    matSelect.addEventListener('change', function (e) {
                        var targetIndex = e.target.value;
                        (viewer.model.materials || []).forEach(function (mat, i) {
                            var visible = targetIndex === 'all' || String(i) === String(targetIndex);
                            mat.setAlphaMode(visible ? 'OPAQUE' : 'BLEND');
                            if (mat.pbrMetallicRoughness) {
                                var factor = mat.pbrMetallicRoughness.baseColorFactor || [1, 1, 1, 1];
                                mat.pbrMetallicRoughness.setBaseColorFactor([factor[0], factor[1], factor[2], visible ? 1 : 0.05]);
                            }
                        });
                    });
                </script>
            </body>
            </html>";

            webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            // Load the generated webpage canvas directly into the application instance frame
            webView.CoreWebView2.NavigateToString(htmlContent);
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            TrimRequest? msg;
            try
            {
                msg = JsonSerializer.Deserialize<TrimRequest>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return;
            }

            if (msg == null || msg.Action != "trimAnimation" || string.IsNullOrEmpty(msg.Animation)) return;

            try
            {
                // Mutates the shared merge result in place - no file dialog, no separate export.
                // Whatever's in `model` when MainForm's own Save button is next clicked is what
                // gets written out, same as the joint/anchor/stiff-arm correction forms.
                GlbMergeService.TrimAnimation(model, msg.Animation, msg.StartFrame, msg.EndFrame);

                // Refreshes the sliders in the page to match the animation's new, smaller
                // keyframe count, so trimming again this session starts from the post-trim
                // numbering instead of stale pre-trim bounds.
                string json = JsonSerializer.Serialize(ComputeAnimFrameTimes(model));
                _ = webView.CoreWebView2.ExecuteScriptAsync($"refreshTrimData({json}, '{EscapeJs(msg.Animation)}');");

                MessageBox.Show(this,
                    $"Trimmed '{msg.Animation}' to frames {msg.StartFrame}-{msg.EndFrame}. This will be included the next time you save the merge.",
                    "Trim Animation", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Failed to trim animation: {ex.Message}",
                    "Trim Animation Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
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

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class TrimRequest
        {
            public string? Action { get; set; }
            public string? Animation { get; set; }
            public int StartFrame { get; set; }
            public int EndFrame { get; set; }
        }
    }
}
