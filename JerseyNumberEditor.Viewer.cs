namespace GlbMerger
{
    // JerseyNumberEditor's script for the shared preview page (FacePreviewPage). The model's
    // materials are patched with the jersey number overlay the game is to draw - this is its
    // reference copy: the game's port (FootballRoguelite model.vert / model.frag) must match it,
    // or what is tuned here stops being what the game shows. It also builds the digit atlas both
    // sample (JerseyNumbers.CellW/CellH/SdfSpread), and hands it to "Save Digit Atlas".
    //
    // Two positions reach the fragment shader:
    //   vNumBind - the bind-pose vertex. Only it decides WHERE a number may go (inside an anchor's
    //              slab, on a face pointing out of it), so a forearm swinging across the chest is
    //              never inside the slab and never takes a number.
    //   vNumPose - where the digits are read from: the skinned vertex taken back through the spine
    //              joint (so a vertex that only follows the spine lands on its bind position), mixed
    //              with vNumBind by Rigidity. 1 moves the number rigidly with the spine - it can't
    //              shear however the skin round it blends, but the cloth slides under it a little;
    //              0 glues it to the cloth, shearing exactly as a baked number did.
    public partial class JerseyNumberEditor
    {
        private static string BuildViewerHtml(string previewFileName) => FacePreviewPage.Build(
            previewFileName, "Click a jersey number to centre the selected anchor there. Drag to orbit, wheel to zoom.", ViewerScript);

        private const string ViewerScript = @"
        var CELL_W = 160, CELL_H = 200, SDF_SPREAD = 24;   // JerseyNumbers.CellW / CellH / SdfSpread

        // ---- The overlay (uniform-fed here; the game feeds it from the joint UBO) ----
        var NUM_VERT_HEAD = [
            'uniform mat4 numToSpine;',   // inverse of the spine joint's current skin matrix
            'uniform float numRigidity;',
            'varying vec3 vNumBind;', 'varying vec3 vNumPose;', 'varying vec3 vNumN;', 'varying float vNumRigid;', ''
        ].join('\n');
        var NUM_VERT_BODY = [
            '',
            'vNumBind = position;',
            'vNumN = normal;',
            'vNumPose = mix(position, (numToSpine * vec4(transformed, 1.0)).xyz, numRigidity);',
            '#ifdef USE_SKINNING',
            'vNumRigid = max(max(skinWeight.x, skinWeight.y), max(skinWeight.z, skinWeight.w));',
            '#else',
            'vNumRigid = 0.0;',
            '#endif', ''
        ].join('\n');
        var NUM_FRAG_HEAD = [
            // mesh -> box: x and y in cell half-heights (x runs +-aspect), z the slab at -1..1
            'uniform mat4 numFront;', 'uniform mat4 numBack;',
            'uniform vec4 numOn;',       // front aspect, back aspect (0 = off), show region, show rigid skin
            'uniform vec4 numDigits;',   // digit count (0-2), first digit, second digit, cell width (heights)
            'uniform vec4 numStyle;',    // gap (heights), outline width (heights)
            'uniform vec3 numFill;', 'uniform vec3 numOutline;',   // linear
            'uniform sampler2D numAtlas;',
            'varying vec3 vNumBind;', 'varying vec3 vNumPose;', 'varying vec3 vNumN;', 'varying float vNumRigid;',
            // Cell height in pixels over the SDF's full +-spread range: turns outline width (in
            // cell heights) into SDF units.
            'const float kNumSdfScale = ' + (CELL_H / (2 * SDF_SPREAD)).toFixed(6) + ';',
            // The SDF of digit d at cell coordinates c (0..1, y up), -1 outside the cell.
            'float NumberSdf(float d, vec2 c) {',
            '    float inCell = step(0.0, c.x) * step(c.x, 1.0) * step(0.0, c.y) * step(c.y, 1.0);',
            '    vec2 uv = vec2((d + clamp(c.x, 0.0, 1.0)) / 10.0, 1.0 - clamp(c.y, 0.0, 1.0));',   // atlas row 0 = top
            '    return mix(-1.0, texture2D(numAtlas, uv).r - 0.5, inCell);',
            '}',
            'vec3 PaintNumber(vec3 c, mat4 m, float aspect, vec3 outward) {',
            '    vec3 b = (m * vec4(vNumBind, 1.0)).xyz;',
            '    float gate = step(1e-4, aspect) * step(abs(b.x), aspect) * step(abs(b.y), 1.0) * step(abs(b.z), 1.0)',
            '               * smoothstep(0.1, 0.3, dot(normalize(vNumN), outward));',
            '    vec2 a = (m * vec4(vNumPose, 1.0)).xy;',
            // Digits side by side, centred; a cell spans the slab height (y -1..1 = 2 units).
            '    float n = numDigits.x, cw = 2.0 * numDigits.w, gap = 2.0 * numStyle.x;',
            '    float left = -0.5 * (n * cw + (n - 1.0) * gap);',
            '    float y = 0.5 * (a.y + 1.0);',
            '    float s0 = NumberSdf(numDigits.y, vec2((a.x - left) / cw, y));',
            '    float s1 = n > 1.5 ? NumberSdf(numDigits.z, vec2((a.x - left - cw - gap) / cw, y)) : -1.0;',
            '    float s = max(s0, s1);',
            '    float aa = 0.7 * max(fwidth(s), 1e-4);',
            '    float fill = smoothstep(-aa, aa, s);',
            '    float outline = smoothstep(-aa, aa, s + numStyle.y * kNumSdfScale);',
            '    c = mix(c, numOutline, outline * gate);',
            '    c = mix(c, numFill, fill * gate);',
            '    return mix(c, vec3(1.0, 0.0, 1.0), 0.3 * gate * numOn.z);',
            '}', ''
        ].join('\n');
        var NUM_FRAG_BODY = [
            '',
            'diffuseColor.rgb = PaintNumber(diffuseColor.rgb, numFront, numOn.x, vec3(0.0, 0.0, 1.0));',
            'diffuseColor.rgb = PaintNumber(diffuseColor.rgb, numBack, numOn.y, vec3(0.0, 0.0, -1.0));',
            // Skin bound to a single joint (JerseyNumbers.RigidWeight) - what a rigid region leaves.
            'diffuseColor.rgb = mix(diffuseColor.rgb, vec3(1.0, 0.1, 0.1), 0.45 * numOn.w * step(0.999, vNumRigid));', ''
        ].join('\n');

        var numUniforms = {
            numToSpine: { value: new THREE.Matrix4() },
            numRigidity: { value: 1 },
            numFront: { value: new THREE.Matrix4() }, numBack: { value: new THREE.Matrix4() },
            numOn: { value: new THREE.Vector4() },
            numDigits: { value: new THREE.Vector4(2, 1, 2, CELL_W / CELL_H) },
            numStyle: { value: new THREE.Vector4(0.02, 0.05, 0, 0) },
            numFill: { value: new THREE.Vector3(1, 1, 1) }, numOutline: { value: new THREE.Vector3(0, 0, 0) },
            numAtlas: { value: null }
        };

        function patchMaterial(mat) {
            mat.extensions = mat.extensions || {};
            mat.extensions.derivatives = true;
            mat.onBeforeCompile = function (shader) {
                for (var k in numUniforms) shader.uniforms[k] = numUniforms[k];
                shader.vertexShader = NUM_VERT_HEAD + shader.vertexShader.replace(
                    '#include <skinning_vertex>', '#include <skinning_vertex>' + NUM_VERT_BODY);
                shader.fragmentShader = NUM_FRAG_HEAD + shader.fragmentShader
                    .replace('#include <map_fragment>', '#include <map_fragment>' + NUM_FRAG_BODY);
            };
            mat.needsUpdate = true;
        }

        // ---- Digit atlas: each glyph rendered, then turned into a signed distance field ----

        // Felzenszwalb & Huttenlocher's 1D squared distance transform, in place on f (length n).
        function edt1d(f, n, d, v, z) {
            var k = 0; v[0] = 0; z[0] = -1e20; z[1] = 1e20;
            for (var q = 1; q < n; q++) {
                var s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]);
                while (s <= z[k]) { k--; s = ((f[q] + q * q) - (f[v[k]] + v[k] * v[k])) / (2 * q - 2 * v[k]); }
                k++; v[k] = q; z[k] = s; z[k + 1] = 1e20;
            }
            k = 0;
            for (var q = 0; q < n; q++) {
                while (z[k + 1] < q) k++;
                d[q] = (q - v[k]) * (q - v[k]) + f[v[k]];
            }
            for (var q = 0; q < n; q++) f[q] = d[q];
        }
        // Squared distance from every pixel to the nearest pixel where `seed` is set.
        function edt2d(seed, w, h) {
            var g = new Float64Array(w * h), n = Math.max(w, h);
            var f = new Float64Array(n), d = new Float64Array(n), v = new Int32Array(n), z = new Float64Array(n + 1);
            for (var i = 0; i < w * h; i++) g[i] = seed[i] ? 0 : 1e20;
            for (var x = 0; x < w; x++) {
                for (var y = 0; y < h; y++) f[y] = g[y * w + x];
                edt1d(f, h, d, v, z);
                for (var y = 0; y < h; y++) g[y * w + x] = f[y];
            }
            for (var y = 0; y < h; y++) {
                for (var x = 0; x < w; x++) f[x] = g[y * w + x];
                edt1d(f, w, d, v, z);
                for (var x = 0; x < w; x++) g[y * w + x] = f[x];
            }
            return g;
        }

        // The game's fonts arrive as files (JerseyNumberEditor serves Graphics/UI): each is loaded
        // once, and the atlas is built when it is ready. A Windows stand-in comes with no URL.
        var atlasCanvas = null, atlasFont = null, fontsReady = {};
        function useFont(font, url) {
            if (!url || fontsReady[font]) { buildAtlas(font, !url); return; }
            if (fontsReady[font] === false) return;   // still loading
            fontsReady[font] = false;
            new FontFace(font, 'url(""' + url + '"")').load().then(function (face) {
                document.fonts.add(face);
                fontsReady[font] = true;
                if (state && state.font === font) buildAtlas(font, false);
            }, function (err) {
                delete fontsReady[font];
                showError('Could not load the font ' + url + ': ' + err);
            });
        }
        function buildAtlas(font, heavy) {
            if (font === atlasFont && atlasCanvas) return;
            atlasFont = font;
            var W = CELL_W * 10, H = CELL_H;
            var glyphs = document.createElement('canvas');
            glyphs.width = W; glyphs.height = H;
            var g = glyphs.getContext('2d');
            g.fillStyle = '#fff';
            g.textAlign = 'center';
            g.textBaseline = 'alphabetic';
            // Digits 75% of the cell high, centred, leaving the SDF its spread round them.
            // A stand-in is asked for its heaviest weight; a game font is drawn as it was designed.
            var weight = heavy ? '900 ' : '';
            var target = 0.75 * CELL_H, size = target;
            g.font = weight + size + 'px ""' + font + '""';
            var m = g.measureText('0');
            var tall = (m.actualBoundingBoxAscent || 0) + (m.actualBoundingBoxDescent || 0);
            if (tall > 0) { size *= target / tall; g.font = weight + size + 'px ""' + font + '""'; }
            var widest = 0;
            for (var dg = 0; dg < 10; dg++) widest = Math.max(widest, g.measureText(String(dg)).width);
            var squeeze = Math.min(1, (CELL_W - 2 * SDF_SPREAD) / Math.max(widest, 1));
            for (var dg = 0; dg < 10; dg++) {
                var mt = g.measureText(String(dg));
                var asc = mt.actualBoundingBoxAscent || target, desc = mt.actualBoundingBoxDescent || 0;
                g.save();
                g.translate(dg * CELL_W + CELL_W / 2, CELL_H / 2 + (asc - desc) / 2);
                g.scale(squeeze, 1);
                g.fillText(String(dg), 0, 0);
                g.restore();
            }
            var src = g.getImageData(0, 0, W, H).data;
            var inside = new Uint8Array(W * H), outside = new Uint8Array(W * H);
            for (var i = 0; i < W * H; i++) { inside[i] = src[i * 4 + 3] > 127 ? 1 : 0; outside[i] = 1 - inside[i]; }
            var toInside = edt2d(inside, W, H), toOutside = edt2d(outside, W, H);
            atlasCanvas = document.createElement('canvas');
            atlasCanvas.width = W; atlasCanvas.height = H;
            var a = atlasCanvas.getContext('2d'), out = a.createImageData(W, H);
            for (var i = 0; i < W * H; i++) {
                var dist = inside[i] ? Math.sqrt(toOutside[i]) - 0.5 : 0.5 - Math.sqrt(toInside[i]);
                var r = Math.round(Math.min(Math.max(0.5 + dist / (2 * SDF_SPREAD), 0), 1) * 255);
                out.data[i * 4] = r; out.data[i * 4 + 1] = r; out.data[i * 4 + 2] = r; out.data[i * 4 + 3] = 255;
            }
            a.putImageData(out, 0, 0);
            var tex = new THREE.CanvasTexture(atlasCanvas);
            tex.flipY = false;   // v = 0 is the atlas's top row, as the game samples it
            tex.generateMipmaps = true;
            tex.minFilter = THREE.LinearMipmapLinearFilter;
            tex.magFilter = THREE.LinearFilter;
            if (numUniforms.numAtlas.value) numUniforms.numAtlas.value.dispose();
            numUniforms.numAtlas.value = tex;
        }
        window.exportAtlas = function () {
            if (!atlasCanvas) return;
            post({ action: 'atlas', png: atlasCanvas.toDataURL('image/png') });
        };

        // ---- State from C# (JerseyNumberEditor.PushState): centres / extents in mesh units ----

        var spineName = null, spineBone = null, spineMesh = null;
        function findSpine() {
            spineBone = null; spineMesh = null;
            meshes.forEach(function (mesh) {
                if (spineBone || !mesh.isSkinnedMesh) return;
                var bones = mesh.skeleton.bones;
                for (var i = 0; i < bones.length; i++)
                    if (normalise(bones[i].name) === normalise(spineName)) { spineBone = i; spineMesh = mesh; return; }
            });
        }

        // The box: centre to origin, turned so z points out of the body (back: 180 about Y), its
        // depth range [-in, out] centred, then scaled so y and the slab run -1..1 and x runs
        // +-aspect - x in the same cell half-heights as y, so digits keep their shape whatever
        // the box's width (JerseyNumber.glsl, AnchorFrame::MeshToFeature).
        function slabMatrix(a, back) {
            var din = a.r[2], dout = a.f, half = Math.max(0.5 * (din + dout), 1e-6);
            var m = new THREE.Matrix4().makeTranslation(-a.c[0], -a.c[1], -a.c[2]);
            if (back) m.premultiply(new THREE.Matrix4().makeRotationY(Math.PI));
            return m.premultiply(new THREE.Matrix4().makeTranslation(0, 0, -0.5 * (dout - din)))
                    .premultiply(new THREE.Matrix4().makeScale(1 / a.r[1], 1 / a.r[1], 1 / half));
        }

        var state = null;
        window.setState = function (s) {
            state = s;
            useFont(s.font, s.fontUrl);
            if (s.spine !== spineName) { spineName = s.spine; findSpine(); }
            var on = [0, 0];
            [numUniforms.numFront.value, numUniforms.numBack.value].forEach(function (m, side) {
                var a = s.anchors[side];
                if (!a.on || a.r[0] <= 0 || a.r[1] <= 0) return;
                m.copy(slabMatrix(a, side === 1));
                on[side] = a.r[0] / a.r[1];
            });
            numUniforms.numOn.value.set(on[0], on[1], s.showRegion ? 1 : 0, s.showRigid ? 1 : 0);
            var n = Math.max(0, Math.min(99, s.number | 0));
            var count = n >= 10 ? 2 : 1;
            numUniforms.numDigits.value.set(count, count === 2 ? Math.floor(n / 10) : n, n % 10,
                                            s.digitWidth * CELL_W / CELL_H);
            numUniforms.numStyle.value.set(s.digitGap, s.outlineWidth, 0, 0);
            numUniforms.numFill.value.set(srgbToLinear(s.fill[0]), srgbToLinear(s.fill[1]), srgbToLinear(s.fill[2]));
            numUniforms.numOutline.value.set(srgbToLinear(s.outline[0]), srgbToLinear(s.outline[1]), srgbToLinear(s.outline[2]));
            numUniforms.numRigidity.value = s.rigidity;
            setAnimation(s.anim, s.playing, s.time);
        };

        // ---- Animation ----

        var mixer = null, action = null, clipName = null;
        function setAnimation(name, playing, time) {
            if (!modelRoot) return;
            if (!mixer) mixer = new THREE.AnimationMixer(modelRoot);
            if (name !== clipName) {
                clipName = name;
                mixer.stopAllAction();
                action = null;
                var clip = name ? animations.find(function (c) { return c.name === name; }) : null;
                if (clip) { action = mixer.clipAction(clip); action.play(); }
                else modelRoot.traverse(function (o) { if (o.isSkinnedMesh) o.skeleton.pose(); });
            }
            if (action) {
                action.paused = !playing;
                if (!playing) { action.time = time * action.getClip().duration; mixer.update(0); }
            }
        }

        var tmpA = new THREE.Matrix4(), tmpB = new THREE.Matrix4();
        function onFrame(dt) {
            if (mixer && action && !action.paused) mixer.update(dt);
            if (spineMesh) {
                modelRoot.updateMatrixWorld(true);
                // three.js skins as bindMatrixInverse * boneWorld * boneInverse * bindMatrix; the
                // spine joint's own skin matrix, inverted, takes a skinned point back to bind pose.
                var sk = spineMesh.skeleton;
                tmpA.multiplyMatrices(sk.bones[spineBone].matrixWorld, sk.boneInverses[spineBone])
                    .premultiply(spineMesh.bindMatrixInverse).multiply(spineMesh.bindMatrix);
                numUniforms.numToSpine.value.copy(tmpB.copy(tmpA).invert());
            }
        }

        function onLoaded() { findSpine(); frameSide(false); }

        // The camera square on to the chest or the back, at the spine joint's height.
        window.frameSide = function (back) {
            if (meshes.length === 0) return;
            var target = new THREE.Vector3();
            var bone = spineMesh ? spineMesh.skeleton.bones[spineBone] : null;
            if (bone) bone.getWorldPosition(target);
            else new THREE.Box3().setFromObject(scene).getCenter(target);
            var forward = new THREE.Vector3(0, 0, back ? -1 : 1).transformDirection(meshes[0].matrixWorld);
            controls.target.copy(target);
            camera.position.copy(target).addScaledVector(forward, modelHeight * 1.1);
            controls.update();
        };

        // A click centres the anchor for the side the clicked face points to (its bind normal).
        function onPick(first, chosen, event) {
            var mesh = chosen.object, f = chosen.face, nrm = mesh.geometry.attributes.normal, nz = 0;
            if (nrm) nz = nrm.getZ(f.a) + nrm.getZ(f.b) + nrm.getZ(f.c);
            post({ action: 'placed', p: meshPointFromHit(chosen), back: nz < 0 });
        }
";
    }
}
