using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Forms;
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
        private readonly string tempPreviewPath;

        // Cache: bone name -> (animation track name, original time/quaternion keys, target node)
        private readonly Dictionary<string, (string TrackName, Node Node, (float Time, Quaternion Value)[] OriginalKeys)> _rotationCache = new();

        public Form1()
        {
            InitializeComponent();
            SetupUI();
            Initialize3DViewer();
            tempPreviewPath = Path.Combine(Path.GetTempPath(), "preview_model.glb");
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

            string htmlContent = @"
            <!DOCTYPE html>
            <html>
            <head>
                <script src='https://cloudflare.com'></script>
                <script src='https://jsdelivr.net'></script>
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

                    function loadModel(url) {
                        if(currentModel) scene.remove(currentModel);
                        loader.load(url, function(gltf) {
                            currentModel = gltf.scene;
                            scene.add(currentModel);
                            if(gltf.animations.length > 0) {
                                mixer = new THREE.AnimationMixer(currentModel);
                                mixer.clipAction(gltf.animations).play();
                            }
                        }, undefined, function(e) { console.error(e); });
                    }

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

        private void Update3DPreview(string filePath)
        {
            if (webView.CoreWebView2 != null)
            {
                string uri = new Uri(filePath).AbsoluteUri;
                webView.CoreWebView2.ExecuteScriptAsync($"loadModel('{uri}')");
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
            Update3DPreview(loadedFilePath);
        }

        // Lazily caches original (unmodified) rotation keys + track name for a bone.
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
            Quaternion offsetRotation = rotZ * rotY * rotX; // explicit XYZ intrinsic order

            var correctedKeys = originalKeys
                .Select(k => (k.Time, Quaternion.Normalize(k.Value * offsetRotation)))
                .ToArray();

            // Overwrites the existing rotation channel for this node/track with corrected keys.
            node.WithRotationAnimation(trackName, correctedKeys);

            currentModel.SaveGLB(tempPreviewPath);
            Update3DPreview(tempPreviewPath);
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

            if (File.Exists(tempPreviewPath))
            {
                File.Copy(tempPreviewPath, saveFileDialog.FileName, true);
                MessageBox.Show("Animation corrections baked and saved successfully!", "Success");
            }
        }
    }
}