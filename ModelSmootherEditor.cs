using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbMerger
{
    // Paints over bumps or spikes on the merged model's surface and smooths them out live, as the
    // brush drags - unlike GeometryOptimizerEditor's paint-then-Apply tool, there is no separate
    // "bake" step here: every frame the brush is held over the surface it runs one Laplacian
    // relaxation pass on the triangles underneath it, so the deformation itself is the feedback.
    //
    // That real-time requirement is also why this editor renders the model itself with a
    // hand-rolled Three.js scene (GLTFLoader + OrbitControls, the same combination
    // ModelAdjusterEditor uses) instead of <model-viewer> the way GeometryOptimizerEditor does.
    // <model-viewer> never exposes its internal geometry for editing - GeometryOptimizerEditor
    // works around that by keeping a second, invisible Three.js parse purely for hit-testing and
    // drawing everything paint-related into a camera-synced overlay canvas on top. That trick can
    // answer "which triangles does the brush cover" but it cannot move a vertex the visible model
    // actually has, which is the one thing a smoothing brush needs to do every frame. Owning the
    // renderer directly means the mesh being picked, deformed and displayed are all the same
    // object - no overlay canvas, no camera sync, no second geometry parse.
    //
    // The smoothing math mirrors what a non-interactive version would do in .NET (see
    // ModelSmoother's class comment): vertices are welded on position only, so every split vertex
    // sitting at one physical point - a UV seam, a hard edge - moves together and no seam tears
    // open; unpainted neighbours are never themselves moved, so they anchor the brushed region's
    // edge; and normals are recomputed only for vertices whose own triangle (or one sharing a
    // corner with the brushed region) actually changed shape, so untouched shading elsewhere is
    // never disturbed. It runs here, in JavaScript, against the live position buffer, because doing
    // it once in .NET after the fact is exactly the workflow being replaced.
    //
    // A stroke's result is sent back to .NET - via ApplyStroke, see OnWebMessageReceived - only
    // once, on pointer-up. Not per frame: the drag itself is entirely local to the browser's own
    // GPU buffers, and marshalling a whole primitive's vertex data across the WebView2 boundary
    // sixty times a second would be the one thing slow enough to break the "real time" this editor
    // exists for.
    //
    // One of the modes hosted by ModelEditorForm (see EditorMode there).
    public class ModelSmootherEditor : UserControl
    {
        private readonly ModelRoot _model;

        private WebView2 _webView = null!;
        private CheckBox _chkPaintMode = null!;
        private TrackBar _sliderBrush = null!, _sliderStrength = null!;
        private Label _lblBrush = null!, _lblStrength = null!, _lblStatus = null!;
        private Button _btnRevert = null!;

        private bool _viewerReady;
        private int _previewVersion;
        private string? _previewPath;

        // Taken once, immediately before the first stroke of the session - the model is shared with
        // every other editor mode and nothing else keeps a copy of the original geometry.
        private List<ModelSmoother.PositionSnapshot>? _originalPositions;

        public ModelSmootherEditor(ModelRoot model, bool darkMode = false)
        {
            _model = model;

            Dock = DockStyle.Fill;

            BuildUi();

            ThemeManager.Apply(this, darkMode);

            _ = InitializeViewerAsync();
        }

        private void BuildUi()
        {
            var controlPanel = new Panel { Dock = DockStyle.Left, Width = 380, AutoScroll = true };

            var flow = new FlowLayoutPanel
            {
                Dock = DockStyle.Top,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(12),
            };

            flow.Controls.Add(new Label
            {
                Text = "Model Smoother",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 8),
            });

            flow.Controls.Add(HelpText(
                "Drag over a bump or spike on the surface - it relaxes toward its surroundings live, " +
                "for as long as the brush is held over it. Release to commit the stroke; the model " +
                "you see is always the model that gets saved."));

            _chkPaintMode = new CheckBox
            {
                Text = "Paint mode (drag to smooth; off = orbit only)",
                AutoSize = true,
                MaximumSize = new System.Drawing.Size(340, 0),
                Checked = true,
                Margin = new Padding(3, 0, 3, 4),
            };
            _chkPaintMode.CheckedChanged += (s, e) => PushPaintMode();
            flow.Controls.Add(_chkPaintMode);

            _lblBrush = new Label { Text = "Brush size: 5%", AutoSize = true, Margin = new Padding(3, 0, 3, 0) };
            _sliderBrush = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 1, Maximum = 40, Value = 5,
                TickFrequency = 5, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderBrush.ValueChanged += (s, e) => { _lblBrush.Text = $"Brush size: {_sliderBrush.Value}%"; PushBrushRadius(); };
            flow.Controls.Add(_lblBrush);
            flow.Controls.Add(_sliderBrush);

            _lblStrength = new Label { Text = "Strength: 30%", AutoSize = true, Margin = new Padding(3, 4, 3, 0) };
            _sliderStrength = new TrackBar
            {
                Width = 330, Height = 45, Minimum = 1, Maximum = 100, Value = 30,
                TickFrequency = 10, Margin = new Padding(3, 0, 3, 4),
            };
            _sliderStrength.ValueChanged += (s, e) => { _lblStrength.Text = $"Strength: {_sliderStrength.Value}%"; PushStrength(); };
            flow.Controls.Add(_lblStrength);
            flow.Controls.Add(_sliderStrength);

            flow.Controls.Add(HelpText(
                "How far the surface moves toward its neighbours per frame while the brush is held. " +
                "It runs continuously, not once per stroke - low strength held a moment is gentle, " +
                "high strength (or holding longer) flattens fast and can round off nearby detail."));

            _btnRevert = MakeButton("Revert Smoothing");
            _btnRevert.Enabled = false;
            _btnRevert.Click += (s, e) => RevertSmoothing();
            flow.Controls.Add(_btnRevert);

            flow.Controls.Add(HelpText(
                "Each stroke moves vertex positions (and recomputes normals nearby) as soon as you " +
                "release the brush - triangle count, UVs and skinning are never touched. Included the " +
                "next time you save the merge."));

            _lblStatus = new Label
            {
                AutoSize = true, MaximumSize = new System.Drawing.Size(340, 0),
                Margin = new Padding(3, 8, 3, 6), ForeColor = System.Drawing.Color.LightGreen,
            };
            flow.Controls.Add(_lblStatus);

            controlPanel.Controls.Add(flow);

            _webView = new WebView2 { Dock = DockStyle.Fill };
            Controls.Add(_webView);
            Controls.Add(controlPanel);
        }

        private static Label HelpText(string text) => new Label
        {
            Text = text,
            AutoSize = true,
            MaximumSize = new System.Drawing.Size(340, 0),
            Margin = new Padding(3, 0, 3, 12),
            ForeColor = System.Drawing.Color.Gray,
        };

        private static Button MakeButton(string text) => new Button
        {
            Text = text,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowOnly,
            MinimumSize = new System.Drawing.Size(330, 0),
            Margin = new Padding(3, 3, 3, 3),
        };

        private void RevertSmoothing()
        {
            if (_originalPositions == null) return;

            ModelSmoother.RestorePositions(_model, _originalPositions);
            _originalPositions = null;
            _btnRevert.Enabled = false;

            _lblStatus.Text = "Smoothing reverted to the original merge result.";
            ReloadPreview();
        }

        private async Task InitializeViewerAsync()
        {
            // Switching the editor's mode dropdown disposes this control while this fire-and-forget
            // startup may still be mid-await, so both the await itself and everything after it have
            // to tolerate that.
            try
            {
                await _webView.EnsureCoreWebView2Async(null);
            }
            catch (ObjectDisposedException) { return; }
            if (IsDisposed || _webView.IsDisposed) return;

            _webView.CoreWebView2.SetVirtualHostNameToFolderMapping("appassets.local", Path.GetTempPath(), CoreWebView2HostResourceAccessKind.Allow);
            _webView.CoreWebView2.WebMessageReceived += OnWebMessageReceived;

            string previewFileName = WritePreviewFile();

            string htmlContent = @"
            <!DOCTYPE html>
            <html lang='en'>
            <head>
                <meta charset='UTF-8'>
                <script crossorigin='anonymous' src='https://cdn.jsdelivr.net/npm/three@0.128.0/build/three.min.js'></script>
                <script crossorigin='anonymous' src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <script crossorigin='anonymous' src='https://cdn.jsdelivr.net/npm/three@0.128.0/examples/js/controls/OrbitControls.js'></script>
                <style>
                    body, html { margin: 0; padding: 0; width: 100%; height: 100%; overflow: hidden; background: #23272a; }
                    #viewerStack { position: relative; width: 100%; height: 100%; }
                    #viewport { width: 100%; height: 100%; display: block; }
                    #paintOverlay {
                        position: absolute; top: 0; left: 0; width: 100%; height: 100%;
                        pointer-events: none; cursor: crosshair; touch-action: none;
                    }
                    #error-overlay {
                        position: absolute; top: 10px; left: 10px; right: 10px;
                        background: rgba(139, 0, 0, 0.92); color: #fff; padding: 12px;
                        border-radius: 6px; font-family: monospace; font-size: 12px;
                        white-space: pre-wrap; display: none; max-height: 40%; overflow: auto;
                    }
                </style>
            </head>
            <body>
                <div id='viewerStack'>
                    <canvas id='viewport'></canvas>
                    <div id='paintOverlay'></div>
                </div>
                <div id='error-overlay'></div>
                <script>
                    // Capped rather than unbounded: an error thrown from inside the per-frame brush
                    // loop would otherwise print again on every single animation frame for as long
                    // as the mouse stayed down, flooding this panel with sixty near-identical lines
                    // a second instead of the one line that actually matters.
                    var errorCount = 0;
                    function showError(msg) {
                        errorCount++;
                        if (errorCount > 5) {
                            if (errorCount === 6) showError('(further errors suppressed)');
                            return;
                        }
                        var el = document.querySelector('#error-overlay');
                        el.style.display = 'block';
                        el.textContent += msg + '\n';
                    }
                    window.onerror = function (message) { showError('JS error: ' + message); };

                    try {
                        var canvas = document.querySelector('#viewport');
                        var paintOverlay = document.querySelector('#paintOverlay');
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

                        // --- Brush state -----------------------------------------------------------
                        var paintMode = true;
                        var painting = false;
                        var brushFraction = 0.05;
                        var brushRadius = 0.05;
                        var strength = 0.3;
                        var modelMaxDim = 1;

                        function resolveBrushRadius() { brushRadius = brushFraction * (modelMaxDim || 1); }

                        window.setPaintMode = function (enabled) {
                            paintMode = enabled;
                            paintOverlay.style.pointerEvents = enabled ? 'auto' : 'none';
                            if (!enabled) { painting = false; hideBrushCursor(); }
                        };
                        window.setBrushRadius = function (fraction) { brushFraction = fraction; resolveBrushRadius(); };
                        window.setStrength = function (value) { strength = value; };

                        // --- Loaded primitives -------------------------------------------------
                        // One entry per mesh primitive: its Three.js Mesh (the SAME object being
                        // rendered - there is no separate copy), the raw triangle index list, a
                        // weld map that groups every vertex sharing a physical position (so a
                        // seam moves as one point), each welded point's neighbours for the
                        // relaxation average, and each ORIGINAL vertex's incident triangle list
                        // for the post-move normal recompute.
                        var paintableMeshes = [];
                        var meshInfoByKey = {};
                        var pickableObjects = [];
                        var touchedThisStroke = new Set();

                        function selectionKey(meshIndex, primIndex) { return meshIndex + '_' + primIndex; }

                        // glTF exporters commonly pack POSITION/NORMAL/UV into one interleaved
                        // buffer view for performance, which GLTFLoader preserves as an
                        // InterleavedBufferAttribute rather than de-interleaving it. Everything
                        // below indexes '.array' directly with a flat v*3 stride, which is only
                        // correct for a plain, privately-owned BufferAttribute - against an
                        // interleaved one it reads the wrong floats, and writing into it during a
                        // stroke would corrupt whatever UV or tangent data shares that same buffer.
                        // getX/getY/getZ/getW are defined identically on both attribute types (and
                        // decode normalized integer encodings too), so copying through them once at
                        // load - this only runs once per primitive, not per frame - gives every
                        // primitive its own flat, safely-mutable copy regardless of how the source
                        // file packed it, which is exactly what lets the brush write straight into
                        // '.array' stay simple and fast during a live stroke. (getComponent() would
                        // do this more generically, but three@0.128.0 - the revision every editor in
                        // this app is pinned to - doesn't have it yet.)
                        function toPlainAttribute(attr) {
                            var itemSize = attr.itemSize;
                            var count = attr.count;
                            var plain = new Float32Array(count * itemSize);
                            for (var i = 0; i < count; i++) {
                                if (itemSize > 0) plain[i * itemSize] = attr.getX(i);
                                if (itemSize > 1) plain[i * itemSize + 1] = attr.getY(i);
                                if (itemSize > 2) plain[i * itemSize + 2] = attr.getZ(i);
                                if (itemSize > 3) plain[i * itemSize + 3] = attr.getW(i);
                            }
                            return new THREE.BufferAttribute(plain, itemSize);
                        }

                        function buildWeldData(mesh, meshIndex, primIndex) {
                            mesh.updateMatrixWorld(true);
                            var geo = mesh.geometry;
                            geo.setAttribute('position', toPlainAttribute(geo.attributes.position));
                            if (geo.attributes.normal) geo.setAttribute('normal', toPlainAttribute(geo.attributes.normal));
                            var posAttr = geo.attributes.position;
                            var nrmAttr = geo.attributes.normal;
                            var index = geo.index;
                            var vertexCount = posAttr.count;
                            var triCount = index.count / 3;
                            var triangles = new Int32Array(index.count);
                            for (var i = 0; i < index.count; i++) triangles[i] = index.getX(i);

                            var scale = 1e6; // 1 / 1e-6 weld tolerance, same as the .NET-side weld
                            var lookup = new Map();
                            var weldOf = new Int32Array(vertexCount);
                            var duplicates = [];
                            var px = posAttr.array;
                            for (var v = 0; v < vertexCount; v++) {
                                var kx = Math.round(px[v * 3] * scale);
                                var ky = Math.round(px[v * 3 + 1] * scale);
                                var kz = Math.round(px[v * 3 + 2] * scale);
                                var key = kx + '_' + ky + '_' + kz;
                                var id = lookup.get(key);
                                if (id === undefined) { id = duplicates.length; lookup.set(key, id); duplicates.push([]); }
                                weldOf[v] = id;
                                duplicates[id].push(v);
                            }

                            var neighborSets = new Array(duplicates.length);
                            for (var w = 0; w < duplicates.length; w++) neighborSets[w] = new Set();
                            var incident = new Array(vertexCount);
                            for (var v2 = 0; v2 < vertexCount; v2++) incident[v2] = [];

                            for (var t = 0; t < triCount; t++) {
                                var a = triangles[t * 3], b = triangles[t * 3 + 1], c = triangles[t * 3 + 2];
                                incident[a].push(t); incident[b].push(t); incident[c].push(t);
                                var wa = weldOf[a], wb = weldOf[b], wc = weldOf[c];
                                if (wa !== wb) { neighborSets[wa].add(wb); neighborSets[wb].add(wa); }
                                if (wb !== wc) { neighborSets[wb].add(wc); neighborSets[wc].add(wb); }
                                if (wc !== wa) { neighborSets[wc].add(wa); neighborSets[wa].add(wc); }
                            }
                            var neighbors = neighborSets.map(function (s) { return Array.from(s); });

                            geo.computeBoundingSphere();
                            var sphereCenter = geo.boundingSphere.center.clone().applyMatrix4(mesh.matrixWorld);
                            var worldScale = new THREE.Vector3().setFromMatrixScale(mesh.matrixWorld);
                            var sphereRadius = geo.boundingSphere.radius * Math.max(worldScale.x, worldScale.y, worldScale.z, 1e-6);

                            return {
                                mesh: mesh, meshIndex: meshIndex, primIndex: primIndex,
                                triangles: triangles, triCount: triCount,
                                weldOf: weldOf, duplicates: duplicates, neighbors: neighbors, incident: incident,
                                worldMatrix: mesh.matrixWorld.clone(),
                                hasNormal: !!nrmAttr,
                                sphereCenter: sphereCenter, sphereRadius: sphereRadius,
                            };
                        }

                        // --- Brush cursor --------------------------------------------------------
                        // Same 'clip the real surface to a ball' shader as GeometryOptimizerEditor's
                        // decal, but simpler here: each decal shares its primitive's OWN geometry and
                        // sits in the SAME scene/camera as the visible model, so ordinary depth
                        // testing occludes it correctly with no separate overlay canvas or camera
                        // sync needed - and because it is that same geometry object, it deforms with
                        // the mesh automatically as a stroke runs.
                        var brushDecalMaterial = new THREE.ShaderMaterial({
                            uniforms: {
                                uCenter: { value: new THREE.Vector3() },
                                uRadius: { value: 1 },
                                uColor: { value: new THREE.Color(0x5ec8ff) },
                            },
                            vertexShader: [
                                'varying vec3 vWorld;',
                                'void main() {',
                                '    vec4 world = modelMatrix * vec4(position, 1.0);',
                                '    vWorld = world.xyz;',
                                '    gl_Position = projectionMatrix * viewMatrix * world;',
                                '}',
                            ].join('\n'),
                            fragmentShader: [
                                'uniform vec3 uCenter;',
                                'uniform float uRadius;',
                                'uniform vec3 uColor;',
                                'varying vec3 vWorld;',
                                'void main() {',
                                '    float d = distance(vWorld, uCenter);',
                                '    if (d > uRadius) discard;',
                                '    float rim = smoothstep(uRadius * 0.82, uRadius, d);',
                                '    gl_FragColor = vec4(uColor, mix(0.32, 0.85, rim));',
                                '}',
                            ].join('\n'),
                            transparent: true,
                            depthTest: true,
                            depthWrite: false,
                            side: THREE.DoubleSide,
                            polygonOffset: true, polygonOffsetFactor: -4, polygonOffsetUnits: -4,
                        });
                        var brushDecalGroup = new THREE.Group();
                        brushDecalGroup.visible = false;
                        scene.add(brushDecalGroup);

                        function rebuildBrushDecals() {
                            for (var i = brushDecalGroup.children.length - 1; i >= 0; i--) {
                                brushDecalGroup.remove(brushDecalGroup.children[i]);
                            }
                            paintableMeshes.forEach(function (info) {
                                var decal = new THREE.Mesh(info.mesh.geometry, brushDecalMaterial);
                                decal.matrixAutoUpdate = false;
                                decal.matrix.copy(info.mesh.matrixWorld);
                                decal.frustumCulled = false;
                                brushDecalGroup.add(decal);
                            });
                        }

                        function showBrushCursor(point) {
                            brushDecalMaterial.uniforms.uCenter.value.copy(point);
                            brushDecalMaterial.uniforms.uRadius.value = brushRadius;
                            brushDecalGroup.visible = true;
                        }
                        function hideBrushCursor() { brushDecalGroup.visible = false; }

                        // --- Live smoothing ------------------------------------------------------
                        var _a = new THREE.Vector3(), _b = new THREE.Vector3(), _c = new THREE.Vector3();
                        var _e1 = new THREE.Vector3(), _e2 = new THREE.Vector3(), _n = new THREE.Vector3();
                        var _centroid = new THREE.Vector3();

                        // One Laplacian pass on the triangles under the brush, for ONE primitive.
                        // Every triangle the ball touches at all (partial overlap counts, same as
                        // GeometryOptimizerEditor's brush), facing the camera, contributes its
                        // corners to the set of welded points that move this frame. Points outside
                        // that set are never moved, so they anchor the region's edge.
                        function applyBrushToPrimitive(info, worldPoint, cameraPos) {
                            var cx = info.sphereCenter.x - worldPoint.x;
                            var cy = info.sphereCenter.y - worldPoint.y;
                            var cz = info.sphereCenter.z - worldPoint.z;
                            var farReach = info.sphereRadius + brushRadius;
                            if (cx * cx + cy * cy + cz * cz > farReach * farReach) return false;

                            var geo = info.mesh.geometry;
                            var posAttr = geo.attributes.position;
                            var pos = posAttr.array;
                            var wm = info.worldMatrix;

                            var eligibleWeld = null;
                            for (var t = 0; t < info.triCount; t++) {
                                var a = info.triangles[t * 3], b = info.triangles[t * 3 + 1], c = info.triangles[t * 3 + 2];
                                _a.set(pos[a * 3], pos[a * 3 + 1], pos[a * 3 + 2]).applyMatrix4(wm);
                                _b.set(pos[b * 3], pos[b * 3 + 1], pos[b * 3 + 2]).applyMatrix4(wm);
                                _c.set(pos[c * 3], pos[c * 3 + 1], pos[c * 3 + 2]).applyMatrix4(wm);
                                _centroid.set(0, 0, 0).add(_a).add(_b).add(_c).multiplyScalar(1 / 3);
                                var radius = Math.sqrt(Math.max(
                                    _centroid.distanceToSquared(_a), _centroid.distanceToSquared(_b), _centroid.distanceToSquared(_c)));
                                var reach = brushRadius + radius;
                                if (_centroid.distanceToSquared(worldPoint) > reach * reach) continue;

                                _e1.subVectors(_b, _a); _e2.subVectors(_c, _a);
                                _n.crossVectors(_e1, _e2);
                                var dot = _n.x * (_centroid.x - cameraPos.x) + _n.y * (_centroid.y - cameraPos.y) + _n.z * (_centroid.z - cameraPos.z);
                                if (dot > 0) continue;

                                if (!eligibleWeld) eligibleWeld = new Set();
                                eligibleWeld.add(info.weldOf[a]);
                                eligibleWeld.add(info.weldOf[b]);
                                eligibleWeld.add(info.weldOf[c]);
                            }
                            if (!eligibleWeld || eligibleWeld.size === 0) return false;

                            // Jacobi-style: every point's move this frame is computed from
                            // positions as they stood BEFORE this frame's pass, so the result
                            // doesn't depend on iteration order.
                            var updates = [];
                            eligibleWeld.forEach(function (w) {
                                var nbrs = info.neighbors[w];
                                if (nbrs.length === 0) return;
                                var sx = 0, sy = 0, sz = 0;
                                for (var k = 0; k < nbrs.length; k++) {
                                    var rep = info.duplicates[nbrs[k]][0];
                                    sx += pos[rep * 3]; sy += pos[rep * 3 + 1]; sz += pos[rep * 3 + 2];
                                }
                                sx /= nbrs.length; sy /= nbrs.length; sz /= nbrs.length;
                                var rep0 = info.duplicates[w][0];
                                var ox = pos[rep0 * 3], oy = pos[rep0 * 3 + 1], oz = pos[rep0 * 3 + 2];
                                updates.push([w, ox + (sx - ox) * strength, oy + (sy - oy) * strength, oz + (sz - oz) * strength]);
                            });
                            for (var u = 0; u < updates.length; u++) {
                                var w2 = updates[u][0], dupes = info.duplicates[w2];
                                for (var d = 0; d < dupes.length; d++) {
                                    var v = dupes[d];
                                    pos[v * 3] = updates[u][1]; pos[v * 3 + 1] = updates[u][2]; pos[v * 3 + 2] = updates[u][3];
                                }
                            }
                            posAttr.needsUpdate = true;

                            if (info.hasNormal) recomputeNormalsNear(info, eligibleWeld);
                            return true;
                        }

                        // Recomputes smooth per-vertex normals (area-weighted, unnormalized-sum of
                        // incident face normals) for every vertex touched by a triangle with at
                        // least one moved corner - a one-triangle halo around the brush, which is
                        // what keeps shading continuous right at its edge. Everything else keeps
                        // its exact original normal.
                        function recomputeNormalsNear(info, eligibleWeld) {
                            var geo = info.mesh.geometry;
                            var pos = geo.attributes.position.array;
                            var nrm = geo.attributes.normal.array;

                            var touchedVerts = new Set();
                            for (var t = 0; t < info.triCount; t++) {
                                var a = info.triangles[t * 3], b = info.triangles[t * 3 + 1], c = info.triangles[t * 3 + 2];
                                if (eligibleWeld.has(info.weldOf[a]) || eligibleWeld.has(info.weldOf[b]) || eligibleWeld.has(info.weldOf[c])) {
                                    touchedVerts.add(a); touchedVerts.add(b); touchedVerts.add(c);
                                }
                            }
                            touchedVerts.forEach(function (v) {
                                var inc = info.incident[v];
                                var ax = 0, ay = 0, az = 0;
                                for (var k = 0; k < inc.length; k++) {
                                    var t = inc[k];
                                    var ia = info.triangles[t * 3], ib = info.triangles[t * 3 + 1], ic = info.triangles[t * 3 + 2];
                                    var e1x = pos[ib * 3] - pos[ia * 3], e1y = pos[ib * 3 + 1] - pos[ia * 3 + 1], e1z = pos[ib * 3 + 2] - pos[ia * 3 + 2];
                                    var e2x = pos[ic * 3] - pos[ia * 3], e2y = pos[ic * 3 + 1] - pos[ia * 3 + 1], e2z = pos[ic * 3 + 2] - pos[ia * 3 + 2];
                                    ax += e1y * e2z - e1z * e2y;
                                    ay += e1z * e2x - e1x * e2z;
                                    az += e1x * e2y - e1y * e2x;
                                }
                                var len = Math.sqrt(ax * ax + ay * ay + az * az);
                                if (len > 1e-10) { nrm[v * 3] = ax / len; nrm[v * 3 + 1] = ay / len; nrm[v * 3 + 2] = az / len; }
                            });
                            geo.attributes.normal.needsUpdate = true;
                        }

                        function applyBrushAtPoint(worldPoint) {
                            var cameraPos = camera.position;
                            for (var i = 0; i < paintableMeshes.length; i++) {
                                var info = paintableMeshes[i];
                                if (applyBrushToPrimitive(info, worldPoint, cameraPos)) {
                                    touchedThisStroke.add(selectionKey(info.meshIndex, info.primIndex));
                                }
                            }
                        }

                        // --- Picking / input -----------------------------------------------------
                        var raycaster = new THREE.Raycaster();
                        var pointerNDC = new THREE.Vector2(-2, -2);

                        function updatePointerNDC(event) {
                            var rect = paintOverlay.getBoundingClientRect();
                            pointerNDC.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
                            pointerNDC.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
                        }

                        function pickAtPointer() {
                            if (pickableObjects.length === 0) return null;
                            raycaster.setFromCamera(pointerNDC, camera);
                            var hits = raycaster.intersectObjects(pickableObjects, false);
                            return hits.length > 0 ? hits[0] : null;
                        }

                        // All pointer handling lives on a transparent overlay div, NOT on the
                        // canvas OrbitControls itself listens to - toggling OrbitControls.enabled
                        // mid-drag instead was tried first and rejected: its pointerdown handler
                        // already registers document-level move/up listeners before this code could
                        // disable it, and if the matching pointerup then also bails on the disabled
                        // check, those listeners never get torn down and orbiting is broken for the
                        // rest of the session (the same class of bug GeometryOptimizerEditor's JS
                        // comments describe for <model-viewer>'s camera-controls toggle). Giving the
                        // overlay pointer-events:auto only while paint mode is on means OrbitControls
                        // never receives the event at all while painting, and never anything to
                        // recover from when painting stops.
                        paintOverlay.addEventListener('pointerdown', function (event) {
                            if (event.button !== 0) return;
                            updatePointerNDC(event);
                            var hit = pickAtPointer();
                            if (!hit) return;
                            painting = true;
                            touchedThisStroke.clear();
                            showBrushCursor(hit.point);
                            event.preventDefault();
                        });
                        paintOverlay.addEventListener('pointermove', function (event) {
                            updatePointerNDC(event);
                            if (painting) return;
                            var hit = pickAtPointer();
                            if (hit) showBrushCursor(hit.point); else hideBrushCursor();
                        });
                        paintOverlay.addEventListener('pointerleave', function () {
                            if (!painting) hideBrushCursor();
                        });
                        paintOverlay.addEventListener('contextmenu', function (event) { event.preventDefault(); });
                        window.addEventListener('pointerup', function () {
                            if (!painting) return;
                            painting = false;
                            if (touchedThisStroke.size === 0) return;

                            var payload = [];
                            touchedThisStroke.forEach(function (key) {
                                var info = meshInfoByKey[key];
                                if (!info) return;
                                var posArr = Array.from(info.mesh.geometry.attributes.position.array);
                                var nrmArr = info.hasNormal ? Array.from(info.mesh.geometry.attributes.normal.array) : null;
                                payload.push({ meshIndex: info.meshIndex, primIndex: info.primIndex, positions: posArr, normals: nrmArr });
                            });
                            touchedThisStroke.clear();
                            if (payload.length > 0 && window.chrome && window.chrome.webview) {
                                window.chrome.webview.postMessage(JSON.stringify({ action: 'strokeApplied', primitives: payload }));
                            }
                        });

                        // --- Model loading ---------------------------------------------------------
                        var currentRoot = null;

                        function tweakMaterials(root) {
                            var seen = [];
                            root.traverse(function (obj) {
                                if (!obj.isMesh || !obj.material) return;
                                var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                                mats.forEach(function (mat) {
                                    if (seen.indexOf(mat) !== -1) return;
                                    seen.push(mat);
                                    mat.side = THREE.DoubleSide;
                                    // Forces opaque rendering even where the source material is
                                    // alphaMode BLEND with no real translucency - see
                                    // GeometryOptimizerEditor's identical fix for why that
                                    // combination otherwise lets the far side of the model show
                                    // through the near side.
                                    mat.transparent = false;
                                    mat.depthWrite = true;
                                    if (typeof mat.metalness === 'number') mat.metalness = Math.min(mat.metalness, 0.15);
                                    if (typeof mat.roughness === 'number') mat.roughness = Math.max(mat.roughness, 0.7);
                                });
                            });
                        }

                        function loadModel(url, frameCamera) {
                            var loader = new THREE.GLTFLoader();
                            loader.load(url, function (gltf) {
                                try {
                                    if (currentRoot) { scene.remove(currentRoot); }
                                    currentRoot = gltf.scene;
                                    scene.add(currentRoot);
                                    currentRoot.updateMatrixWorld(true);
                                    tweakMaterials(currentRoot);

                                    var meshes = [];
                                    var byKey = {};
                                    currentRoot.traverse(function (obj) {
                                        if (!obj.isMesh || !obj.userData || obj.userData.glbMergerMeshIndex === undefined) return;
                                        var meshIndex = obj.userData.glbMergerMeshIndex;
                                        var primIndex = 0;
                                        var parent = obj.parent;
                                        if (parent && parent.children.length > 1 && parent.children.every(function (c) {
                                            return c.userData && c.userData.glbMergerMeshIndex === meshIndex;
                                        })) {
                                            primIndex = parent.children.indexOf(obj);
                                        }
                                        var info = buildWeldData(obj, meshIndex, primIndex);
                                        meshes.push(info);
                                        byKey[selectionKey(meshIndex, primIndex)] = info;
                                    });
                                    paintableMeshes = meshes;
                                    meshInfoByKey = byKey;
                                    pickableObjects = meshes.map(function (i) { return i.mesh; });
                                    rebuildBrushDecals();
                                    hideBrushCursor();
                                    touchedThisStroke.clear();

                                    var box = new THREE.Box3().setFromObject(currentRoot);
                                    var size = box.getSize(new THREE.Vector3());
                                    modelMaxDim = Math.max(size.x, size.y, size.z) || 1;
                                    resolveBrushRadius();

                                    if (frameCamera) {
                                        var center = box.getCenter(new THREE.Vector3());
                                        var dist = modelMaxDim * 2.2;
                                        controls.target.copy(center);
                                        camera.position.copy(center).add(new THREE.Vector3(dist, dist * 0.6, dist));
                                        camera.near = modelMaxDim / 1000;
                                        camera.far = modelMaxDim * 100;
                                        camera.updateProjectionMatrix();
                                        controls.update();
                                    }

                                    if (window.chrome && window.chrome.webview) {
                                        window.chrome.webview.postMessage(JSON.stringify({ action: 'ready' }));
                                    }
                                } catch (innerErr) {
                                    showError('Error setting up preview: ' + innerErr.message);
                                }
                            }, undefined, function (error) {
                                showError('Failed to load preview: ' + (error && error.message ? error.message : error));
                            });
                        }

                        window.reloadModel = function (url) { loadModel(url, false); };

                        loadModel('https://appassets.local/" + previewFileName + @"', true);

                        window.addEventListener('resize', function () {
                            camera.aspect = window.innerWidth / window.innerHeight;
                            camera.updateProjectionMatrix();
                            renderer.setSize(window.innerWidth, window.innerHeight);
                        });

                        function animate() {
                            requestAnimationFrame(animate);
                            controls.update();

                            // Caught locally rather than left to window.onerror: a bad frame here
                            // would otherwise re-throw on every single frame for as long as the
                            // brush stayed down. Catching it, showing ONE real message (err.message
                            // is intact here regardless of any CDN script's own CORS headers - that
                            // redaction only applies to the uncaught 'error' event, not to a value a
                            // local try/catch already holds) and turning painting off is what keeps
                            // one bug from becoming sixty near-identical log lines a second.
                            if (painting) {
                                try {
                                    var hit = pickAtPointer();
                                    if (hit) {
                                        showBrushCursor(hit.point);
                                        applyBrushAtPoint(hit.point);
                                    }
                                } catch (frameErr) {
                                    showError('Smoothing error: ' + frameErr.message);
                                    painting = false;
                                }
                            }

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

        // Dumps the current in-memory model to a fresh temp file for the preview to load. Each
        // reload writes a NEW file rather than overwriting the one the browser already read, which
        // would race its cache and could show pre-revert geometry.
        private string WritePreviewFile()
        {
            var previous = _previewPath;
            _previewPath = Path.Combine(Path.GetTempPath(), $"glbmerger_smooth_preview_{_previewVersion++}.glb");

            // Tags every mesh with its own LogicalMeshes index via glTF extras, purely so the brush
            // hit-test (against the preview's Three.js objects) can be mapped back to a ModelRoot
            // primitive. Extras are cleared again immediately after saving: _model is the same
            // instance the rest of the app saves, and this tag has no business surviving into the
            // real output file.
            var previousExtras = new JsonNode?[_model.LogicalMeshes.Count];
            for (int i = 0; i < _model.LogicalMeshes.Count; i++)
            {
                previousExtras[i] = _model.LogicalMeshes[i].Extras;
                _model.LogicalMeshes[i].Extras = new JsonObject { ["glbMergerMeshIndex"] = i };
            }
            try
            {
                _model.SaveGLB(_previewPath);
            }
            finally
            {
                for (int i = 0; i < _model.LogicalMeshes.Count; i++)
                    _model.LogicalMeshes[i].Extras = previousExtras[i]!;
            }

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
            ViewerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ViewerMessage>(e.TryGetWebMessageAsString(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch
            {
                return;
            }

            if (message == null || IsDisposed) return;

            if (message.Action == "ready")
            {
                _viewerReady = true;
                PushPaintMode();
                PushBrushRadius();
                PushStrength();
                return;
            }

            if (message.Action == "strokeApplied" && message.Primitives is { Length: > 0 })
            {
                _originalPositions ??= ModelSmoother.SnapshotPositions(_model);
                foreach (var p in message.Primitives)
                {
                    var positions = ToVector3List(p.Positions);
                    var normals = p.Normals is { Length: > 0 } ? ToVector3List(p.Normals) : null;
                    ModelSmoother.ApplyStroke(_model, p.MeshIndex, p.PrimIndex, positions, normals);
                }
                _btnRevert.Enabled = true;
                _lblStatus.Text = message.Primitives.Length == 1
                    ? "Smoothed live - included the next time you save the merge."
                    : $"Smoothed {message.Primitives.Length} primitives live - included the next time you save the merge.";
            }
        }

        private static List<Vector3> ToVector3List(float[] flat)
        {
            var list = new List<Vector3>(flat.Length / 3);
            for (int i = 0; i + 2 < flat.Length; i += 3)
                list.Add(new Vector3(flat[i], flat[i + 1], flat[i + 2]));
            return list;
        }

        private void PushPaintMode()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            _ = _webView.CoreWebView2.ExecuteScriptAsync($"setPaintMode({(_chkPaintMode.Checked ? "true" : "false")});");
        }

        private void PushBrushRadius()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            float fraction = _sliderBrush.Value / 100f;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setBrushRadius({fraction.ToString(CultureInfo.InvariantCulture)});");
        }

        private void PushStrength()
        {
            if (!_viewerReady || _webView.CoreWebView2 == null) return;
            float strength = _sliderStrength.Value / 100f;
            _ = _webView.CoreWebView2.ExecuteScriptAsync(
                $"setStrength({strength.ToString(CultureInfo.InvariantCulture)});");
        }

        private static string EscapeJs(string s) => s.Replace("\\", "\\\\").Replace("'", "\\'");

        private sealed class ViewerMessage
        {
            public string? Action { get; set; }
            public PrimitivePayload[]? Primitives { get; set; }
        }

        private sealed class PrimitivePayload
        {
            public int MeshIndex { get; set; }
            public int PrimIndex { get; set; }
            public float[] Positions { get; set; } = Array.Empty<float>();
            public float[]? Normals { get; set; }
        }
    }
}
