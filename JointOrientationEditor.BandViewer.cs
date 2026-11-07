namespace GlbMerger;

public partial class JointOrientationEditor
{
    private const string BandViewerScript = """
        var bandCharacterRoot = null;
        var bandGroundQuad, bandGroundGrid, bandGroundSize;
        var bandInstrumentRoot = null;
        var bandPairs = [];
        var bandLoadToken = 0;
        var bandVisible = true;
        var bandLastReport = 0;
        function bandPost(action, values) {
            window.chrome.webview.postMessage(JSON.stringify(Object.assign({ action: action }, values || {})));
        }
        window.setBandPlaybackRate = function(rate) { playbackRate = Math.max(0.01, rate); };
        window.seekBandTime = function(time) {
            if (!mixer || !currentAction || !currentClip) return;
            paused = true;
            baseQuaternions = {}; basePositions = {};
            var target = Math.max(0, Math.min(time, Math.max(0, currentClip.duration - 0.000001)));
            mixer.setTime(0); currentAction.time = target; mixer.update(0);
            bandPost('bandTime', { time: target });
        };
        window.setBandInstrumentVisible = function(visible) {
            bandVisible = visible;
            if (bandInstrumentRoot) bandInstrumentRoot.visible = visible;
        };
        function disposeBandRoot(root) {
            if (!root) return;
            root.traverse(function(obj) {
                if (!obj.isMesh) return;
                if (obj.geometry) obj.geometry.dispose();
                var materials = Array.isArray(obj.material) ? obj.material : [obj.material];
                materials.forEach(function(material) {
                    if (!material) return;
                    Object.keys(material).forEach(function(key) {
                        if (material[key] && material[key].isTexture) material[key].dispose();
                    });
                    material.dispose();
                });
            });
        }
        window.loadBandInstrument = function(url) {
            var token = ++bandLoadToken;
            new THREE.GLTFLoader().load(url, function(gltf) {
                if (token !== bandLoadToken) { disposeBandRoot(gltf.scene); return; }
                var pairs = [];
                gltf.scene.traverse(function(node) {
                    var source = bonesByName[node.name];
                    if (source && !node.isMesh && !source.isMesh) pairs.push({ source: source, target: node });
                    if (node.isMesh) node.frustumCulled = false;
                });
                if (!pairs.length) {
                    disposeBandRoot(gltf.scene);
                    bandPost('bandInstrumentError', { message: 'No shared instrument joints found.' }); return;
                }
                if (bandInstrumentRoot) { scene.remove(bandInstrumentRoot); disposeBandRoot(bandInstrumentRoot); }
                bandInstrumentRoot = gltf.scene; bandPairs = pairs;
                bandInstrumentRoot.visible = bandVisible;
                scene.add(bandInstrumentRoot);
                updateBandInstrument();
                frameBandAssembly();
                bandPost('bandInstrumentReady', { joints: pairs.length });
            }, undefined, function(error) {
                if (token === bandLoadToken) bandPost('bandInstrumentError', { message: 'Instrument preview failed: ' + (error.message || error) });
            });
        };
        function updateBandInstrument() {
            // Shared local transforms preserve the original origin, hierarchy,
            // inverse binds, and root scale; no independent normalization.
            bandPairs.forEach(function(pair) {
                pair.target.position.copy(pair.source.position);
                pair.target.quaternion.copy(pair.source.quaternion);
                pair.target.scale.copy(pair.source.scale);
            });
            if (bandInstrumentRoot) bandInstrumentRoot.updateMatrixWorld(true);
            if (currentAction && performance.now() - bandLastReport > 100) {
                bandLastReport = performance.now();
                bandPost('bandTime', { time: currentAction.time });
            }
        }
        function frameBandAssembly() {
            if (!bandCharacterRoot) return;
            scene.updateMatrixWorld(true);
            var bounds = new THREE.Box3();
            [bandCharacterRoot, bandInstrumentRoot].forEach(function(root) {
                if (!root) return;
                root.traverse(function(mesh) {
                    if (!mesh.isMesh || !mesh.geometry.attributes.position) return;
                    if (mesh.isSkinnedMesh) mesh.skeleton.update();
                    var positions = mesh.geometry.attributes.position;
                    var point = new THREE.Vector3();
                    for (var i = 0; i < positions.count; ++i) {
                        point.fromBufferAttribute(positions, i);
                        if (mesh.isSkinnedMesh) mesh.boneTransform(i, point);
                        point.applyMatrix4(mesh.matrixWorld);
                        if (Number.isFinite(point.x + point.y + point.z)) bounds.expandByPoint(point);
                    }
                });
            });
            if (bounds.isEmpty()) return;
            var center = bounds.getCenter(new THREE.Vector3());
            var size = bounds.getSize(new THREE.Vector3());
            var radius = Math.max(size.length() * 0.5, 0.01);
            var halfFov = Math.atan(Math.tan(camera.fov * Math.PI / 360) * Math.min(1, camera.aspect));
            var distance = radius / Math.sin(halfFov) * 1.15;
            controls.target.copy(center);
            camera.position.copy(center).add(new THREE.Vector3(0.65, 0.25, 1).normalize().multiplyScalar(distance));
            camera.near = radius / 1000; camera.far = distance + radius * 100;
            camera.updateProjectionMatrix(); controls.update();
            // The legacy grid uses unskinned geometry bounds. Rebuild it for the posed assembly.
            bandGroundQuad.position.set(center.x, bounds.min.y - radius * 0.005, center.z);
            bandGroundQuad.scale.setScalar(radius * 5 / bandGroundSize);
            bandGroundGrid.position.copy(bandGroundQuad.position); bandGroundGrid.position.y += radius * 0.001;
            bandGroundGrid.scale.copy(bandGroundQuad.scale);
            window.bandFramedBounds = bounds;
        }
        function bandCharacterLoaded(root, quad, grid, gridSize) {
            bandCharacterRoot = root;
            bandGroundQuad = quad; bandGroundGrid = grid; bandGroundSize = gridSize;
            root.traverse(function(node) { if (node.isMesh) node.frustumCulled = false; });
            if (mixer) mixer.update(0);
            frameBandAssembly();
            bandPost('bandReady');
        }
        """;
}
