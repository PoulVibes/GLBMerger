using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;

namespace GlbJointCorrector
{
    public partial class Form1 : Form
    {
        private WebView2 webView = null!;
        private TrackBar sliderX = null!, sliderY = null!, sliderZ = null!;
        private ComboBox boneDropdown = null!;
        private Button btnLoad = null!, btnSave = null!;

        private ModelRoot? currentModel;
        private string? loadedFilePath;
        private readonly string tempPreviewFolder;
        private readonly string tempPreviewFile = "preview_model.glb";
        private bool _viewerReady;
        private string? _pendingPreviewFile;

        private const string VirtualHost = "appassets.local";

        private readonly Dictionary<string, (string TrackName, Node Node, (float Time, Quaternion Value)[] OriginalKeys)> _rotationCache = new();

        public Form1()
        {
            InitializeComponent();
            SetupUI();
            tempPreviewFolder = Path.GetTempPath();
            Initialize3DViewer();
        }

        private void SetupUI()
        {
            this.Text = "GLB Joint Corrector";
            this.Size = new System.Drawing.Size(1200, 800);

            Panel ctrlPanel = new Panel { Dock = DockStyle.Left, Width = 300 };

            btnLoad = new Button { Text = "Load GLB", Top = 20, Left = 20, Width = 260 };
            btnLoad.Click += BtnLoad_Click!;

            boneDropdown = new ComboBox { Top = 60, Left = 20, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            boneDropdown.SelectedIndexChanged += JointSelectionChanged!;

            sliderX = CreateSlider("X Rotation", 100, ctrlPanel);
            sliderY = CreateSlider("Y Rotation", 180, ctrlPanel);
            sliderZ = CreateSlider("Z Rotation", 260, ctrlPanel);

            btnSave = new Button { Text = "Export Corrected GLB", Top = 360, Left = 20, Width = 260, Enabled = false };
            btnSave.Click += BtnSave_Click!;

            ctrlPanel.Controls.AddRange(new Control[] { btnLoad, boneDropdown, btnSave });
            this.Controls.Add(ctrlPanel);

            webView = new WebView2 { Dock = DockStyle.Fill };
            this.Controls.Add(webView);
        }

        private TrackBar CreateSlider(string labelText, int topPosition, Panel panel)
        {
            Label lbl = new Label { Text = labelText, Top = topPosition, Left = 20, Width = 260 };
            TrackBar slider = new TrackBar { Top = topPosition + 20, Left = 20, Width = 260, Minimum = -180, Maximum = 180, Value = 0 };
            slider.Scroll += Slider_Scroll!;
            panel.Controls.Add(lbl);
            panel.Controls.Add(slider);
            return slider;
        }

        private async void Initialize3DViewer()
        {
            await webView.EnsureCoreWebView2Async();

            // Dev tools so console errors are visible (F12 in the WebView2 window).
            webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            webView.CoreWebView2.Settings.IsWebMessageEnabled = true;

            // Map temp folder to a virtual https host so file:// CORS restrictions don't block loading.
            webView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                VirtualHost, tempPreviewFolder, CoreWebView2HostResourceAccessKind.Allow);

            webView.CoreWebView2.NavigationCompleted += (s, e) =>
            {
                _viewerReady = e.IsSuccess;
                if (_viewerReady && _pendingPreviewFile != null)
                {
                    LoadModelInViewer(_pendingPreviewFile);
                    _pendingPreviewFile = null;
                }
            };

            string htmlContent = @"
            <!DOCTYPE html>
            <html>
            <head>
                <script src='https://unpkg.com/three@0.128.0/build/three.min.js'></script>
                <script src='https://unpkg.com/three@0.128.0/examples/js/loaders/GLTFLoader.js'></script>
                <style>body { margin: 0; overflow: hidden; background-color: #222; }</style>
            </head>
            <body>
                <script>
                    let scene = new THREE.Scene();
                    let camera = new THREE.PerspectiveCamera(45, window.innerWidth / window.innerHeight, 0.1, 100);
                    camera.position.set(0, 1.5, 3);

                    let renderer = new THREE.WebGLRenderer({ antialias: true });
                    renderer.setSize(window.innerWidth, window.innerHeight);
                    document.body.appendChild(renderer.domElement);

                    let light = new THREE.AmbientLight(0xffffff, 0.8); scene.add(light);
                    let dirLight = new THREE.DirectionalLight(0xffffff, 0.6); dirLight.position.set(5, 10, 7); scene.add(dirLight);

                    let mixer, clock = new THREE.Clock(), currentModel;
                    let loader = new THREE.GLTFLoader();

                    window.loadModel = function(url) {
                        if (currentModel) scene.remove(currentModel);
                        loader.load(url, function(gltf) {
                            currentModel = gltf.scene;
                            scene.add(currentModel);

                            let box = new THREE.Box3().setFromObject(currentModel);

                            let size = box.getSize(new THREE.Vector3());
                            let center = box.getCenter(new THREE.Vector3());
                            let maxDim = Math.max(size.x, size.y, size.z);

                            let distance = Math.max(maxDim, 2);
                            camera.position.set(center.x, center.y, center.z + distance*2);
                            
                            camera.updateProjectionMatrix();
                            camera.lookAt(center);
                            
                            if (gltf.animations.length > 0) {
                                mixer = new THREE.AnimationMixer(currentModel);
                                mixer.clipAction(gltf.animations[0]).play();
                            }
                        }, undefined, function(err) {
                            console.error('GLTFLoader error:', err);
                        });
                    };

                    function animate() {
                        requestAnimationFrame(animate);
                        if (mixer) mixer.update(clock.getDelta());
                        renderer.render(scene, camera);
                    }
                    animate();
                </script>
            </body>
            </html>";

            webView.CoreWebView2.NavigateToString(htmlContent);
        }

        private void LoadModelInViewer(string fileName)
        {
            string url = $"https://{VirtualHost}/{fileName}";
            webView.CoreWebView2.ExecuteScriptAsync($"loadModel('{url}')");
        }

        private void Update3DPreview(string filePath)
        {
            string fileName = Path.GetFileName(filePath);

            if (_viewerReady)
            {
                LoadModelInViewer(fileName);
            }
            else
            {
                _pendingPreviewFile = fileName; // fire once nav completes
            }
        }

        private void BtnLoad_Click(object sender, EventArgs e)
        {
            using OpenFileDialog openFileDialog = new() { Filter = "GLB Files|*.glb" };
            if (openFileDialog.ShowDialog() != DialogResult.OK) return;

            loadedFilePath = openFileDialog.FileName;
            currentModel = ModelRoot.Load(loadedFilePath);
            _rotationCache.Clear();

            boneDropdown.Items.Clear();
            var boneNames = currentModel.LogicalNodes
                .Where(n => !string.IsNullOrEmpty(n.Name))
                .Select(n => n.Name!)
                .ToArray();

            boneDropdown.Items.AddRange(boneNames);
            if (boneDropdown.Items.Count > 0) boneDropdown.SelectedIndex = 0;

            btnSave.Enabled = true;

            // Copy into the mapped temp folder under a stable name the virtual host can serve.
            string previewPath = Path.Combine(tempPreviewFolder, tempPreviewFile);
            File.Copy(loadedFilePath, previewPath, true);
            Update3DPreview(previewPath);
        }

        private bool TryGetOriginalKeys(string boneName, out string trackName, out Node? node, out (float Time, Quaternion Value)[] keys)
        {
            if (_rotationCache.TryGetValue(boneName, out var cached))
            {
                trackName = cached.TrackName;
                node = cached.Node;
                keys = cached.OriginalKeys;
                return true;
            }

            trackName = string.Empty;
            node = null;
            keys = Array.Empty<(float, Quaternion)>();

            if (currentModel == null) return false;

            foreach (var animation in currentModel.LogicalAnimations)
            {
                foreach (var ch in animation.Channels)
                {
                    if (ch.TargetNode?.Name == boneName && ch.TargetNodePath == PropertyPath.rotation)
                    {
                        var sampler = ch.GetRotationSampler();
                        if (sampler == null) continue;

                        var originalKeys = sampler.GetLinearKeys().ToArray();
                        _rotationCache[boneName] = (animation.Name, ch.TargetNode, originalKeys);

                        trackName = animation.Name;
                        node = ch.TargetNode;
                        keys = originalKeys;
                        return true;
                    }
                }
            }

            return false;
        }

        private void Slider_Scroll(object sender, EventArgs e)
        {
            if (currentModel == null || loadedFilePath == null || boneDropdown.SelectedItem == null) return;

            string targetBoneName = boneDropdown.SelectedItem.ToString()!;
            if (!TryGetOriginalKeys(targetBoneName, out var trackName, out var node, out var originalKeys) || node == null) return;

            const float degToRad = (float)Math.PI / 180f;
            Quaternion rotX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, sliderX.Value * degToRad);
            Quaternion rotY = Quaternion.CreateFromAxisAngle(Vector3.UnitY, sliderY.Value * degToRad);
            Quaternion rotZ = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, sliderZ.Value * degToRad);
            Quaternion offsetRotation = rotZ * rotY * rotX;

            var correctedKeys = originalKeys
                .Select(k => (k.Time, Quaternion.Normalize(k.Value * offsetRotation)))
                .ToArray();

            node.WithRotationAnimation(trackName, correctedKeys);

            string previewPath = Path.Combine(tempPreviewFolder, tempPreviewFile);
            currentModel.SaveGLB(previewPath);
            Update3DPreview(previewPath);
        }

        private void JointSelectionChanged(object sender, EventArgs e)
        {
            sliderX.Value = 0;
            sliderY.Value = 0;
            sliderZ.Value = 0;
        }

        private void BtnSave_Click(object sender, EventArgs e)
        {
            using SaveFileDialog saveFileDialog = new() { Filter = "GLB Files|*.glb", FileName = "fixed_model.glb" };
            if (saveFileDialog.ShowDialog() != DialogResult.OK) return;

            string previewPath = Path.Combine(tempPreviewFolder, tempPreviewFile);
            if (File.Exists(previewPath))
            {
                File.Copy(previewPath, saveFileDialog.FileName, true);
                MessageBox.Show("Animation corrections baked and saved successfully!", "Success");
            }
        }
    }
}
