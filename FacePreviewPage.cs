namespace GlbMerger
{
    // The three.js page both face-feature editors preview in (Eye Anchors, Brows & Mouth): load the
    // model, frame its head, orbit, and turn a click into a hit - the editor's own script does the
    // rest. That script is pasted in ahead of the page's setup and must define:
    //
    //   patchMaterial(mat)          - called once per mesh material as the model loads (the
    //                                 editor's copy of the game's overlay shader goes in here)
    //   onPick(first, chosen, ev)   - a click on the model: `first` the nearest hit, `chosen` the
    //                                 one behind it with Shift held (through glasses), else `first`
    //
    // and may define onLoaded() and onFrame(dt). The page gives it: post(obj), showError(msg),
    // setHint(text), srgbToLinear(c), normalise(name), materialOf(hit), sampleTexture(hit, map),
    // sampleMR(hit), meshPointFromHit(hit), frameHead(), and once loaded the globals renderer,
    // scene, camera, meshes, headBone, modelHeight.
    //
    // Everything runs in the mesh's raw bind-pose POSITION space - the space the anchors are
    // authored in - so an overlay keyed off `position` sticks to the face however the rig is posed.
    internal static class FacePreviewPage
    {
        public static string Build(string previewFileName, string hint, string editorScript) => @"
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
        #hint { position: absolute; bottom: 8px; left: 10px; color: #aaa; font: 12px sans-serif; }
        #inset { position: absolute; top: 10px; right: 10px; border: 1px solid #555; display: none; image-rendering: pixelated; }
    </style>
</head>
<body>
    <canvas id='viewport'></canvas>
    <canvas id='inset'></canvas>
    <div id='error-overlay'></div>
    <div id='hint'></div>
    <script>
        var DEFAULT_HINT = '" + hint + @"';
        function showError(msg) {
            var el = document.querySelector('#error-overlay');
            el.style.display = 'block';
            el.textContent += msg + '\n';
        }
        window.onerror = function (message) { showError('JS error: ' + message); };
        function post(obj) { if (window.chrome && window.chrome.webview) window.chrome.webview.postMessage(JSON.stringify(obj)); }
        function setHint(text) { document.querySelector('#hint').textContent = text || DEFAULT_HINT; }
        setHint();
        function srgbToLinear(c) { return c <= 0.04045 ? c / 12.92 : Math.pow((c + 0.055) / 1.055, 2.4); }
        function normalise(name) {
            var s = (name || '').toLowerCase().replace(/[^a-z0-9]/g, '');
            return s.indexOf('mixamorig') === 0 ? s.substring(9) : s;
        }
        function materialOf(hit) {
            return Array.isArray(hit.object.material) ? hit.object.material[hit.face.materialIndex] : hit.object.material;
        }

        // A texture's pixels, readable: the GLB's images load as blob URLs, so the canvas isn't tainted.
        var textureCanvases = new Map();
        function textureCanvas(map) {
            var img = map.image, cv = textureCanvases.get(img);
            if (!cv) {
                cv = document.createElement('canvas');
                cv.width = img.width; cv.height = img.height;
                cv.getContext('2d').drawImage(img, 0, 0);
                textureCanvases.set(img, cv);
            }
            return cv;
        }

        // sRGB [r, g, b] 0..1, a 3x3 average round the hit's texel.
        function sampleTexture(hit, map) {
            if (!map || !map.image || !hit.uv) return null;
            var cv = textureCanvas(map);
            var uv = hit.uv.clone();
            map.transformUv(uv);
            var x = Math.min(Math.max(Math.floor(uv.x * cv.width) - 1, 0), cv.width - 3);
            var y = Math.min(Math.max(Math.floor(uv.y * cv.height) - 1, 0), cv.height - 3);
            var d = cv.getContext('2d').getImageData(x, y, 3, 3).data, rgb = [0, 0, 0];
            for (var i = 0; i < 9; i++) { rgb[0] += d[i * 4]; rgb[1] += d[i * 4 + 1]; rgb[2] += d[i * 4 + 2]; }
            return [rgb[0] / 2295, rgb[1] / 2295, rgb[2] / 2295];   // 9 texels x 255
        }

        // Roughness / metalness under the hit: the material's factors times its metallicRoughness
        // texture (glTF: G = roughness, B = metalness), if it has one.
        function sampleMR(hit) {
            var mat = materialOf(hit);
            if (!mat || mat.roughness === undefined) return null;
            var tex = mat.roughnessMap || mat.metalnessMap;
            var t = tex ? sampleTexture(hit, tex) : null;
            return [mat.roughness * (t ? t[1] : 1), mat.metalness * (t ? t[2] : 1)];
        }

        // ---- The editor's own script ----
" + editorScript + @"
        // ---- Page setup ----
        try {
            var canvas = document.querySelector('#viewport');
            var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
            renderer.setPixelRatio(window.devicePixelRatio);
            renderer.setSize(window.innerWidth, window.innerHeight);
            renderer.outputEncoding = THREE.sRGBEncoding;

            var scene = new THREE.Scene();
            scene.background = new THREE.Color(0x1a1c1e);
            var camera = new THREE.PerspectiveCamera(35, window.innerWidth / window.innerHeight, 0.001, 10000);
            var controls = new THREE.OrbitControls(camera, renderer.domElement);
            controls.enableDamping = true;

            scene.add(new THREE.HemisphereLight(0xffffff, 0x444444, 1.2));
            scene.add(new THREE.AmbientLight(0xffffff, 0.6));
            var dirLight = new THREE.DirectionalLight(0xffffff, 1.2);
            dirLight.position.set(2, 5, 7.5);
            scene.add(dirLight);

            var meshes = [];
            var headBone = null;
            var modelHeight = 1;

            window.frameHead = function () {
                if (meshes.length === 0) return;
                var target = new THREE.Vector3();
                if (headBone) headBone.getWorldPosition(target);
                else new THREE.Box3().setFromObject(scene).getCenter(target);
                var forward = new THREE.Vector3(0, 0, 1).transformDirection(meshes[0].matrixWorld);
                controls.target.copy(target);
                camera.position.copy(target).addScaledVector(forward, modelHeight * 0.45);
                controls.update();
            };

            // The clicked point back in raw POSITION space: barycentric weights on the triangle
            // as drawn (skinned), applied to its bind-pose corners - right whether or not the
            // rest pose matches the bind pose.
            window.meshPointFromHit = function (hit) {
                var mesh = hit.object, pos = mesh.geometry.attributes.position, f = hit.face;
                var ids = [f.a, f.b, f.c], drawn = [];
                for (var k = 0; k < 3; k++) {
                    var v = new THREE.Vector3();
                    if (mesh.isSkinnedMesh) mesh.boneTransform(ids[k], v);
                    else v.fromBufferAttribute(pos, ids[k]);
                    drawn.push(v.applyMatrix4(mesh.matrixWorld));
                }
                var bary = new THREE.Vector3();
                THREE.Triangle.getBarycoord(hit.point, drawn[0], drawn[1], drawn[2], bary);
                var w = [bary.x, bary.y, bary.z], out = [0, 0, 0];
                for (var k = 0; k < 3; k++) {
                    out[0] += w[k] * pos.getX(ids[k]);
                    out[1] += w[k] * pos.getY(ids[k]);
                    out[2] += w[k] * pos.getZ(ids[k]);
                }
                return out;
            };

            var loader = new THREE.GLTFLoader();
            loader.load('https://appassets.local/" + previewFileName + @"', function (gltf) {
                scene.add(gltf.scene);
                gltf.scene.traverse(function (obj) {
                    if (obj.isMesh) {
                        meshes.push(obj);
                        var mats = Array.isArray(obj.material) ? obj.material : [obj.material];
                        mats.forEach(patchMaterial);
                    }
                    if (obj.isBone && !headBone && normalise(obj.name) === 'head') headBone = obj;
                });
                gltf.scene.updateMatrixWorld(true);
                var box = new THREE.Box3().setFromObject(gltf.scene);
                modelHeight = Math.max(box.max.y - box.min.y, 1e-3);
                camera.near = modelHeight * 0.001;
                camera.far = modelHeight * 100;
                camera.updateProjectionMatrix();
                frameHead();
                if (typeof onLoaded === 'function') onLoaded();
                post({ action: 'loaded' });
            }, undefined, function (error) {
                showError('Failed to load preview: ' + (error && error.message ? error.message : error));
            });

            window.addEventListener('resize', function () {
                camera.aspect = window.innerWidth / window.innerHeight;
                camera.updateProjectionMatrix();
                renderer.setSize(window.innerWidth, window.innerHeight);
            });

            // A press that moved more than a few pixels was an orbit drag, not a click.
            var raycaster = new THREE.Raycaster();
            var pickVec = new THREE.Vector2();
            var pressX = 0, pressY = 0;
            canvas.addEventListener('pointerdown', function (event) { pressX = event.clientX; pressY = event.clientY; });
            canvas.addEventListener('click', function (event) {
                if (Math.abs(event.clientX - pressX) > 4 || Math.abs(event.clientY - pressY) > 4) return;
                var rect = canvas.getBoundingClientRect();
                pickVec.x = ((event.clientX - rect.left) / rect.width) * 2 - 1;
                pickVec.y = -((event.clientY - rect.top) / rect.height) * 2 + 1;
                raycaster.setFromCamera(pickVec, camera);
                var hits = raycaster.intersectObjects(meshes, false);
                if (hits.length === 0 || !hits[0].face) return;
                var chosen = hits[0];
                if (event.shiftKey)
                    for (var h = 1; h < hits.length; h++)
                        if (hits[h].face && hits[h].distance > hits[0].distance + 1e-4 * modelHeight) { chosen = hits[h]; break; }
                onPick(hits[0], chosen, event);
            });

            var frameClock = new THREE.Clock();
            (function animate() {
                requestAnimationFrame(animate);
                if (typeof onFrame === 'function') onFrame(Math.min(frameClock.getDelta(), 0.1));
                controls.update();
                renderer.render(scene, camera);
            })();
        } catch (err) {
            showError('Setup error: ' + err.message);
        }
    </script>
</body>
</html>";
    }
}
