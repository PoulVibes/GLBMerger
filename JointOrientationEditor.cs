using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
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
    //
    // One of the four modes hosted by ModelEditorForm (see EditorMode there), which owns the
    // window chrome - the title, the sizing, and the single "Done" button shared by all four -
    // so this control only contributes its own left-hand controls and 3D preview.
    public class JointOrientationEditor : UserControl
    {
        private readonly ModelRoot _model;

        private ComboBox _boneDropdown = null!;
        private ComboBox _animDropdown = null!;
        private TrackBar _sliderX = null!, _sliderY = null!, _sliderZ = null!;
        private Label _lblX = null!, _lblY = null!, _lblZ = null!;
        private NumericUpDown _numPosX = null!, _numPosY = null!, _numPosZ = null!;
        private Label _lblPosX = null!, _lblPosY = null!, _lblPosZ = null!;
        // "Mirror" checkboxes next to each rotation slider / position field: when checked, moving
        // that axis also pushes a corresponding correction onto the opposite-side bone (e.g.
        // LeftArm -> RightArm), found by swapping Left/Right in the bone name. See
        // ApplyMirrorIfNeeded for the sign convention per axis.
        private CheckBox _chkMirrorX = null!, _chkMirrorY = null!, _chkMirrorZ = null!;
        private CheckBox _chkMirrorPosX = null!, _chkMirrorPosY = null!, _chkMirrorPosZ = null!;
        // "Inv" checkboxes, one per Mirror checkbox: flips that axis's default mirror sign
        // convention. Only has any effect while the paired Mirror checkbox is also checked.
        private CheckBox _chkInverseX = null!, _chkInverseY = null!, _chkInverseZ = null!;
        private CheckBox _chkInversePosX = null!, _chkInversePosY = null!, _chkInversePosZ = null!;
        // Only enabled when the selected bone is one of a multi-joint "Spine*" chain. When
        // checked, the dialed-in rotation on the selected spine joint is treated as the desired
        // TOTAL bend and is split evenly across every spine joint instead of applied to just the
        // one joint.
        private CheckBox _chkDistributeSpine = null!;
        private Label _lblStatus = null!;
        private Button _btnPause = null!;
        private WebView2 _webView = null!;
        private bool _viewerReady;
        private bool _paused;

        // Bone name -> not-yet-saved slider offset in degrees. Kept as the raw X/Y/Z slider
        // values (not a quaternion) so re-selecting a bone can restore the sliders exactly,
        // rather than trying to decompose a quaternion back into Euler angles.
        private readonly Dictionary<string, (int X, int Y, int Z)> _pendingOffsets = new();

        // Bone name -> not-yet-saved position offset in meters, one entry per axis - the
        // translation counterpart to _pendingOffsets above. Kept separate (rather than folded
        // into one struct) so a bone can have a pending rotation change, a pending position
        // change, or both, independently. Interpreted as a WORLD-space offset (see
        // ToParentLocalOffset) rather than raw local, so it holds a steady position even when
        // the bone's parent chain is itself animated.
        private readonly Dictionary<string, (float X, float Y, float Z)> _pendingTranslationOffsets = new();

        // Bone name -> its rotation keys (or bind rotation) exactly as they were before any
        // adjustment this "session" (i.e. since the bone was first touched after the last
        // animation swap). Saving re-derives the correction from this baseline every time rather
        // than from whatever is currently in the model, so hitting Save repeatedly while still
        // moving the slider doesn't compound the rotation onto itself.
        private readonly Dictionary<string, (float Time, Quaternion Value)[]> _originalKeysCache = new();

        // Translation counterpart to _originalKeysCache, same anti-compounding purpose.
        private readonly Dictionary<string, (float Time, Vector3 Value)[]> _originalTranslationKeysCache = new();

        // Bone name -> which rotation/position axes have "Mirror" checked for that bone, kept per
        // bone (like _pendingOffsets) so switching away and back restores the checkboxes exactly.
        private readonly Dictionary<string, (bool X, bool Y, bool Z)> _mirrorRotFlags = new();
        private readonly Dictionary<string, (bool X, bool Y, bool Z)> _mirrorPosFlags = new();

        // Bone name -> which axes have "Inv" checked, flipping that axis's default mirror sign.
        private readonly Dictionary<string, (bool X, bool Y, bool Z)> _mirrorRotInverseFlags = new();
        private readonly Dictionary<string, (bool X, bool Y, bool Z)> _mirrorPosInverseFlags = new();

        // Spine bone name -> whether "Distribute across spine" was checked while that bone was
        // selected.
        private readonly Dictionary<string, bool> _distributeSpineFlags = new();

        private bool _suppressSliderEvents;

        public JointOrientationEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;

            Dock = DockStyle.Fill;

            BuildUi();
            PopulateBoneList();
            PopulateAnimationList();

            // The 3D preview itself is a WebView2 rendering its own already-dark scene, so only
            // the surrounding WinForms control panel (sliders, dropdowns, buttons) needs theming.
            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            // AutoScroll so shrinking the window toward MinimumSize scrolls the panel instead of
            // clipping the bottom controls, now that there's a full extra row of position controls.
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 380, Padding = new Padding(12), AutoScroll = true };

            // Every control's Top is derived from the actual height of whatever was placed above
            // it, instead of a hardcoded constant - fixed gaps (478, 518, 556...) assumed each
            // control's height would always match whatever font/DPI they were tuned against.
            // Once buttons became AutoSize (to stop their own text clipping), they could grow
            // taller than those fixed gaps allowed for and start overlapping the control below.
            int y = 12;
            const int gap = 6;

            var lblBone = new Label { Text = "Joint / Bone:", Left = 12, Top = y, AutoSize = true };
            y += lblBone.Height + 2;
            _boneDropdown = new ComboBox { Left = 12, Top = y, Width = 280, DropDownStyle = ComboBoxStyle.DropDownList };
            _boneDropdown.SelectedIndexChanged += (s, e) => OnBoneSelected();
            y += _boneDropdown.Height + gap * 2;

            var lblAnim = new Label { Text = "Preview Animation:", Left = 12, Top = y, AutoSize = true };
            y += lblAnim.Height + 2;
            _animDropdown = new ComboBox { Left = 12, Top = y, Width = 190, DropDownStyle = ComboBoxStyle.DropDownList };
            _animDropdown.SelectedIndexChanged += (s, e) => OnAnimationSelected();

            // AutoSize + GrowOnly (rather than a fixed Width) so the button still fits its text
            // if the font/DPI differs from whatever this pixel width was tuned against, or when
            // it swaps to the longer "Resume" label - GrowOnly keeps the original width as a
            // floor instead of letting "Pause" shrink the button down from it.
            _btnPause = new Button { Text = "Pause", Left = 210, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(82, 0) };
            _btnPause.Click += (s, e) => TogglePause();
            y += Math.Max(_animDropdown.Height, _btnPause.Height) + gap * 2;

            (_lblX, _sliderX, _chkMirrorX, _chkInverseX) = MakeSlider("X Rotation", ref y);
            (_lblY, _sliderY, _chkMirrorY, _chkInverseY) = MakeSlider("Y Rotation", ref y);
            (_lblZ, _sliderZ, _chkMirrorZ, _chkInverseZ) = MakeSlider("Z Rotation", ref y);
            _chkMirrorX.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkMirrorY.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkMirrorZ.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkInverseX.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkInverseY.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkInverseZ.CheckedChanged += (s, e) => OnMirrorFlagChanged();

            // Only ever meaningful for a bone that's part of a multi-joint "Spine*" chain -
            // disabled/unchecked otherwise (see OnBoneSelected).
            _chkDistributeSpine = new CheckBox { Text = "Distribute turn across all spine joints", Left = 12, Top = y, AutoSize = true };
            _chkDistributeSpine.CheckedChanged += (s, e) => OnDistributeSpineFlagChanged();
            y += _chkDistributeSpine.Height + gap * 2;

            (_lblPosX, _numPosX, _chkMirrorPosX, _chkInversePosX) = MakeNumericPosition("X Position", ref y);
            (_lblPosY, _numPosY, _chkMirrorPosY, _chkInversePosY) = MakeNumericPosition("Y Position", ref y);
            (_lblPosZ, _numPosZ, _chkMirrorPosZ, _chkInversePosZ) = MakeNumericPosition("Z Position", ref y);
            _chkMirrorPosX.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkMirrorPosY.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkMirrorPosZ.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkInversePosX.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkInversePosY.CheckedChanged += (s, e) => OnMirrorFlagChanged();
            _chkInversePosZ.CheckedChanged += (s, e) => OnMirrorFlagChanged();

            y += gap;

            // AutoSize + GrowOnly for the same reason as _btnPause above: 280 stays the floor
            // width (matching the rest of this column) but grows instead of clipping if the
            // text needs more room than that under the runtime font/DPI.
            var btnSave = new Button { Text = "Save Adjustments to Animation", Left = 12, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(280, 0) };
            btnSave.Click += (s, e) => SaveAdjustments();
            y += btnSave.Height + gap;

            var btnReset = new Button { Text = "Reset This Joint", Left = 12, Top = y, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowOnly, MinimumSize = new System.Drawing.Size(280, 0) };
            btnReset.Click += (s, e) => ResetCurrentBone();
            y += btnReset.Height + gap;

            _lblStatus = new Label { Left = 12, Top = y, Width = 280, Height = 32, AutoSize = false, ForeColor = System.Drawing.Color.LightGreen };

            controlPanel.Controls.AddRange(new Control[]
            {
                lblBone, _boneDropdown, lblAnim, _animDropdown, _btnPause,
                _lblX, _sliderX, _chkMirrorX, _chkInverseX,
                _lblY, _sliderY, _chkMirrorY, _chkInverseY,
                _lblZ, _sliderZ, _chkMirrorZ, _chkInverseZ,
                _chkDistributeSpine,
                _lblPosX, _numPosX, _chkMirrorPosX, _chkInversePosX,
                _lblPosY, _numPosY, _chkMirrorPosY, _chkInversePosY,
                _lblPosZ, _numPosZ, _chkMirrorPosZ, _chkInversePosZ,
                btnSave, btnReset, _lblStatus
            });

            _webView = new WebView2 { Dock = DockStyle.Fill };

            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private async System.Threading.Tasks.Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this fire-and-forget
            // startup may still be mid-await, so both the await itself and everything after it have
            // to tolerate the control having gone away underneath them.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            string tempFolder = Path.GetTempPath();
            string previewPath = Path.Combine(tempFolder, "glbmerger_joint_preview.glb");
            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", tempFolder, CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.NavigationCompleted += (s, e) => _viewerReady = e.IsSuccess;

            // The model is only ever saved to disk *once*, just to give the loader something to
            // read - after that, every correction is applied directly to the loaded Three.js bone
            // object in the render loop, with no further saving or reloading. That's what makes
            // this live: model-viewer (used by the Animation Trim mode) only knows how to
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
                        // The currently-playing clip and its action, kept as direct references (not
                        // looked up via mixer's private/internal action list) so the correction
                        // loops below can sample track data straight from the clip regardless of
                        // what mixer.update() has or hasn't written into the live bone objects -
                        // see sampleTrackValue for why that live-property route isn't safe to trust.
                        var currentClip = null;
                        var currentAction = null;

                        // boneName -> THREE.Quaternion offset, re-applied on top of whatever the
                        // bone's rotation is *this frame* (bind pose, or animation-driven) - so it
                        // works identically whether an animation is playing or not, and moving a
                        // slider just changes what gets re-applied next frame, live.
                        var liveCorrections = {};
                        // boneName -> the bone's un-corrected rotation for this frame. Corrections
                        // are always applied on top of this rather than mutated in place, so
                        // pausing doesn't cause the same correction to be re-applied onto an
                        // already-corrected value every frame (which is what was making paused
                        // joints spin), and so a track whose value happens to be constant across
                        // the whole clip doesn't get its own already-corrected output fed back in
                        // as next frame's 'clean' base (which is what was making corrected joints
                        // drift/bounce instead of holding position - see sampleTrackValue).
                        var baseQuaternions = {};

                        // Position counterparts to liveCorrections/baseQuaternions above.
                        var liveTranslationCorrections = {};
                        var basePositions = {};

                        // Reads a bone's own animation-curve value directly from its KeyframeTrack
                        // via the track's interpolant, entirely bypassing bone.position/quaternion.
                        // Those live properties are NOT a safe source for 'the clean, uncorrected
                        // value this frame': mixer.update() only reliably refreshes a bone's
                        // property when the track's interpolated value actually *changes* between
                        // frames. For a track whose value is constant across every keyframe (the
                        // common case for a non-root bone's position, or any bone that simply
                        // doesn't move on some axis) mixer.update() leaves the property holding
                        // whatever this code last wrote there - so re-reading it back as 'this
                        // frame's clean base' was actually re-reading *last frame's own corrected
                        // output*, turning a constant offset into `position += delta` every frame
                        // forever (a runaway integrator) instead of a fixed correction. Sampling the
                        // interpolant directly sidesteps that entirely, since it depends only on the
                        // track's own keyframe data and the query time, never on what's currently
                        // sitting in the live property.
                        function sampleTrackValue(clip, boneName, suffix, time) {
                            if (!clip) return null;
                            for (var i = 0; i < clip.tracks.length; i++) {
                                var track = clip.tracks[i];
                                var lastDot = track.name.lastIndexOf('.');
                                if (lastDot === -1) continue;
                                if (track.name.substring(0, lastDot) !== boneName) continue;
                                if (track.name.substring(lastDot + 1) !== suffix) continue;
                                if (!track.__interpolant) track.__interpolant = track.createInterpolant();
                                return track.__interpolant.evaluate(time);
                            }
                            return null;
                        }

                        window.setLiveCorrection = function (boneName, x, y, z, w) {
                            if (!liveCorrections[boneName]) liveCorrections[boneName] = new THREE.Quaternion();
                            liveCorrections[boneName].set(x, y, z, w);
                        };

                        window.setLiveTranslationCorrection = function (boneName, x, y, z) {
                            if (!liveTranslationCorrections[boneName]) liveTranslationCorrections[boneName] = new THREE.Vector3();
                            liveTranslationCorrections[boneName].set(x, y, z);
                        };

                        window.setAnimationByName = function (name) {
                            baseQuaternions = {};
                            basePositions = {};
                            currentClip = null;
                            currentAction = null;
                            if (!mixer) return;
                            mixer.stopAllAction();
                            if (!name) return;
                            var clip = window._clips.filter(function (c) { return c.name === name; })[0];
                            if (clip) {
                                currentClip = clip;
                                currentAction = mixer.clipAction(clip);
                                currentAction.play();
                            }
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
                                    currentClip = window._clips[0];
                                    currentAction = mixer.clipAction(currentClip);
                                    currentAction.play();
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
                            var clipTime = currentAction ? currentAction.time : 0;

                            for (var name in liveCorrections) {
                                var bone = bonesByName[name];
                                if (!bone) continue;

                                if (!paused) {
                                    var sampledRot = sampleTrackValue(currentClip, name, 'quaternion', clipTime);
                                    if (sampledRot) {
                                        if (!baseQuaternions[name]) baseQuaternions[name] = new THREE.Quaternion();
                                        baseQuaternions[name].fromArray(sampledRot);
                                    } else if (!baseQuaternions[name]) {
                                        // No track for this bone in the current clip (e.g. an
                                        // un-keyframed hand/finger joint) or paused - fall back to
                                        // whatever the bone's rotation already is, once.
                                        baseQuaternions[name] = bone.quaternion.clone();
                                    }
                                } else if (!baseQuaternions[name]) {
                                    baseQuaternions[name] = bone.quaternion.clone();
                                }

                                bone.quaternion.copy(baseQuaternions[name]).premultiply(liveCorrections[name]);
                            }

                            for (var posName in liveTranslationCorrections) {
                                var posBone = bonesByName[posName];
                                if (!posBone) continue;

                                if (!paused) {
                                    var sampledPos = sampleTrackValue(currentClip, posName, 'position', clipTime);
                                    if (sampledPos) {
                                        if (!basePositions[posName]) basePositions[posName] = new THREE.Vector3();
                                        basePositions[posName].fromArray(sampledPos);
                                    } else if (!basePositions[posName]) {
                                        basePositions[posName] = posBone.position.clone();
                                    }
                                } else if (!basePositions[posName]) {
                                    basePositions[posName] = posBone.position.clone();
                                }

                                // The correction is a WORLD-space delta (e.g. 'always 10cm
                                // lower'), not a raw local one - added as-is, it would get
                                // dragged through whatever rotation the parent chain is doing
                                // that frame (a spine swaying with a breathing idle, say), making
                                // the offset visibly wobble instead of holding a steady position.
                                // Counter-rotating by the parent's current world orientation is
                                // what keeps it anchored in world space for the whole clip.
                                var localDelta = liveTranslationCorrections[posName].clone();
                                if (posBone.parent) {
                                    posBone.parent.updateWorldMatrix(true, false);
                                    var parentWorldQuat = new THREE.Quaternion();
                                    posBone.parent.getWorldQuaternion(parentWorldQuat);
                                    localDelta.applyQuaternion(parentWorldQuat.invert());
                                }

                                posBone.position.copy(basePositions[posName]).add(localDelta);
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

        private (Label, TrackBar, CheckBox, CheckBox) MakeSlider(string text, ref int y)
        {
            var lbl = new Label { Text = $"{text}: 0°", Left = 12, Top = y, AutoSize = true };
            // "Mirror" and "Inv" sit to the right of the label on the same row - the slider
            // itself is nearly the full panel width, so there's no room beside it.
            var chkMirror = new CheckBox { Text = "Mirror", Left = 200, Top = y, AutoSize = true };
            var chkInverse = new CheckBox { Text = "Inv", Left = 270, Top = y, AutoSize = true };
            y += lbl.Height + 2;
            var slider = new TrackBar
            {
                Left = 12, Top = y, Width = 280, Height = 45,
                Minimum = -180, Maximum = 180, Value = 0,
                TickFrequency = 30
            };
            y += slider.Height + 6;
            slider.ValueChanged += (s, e) => { lbl.Text = $"{text}: {slider.Value}°"; ApplyCorrection(); };
            return (lbl, slider, chkMirror, chkInverse);
        }

        // Position uses a NumericUpDown rather than a TrackBar like rotation does - joint nudges
        // need typed precision more than a slider does. Deliberately NOT scaled/labeled as meters:
        // a glTF's translation units are only "meters" by convention, and plenty of real files
        // (this app has already hit this with the "YOffset" per-animation column, which uses the
        // same unscaled +/-9999 range for the same reason) are authored at a completely different
        // scale - one test rig has its whole skeleton on the order of 100+ units tall, where a
        // +/-5 range would be too small to ever produce a visible correction. Left unitless and
        // wide so it works regardless of the model's own scale; the user can always type a precise
        // value even though the increment steps coarsely.
        private (Label, NumericUpDown, CheckBox, CheckBox) MakeNumericPosition(string text, ref int y)
        {
            var lbl = new Label { Text = $"{text}: 0", Left = 12, Top = y, AutoSize = true };
            y += lbl.Height + 2;
            var numeric = new NumericUpDown
            {
                Left = 12, Top = y, Width = 120,
                Minimum = -9999m, Maximum = 9999m, DecimalPlaces = 3, Increment = 0.1m, Value = 0m
            };
            // The numeric field is only 120px wide (unlike the near-full-width rotation slider),
            // so "Mirror"/"Inv" fit beside it instead of needing the label's row.
            var chkMirror = new CheckBox { Text = "Mirror", Left = 142, Top = y + 2, AutoSize = true };
            var chkInverse = new CheckBox { Text = "Inv", Left = 212, Top = y + 2, AutoSize = true };
            y += numeric.Height + 6;
            numeric.ValueChanged += (s, e) => { lbl.Text = $"{text}: {numeric.Value}"; ApplyCorrection(); };
            return (lbl, numeric, chkMirror, chkInverse);
        }

        // Only nodes that actually act as a skin's joints are real bones - every other named
        // node in the file is geometry (a mesh-bearing part, or an empty grouping node) and has
        // no orientation to "fix". Listing every LogicalNode used to mix real bones in with those
        // geometry names, which is what showed up as clutter in this dropdown.
        private void PopulateBoneList()
        {
            var jointNames = new HashSet<string>();
            foreach (var skin in _model.LogicalSkins)
                for (int i = 0; i < skin.JointsCount; i++)
                {
                    var (jointNode, _) = skin.GetJoint(i);
                    if (!string.IsNullOrEmpty(jointNode.Name))
                        jointNames.Add(jointNode.Name);
                }

            var names = jointNames.OrderBy(n => n).ToArray();

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

        // Converts a desired WORLD-space offset into the local-space vector that, once composed
        // through this bone's animated parent chain, reproduces that same world-space
        // displacement at the given animation time. A bone's local translation only ever means
        // "however far along my parent's current axes" - if any ancestor rotates over the course
        // of the clip (a spine swaying with a breathing idle, say), a plain constant local offset
        // gets dragged along for that rotation and visibly wobbles instead of holding a steady
        // position. This undoes exactly that rotation so the offset stays fixed in world space
        // for the whole animation, not just at the frame it happened to be dialed in against.
        private static Vector3 ToParentLocalOffset(Node node, Animation? anim, float time, Vector3 worldOffset)
        {
            if (node.VisualParent == null) return worldOffset;

            var parentWorldRotation = GetWorldRotationAt(node.VisualParent, anim, time);
            return Vector3.Transform(worldOffset, Quaternion.Inverse(parentWorldRotation));
        }

        // Cumulative world rotation of `node` at `time` in `anim` (or the bind pose if anim is
        // null) - composes every ancestor's own local rotation at that same time, root-first,
        // the same way the glTF node hierarchy itself composes world transforms.
        private static Quaternion GetWorldRotationAt(Node node, Animation? anim, float time)
        {
            var local = GetLocalRotationAt(node, anim, time);
            return node.VisualParent == null
                ? local
                : Quaternion.Normalize(Quaternion.Multiply(GetWorldRotationAt(node.VisualParent, anim, time), local));
        }

        private static Quaternion GetLocalRotationAt(Node node, Animation? anim, float time)
        {
            var channel = anim?.Channels.FirstOrDefault(c => c.TargetNode == node && c.TargetNodePath == PropertyPath.rotation);
            if (channel == null)
            {
                Matrix4x4.Decompose(node.LocalMatrix, out _, out var bind, out _);
                return Quaternion.Normalize(bind);
            }

            var keys = channel.GetRotationSampler().GetLinearKeys().OrderBy(k => k.Key).ToArray();
            return Quaternion.Normalize(SampleQuaternionAt(keys, time));
        }

        // Simple linear-time lookup + Slerp between the bracketing keys - the rotation channels
        // read here are typically a few hundred keys at most (one per animation frame), so this
        // doesn't need anything fancier than a scan for a Save that runs once per click.
        private static Quaternion SampleQuaternionAt((float Time, Quaternion Value)[] sortedKeys, float time)
        {
            if (sortedKeys.Length == 0) return Quaternion.Identity;
            if (time <= sortedKeys[0].Time) return sortedKeys[0].Value;
            if (time >= sortedKeys[^1].Time) return sortedKeys[^1].Value;

            for (int i = 0; i < sortedKeys.Length - 1; i++)
            {
                if (time < sortedKeys[i + 1].Time)
                {
                    float span = sortedKeys[i + 1].Time - sortedKeys[i].Time;
                    float t = span > 0f ? (time - sortedKeys[i].Time) / span : 0f;
                    return Quaternion.Slerp(sortedKeys[i].Value, sortedKeys[i + 1].Value, t);
                }
            }
            return sortedKeys[^1].Value;
        }

        // Finds the opposite-side counterpart of a bone by swapping "Left"/"Right" in its name
        // (the Mixamo-style convention this app's rig handling already assumes elsewhere - see
        // the LegChains table in GlbMergeService). Returns null for bones with no such pair (e.g.
        // Spine, Hips, Head).
        private static string? GetMirrorBoneName(string boneName)
        {
            if (boneName.Contains("Left", StringComparison.Ordinal)) return boneName.Replace("Left", "Right");
            if (boneName.Contains("Right", StringComparison.Ordinal)) return boneName.Replace("Right", "Left");
            return null;
        }

        private static readonly Regex SpineBoneNamePattern = new(@"^Spine\d*$");

        private static bool IsSpineBone(string boneName) => SpineBoneNamePattern.IsMatch(boneName);

        // All "Spine", "Spine1", "Spine2", ... joints present in this model's skin, in chain
        // order (base of the spine first).
        private List<string> GetSpineBoneNames() =>
            _boneDropdown.Items.Cast<string>()
                .Where(IsSpineBone)
                .OrderBy(n => n.Length > 5 ? int.Parse(n.Substring(5)) : 0)
                .ToList();

        private void OnBoneSelected()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;

            var (x, y, z) = _pendingOffsets.TryGetValue(boneName, out var pending) ? pending : (0, 0, 0);
            var (px, py, pz) = _pendingTranslationOffsets.TryGetValue(boneName, out var pendingPos) ? pendingPos : (0f, 0f, 0f);
            var (mrx, mry, mrz) = _mirrorRotFlags.TryGetValue(boneName, out var mirrorRot) ? mirrorRot : (false, false, false);
            var (mpx, mpy, mpz) = _mirrorPosFlags.TryGetValue(boneName, out var mirrorPos) ? mirrorPos : (false, false, false);
            var (irx, iry, irz) = _mirrorRotInverseFlags.TryGetValue(boneName, out var invRot) ? invRot : (false, false, false);
            var (ipx, ipy, ipz) = _mirrorPosInverseFlags.TryGetValue(boneName, out var invPos) ? invPos : (false, false, false);
            bool isDistributableSpineBone = IsSpineBone(boneName) && GetSpineBoneNames().Count > 1;

            _suppressSliderEvents = true;
            _sliderX.Value = x;
            _sliderY.Value = y;
            _sliderZ.Value = z;
            _lblX.Text = $"X Rotation: {x}°";
            _lblY.Text = $"Y Rotation: {y}°";
            _lblZ.Text = $"Z Rotation: {z}°";
            _chkMirrorX.Checked = mrx;
            _chkMirrorY.Checked = mry;
            _chkMirrorZ.Checked = mrz;
            _chkMirrorPosX.Checked = mpx;
            _chkMirrorPosY.Checked = mpy;
            _chkMirrorPosZ.Checked = mpz;
            _chkInverseX.Checked = irx;
            _chkInverseY.Checked = iry;
            _chkInverseZ.Checked = irz;
            _chkInversePosX.Checked = ipx;
            _chkInversePosY.Checked = ipy;
            _chkInversePosZ.Checked = ipz;
            _chkDistributeSpine.Enabled = isDistributableSpineBone;
            _chkDistributeSpine.Checked = isDistributableSpineBone
                && _distributeSpineFlags.TryGetValue(boneName, out var distribute) && distribute;
            // Clamped defensively - pending values only ever come from these same controls, but a
            // value sitting exactly on the NumericUpDown's Min/Max boundary can round the wrong way
            // through the float/decimal conversion and throw when assigned back.
            _numPosX.Value = Math.Clamp((decimal)px, _numPosX.Minimum, _numPosX.Maximum);
            _numPosY.Value = Math.Clamp((decimal)py, _numPosY.Minimum, _numPosY.Maximum);
            _numPosZ.Value = Math.Clamp((decimal)pz, _numPosZ.Minimum, _numPosZ.Maximum);
            _lblPosX.Text = $"X Position: {px}";
            _lblPosY.Text = $"Y Position: {py}";
            _lblPosZ.Text = $"Z Position: {pz}";
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

        // Translation counterpart to GetOrCacheOriginalKeys - same once-per-session baseline
        // capture, same bind-pose fallback when the bone has no translation channel in this clip
        // (the common case for anything that isn't the animation's root motion bone).
        private (float Time, Vector3 Value)[] GetOrCacheOriginalTranslationKeys(string boneName, bool isStaticPose, Animation? anim, Node node)
        {
            if (_originalTranslationKeysCache.TryGetValue(boneName, out var cached)) return cached;

            (float Time, Vector3 Value)[] keys;
            var channel = isStaticPose ? null : anim!.Channels.FirstOrDefault(c => c.TargetNode == node && c.TargetNodePath == PropertyPath.translation);
            if (channel != null)
            {
                var sampler = channel.GetTranslationSampler();
                keys = sampler.GetLinearKeys().OrderBy(k => k.Key).Select(k => (k.Key, k.Value)).ToArray();
            }
            else
            {
                Matrix4x4.Decompose(node.LocalMatrix, out _, out _, out var bindTranslation);
                keys = new[] { (0f, bindTranslation) };
            }

            _originalTranslationKeysCache[boneName] = keys;
            return keys;
        }

        private void ResetCurrentBone()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;

            var node = _model.LogicalNodes.First(n => n.Name == boneName);

            if (_originalKeysCache.TryGetValue(boneName, out var originalKeys))
            {
                if (_animDropdown.SelectedIndex <= 0)
                    node.WithLocalRotation(originalKeys[0].Value);
                else
                    node.WithRotationAnimation((string)_animDropdown.SelectedItem!, originalKeys);
                _originalKeysCache.Remove(boneName);
            }

            if (_originalTranslationKeysCache.TryGetValue(boneName, out var originalTranslationKeys))
            {
                if (_animDropdown.SelectedIndex <= 0)
                    node.WithLocalTranslation(originalTranslationKeys[0].Value);
                else
                    node.WithTranslationAnimation((string)_animDropdown.SelectedItem!, originalTranslationKeys);
                _originalTranslationKeysCache.Remove(boneName);
            }

            _pendingOffsets.Remove(boneName);
            _pendingTranslationOffsets.Remove(boneName);
            _mirrorRotFlags.Remove(boneName);
            _mirrorPosFlags.Remove(boneName);
            _mirrorRotInverseFlags.Remove(boneName);
            _mirrorPosInverseFlags.Remove(boneName);
            _distributeSpineFlags.Remove(boneName);
            OnBoneSelected();

            if (_viewerReady)
            {
                _webView.CoreWebView2.ExecuteScriptAsync($"setLiveCorrection('{EscapeJs(boneName)}', 0, 0, 0, 1);");
                _webView.CoreWebView2.ExecuteScriptAsync($"setLiveTranslationCorrection('{EscapeJs(boneName)}', 0, 0, 0);");
            }
            _lblStatus.Text = "";
        }

        private void ResetAllPendingOffsets()
        {
            if (_viewerReady)
            {
                foreach (var boneName in _pendingOffsets.Keys)
                    _webView.CoreWebView2.ExecuteScriptAsync($"setLiveCorrection('{EscapeJs(boneName)}', 0, 0, 0, 1);");
                foreach (var boneName in _pendingTranslationOffsets.Keys)
                    _webView.CoreWebView2.ExecuteScriptAsync($"setLiveTranslationCorrection('{EscapeJs(boneName)}', 0, 0, 0);");
            }
            _pendingOffsets.Clear();
            _originalKeysCache.Clear();
            _pendingTranslationOffsets.Clear();
            _originalTranslationKeysCache.Clear();
            _mirrorRotFlags.Clear();
            _mirrorPosFlags.Clear();
            _mirrorRotInverseFlags.Clear();
            _mirrorPosInverseFlags.Clear();
            _distributeSpineFlags.Clear();

            if (_boneDropdown.SelectedItem is string) OnBoneSelected();
            _lblStatus.Text = "";
        }

        // ExecuteScriptAsync builds a literal JS number from this - $"{value}" would use the
        // current culture (e.g. "0,125" with a comma on many non-US locales), which is not valid
        // JS syntax and would silently corrupt the script. Always format invariant.
        private static string Inv(float v) => v.ToString(CultureInfo.InvariantCulture);

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
            GetOrCacheOriginalTranslationKeys(boneName, isStaticPose, anim, node);

            _pendingTranslationOffsets[boneName] = ((float)_numPosX.Value, (float)_numPosY.Value, (float)_numPosZ.Value);
            _lblStatus.Text = "";

            if (_chkDistributeSpine.Enabled && _chkDistributeSpine.Checked)
                ApplyDistributedSpineRotation(isStaticPose, anim);
            else
                ApplySingleBoneRotation(boneName, _sliderX.Value, _sliderY.Value, _sliderZ.Value);

            // Mirrors the offset live onto the actual Three.js bone object - no file save or
            // reload involved, so this is instant and doesn't flicker. Nothing is written to the
            // underlying model yet; that only happens on "Save Adjustments to Animation".
            if (_viewerReady)
            {
                _webView.CoreWebView2.ExecuteScriptAsync(
                    $"setLiveTranslationCorrection('{EscapeJs(boneName)}', {Inv((float)_numPosX.Value)}, {Inv((float)_numPosY.Value)}, {Inv((float)_numPosZ.Value)});");
            }

            ApplyMirrorIfNeeded(boneName, isStaticPose, anim);
        }

        // Sets one bone's pending rotation offset and pushes it to the live preview. Shared by the
        // normal single-joint path, the spine-distribute path, and the mirror path below - all
        // three ultimately just want "this bone gets this X/Y/Z degrees applied".
        private void ApplySingleBoneRotation(string boneName, int xDeg, int yDeg, int zDeg)
        {
            _pendingOffsets[boneName] = (xDeg, yDeg, zDeg);
            if (!_viewerReady) return;

            var offset = ComputeOffsetQuaternion(xDeg, yDeg, zDeg);
            _webView.CoreWebView2.ExecuteScriptAsync(
                $"setLiveCorrection('{EscapeJs(boneName)}', {Inv(offset.X)}, {Inv(offset.Y)}, {Inv(offset.Z)}, {Inv(offset.W)});");
        }

        // Treats the selected spine joint's dialed-in X/Y/Z as the TOTAL desired bend and splits
        // it evenly across every Spine* joint in the model, so turning one joint's slider bends
        // the whole spine chain by roughly that amount rather than just the one joint.
        private void ApplyDistributedSpineRotation(bool isStaticPose, Animation? anim)
        {
            var spineNames = GetSpineBoneNames();
            int n = spineNames.Count;
            int dx = (int)Math.Round(_sliderX.Value / (double)n, MidpointRounding.AwayFromZero);
            int dy = (int)Math.Round(_sliderY.Value / (double)n, MidpointRounding.AwayFromZero);
            int dz = (int)Math.Round(_sliderZ.Value / (double)n, MidpointRounding.AwayFromZero);

            foreach (var spineName in spineNames)
            {
                var spineNode = _model.LogicalNodes.First(nd => nd.Name == spineName);
                GetOrCacheOriginalKeys(spineName, isStaticPose, anim, spineNode);
                ApplySingleBoneRotation(spineName, dx, dy, dz);
            }
        }

        // Propagates the current axis values onto the opposite-side bone for whichever axes have
        // "Mirror" checked, leaving that bone's other (unchecked) axes untouched. Rotation mirrors
        // using the standard biped "Behavior" convention (same as Maya's Mirror Joint): the X
        // (bend) axis is copied as-is, while Y/Z (twist/splay) are sign-flipped, since Left/Right
        // joint pairs are typically authored as mirror images of each other. Position is mirrored
        // by negating only the lateral (X) world-space component and keeping Y/Z as-is, since this
        // app's rigs are Y-up/Z-forward with X running left-right (see ToParentLocalOffset for why
        // position offsets are world-space to begin with).
        private void ApplyMirrorIfNeeded(string boneName, bool isStaticPose, Animation? anim)
        {
            var mirrorName = GetMirrorBoneName(boneName);
            if (mirrorName == null || !_boneDropdown.Items.Contains(mirrorName)) return;

            (bool X, bool Y, bool Z) rotFlags = _mirrorRotFlags.TryGetValue(boneName, out var mr) ? mr : (false, false, false);
            (bool X, bool Y, bool Z) posFlags = _mirrorPosFlags.TryGetValue(boneName, out var mp) ? mp : (false, false, false);
            if (!rotFlags.X && !rotFlags.Y && !rotFlags.Z && !posFlags.X && !posFlags.Y && !posFlags.Z) return;

            (bool X, bool Y, bool Z) rotInverse = _mirrorRotInverseFlags.TryGetValue(boneName, out var ir) ? ir : (false, false, false);
            (bool X, bool Y, bool Z) posInverse = _mirrorPosInverseFlags.TryGetValue(boneName, out var ip) ? ip : (false, false, false);

            var mirrorNode = _model.LogicalNodes.First(n => n.Name == mirrorName);

            if (rotFlags.X || rotFlags.Y || rotFlags.Z)
            {
                GetOrCacheOriginalKeys(mirrorName, isStaticPose, anim, mirrorNode);
                var (mx, my, mz) = _pendingOffsets.TryGetValue(mirrorName, out var existingRot) ? existingRot : (0, 0, 0);
                // Default "Behavior" mirror sign per axis - X (bend) copied as-is, Y/Z
                // (twist/splay) sign-flipped. "Inv" flips that default for just its own axis.
                if (rotFlags.X) mx = rotInverse.X ? -_sliderX.Value : _sliderX.Value;
                if (rotFlags.Y) my = rotInverse.Y ? _sliderY.Value : -_sliderY.Value;
                if (rotFlags.Z) mz = rotInverse.Z ? _sliderZ.Value : -_sliderZ.Value;
                ApplySingleBoneRotation(mirrorName, mx, my, mz);
            }

            if (posFlags.X || posFlags.Y || posFlags.Z)
            {
                GetOrCacheOriginalTranslationKeys(mirrorName, isStaticPose, anim, mirrorNode);
                var (mpx, mpy, mpz) = _pendingTranslationOffsets.TryGetValue(mirrorName, out var existingPos) ? existingPos : (0f, 0f, 0f);
                // Default mirror sign per axis - lateral X negated, Y/Z copied as-is. "Inv"
                // flips that default for just its own axis.
                if (posFlags.X) mpx = posInverse.X ? (float)_numPosX.Value : -(float)_numPosX.Value;
                if (posFlags.Y) mpy = posInverse.Y ? -(float)_numPosY.Value : (float)_numPosY.Value;
                if (posFlags.Z) mpz = posInverse.Z ? -(float)_numPosZ.Value : (float)_numPosZ.Value;
                _pendingTranslationOffsets[mirrorName] = (mpx, mpy, mpz);

                if (_viewerReady)
                    _webView.CoreWebView2.ExecuteScriptAsync(
                        $"setLiveTranslationCorrection('{EscapeJs(mirrorName)}', {Inv(mpx)}, {Inv(mpy)}, {Inv(mpz)});");
            }
        }

        private void OnMirrorFlagChanged()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;
            _mirrorRotFlags[boneName] = (_chkMirrorX.Checked, _chkMirrorY.Checked, _chkMirrorZ.Checked);
            _mirrorPosFlags[boneName] = (_chkMirrorPosX.Checked, _chkMirrorPosY.Checked, _chkMirrorPosZ.Checked);
            _mirrorRotInverseFlags[boneName] = (_chkInverseX.Checked, _chkInverseY.Checked, _chkInverseZ.Checked);
            _mirrorPosInverseFlags[boneName] = (_chkInversePosX.Checked, _chkInversePosY.Checked, _chkInversePosZ.Checked);
            ApplyCorrection();
        }

        private void OnDistributeSpineFlagChanged()
        {
            if (_boneDropdown.SelectedItem is not string boneName) return;
            _distributeSpineFlags[boneName] = _chkDistributeSpine.Checked;
            ApplyCorrection();
        }

        private void SaveAdjustments()
        {
            if (_pendingOffsets.Count == 0 && _pendingTranslationOffsets.Count == 0)
            {
                MessageBox.Show(this, "No joint adjustments to save.", "Fix Joint Orientation",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            bool isStaticPose = _animDropdown.SelectedIndex <= 0;
            string? animName = isStaticPose ? null : (string)_animDropdown.SelectedItem!;
            Animation? anim = isStaticPose ? null : _model.LogicalAnimations.First(a => a.Name == animName);

            // A bone can have a pending rotation offset, a pending position offset, or both -
            // ApplyCorrection always sets both dictionaries together, so in practice this union is
            // just whichever bones have been touched at all, but keeping them independent here
            // means neither kind of adjustment is silently skipped if that ever changes.
            var affectedBones = new HashSet<string>(_pendingOffsets.Keys);
            affectedBones.UnionWith(_pendingTranslationOffsets.Keys);

            foreach (var boneName in affectedBones)
            {
                var node = _model.LogicalNodes.First(n => n.Name == boneName);

                if (_pendingOffsets.TryGetValue(boneName, out var degrees))
                {
                    var offset = ComputeOffsetQuaternion(degrees.X, degrees.Y, degrees.Z);
                    var originalKeys = GetOrCacheOriginalKeys(boneName, isStaticPose, anim, node);

                    if (isStaticPose)
                        node.WithLocalRotation(Quaternion.Normalize(Quaternion.Multiply(offset, originalKeys[0].Value)));
                    else
                    {
                        var corrected = originalKeys.Select(k => (k.Time, Quaternion.Normalize(Quaternion.Multiply(offset, k.Value)))).ToArray();
                        node.WithRotationAnimation(animName!, corrected);
                    }
                }

                if (_pendingTranslationOffsets.TryGetValue(boneName, out var posOffset))
                {
                    // Treated as a WORLD-space delta (e.g. "always 10cm lower"), not a raw local
                    // one - see ToParentLocalOffset for why a plain local add wobbles when the
                    // bone's parent chain is itself animated (a spine swaying with a breathing
                    // idle, say).
                    var worldOffset = new Vector3(posOffset.X, posOffset.Y, posOffset.Z);
                    var originalTranslationKeys = GetOrCacheOriginalTranslationKeys(boneName, isStaticPose, anim, node);

                    if (isStaticPose)
                    {
                        var localDelta = ToParentLocalOffset(node, null, 0f, worldOffset);
                        node.WithLocalTranslation(originalTranslationKeys[0].Value + localDelta);
                    }
                    else
                    {
                        var corrected = originalTranslationKeys
                            .Select(k => (k.Time, k.Value + ToParentLocalOffset(node, anim, k.Time, worldOffset)))
                            .ToArray();
                        node.WithTranslationAnimation(animName!, corrected);
                    }
                }
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
