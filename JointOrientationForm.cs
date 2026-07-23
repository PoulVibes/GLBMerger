using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Lets the user apply a fixed X/Y/Z rotation offset to a specific bone/joint, correcting
    // rigs where a hand, foot, or other part was authored facing the wrong way. Adjustments are
    // per-animation: sliders only drive a live preview until "Save Adjustments to Animation" is
    // clicked, at which point the offset is baked into the currently selected animation's
    // rotation channel only (or the bind pose, if "None (Static Pose)" is selected). Switching
    // to a different animation discards any not-yet-saved adjustments, since they were only ever
    // meant to apply to the animation they were dialed in against.
    public class JointOrientationForm : Form
    {
        private readonly ModelRoot _model;

        private ComboBox _boneDropdown = null!;
        private ComboBox _animDropdown = null!;
        private TrackBar _sliderX = null!, _sliderY = null!, _sliderZ = null!;
        private Label _lblX = null!, _lblY = null!, _lblZ = null!;
        private Label _lblStatus = null!;
        private Button _btnPause = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;

        // Bone name -> not-yet-saved slider offset in degrees. Kept as the raw X/Y/Z slider
        // values (not a quaternion) so re-selecting a bone can restore the sliders exactly,
        // rather than trying to decompose a quaternion back into Euler angles.
        private readonly Dictionary<string, (int X, int Y, int Z)> _pendingOffsets = new();

        // Bone name -> its rotation keys (or bind rotation) exactly as they were before any
        // adjustment this "session" (i.e. since the bone was first touched after the last
        // animation swap). Saving re-derives the correction from this baseline every time rather
        // than from whatever is currently in the model, so hitting Save repeatedly while still
        // moving the slider doesn't compound the rotation onto itself.
        private readonly Dictionary<string, (float Time, Quaternion Value)[]> _originalKeysCache = new();
        private bool _suppressSliderEvents;

        public JointOrientationForm(ModelRoot model)
        {
            _model = model;

            Text = "Fix Joint Orientation";
            Width = 1000;
            Height = 700;
            MinimumSize = new System.Drawing.Size(700, 450);
            StartPosition = FormStartPosition.CenterParent;

            BuildUi();
            PopulateBoneList();
            PopulateAnimationList();

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 320, Padding = new Padding(12) };

            var lblBone = new Label { Text = "Joint / Bone:", Left = 12, Top = 12, AutoSize = true };
            _boneDropdown = new ComboBox { Left = 12, Top = 32, Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            _boneDropdown.SelectedIndexChanged += (s, e) => OnBoneSelected();

            var lblAnim = new Label { Text = "Preview Animation:", Left = 12, Top = 66, AutoSize = true };
            _animDropdown = new ComboBox { Left = 12, Top = 86, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();

            _btnPause = new Button { Text = "Pause", Left = 210, Top = 85, Width = 82 };
            _btnPause.Click += (s, e) => TogglePause();

            (_lblX, _sliderX) = MakeSlider("X Rotation", 128);
            (_lblY, _sliderY) = MakeSlider("Y Rotation", 198);
            (_lblZ, _sliderZ) = MakeSlider("Z Rotation", 268);

            var btnSave = new Button { Text = "Save Adjustments to Animation", Left = 12, Top = 340, Width = 280 };
            btnSave.Click += (s, e) => SaveAdjustments();

            var btnReset = new Button { Text = "Reset This Joint", Left = 12, Top = 380, Width = 280 };
            btnReset.Click += (s, e) => ResetCurrentBone();

            _lblStatus = new Label { Left = 12, Top = 418, Width = 280, Height = 32, AutoSize = false, ForeColor = System.Drawing.Color.LightGreen };

            var btnClose = new Button { Text = "Done", Left = 12, Top = 458, Width = 280, DialogResult = DialogResult.OK };

            controlPanel.Controls.AddRange(new Control[]
            {
                lblBone, _boneDropdown, lblAnim, _animDropdown, _btnPause,
                _lblX, _sliderX, _lblY, _sliderY, _lblZ, _sliderZ,
                btnSave, btnReset, _lblStatus, btnClose
            });
            AcceptButton = btnClose;

            _webView = new WebView2 { Dock = DockStyle.Fill };

            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            await _webView.EnsureCoreWebView2Async(null);

            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_joint_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) => _viewerReady = e.IsSuccess;

            // The model is only ever saved to disk *once*, just to give the loader something to
            // read - after that, every correction is applied directly to the loaded Three.js bone
            // object in the render loop, with no further saving or reloading. That's what makes
            // this live: model-viewer (used by the main "Open Model Viewer") only knows how to
            // load a whole file and has no API for touching an individual bone, so getting a
            // correction on screen there means writing a new file and reloading it from scratch -
            // the flicker the user was seeing. Raw Three.js exposes the actual bone objects, so a
            // slider move can just set a rotation directly with no file round-trip at all.
            _model.SaveGLB(previewPath);
            var previewFileName = Path.GetFileName(previewPath);

            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <script src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #viewport { width: 100%; height: 100%; display: block; }
                    #error-overlay {
                        position: absolute; top: 10px; left: 10px; right: 10px;
                        background: rgba(139, 0, 0, 0.92); color: #fff; padding: 12px;
                        border-radius: 6px; font-family: monospace; font-size: 12px;
                        white-space: pre-wrap; display: none; max-height: 40%; overflow: auto;
                    }
                </style>
            </head>
            <body>
                <canvas id='viewport'></canvas>
                <div id='error-overlay'></div>
                <script>
                    function showError(msg) {
                        var el = document.querySelector('#error-overlay');
                        el.style.display = 'block';
                        el.textContent += msg + '\n';
                    }
                    window.onerror = function (message) { showError('JS error: ' + message); };

                    try {
                        var canvas = document.querySelector('#viewport');
                        var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
                        renderer.setPixelRatio(window.devicePixelRatio);
                        renderer.setSize(window.innerWidth, window.innerHeight);
                        renderer.outputEncoding = THREE.sRGBEncoding;
                        renderer.toneMapping = THREE.ACESFilmicToneMapping;
                        renderer.toneMappingExposure = 1.1;

                        var scene = new THREE.Scene();
                        scene.background = new THREE.Color(0x1a1c1e);

                        var camera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.01, 10000);
                        var controls = new THREE.OrbitControls(camera, renderer.domElement);
                        controls.enableDamping = true;

                        scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
                        scene.add(new THREE.AmbientLight(0xffffff, 0.6));
                        var dirLight = new THREE.DirectionalLight(0xffffff, 1.5);
                        dirLight.position.set(5, 10, 7.5);
                        scene.add(dirLight);
                        var fillLight = new THREE.DirectionalLight(0xffffff, 0.8);
                        fillLight.position.set(-5, 5, -7.5);
                        scene.add(fillLight);

                        var mixer = null;
                        var clock = new THREE.Clock();
                        var paused = false;
                        var bonesByName = {};
                        // boneName -> THREE.Quaternion offset, re-applied on top of whatever the
                        // bone's rotation is *this frame* (bind pose, or animation-driven) - so it
                        // works identically whether an animation is playing or not, and moving a
                        // slider just changes what gets re-applied next frame, live.
                        var liveCorrections = {};
                        // boneName -> the bone's un-corrected rotation, snapshotted right after
                        // mixer.update() sets it fresh each frame. Corrections are always applied
                        // on top of this snapshot rather than mutated in place, so pausing (which
                        // stops mixer.update from refreshing the bone) doesn't cause the same
                        // correction to be re-applied onto an already-corrected value every frame,
                        // which is what was making paused joints spin.
                        var baseQuaternions = {};

                        window.setLiveCorrection = function (boneName, x, y, z, w) {
                            if (!liveCorrections[boneName]) liveCorrections[boneName] = new THREE.Quaternion();
                            liveCorrections[boneName].set(x, y, z, w);
                        };

                        window.setAnimationByName = function (name) {
                            baseQuaternions = {};
                            if (!mixer) return;
                            mixer.stopAllAction();
                            if (!name) return;
                            var clip = window._clips.filter(function (c) { return c.name === name; })[0];
                            if (clip) mixer.clipAction(clip).play();
                        };

                        window.setPaused = function (value) {
                            paused = value;
                        };

                        var loader = new THREE.GLTFLoader();
                        loader.load('https://appassets.local/" + previewFileName + @"', function (gltf) {
                            try {
                                scene.add(gltf.scene);

                                var orderedMaterials = [];
                                gltf.scene.traverse(function (obj) {
                                    if (obj.isMesh && obj.material) {
                                        var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                                        mats.forEach(function (mat) {
                                            mat.side = THREE.DoubleSide;
                                            if (orderedMaterials.indexOf(mat) === -1) orderedMaterials.push(mat);
                                        });
                                    }
                                    if (obj.name) bonesByName[obj.name] = obj;
                                });

                                // Default to showing only the first material, same as the main
                                // model viewer - the rest start faded out instead of everything
                                // being shown at once.
                                orderedMaterials.forEach(function (mat, i) {
                                    var visible = i === 0;
                                    mat.transparent = !visible;
                                    mat.opacity = visible ? 1 : 0.05;
                                    // Faded-out materials must not write depth, or their invisible
                                    // triangles still occlude the visible mesh behind them - this is
                                    // what caused the see-through/depth-confusion glitches, especially
                                    // once the skinned mesh deforms (e.g. arms swinging).
                                    mat.depthWrite = visible;

                                    // Same metallic/roughness dampening as the main model viewer,
                                    // so this preview doesn't look shinier/darker than the real thing.
                                    if (typeof mat.metalness === 'number') mat.metalness = Math.min(mat.metalness, 0.15);
                                    if (typeof mat.roughness === 'number') mat.roughness = Math.max(mat.roughness, 0.7);
                                });

                                var box = new THREE.Box3().setFromObject(gltf.scene);
                                var size = box.getSize(new THREE.Vector3());
                                var center = box.getCenter(new THREE.Vector3());
                                var maxDim = (Math.max(size.x, size.y, size.z) || 1) * 2.5;

                                controls.target.copy(center);
                                camera.position.copy(center).add(new THREE.Vector3(maxDim, maxDim * 0.6, maxDim));
                                camera.near = maxDim / 1000;
                                camera.far = maxDim * 100;
                                camera.updateProjectionMatrix();
                                controls.update();

                                window._clips = gltf.animations || [];
                                if (window._clips.length > 0) {
                                    mixer = new THREE.AnimationMixer(gltf.scene);
                                    // Autoplay the first clip so playback starts in sync with the
                                    // .NET side's own default selection (its first real
                                    // animation) without needing a round-trip back from C#, which
                                    // could race with the model still loading.
                                    mixer.clipAction(window._clips[0]).play();
                                }
                            } catch (innerErr) {
                                showError('Error setting up loaded model: ' + innerErr.message);
                            }
                        }, undefined, function (error) {
                            showError('Failed to load preview.glb: ' + (error && error.message ? error.message : error));
                        });

                        window.addEventListener('resize', function () {
                            camera.aspect = window.innerWidth / window.innerHeight;
                            camera.updateProjectionMatrix();
                            renderer.setSize(window.innerWidth, window.innerHeight);
                        });

                        function animate() {
                            requestAnimationFrame(animate);
                            var delta = clock.getDelta();
                            if (mixer && !paused) mixer.update(delta);

                            for (var name in liveCorrections) {
                                var bone = bonesByName[name];
                                if (!bone) continue;

                                if (!paused) {
                                    // mixer.update() (or the static bind pose, if there's no
                                    // mixer) just gave this bone a clean, uncorrected rotation -
                                    // safe to snapshot as this frame's base.
                                    if (!baseQuaternions[name]) baseQuaternions[name] = bone.quaternion.clone();
                                    else baseQuaternions[name].copy(bone.quaternion);
                                } else if (!baseQuaternions[name]) {
                                    // Paused with no base yet (bone first touched while already
                                    // paused) - its current rotation hasn't been corrected, so
                                    // it's still clean.
                                    baseQuaternions[name] = bone.quaternion.clone();
                                }

                                bone.quaternion.copy(baseQuaternions[name]).premultiply(liveCorrections[name]);
                            }

                            controls.update();
                            renderer.render(scene, camera);
                        }
                        animate();
                    } catch (err) {
                        showError('Setup error: ' + err.message);
                    }
                </script>
            </body>
            </html>";

            _webView.CoreWebView2.NavigateToString(htmlContent);
        }

        private void PopulateAnimationList()
        {
            _animDropdown.Items.Add("None (Static Pose)");
            foreach (var anim in _model.LogicalAnimations)
                _animDropdown.Items.Add(anim.Name ?? $"Anim_{anim.LogicalIndex}");

            _animDropdown.SelectedIndex = _animDropdown.Items.Count > 1 ? 1 : 0;
        }

        private void OnAnimationSelected()
        {
            // Adjustments are per-animation, so switching to a different animation should not
            // carry over any not-yet-saved corrections dialed in against the previous one.
            ResetAllPendingOffsets();

            // A newly selected animation should start playing, not stay paused from whatever the
            // previous one was left at.
            _paused = false;
            _btnPause.Text = "Pause";

            if (!_viewerReady) return;

            var name = _animDropdown.SelectedIndex > 0 ? (string)_animDropdown.SelectedItem! : null;
            var script = name == null
                ? "setAnimationByName(null);"
                : $"setAnimationByName('{EscapeJs(name)}');";
            _webView.CoreWebView2.ExecuteScriptAsync(script);
            _webView.CoreWebView2.ExecuteScriptAsync("setPaused(false);");
        }

        private void TogglePause()
        {
            if (!_viewerReady) return;

            _paused = !_paused;
            _btnPause.Text = _paused ? "Resume" : "Pause";
            _webView.CoreWebView2.ExecuteScriptAsync(_paused ? "setPaused(true);" : "setPaused(false);");
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private (Label, TrackBar) MakeSlider(string text, int top)
        {
            var lbl = new Label { Text = $"{text}: 0°", Left = 12, Top = top, Width = 280, AutoSize = false };
            var slider = new TrackBar
            {
                Left = 12, Top = top + 18, Width = 280, Height = 45,
                Minimum = -180, Maximum = 180, Value = 0,
                TickFrequency = 30
            };
            slider.ValueChanged += (s, e) => { lbl.Text = $"{text}: {slider.Value}°"; ApplyCorrection(); };
            return (lbl, slider);
        }

        private void PopulateBoneList()
        {
            var names = _model.LogicalNodes
                .Where(n => !string.IsNullOrEmpty(n.Name))
                .Select(n => n.Name!)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();

            _boneDropdown.Items.AddRange(names);
            if (names.Length > 0) _boneDropdown.SelectedIndex = 0;
        }

        private static Quaternion ComputeOffsetQuaternion(int xDeg, int yDeg, int zDeg)
        {
            const float degToRad = MathF.PI / 180f;
            var rotX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, xDeg * degToRad);
            var rotY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, yDeg * degToRad);
            var rotZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, zDeg * degToRad);
            // Pre-multiplied (offset * original, not original * offset): this is a fixed
            // axis-convention correction, the same kind of fix this tool already applies
            // elsewhere for a rig's root axis mismatch - pre-multiplying is what keeps a
            // correction like this consistent regardless of the bone's own animated rotation,
            // verified empirically there (post-multiplying landed about 90 degrees off).
            return Quaternion.Normalize(Quaternion.Multiply(Quaternion.Multiply(rotZ, rotY), rotX));
        }

        private void OnBoneSelected()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;

            var (x, y, z) = _pendingOffsets.TryGetValue(boneName, out var pending) ? pending : (0, 0, 0);

            _suppressSliderEvents = true;
            _sliderX.Value = x;
            _sliderY.Value = y;
            _sliderZ.Value = z;
            _lblX.Text = $"X Rotation: {x}°";
            _lblY.Text = $"Y Rotation: {y}°";
            _lblZ.Text = $"Z Rotation: {z}°";
            _suppressSliderEvents = false;
            // Restores whatever not-yet-saved offset was already dialed in for this bone, so
            // switching away and back doesn't lose it - the live 3D view is left untouched too,
            // since it's already showing this same pending offset.
        }

        // Captures a bone's rotation data (its keys in the currently selected animation, or its
        // bind rotation for "None (Static Pose)") exactly once per session, before any correction
        // has touched it. Every later Save recomputes from this fixed baseline instead of from
        // whatever is currently in the model, so repeated saves don't stack the offset on itself.
        private (float Time, Quaternion Value)[] GetOrCacheOriginalKeys(string boneName, bool isStaticPose, Animation? anim, Node node)
        {
            if (_originalKeysCache.TryGetValue(boneName, out var cached)) return cached;

            (float Time, Quaternion Value)[] keys;
            var channel = isStaticPose ? null : anim!.Channels.FirstOrDefault(c => c.TargetNode == node && c.TargetNodePath == PropertyPath.rotation);
            if (channel != null)
            {
                var sampler = channel.GetRotationSampler();
                keys = sampler.GetLinearKeys().OrderBy(k => k.Key).Select(k => (k.Key, k.Value)).ToArray();
            }
            else
            {
                // Either the static-pose case, or this bone has no rotation channel in this
                // particular clip (it never moves in it) - either way, fall back to a single flat
                // keyframe at the bind rotation so the correction still takes effect.
                Matrix4x4.Decompose(node.LocalMatrix, out _, out var bind, out _);
                keys = new[] { (0f, Quaternion.Normalize(bind)) };
            }

            _originalKeysCache[boneName] = keys;
            return keys;
        }

        private void ResetCurrentBone()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;

            if (_originalKeysCache.TryGetValue(boneName, out var originalKeys))
            {
                var node = _model.LogicalNodes.First(n => n.Name == boneName);
                if (_animDropdown.SelectedIndex <= 0)
                    node.WithLocalRotation(originalKeys[0].Value);
                else
                    node.WithRotationAnimation((string)_animDropdown.SelectedItem!, originalKeys);
                _originalKeysCache.Remove(boneName);
            }

            _pendingOffsets.Remove(boneName);
            OnBoneSelected();

            if (_viewerReady)
                _webView.CoreWebView2.ExecuteScriptAsync($"setLiveCorrection('{EscapeJs(boneName)}', 0, 0, 0, 1);");
            _lblStatus.Text = "";
        }

        private void ResetAllPendingOffsets()
        {
            if (_viewerReady)
            {
                foreach (var boneName in _pendingOffsets.Keys)
                    _webView.CoreWebView2.ExecuteScriptAsync($"setLiveCorrection('{EscapeJs(boneName)}', 0, 0, 0, 1);");
            }
            _pendingOffsets.Clear();
            _originalKeysCache.Clear();

            if (_boneDropdown.SelectedItem is string) OnBoneSelected();
            _lblStatus.Text = "";
        }

        private void ApplyCorrection()
        {
            if (_suppressSliderEvents) return;
            if (_boneDropdown.SelectedItem is not string boneName) return;

            bool isStaticPose = _animDropdown.SelectedIndex <= 0;
            Animation? anim = isStaticPose ? null : _model.LogicalAnimations.First(a => a.Name == (string)_animDropdown.SelectedItem!);
            var node = _model.LogicalNodes.First(n => n.Name == boneName);
            // Capture the baseline now, before Save could ever overwrite the model, so it reflects
            // this bone's truly-original data for the current animation.
            GetOrCacheOriginalKeys(boneName, isStaticPose, anim, node);

            _pendingOffsets[boneName] = (_sliderX.Value, _sliderY.Value, _sliderZ.Value);
            _lblStatus.Text = "";

            var offset = ComputeOffsetQuaternion(_sliderX.Value, _sliderY.Value, _sliderZ.Value);

            // Mirrors the offset live onto the actual Three.js bone object - no file save or
            // reload involved, so this is instant and doesn't flicker. Nothing is written to the
            // underlying model yet; that only happens on "Save Adjustments to Animation".
            if (_viewerReady)
                _webView.CoreWebView2.ExecuteScriptAsync(
                    $"setLiveCorrection('{EscapeJs(boneName)}', {offset.X}, {offset.Y}, {offset.Z}, {offset.W});");
        }

        private void SaveAdjustments()
        {
            if (_pendingOffsets.Count == 0)
            {
                MessageBox.Show(this, "No joint adjustments to save.", "Fix Joint Orientation",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool isStaticPose = _animDropdown.SelectedIndex <= 0;
            string? animName = isStaticPose ? null : (string)_animDropdown.SelectedItem!;
            Animation? anim = isStaticPose ? null : _model.LogicalAnimations.First(a => a.Name == animName);

            foreach (var (boneName, degrees) in _pendingOffsets)
            {
                var offset = ComputeOffsetQuaternion(degrees.X, degrees.Y, degrees.Z);
                var node = _model.LogicalNodes.First(n => n.Name == boneName);
                var originalKeys = GetOrCacheOriginalKeys(boneName, isStaticPose, anim, node);

                if (isStaticPose)
                {
                    node.WithLocalRotation(Quaternion.Normalize(Quaternion.Multiply(offset, originalKeys[0].Value)));
                    continue;
                }

                var corrected = originalKeys.Select(k => (k.Time, Quaternion.Normalize(Quaternion.Multiply(offset, k.Value)))).ToArray();
                node.WithRotationAnimation(animName!, corrected);
            }

            // Deliberately does NOT reset sliders, the live preview, or pending offsets - the user
            // wants to keep dialing in a joint and re-save without losing their place. Everything
            // only resets when switching to a different animation (see OnAnimationSelected /
            // ResetAllPendingOffsets), since a correction is only ever meaningful relative to the
            // one specific animation (or bind pose) it was computed against.
            _lblStatus.Text = isStaticPose
                ? "Saved to bind (static) pose."
                : $"Saved to '{animName}'.";
        }
    }
}
