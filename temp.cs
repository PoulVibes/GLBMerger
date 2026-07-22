using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Numerics;
using System.Windows.Forms;
using Microsoft.Web.WebView2.WinForms;
using SharpGLTF.Schema2;
using SharpGLTF.Transforms;

namespace GlbJointCorrector
{
    public partial class Form1 : Form
    {
        // 1. CLASS UI AND STORAGE VARIABLES
        private WebView2 webView = null!;
        private TrackBar sliderX = null!, sliderY = null!, sliderZ = null!;
        private ComboBox boneDropdown = null!;
        private Button btnLoad = null!, btnSave = null!;
        
        private ModelRoot? currentModel;
        private string? loadedFilePath;
        private string tempPreviewPath;

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

            // Left Control Panel Container
            Panel ctrlPanel = new Panel { Dock = DockStyle.Left, Width = 300 };
            
            btnLoad = new Button { Text = "Load GLB", Top = 20, Left = 20, Width = 260 };
            btnLoad.Click += BtnLoad_Click!;

            boneDropdown = new ComboBox { Top = 60, Left = 20, Width = 260, DropDownStyle = ComboBoxStyle.DropDownList };
            boneDropdown.SelectedIndexChanged += JointSelectionChanged!;

            // Angle Trackbars (-180 to 180 degrees)
            sliderX = CreateSlider("X Rotation", 100, ctrlPanel);
            sliderY = CreateSlider("Y Rotation", 180, ctrlPanel);
            sliderZ = CreateSlider("Z Rotation", 260, ctrlPanel);

            btnSave = new Button { Text = "Export Corrected GLB", Top = 360, Left = 20, Width = 260, Enabled = false };
            btnSave.Click += BtnSave_Click!;

            ctrlPanel.Controls.AddRange(new Control[] { btnLoad, boneDropdown, btnSave });
            this.Controls.Add(ctrlPanel);

            // Right 3D Preview Engine Container
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

        // 2. THREE.JS 3D VIEWPORT ENGINE INTEGRATION
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

        // 3. I/O BUTTON LOGIC
        private void BtnLoad_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog { Filter = "GLB Files|*.glb" })
            {
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    loadedFilePath = openFileDialog.FileName;
                    currentModel = ModelRoot.Load(loadedFilePath);

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
            }
        }

        // 4. VALIDATED ANIMATION SLIDER LOGIC
        private void Slider_Scroll(object sender, EventArgs e)
        {
            if (currentModel == null || loadedFilePath == null || boneDropdown.SelectedItem == null) return;

            string targetBoneName = boneDropdown.SelectedItem.ToString()!;
            Vector3 sliderRotations = new Vector3(sliderX.Value, sliderY.Value, sliderZ.Value);

            // Open a clean copy to prevent altering data streams in place
            var processedModel = ModelRoot.Load(loadedFilePath);
            
            float degreesToRadians = (float)Math.PI / 180f;
            Quaternion offsetRotation = Quaternion.CreateFromYawPitchRoll(
                sliderRotations.Y * degreesToRadians,
                sliderRotations.X * degreesToRadians,
                sliderRotations.Z * degreesToRadians
            );

            // Read the data streams explicitly using valid core accessors
            foreach (var animation in processedModel.LogicalAnimations)
            {
                foreach (var channel in animation.Channels)
                {
                    if (channel.TargetNode.Name == targetBoneName && channel.TargetNodePath == PropertyPath.rotation)
                    {
                        // Get linear curves using the safe built-in structure framework iterator
                        var sampler = channel.GetRotationSampler();
                        var originalKeys = sampler.GetLinearKeys().ToArray();

                        // Write changes directly into the underlying structural memory buffer arrays
                        var outputBufferAccessor = sampler.Output;
                        var quaternionArray = outputBufferAccessor.AsQuaternionArray();

                        for (int i = 0; i < quaternionArray.Count; i++)
                        {
                            Quaternion originalRotation = quaternionArray[i];
                            
                            // Combine original frame orientation with our real-time slider offset matrix
                            Quaternion correctedRotation = originalRotation * offsetRotation;
                            quaternionArray[i] = Quaternion.Normalize(correctedRotation);
                        }
                    }
                }
            }

            // Instantly commit changes back into preview storage
            processedModel.SaveGLB(tempPreviewPath);
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
            using (SaveFileDialog saveFileDialog = new SaveFileDialog { Filter = "GLB Files|*.glb", FileName = "fixed_model.glb" })
            {
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    if (File.Exists(tempPreviewPath))
                    {
                        File.Copy(tempPreviewPath, saveFileDialog.FileName, true);
                        MessageBox.Show("Animation corrections baked and saved successfully!", "Success");
                    }
                }
            }
        }
    }
}
