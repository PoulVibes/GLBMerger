using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GlbMerger
{
    public class ViewerForm : Form
    {
        private WebView2 webView;
        private string glbFilePath;

        public ViewerForm(string glbPath)
        {
            this.glbFilePath = glbPath;
            this.Text = "3D GLB Model Asset Inspector";
            this.Width = 900;
            this.Height = 700;
            this.StartPosition = FormStartPosition.CenterParent;

            webView = new WebView2 { Dock = DockStyle.Fill };
            this.Controls.Add(webView);

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
                File.Copy(glbFilePath, targetGlbCopyPath, true);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to cache file for 3D engine: {ex.Message}", "Viewer Engine Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

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
                </div>

                <script>
                    var viewer = document.querySelector('#viewer');
                    var animSelect = document.querySelector('#anim-selector');
                    var matSelect = document.querySelector('#material-selector');

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
                        if (e.target.value) {
                            viewer.animationName = e.target.value;
                            viewer.play();
                        } else {
                            viewer.pause();
                            viewer.currentTime = 0;
                        }
                    });

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

            // Load the generated webpage canvas directly into the application instance frame
            webView.CoreWebView2.NavigateToString(htmlContent);
        }
    }
}
